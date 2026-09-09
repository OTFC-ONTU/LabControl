using System.Net;
using System.Security.Cryptography.X509Certificates;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// The M5 acceptance drills that do not need a shared port (portion 8): the bulk import of
/// three independent labs with their offline authorization in one workflow, the restart that
/// proves the input files are not needed any more, and the identity rules of re-import —
/// same room on two devices, backup over an existing profile, and the same lab id arriving
/// under a different key.
/// </summary>
public sealed class AcceptanceDrillTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A device with no labs at all: what a teacher's console is after installation.</summary>
    private static ConsoleBootstrap NewDevice(out string dataDirectory)
    {
        dataDirectory = TestConsole.TempDirectory();
        return new ConsoleBootstrap(new ConsoleOptions
        {
            DataDirectory = dataDirectory,
            Port = 0,
            BeaconPort = TestConsole.BeaconPort,
            BindAddress = IPAddress.Loopback,
        }, TestLogging.Factory, () => new FileSecretProtector());
    }

    /// <summary>An administrator console whose <c>lab.json</c> already lists <paramref name="pcs"/> PCs.</summary>
    private static async Task<TestConsole> RoomAsync(string name, int pcs)
    {
        var console = await TestConsole.CreateLabAsync(name);
        console.Session.Registry.Persist(document => document.Machines = Enumerable.Range(1, pcs)
            .Select(number => new MachineRecord
            {
                AgentId = Guid.NewGuid().ToString("d"),
                Number = number,
                Hostname = string.Format(Defaults.MachineNameFormat, number),
                Mac = $"02:00:5E:00:{pcs:X2}:{number:X2}",
            })
            .ToList());
        console.Session.SaveLab();
        return console;
    }

    /// <summary>
    /// The acceptance drill: <i>bulk-import three independent labs, each with up to 30
    /// simulated PCs, in one workflow; restart the console: all three remain listed, none is
    /// active until selected, and the original input files are no longer needed. Complete
    /// initial authorization offline.</i>
    /// <para>
    /// The authorization is the batched one of portion 8 (finding B): one <i>Authorize all…</i>
    /// writes a request per lab into one folder, the administrators approve them, and one
    /// <i>Add labs…</i> batch imports the three grants. Nothing here activates a lab, and the
    /// files are deleted before the restart.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Three_labs_import_and_are_authorized_in_one_workflow_and_survive_the_files_being_deleted()
    {
        var rooms = new List<TestConsole>();
        var files = TestConsole.TempDirectory();
        var requests = TestConsole.TempDirectory();
        var bootstrap = NewDevice(out var dataDirectory);

        try
        {
            for (var i = 1; i <= 3; i++)
            {
                rooms.Add(await RoomAsync($"Room 21{i}", Defaults.MaxStudentPcs));
            }

            // One batch: three lab files chosen together, as the combined picker filter allows.
            var labFiles = new List<string>();
            foreach (var room in rooms)
            {
                // Every room here carries the same display name on purpose: the index must tell
                // them apart by lab id, not by what they are called.
                var path = Path.Combine(files, $"{room.Session.LabId}.lclab");
                Assert.True(room.Bootstrap.TryExportLabFile(room.Session, path, out var error), error);
                labFiles.Add(path);
            }

            var imports = new LabImports(bootstrap, _ => Task.FromResult<BackupSecret?>(null), "MacBook Air", TestLogging.Factory.CreateLogger("imports"));
            var added = await imports.ImportAsync(labFiles);
            Assert.Equal(3, added.Count);
            Assert.All(added, result => Assert.True(result.Ok, result.Message));

            // The picker really can offer a mixed selection: one filter covers every document type.
            var combined = imports.Filters[0];
            Assert.Equal(Defaults.ConsoleDocumentExtensions.Select(e => "*" + e).Order(), combined.Patterns.Order());

            Assert.Equal(3, bootstrap.Profiles.Profiles.Count);
            Assert.All(bootstrap.Profiles.Profiles, profile =>
            {
                Assert.Equal(ProfileAccess.Teacher, profile.Access);
                Assert.Equal(ProfileAuthorization.NeedsAuthorization, profile.Authorization);
                Assert.Equal(Defaults.MaxStudentPcs, profile.PcCount);
            });

            // Finding B: one action, one folder, one request per lab that needs authorization.
            var written = imports.Devices.WriteRequests(requests, DateTimeOffset.UtcNow);
            Assert.Equal(3, written.Count);
            Assert.All(written, result => Assert.True(result.Ok, result.Message));
            Assert.Equal(3, Directory.GetFiles(requests, "*" + Defaults.DeviceRequestFileExtension).Length);
            Assert.Equal(3, written.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // Each device identity is this device's own: three requests, three instance ids,
            // and none of them is any administrator console's.
            var instances = bootstrap.Profiles.Profiles.Select(p => p.InstanceId).ToList();
            Assert.Equal(3, instances.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Empty(instances.Intersect(rooms.Select(r => r.Session.Instance.InstanceId), StringComparer.OrdinalIgnoreCase));

            // Offline approval: the administrator of each room answers its own request.
            var grants = new List<string>();
            foreach (var path in Directory.GetFiles(requests, "*" + Defaults.DeviceRequestFileExtension).Order())
            {
                var request = DeviceAccess.ReadRequest(path);
                var room = rooms.Single(r => string.Equals(r.Session.LabId, request.LabId, StringComparison.OrdinalIgnoreCase));
                var devices = new DeviceAccess(room.Bootstrap, id => string.Equals(id, room.Session.LabId, StringComparison.OrdinalIgnoreCase) ? room.Session : null);
                var lab = room.Session.Vault!.Peek()!;
                grants.Add(devices.ApproveRequest(path, lab).GrantPath);
            }

            var accepted = await imports.ImportAsync(grants);
            Assert.Equal(3, accepted.Count);
            Assert.All(accepted, result => Assert.True(result.Ok, result.Message));
            Assert.All(bootstrap.Profiles.Profiles, profile => Assert.Equal(ProfileAuthorization.Authorized, profile.Authorization));

            // No lab needs anything any more, so the batched action has nothing to write.
            Assert.Empty(imports.Devices.NeedingAuthorization(DateTimeOffset.UtcNow));
            Assert.Empty(imports.Devices.WriteRequests(TestConsole.TempDirectory(), DateTimeOffset.UtcNow));

            // The stick goes home: every file the workflow used is deleted.
            Directory.Delete(files, recursive: true);
            Directory.Delete(requests, recursive: true);
            foreach (var room in rooms)
            {
                await room.Session.DisposeAsync();
            }

            // Restart: a new bootstrap and a new controller over the same data directory.
            var restarted = new ConsoleBootstrap(new ConsoleOptions
            {
                DataDirectory = dataDirectory,
                Port = 0,
                BeaconPort = TestConsole.BeaconPort,
                BindAddress = IPAddress.Loopback,
            }, TestLogging.Factory, () => new FileSecretProtector());
            await using var controller = new ActiveLabController(restarted, TestLogging.Factory);

            Assert.Equal(3, restarted.Profiles.Profiles.Count);
            Assert.All(restarted.Profiles.Profiles, profile =>
            {
                Assert.Equal(ProfileAuthorization.Authorized, profile.Authorization);
                Assert.Equal(Defaults.MaxStudentPcs, profile.PcCount);
            });

            // Nothing is active until a room is selected.
            Assert.Null(controller.Active);
            Assert.Equal(ActivationState.Idle, controller.Status.State);
            Assert.Equal(0, controller.Activations);

            // Selecting one opens it — with its saved mosaic — and the other two stay saved and closed.
            var chosen = restarted.Profiles.Profiles.OrderBy(p => p.LabName, StringComparer.Ordinal).First();
            var opened = await controller.ActivateAsync(chosen.LabId, Ct);
            Assert.True(opened.Ok, opened.Error);
            Assert.Equal(chosen.LabId, controller.Active!.LabId);
            Assert.Equal(Defaults.MaxStudentPcs, controller.Active.Registry.Document.Machines.Count);
            Assert.Null(controller.Active.Vault);
            Assert.Equal(1, controller.Activations);
        }
        finally
        {
            foreach (var room in rooms)
            {
                await room.DisposeAsync();
            }

            Delete(dataDirectory);
            Delete(files);
            Delete(requests);
        }
    }

    /// <summary>
    /// The identity rules of re-import (M5 <i>Deliverables</i>, "Bulk import and safe
    /// refresh"): re-importing the same lab file keeps this device's identity instead of
    /// minting a new one, and the same room imported on a second device gets its own key and
    /// its own instance id rather than a copy of the first device's.
    /// </summary>
    [Fact]
    public async Task Re_importing_a_lab_file_keeps_the_device_identity_and_a_second_device_gets_its_own()
    {
        await using var room = await RoomAsync("Room 214", 4);
        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        Assert.True(room.Bootstrap.TryExportLabFile(room.Session, labFile, out var error), error);

        var first = NewDevice(out var firstDirectory);
        var second = NewDevice(out var secondDirectory);

        try
        {
            var firstImports = new LabImports(first, _ => Task.FromResult<BackupSecret?>(null), "MacBook Air", TestLogging.Factory.CreateLogger("first"));
            Assert.True((await firstImports.ImportAsync([labFile])).Single().Ok);
            var identity = first.Profiles.Find(room.Session.LabId)!.InstanceId;
            var pendingKey = first.StoreFor(room.Session.LabId).LoadAccess()!.PendingKey!.Reference;

            // Re-import of the very same file, and of a newer snapshot: the identity stands.
            Assert.True((await firstImports.ImportAsync([labFile])).Single().Ok);
            var refreshed = Path.Combine(files, "room-again.lclab");
            Assert.True(room.Bootstrap.TryExportLabFile(room.Session, refreshed, out error), error);
            Assert.True((await firstImports.ImportAsync([refreshed])).Single().Ok);

            first.Profiles.Load();
            Assert.Equal(identity, first.Profiles.Find(room.Session.LabId)!.InstanceId);
            Assert.Equal(pendingKey, first.StoreFor(room.Session.LabId).LoadAccess()!.PendingKey!.Reference);
            Assert.Single(first.Profiles.Profiles);

            // The same room on a second device: its own instance id and its own pending key.
            var secondImports = new LabImports(second, _ => Task.FromResult<BackupSecret?>(null), "Windows desk PC", TestLogging.Factory.CreateLogger("second"));
            Assert.True((await secondImports.ImportAsync([labFile])).Single().Ok);
            var other = second.Profiles.Find(room.Session.LabId)!;

            Assert.NotEqual(identity, other.InstanceId);
            var firstAccess = first.StoreFor(room.Session.LabId).LoadAccess()!;
            var secondAccess = second.StoreFor(room.Session.LabId).LoadAccess()!;
            Assert.NotEqual(firstAccess.PendingKey!.Reference, secondAccess.PendingKey!.Reference);
            Assert.NotEqual(firstAccess.InstanceId, secondAccess.InstanceId);

            // Both devices pinned the same authority, and neither holds the lab key.
            Assert.Equal(firstAccess.Authority, secondAccess.Authority);
            Assert.False(first.StoreFor(room.Session.LabId).HasLabKey);
            Assert.False(second.StoreFor(room.Session.LabId).HasLabKey);
        }
        finally
        {
            Delete(firstDirectory);
            Delete(secondDirectory);
            Delete(files);
        }
    }

    /// <summary>
    /// A backup of a lab this device already administers adds no second room, and a lab id
    /// that arrives under a different key is refused for a <c>.lcbak</c> exactly as it is for
    /// a <c>.lclab</c> — the acceptance criterion says "the same id with different trust is
    /// rejected", and a backup must not be the way around it.
    /// </summary>
    [Fact]
    public async Task A_backup_never_duplicates_a_room_and_the_same_lab_id_under_another_key_is_refused()
    {
        await using var room = await RoomAsync("Room 214", 3);
        var files = TestConsole.TempDirectory();
        var backup = Path.Combine(files, "room.lcbak");
        Assert.True(room.Bootstrap.TryExportBackup(room.Session, backup, out var error), error);

        var device = NewDevice(out var directory);
        try
        {
            var imports = new LabImports(device, _ => Task.FromResult<BackupSecret?>(new BackupSecret(TestConsole.Passphrase, null)),
                "Windows desk PC", TestLogging.Factory.CreateLogger("imports"));

            var added = (await imports.ImportAsync([backup])).Single();
            Assert.True(added.Ok, added.Message);
            var profile = Assert.Single(device.Profiles.Profiles);
            Assert.Equal(ProfileAccess.Administrator, profile.Access);
            var identity = profile.InstanceId;

            // The same backup again: refused by name, one room, the identity untouched.
            var again = (await imports.ImportAsync([backup])).Single();
            Assert.False(again.Ok);
            Assert.Contains("already", again.Message, StringComparison.OrdinalIgnoreCase);
            device.Profiles.Load();
            Assert.Single(device.Profiles.Profiles);
            Assert.Equal(identity, device.Profiles.Find(room.Session.LabId)!.InstanceId);

            // A different lab whose id was made to collide, delivered as a backup: refused
            // because the pinned authority is not this one, and the saved profile is untouched.
            var impostorPath = Path.Combine(files, "impostor.lcbak");
            await using (var impostor = await TestConsole.CreateLabAsync("Impostor"))
            {
                Assert.True(impostor.Bootstrap.TryExportBackup(impostor.Session, impostorPath, out error), error);
            }

            RewriteLabId(impostorPath, room.Session.LabId);
            var refused = (await imports.ImportAsync([impostorPath])).Single();
            Assert.False(refused.Ok, refused.Message);
            device.Profiles.Load();
            var kept = Assert.Single(device.Profiles.Profiles);
            Assert.Equal(identity, kept.InstanceId);
            Assert.Equal(ProfileRecord.AuthorityFingerprintOf(room.Session.Vault!.Document.Authority), kept.AuthorityFingerprint);
            using var pinned = X509CertificateLoader.LoadCertificate(device.StoreFor(room.Session.LabId).LoadLabKey().Authority);
            Assert.Equal(room.Authority.Thumbprint, pinned.Thumbprint);

            // The same collision as a teacher lab file is refused too, and by the same rule.
            var impostorLab = Path.Combine(files, "impostor.lclab");
            await using (var impostor = await TestConsole.CreateLabAsync("Impostor 2"))
            {
                Assert.True(impostor.Bootstrap.TryExportLabFile(impostor.Session, impostorLab, out error), error);
            }

            RewriteLabId(impostorLab, room.Session.LabId);
            var refusedFile = (await imports.ImportAsync([impostorLab])).Single();
            Assert.False(refusedFile.Ok, refusedFile.Message);
            device.Profiles.Load();
            Assert.Single(device.Profiles.Profiles);
        }
        finally
        {
            Delete(directory);
            Delete(files);
        }
    }

    /// <summary>
    /// A teacher profile that a backup upgrades keeps its instance and its history — the same
    /// room, not a second one — and a wrong passphrase leaves it exactly as it was.
    /// </summary>
    [Fact]
    public async Task A_backup_upgrades_a_teacher_profile_in_place_and_a_wrong_passphrase_changes_nothing()
    {
        await using var room = await RoomAsync("Room 214", 2);
        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        var backup = Path.Combine(files, "room.lcbak");
        Assert.True(room.Bootstrap.TryExportLabFile(room.Session, labFile, out var error), error);
        Assert.True(room.Bootstrap.TryExportBackup(room.Session, backup, out error), error);

        var device = NewDevice(out var directory);
        try
        {
            var passphrase = "wrong passphrase";
            var imports = new LabImports(device, _ => Task.FromResult<BackupSecret?>(new BackupSecret(passphrase, null)),
                "MacBook Air", TestLogging.Factory.CreateLogger("imports"));

            Assert.True((await imports.ImportAsync([labFile])).Single().Ok);
            var identity = device.Profiles.Find(room.Session.LabId)!.InstanceId;

            var wrong = (await imports.ImportAsync([backup])).Single();
            Assert.False(wrong.Ok);
            device.Profiles.Load();
            var stillTeacher = Assert.Single(device.Profiles.Profiles);
            Assert.Equal(ProfileAccess.Teacher, stillTeacher.Access);
            Assert.Equal(identity, stillTeacher.InstanceId);
            Assert.False(device.StoreFor(room.Session.LabId).HasLabKey);

            passphrase = TestConsole.Passphrase;
            var upgraded = (await imports.ImportAsync([backup])).Single();
            Assert.True(upgraded.Ok, upgraded.Message);
            device.Profiles.Load();
            var administrator = Assert.Single(device.Profiles.Profiles);
            Assert.Equal(ProfileAccess.Administrator, administrator.Access);
            Assert.True(device.StoreFor(room.Session.LabId).HasLabKey);

            // One room, in the directory it already had: an upgrade, never a second profile.
            // (A teacher profile that had been granted its certificate keeps that instance too;
            // TeacherAccessTests covers that path — this one never got a grant, so the upgrade
            // mints the administrator instance the profile still lacks.)
            Assert.Single(Directory.GetDirectories(Path.Combine(directory, Defaults.LabsDirectoryName)));
            Assert.NotEmpty(administrator.InstanceId);
        }
        finally
        {
            Delete(directory);
            Delete(files);
        }
    }

    /// <summary>
    /// The chooser's side of finding B: <i>Authorize all…</i> is one button that appears only
    /// while some lab needs authorization, writes one request per such lab into the folder the
    /// teacher picked, and skips every lab that needs nothing — the administrator profile
    /// among them.
    /// </summary>
    [Fact]
    public async Task Authorize_all_writes_one_request_per_lab_that_needs_one_and_skips_the_rest()
    {
        var rooms = new List<TestConsole>();
        var files = TestConsole.TempDirectory();
        var folder = TestConsole.TempDirectory();
        var bootstrap = NewDevice(out var dataDirectory);

        try
        {
            for (var i = 0; i < 3; i++)
            {
                rooms.Add(await RoomAsync($"Room {i}", 2));
            }

            var documents = new List<string>();
            foreach (var room in rooms.Take(2))
            {
                var path = Path.Combine(files, $"{room.Session.LabId}.lclab");
                Assert.True(room.Bootstrap.TryExportLabFile(room.Session, path, out var error), error);
                documents.Add(path);
            }

            // The third room arrives as a backup: an administrator profile, which needs nothing.
            var backup = Path.Combine(files, "admin.lcbak");
            Assert.True(rooms[2].Bootstrap.TryExportBackup(rooms[2].Session, backup, out var backupError), backupError);
            documents.Add(backup);

            await using var controller = new ActiveLabController(bootstrap, TestLogging.Factory);
            var dialogs = new RecordingDialogs { Folder = folder };
            var chooser = new LabChooserViewModel(bootstrap, controller, dialogs, action => action(), TestLogging.Factory.CreateLogger("chooser"));

            // Nothing is saved yet: the button is not there.
            Assert.Equal(0, chooser.PendingAuthorizations);
            Assert.False(chooser.HasPendingAuthorizations);
            Assert.False(chooser.AuthorizeAllCommand.CanExecute(null));

            dialogs.Files = documents;
            await chooser.AddLabsCommand.ExecuteAsync(null);
            Assert.Equal(3, dialogs.LastResults.Count);
            Assert.All(dialogs.LastResults, result => Assert.True(result.Ok, result.Message));

            // The picker was offered the combined filter first, then one per type.
            Assert.Equal(Defaults.ConsoleDocumentExtensions.Count + 1, dialogs.LastFilters.Count);
            Assert.Equal(Defaults.ConsoleDocumentExtensions.Select(e => "*" + e).Order(), dialogs.LastFilters[0].Patterns.Order());
            Assert.All(dialogs.LastFilters.Skip(1), filter => Assert.Single(filter.Patterns));

            // Two labs need authorization; the administrator one does not.
            Assert.Equal(2, chooser.PendingAuthorizations);
            Assert.True(chooser.HasPendingAuthorizations);
            Assert.Contains("2", chooser.AuthorizeAllLabel, StringComparison.Ordinal);
            Assert.True(chooser.AuthorizeAllCommand.CanExecute(null));

            await chooser.AuthorizeAllCommand.ExecuteAsync(null);

            var written = Directory.GetFiles(folder, "*" + Defaults.DeviceRequestFileExtension);
            Assert.Equal(2, written.Length);
            var forLabs = written.Select(path => DeviceAccess.ReadRequest(path).LabId).ToList();
            Assert.Equal(rooms.Take(2).Select(r => r.Session.LabId).Order(), forLabs.Order());
            Assert.DoesNotContain(rooms[2].Session.LabId, forLabs);
            Assert.Equal(2, dialogs.LastResults.Count);
            Assert.All(dialogs.LastResults, result => Assert.True(result.Ok, result.Message));
            Assert.Empty(chooser.Error);
        }
        finally
        {
            foreach (var room in rooms)
            {
                await room.DisposeAsync();
            }

            Delete(dataDirectory);
            Delete(files);
            Delete(folder);
        }
    }

    /// <summary>An <see cref="IDialogs"/> that answers with what the test put in it and remembers what it was shown.</summary>
    private sealed class RecordingDialogs : ViewModels.IDialogs
    {
        public IReadOnlyList<string> Files { get; set; } = [];

        public string? Folder { get; set; }

        public IReadOnlyList<FileFilter> LastFilters { get; private set; } = [];

        public IReadOnlyList<ImportFileResult> LastResults { get; private set; } = [];

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileFilter> filters)
        {
            LastFilters = filters;
            return Task.FromResult(Files);
        }

        public Task<string?> PickFolderAsync(string title) => Task.FromResult(Folder);

        public Task ShowImportResultsAsync(IReadOnlyList<ImportFileResult> results)
        {
            LastResults = results;
            return Task.CompletedTask;
        }

        public Task<ViewModels.UnlockAnswer?> UnlockAsync(string reason) =>
            Task.FromResult<ViewModels.UnlockAnswer?>(new ViewModels.UnlockAnswer(TestConsole.Passphrase, null));

        public Task<ViewModels.HolderAnswer?> AddHolderAsync() => Task.FromResult<ViewModels.HolderAnswer?>(null);

        public Task<string?> AskTextAsync(string title, string prompt, string initial = "", bool secret = false) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false) => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowRecoveryCodeAsync(RecoveryCode code) => Task.FromResult(false);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult<string?>(null);

        public Task<string?> PickOpenFileAsync(string title, string extension) => Task.FromResult<string?>(null);

        public Task<ViewModels.SendFilesAnswer?> SendFilesAsync(int pcCount) => Task.FromResult<ViewModels.SendFilesAnswer?>(null);

        public Task<Shared.Setup.AgentBuild?> PushAgentBuildAsync(int pcCount) => Task.FromResult<Shared.Setup.AgentBuild?>(null);

        public void ShowScreen(ViewModels.ScreenViewModel screen)
        {
        }
    }

    /// <summary>
    /// Rewrites the <c>lab_id</c> of a signed document so that a lab id this device already
    /// holds arrives under another lab's key — what the acceptance criteria call "the same id
    /// with different trust". The signature still covers the real payload, so only the outer
    /// claim moves; that is exactly the file an attacker can make.
    /// </summary>
    private static void RewriteLabId(string path, string labId)
    {
        var text = File.ReadAllText(path);
        var document = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
        document["lab_id"] = labId;
        if (document["lab_key"] is System.Text.Json.Nodes.JsonObject key)
        {
            key["lab_id"] = labId;
        }

        File.WriteAllText(path, document.ToJsonString());
    }

    private static void Delete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }
}
