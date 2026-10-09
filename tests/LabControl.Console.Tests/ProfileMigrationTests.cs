using Xunit;

using System.Net;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Tests;

/// <summary>
/// M5 portion 1 (D-55): the pre-M5 single-lab directory moves into <c>labs/&lt;lab_id&gt;/</c>
/// with the same key, the same instance and every file byte for byte; a crash after any step
/// resumes to the same end state and never touches an original before the commit point;
/// nothing at the root is deleted unless it matches its copy; a migrated or fresh directory
/// is left alone; two <c>--data</c> directories never see each other; and one process holds
/// a data directory at a time.
/// </summary>
public sealed class ProfileMigrationTests
{
    private const string Passphrase = "correct horse battery staple";

    private static readonly string[] Documents =
        [Defaults.LabKeyFileName, Defaults.InstanceFileName, Defaults.LabFileName, Defaults.EnrollmentFileName, Defaults.ScriptsFileName];

    /// <summary>A pre-M5 root as it was created, with the hash of every lab file in it.</summary>
    private sealed record Legacy(string Directory, string LabId, string InstanceId, IReadOnlyDictionary<string, string> Originals);

    /// <summary>
    /// A data directory exactly as an M4 console left it: everything flat at the root, the
    /// way <c>ConsoleBootstrap.CreateLab</c> wrote it before M5, plus a script, a package
    /// file, an event log and the app-level console log.
    /// </summary>
    private static Legacy CreateLegacyLab(string? directory = null)
    {
        directory ??= TestConsole.TempDirectory();
        var store = new LabStore(directory);
        store.EnsureDirectories();

        var lab = LabKey.Create("Room 214", "Viacheslav", Passphrase, out _, iterations: TestConsole.Iterations);
        store.SaveLabKey(lab.Document);
        var instance = ConsoleInstance.Mint(lab, "MacBook", new FileSecretProtector());
        store.SaveInstance(instance.Document);
        store.SaveLab(new LabDocument
        {
            LabId = lab.LabId,
            LabName = lab.LabName,
            Machines = [new MachineRecord { AgentId = "a", Number = 7, Hostname = "PC-07" }, new MachineRecord { AgentId = "b", Number = 8, Hostname = "PC-08" }],
            Instances = [new InstanceRecord { InstanceId = instance.InstanceId, Name = "MacBook", IsThisMachine = true }],
        });
        store.SaveEnrollment(new EnrollmentDocument { LabId = lab.LabId, Codes = [new EnrollmentCodeRecord { Code = "ABCDEFGHJKMNPQRSTVWX", Batch = "stick-1" }] });
        store.SaveScripts(new ScriptsDocument { LabId = lab.LabId, Scripts = [new ScriptRecord { Id = "s", Name = "from-the-macbook", Text = "hostname\n" }] });
        store.WriteCatalog(new Dictionary<string, string> { ["jdk.yaml"] = "name: jdk" });
        File.WriteAllText(Path.Combine(store.LogsDirectory, "events-2026-09-01.jsonl"), "{}\n");
        Directory.CreateDirectory(Path.Combine(store.LogsDirectory, Defaults.JobBatchesDirectoryName));
        File.WriteAllText(Path.Combine(store.LogsDirectory, Defaults.JobBatchesDirectoryName, "batch.json"), "{}");
        File.WriteAllText(Path.Combine(store.LogsDirectory, "console-20260901.log"), "app log\n");

        instance.Dispose();
        return new Legacy(directory, lab.LabId, instance.InstanceId, HashLabFiles(directory));
    }

    private static ConsoleOptions OptionsFor(string directory) =>
        new() { DataDirectory = directory, Port = 0, BindAddress = IPAddress.Loopback, BeaconPort = TestConsole.BeaconPort };

    private static ProfileMigration Migration(string directory) =>
        new(directory, TestLogging.Factory.CreateLogger<ProfileMigration>());

    /// <summary>
    /// SHA-256 of every lab file in a lab directory (a legacy root or <c>labs/&lt;id&gt;</c>)
    /// by relative path: the five documents, <c>packages/**</c> and <c>logs/**</c> minus the
    /// app's own <c>console-*.log</c>. Content, not presence, is what a move must preserve.
    /// </summary>
    private static IReadOnlyDictionary<string, string> HashLabFiles(string directory)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Documents)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                hashes[name] = JsonStore.FingerprintFile(path);
            }
        }

        foreach (var sub in new[] { Defaults.PackagesDirectoryName, Defaults.LogsDirectoryName })
        {
            var root = Path.Combine(directory, sub);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (Path.GetFileName(file).StartsWith(Defaults.ConsoleLogFilePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                hashes[sub + "/" + Path.GetRelativePath(root, file).Replace('\\', '/')] = JsonStore.FingerprintFile(file);
            }
        }

        return hashes;
    }

    private static void AssertSameFiles(IReadOnlyDictionary<string, string> expected, string directory) =>
        Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), HashLabFiles(directory).OrderBy(pair => pair.Key, StringComparer.Ordinal));

    private static void AssertRootIsClean(string directory)
    {
        foreach (var name in Documents)
        {
            Assert.False(File.Exists(Path.Combine(directory, name)), $"{name} still at the root");
            Assert.False(File.Exists(JsonStore.TemporaryPathFor(Path.Combine(directory, name))), $"{name} temporary still at the root");
        }

        Assert.False(Directory.Exists(Path.Combine(directory, Defaults.PackagesDirectoryName)));
        Assert.False(Directory.Exists(Path.Combine(directory, Defaults.LogsDirectoryName)), "logs/ at the root is gone once its lab files and app logs have moved");
        Assert.False(ProfileMigration.IsPending(directory));
    }

    /// <summary>The moved lab is complete, byte for byte what was at the root, and opens as the same instance.</summary>
    private static void AssertMigrated(Legacy legacy, IReadOnlyDictionary<string, string>? expected = null)
    {
        var (directory, labId, instanceId, _) = legacy;
        expected ??= legacy.Originals;

        var profiles = new ProfileStore(directory);
        var entry = Assert.Single(profiles.Profiles, profile => profile.LabId == labId);
        Assert.Equal("Room 214", entry.LabName);
        Assert.Equal($"{Defaults.LabsDirectoryName}/{labId}", entry.Directory);
        Assert.Equal(ProfileAccess.Administrator, entry.Access);
        Assert.Equal(ProfileAuthorization.Authorized, entry.Authorization);
        Assert.Equal(ProfileSource.Migrated, entry.Source);
        Assert.Equal(instanceId, entry.InstanceId);
        Assert.Equal("MacBook", entry.InstanceName);
        Assert.Equal(2, entry.PcCount);
        Assert.Equal(64, entry.AuthorityFingerprint.Length);

        var moved = Path.Combine(directory, Defaults.LabsDirectoryName, labId);
        Assert.Equal(moved, profiles.Directory(labId));
        Assert.False(Directory.Exists(moved + Defaults.MigratingDirectorySuffix));
        AssertSameFiles(expected, moved);
        Assert.False(File.Exists(Path.Combine(moved, Defaults.LogsDirectoryName, "console-20260901.log")), "the app log is not lab data");
        // The old app log moved to the data root, where Serilog writes now and retention applies.
        Assert.Equal("app log\n", File.ReadAllText(Path.Combine(directory, "console-20260901.log")));
        AssertRootIsClean(directory);

        // The bootstrap reopens the same instance, and the key still opens with the passphrase.
        var bootstrap = new ConsoleBootstrap(OptionsFor(directory), TestLogging.Factory);
        Assert.True(bootstrap.HasLab);
        var opened = bootstrap.OpenExisting(labId);
        try
        {
            Assert.Equal(labId, opened.Vault!.LabId);
            Assert.Equal(instanceId, opened.Instance.InstanceId);
            Assert.Equal(moved, opened.Store.Directory);
            Assert.True(opened.Vault!.TryUnlock(Passphrase));
            Assert.Equal("from-the-macbook", Assert.Single(opened.Store.LoadScripts()!.Scripts).Name);
            Assert.Equal("ABCDEFGHJKMNPQRSTVWX", Assert.Single(opened.Store.LoadEnrollment(labId).Codes).Code);
        }
        finally
        {
            opened.Vault!.Dispose();
            opened.Instance.Dispose();
        }
    }

    [Fact]
    public void A_single_lab_directory_moves_into_labs_with_the_same_key_and_instance()
    {
        var legacy = CreateLegacyLab();
        var (directory, labId, _, _) = legacy;
        try
        {
            var steps = new List<MigrationStep>();
            var migration = Migration(directory);
            migration.StepCompleted += steps.Add;

            Assert.True(ProfileMigration.IsPending(directory));
            Assert.Equal(labId, migration.Run());
            Assert.Equal(new[] { MigrationStep.Copied, MigrationStep.Renamed, MigrationStep.Committed, MigrationStep.Deleted }, steps);

            AssertMigrated(legacy);
            Assert.Equal(labId, new ProfileStore(directory).LastUsedLabId);
            Assert.Equal(labId, new ConsoleBootstrap(OptionsFor(directory), TestLogging.Factory).DefaultLabId);

            // The moved key and instance are still private to their owner.
            if (!OperatingSystem.IsWindows())
            {
                var moved = Path.Combine(directory, Defaults.LabsDirectoryName, labId);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(moved, Defaults.LabKeyFileName)));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(moved, Defaults.InstanceFileName)));
            }
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Theory]
    [InlineData(MigrationStep.Copied)]
    [InlineData(MigrationStep.Renamed)]
    [InlineData(MigrationStep.Committed)]
    public void A_crash_after_any_step_resumes_to_the_same_end_state_on_the_next_launch(MigrationStep crashAfter)
    {
        var legacy = CreateLegacyLab();
        var (directory, labId, _, originals) = legacy;
        try
        {
            var first = Migration(directory);
            first.StepCompleted += step =>
            {
                if (step == crashAfter)
                {
                    throw new SimulatedCrash();
                }
            };

            Assert.Throws<SimulatedCrash>(() => first.Run());

            // Whatever step it died after, the originals are byte for byte what they were until
            // the delete step, and afterwards the new directory is complete.
            AssertSameFiles(originals, directory);
            Assert.True(ProfileMigration.IsPending(directory));
            if (crashAfter == MigrationStep.Copied)
            {
                Assert.True(Directory.Exists(Path.Combine(directory, Defaults.LabsDirectoryName, labId + Defaults.MigratingDirectorySuffix)));
                Assert.False(File.Exists(Path.Combine(directory, Defaults.ProfilesFileName)));
            }

            if (crashAfter == MigrationStep.Renamed)
            {
                Assert.True(Directory.Exists(Path.Combine(directory, Defaults.LabsDirectoryName, labId)));
                Assert.False(File.Exists(Path.Combine(directory, Defaults.ProfilesFileName)));
            }

            if (crashAfter == MigrationStep.Committed)
            {
                Assert.True(File.Exists(Path.Combine(directory, Defaults.ProfilesFileName)));
            }

            // The next launch.
            var steps = new List<MigrationStep>();
            var second = Migration(directory);
            second.StepCompleted += steps.Add;
            Assert.Equal(labId, second.Run());
            Assert.Equal(MigrationStep.Deleted, steps[^1]);
            if (crashAfter >= MigrationStep.Renamed)
            {
                Assert.DoesNotContain(MigrationStep.Copied, steps);
            }

            AssertMigrated(legacy);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void A_crash_in_the_middle_of_deleting_the_originals_is_finished_by_the_next_launch()
    {
        var legacy = CreateLegacyLab();
        var (directory, labId, _, _) = legacy;
        try
        {
            var first = Migration(directory);
            first.StepCompleted += step =>
            {
                if (step == MigrationStep.Committed)
                {
                    throw new SimulatedCrash();
                }
            };
            Assert.Throws<SimulatedCrash>(() => first.Run());

            // The delete step goes lab documents, directories, instance, key — so a crash in
            // it leaves the key, and the next launch still sees a pending root.
            File.Delete(Path.Combine(directory, Defaults.LabFileName));
            File.Delete(Path.Combine(directory, Defaults.ScriptsFileName));
            Directory.Delete(Path.Combine(directory, Defaults.PackagesDirectoryName), recursive: true);
            File.Delete(Path.Combine(directory, Defaults.InstanceFileName));
            Assert.True(ProfileMigration.IsPending(directory));

            var steps = new List<MigrationStep>();
            var second = Migration(directory);
            second.StepCompleted += steps.Add;
            Assert.Equal(labId, second.Run());
            Assert.Equal(new[] { MigrationStep.Deleted }, steps);
            AssertMigrated(legacy);

            // And the leftover that has neither sentinel — the key went, the log directory did
            // not — is still recognised through the index and cleaned up.
            var moved = Path.Combine(directory, Defaults.LabsDirectoryName, labId);
            Directory.CreateDirectory(Path.Combine(directory, Defaults.LogsDirectoryName));
            File.Copy(Path.Combine(moved, Defaults.LogsDirectoryName, "events-2026-09-01.jsonl"), Path.Combine(directory, Defaults.LogsDirectoryName, "events-2026-09-01.jsonl"));
            File.Copy(Path.Combine(moved, Defaults.LabFileName), Path.Combine(directory, Defaults.LabFileName));
            Assert.True(ProfileMigration.IsPending(directory));
            Assert.Equal(labId, Migration(directory).Run());
            AssertMigrated(legacy);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void A_half_copied_staging_directory_is_discarded_and_copied_again()
    {
        var legacy = CreateLegacyLab();
        var (directory, labId, _, _) = legacy;
        try
        {
            // A copy that died before the instance document made it across.
            var staging = Path.Combine(directory, Defaults.LabsDirectoryName, labId + Defaults.MigratingDirectorySuffix);
            Directory.CreateDirectory(staging);
            File.Copy(Path.Combine(directory, Defaults.LabKeyFileName), Path.Combine(staging, Defaults.LabKeyFileName));
            File.WriteAllText(Path.Combine(staging, "garbage.txt"), "half");

            Assert.Equal(labId, Migration(directory).Run());
            AssertMigrated(legacy);
            Assert.False(File.Exists(Path.Combine(directory, Defaults.LabsDirectoryName, labId, "garbage.txt")));
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void An_uncommitted_copy_that_differs_from_the_root_is_thrown_away_and_the_root_copied_again()
    {
        var legacy = CreateLegacyLab();
        var (directory, labId, _, originals) = legacy;
        try
        {
            // labs/<id>/ exists from an earlier attempt that renamed but never committed, and
            // the root has moved on since: one more PC, one more package, one fewer log.
            var target = Path.Combine(directory, Defaults.LabsDirectoryName, labId);
            CopyRootTo(directory, target);
            var store = new LabStore(directory);
            var lab = store.LoadLab(labId, "");
            lab.Machines.Add(new MachineRecord { AgentId = "c", Number = 9, Hostname = "PC-09" });
            store.SaveLab(lab);
            File.WriteAllText(Path.Combine(store.PackagesDirectory, "python.yaml"), "name: python");
            File.WriteAllText(Path.Combine(store.LogsDirectory, "events-2026-09-01.jsonl"), "{}\n{}\n");
            var newer = HashLabFiles(directory);
            Assert.NotEqual(originals, newer);

            var steps = new List<MigrationStep>();
            var migration = Migration(directory);
            migration.StepCompleted += steps.Add;
            Assert.Equal(labId, migration.Run());
            Assert.Equal(new[] { MigrationStep.Copied, MigrationStep.Renamed, MigrationStep.Committed, MigrationStep.Deleted }, steps);

            // The root's newer state is what ended up in labs/<id>/, and nothing was lost.
            AssertSameFiles(newer, target);
            Assert.Equal(3, new ProfileStore(directory).Find(labId)!.PcCount);
            Assert.Equal("name: python", File.ReadAllText(Path.Combine(target, Defaults.PackagesDirectoryName, "python.yaml")));
            AssertRootIsClean(directory);
            Assert.Empty(Directory.EnumerateDirectories(directory, Defaults.MigrationConflictDirectoryPrefix + "*"));
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void A_root_that_no_longer_matches_a_committed_lab_is_moved_aside_never_deleted()
    {
        var legacy = CreateLegacyLab();
        var (directory, labId, _, originals) = legacy;
        try
        {
            Assert.Equal(labId, Migration(directory).Run());
            var target = Path.Combine(directory, Defaults.LabsDirectoryName, labId);
            var committed = HashLabFiles(target);

            // docs/DEVELOPMENT.md "Downgrading": the owner copied labs/<id>/ back to the root and ran a pre-M5
            // build, which enrolled a PC and wrote a log. Then this build starts again.
            CopyRootTo(target, directory);
            var store = new LabStore(directory);
            var lab = store.LoadLab(labId, "");
            lab.Machines.Add(new MachineRecord { AgentId = "c", Number = 9, Hostname = "PC-09" });
            store.SaveLab(lab);
            File.WriteAllText(Path.Combine(store.LogsDirectory, "events-2026-09-02.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(store.LogsDirectory, "console-20260902.log"), "old build\n");
            var newer = HashLabFiles(directory);
            Assert.True(ProfileMigration.IsPending(directory));

            var steps = new List<MigrationStep>();
            var migration = Migration(directory);
            migration.StepCompleted += steps.Add;
            var when = new DateTimeOffset(2026, 9, 8, 10, 30, 45, TimeSpan.Zero);
            Assert.Equal(labId, migration.Run(when));
            Assert.Equal(new[] { MigrationStep.MovedAside }, steps);

            // The committed lab is untouched, the root's files are complete in the conflict
            // directory — including the ones that matched — and the root itself is clean.
            AssertSameFiles(committed, target);
            var conflict = Path.Combine(directory, Defaults.MigrationConflictDirectoryPrefix + "20260908-103045");
            Assert.True(Directory.Exists(conflict), "the conflict directory");
            AssertSameFiles(newer, conflict);
            Assert.Equal(3, new LabStore(conflict).LoadLab(labId, "").Machines.Count);
            AssertRootIsClean(directory);
            Assert.Equal("old build\n", File.ReadAllText(Path.Combine(directory, "console-20260902.log")));
            Assert.False(ProfileMigration.IsPending(directory));

            // The console starts from the committed lab: two PCs, the same instance.
            AssertMigrated(legacy, originals);
            Assert.Equal(2, new ProfileStore(directory).Find(labId)!.PcCount);
            Assert.Null(Migration(directory).Run());
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void A_symbolic_link_under_packages_is_skipped_and_the_lab_still_moves()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating a symlink needs a privilege the test runner may not have
        }

        var legacy = CreateLegacyLab();
        var (directory, labId, _, originals) = legacy;
        try
        {
            var packages = Path.Combine(directory, Defaults.PackagesDirectoryName);
            Directory.CreateSymbolicLink(Path.Combine(packages, "loop"), packages);
            File.CreateSymbolicLink(Path.Combine(packages, "jdk-link.yaml"), Path.Combine(packages, "jdk.yaml"));

            Assert.Equal(labId, Migration(directory).Run());
            AssertMigrated(legacy, originals);
            var moved = Path.Combine(directory, Defaults.LabsDirectoryName, labId, Defaults.PackagesDirectoryName);
            Assert.False(Directory.Exists(Path.Combine(moved, "loop")));
            Assert.False(File.Exists(Path.Combine(moved, "jdk-link.yaml")));
            Assert.Equal(new[] { "jdk.yaml" }, Directory.EnumerateFileSystemEntries(moved).Select(path => Path.GetFileName(path)).ToArray());
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task A_legacy_root_beside_an_index_that_names_another_lab_becomes_a_second_profile()
    {
        var directory = TestConsole.TempDirectory();
        try
        {
            var bootstrap = new ConsoleBootstrap(OptionsFor(directory), TestLogging.Factory);
            string labA;
            await using (var sessionA = bootstrap.CreateLab("Room A", "Viacheslav", Passphrase, "MacBook", out _))
            {
                labA = sessionA.LabId;
            }

            var legacy = CreateLegacyLab(directory);
            Assert.True(ProfileMigration.IsPending(directory));
            Assert.Equal(legacy.LabId, Migration(directory).Run());

            AssertMigrated(legacy);
            var profiles = new ProfileStore(directory);
            Assert.Equal(2, profiles.Profiles.Count);
            Assert.Equal(labA, profiles.LastUsedLabId);
            Assert.True(File.Exists(Path.Combine(directory, Defaults.LabsDirectoryName, labA, Defaults.LabKeyFileName)));

            var reopened = new ConsoleBootstrap(OptionsFor(directory), TestLogging.Factory);
            Assert.Equal(labA, reopened.DefaultLabId);
            var openedA = reopened.OpenExisting(labA);
            var openedB = reopened.OpenExisting(legacy.LabId);
            try
            {
                Assert.Equal("Room A", openedA.Vault!.LabName);
                Assert.Equal("Room 214", openedB.Vault!.LabName);
            }
            finally
            {
                openedA.Vault!.Dispose();
                openedA.Instance.Dispose();
                openedB.Vault!.Dispose();
                openedB.Instance.Dispose();
            }
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void Without_a_last_used_mark_the_most_recently_used_lab_opens()
    {
        var directory = TestConsole.TempDirectory();
        try
        {
            var profiles = new ProfileStore(directory);
            profiles.Upsert(new ProfileRecord { LabId = "aaaa", LabName = "older use, newer add", AddedAtUnix = 300, LastUsedUnix = 100 });
            profiles.Upsert(new ProfileRecord { LabId = "bbbb", LabName = "newer use", AddedAtUnix = 200, LastUsedUnix = 200 });
            profiles.Upsert(new ProfileRecord { LabId = "cccc", LabName = "never used, newest add", AddedAtUnix = 400, LastUsedUnix = 0 });
            profiles.LastUsedLabId = null;
            profiles.Save();

            Assert.Equal("bbbb", new ConsoleBootstrap(OptionsFor(directory), TestLogging.Factory).DefaultLabId);

            // Nothing ever used: the one added last.
            foreach (var profile in profiles.Profiles)
            {
                profile.LastUsedUnix = 0;
            }

            profiles.Save();
            Assert.Equal("cccc", new ConsoleBootstrap(OptionsFor(directory), TestLogging.Factory).DefaultLabId);

            // A last-used mark that names a lab in the index wins.
            profiles.LastUsedLabId = "aaaa";
            profiles.Save();
            Assert.Equal("aaaa", new ConsoleBootstrap(OptionsFor(directory), TestLogging.Factory).DefaultLabId);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void A_second_launch_after_the_migration_changes_nothing()
    {
        var (directory, labId, _, _) = CreateLegacyLab();
        try
        {
            Assert.Equal(labId, Migration(directory).Run());

            var moved = Path.Combine(directory, Defaults.LabsDirectoryName, labId);
            var before = Snapshot(directory);
            var profilesBefore = File.ReadAllText(Path.Combine(directory, Defaults.ProfilesFileName));

            var again = Migration(directory);
            again.StepCompleted += _ => throw new InvalidOperationException("nothing should happen on a migrated directory");
            Assert.Null(again.Run());

            Assert.Equal(before, Snapshot(directory));
            Assert.Equal(profilesBefore, File.ReadAllText(Path.Combine(directory, Defaults.ProfilesFileName)));
            Assert.True(Directory.Exists(moved));
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void A_key_and_an_instance_from_different_labs_are_refused_untouched()
    {
        var (directory, _, _, originals) = CreateLegacyLab();
        try
        {
            var other = LabKey.Create("Other", "Someone", Passphrase, out _, iterations: TestConsole.Iterations);
            var stranger = ConsoleInstance.Mint(other, "Stranger", new FileSecretProtector());
            new LabStore(directory).SaveInstance(stranger.Document);
            stranger.Dispose();
            var mixed = HashLabFiles(directory);
            Assert.NotEqual(originals, mixed);

            var error = Assert.Throws<InvalidDataException>(() => Migration(directory).Run());
            Assert.Contains(Defaults.InstanceFileName, error.Message, StringComparison.Ordinal);
            Assert.Contains(Defaults.LabKeyFileName, error.Message, StringComparison.Ordinal);
            AssertSameFiles(mixed, directory);
            Assert.False(Directory.Exists(Path.Combine(directory, Defaults.LabsDirectoryName)));
            Assert.False(File.Exists(Path.Combine(directory, Defaults.ProfilesFileName)));
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void An_instance_without_a_key_is_a_broken_installation_and_is_refused_untouched()
    {
        var (directory, _, _, _) = CreateLegacyLab();
        try
        {
            File.Delete(Path.Combine(directory, Defaults.LabKeyFileName));
            var broken = HashLabFiles(directory);
            Assert.True(ProfileMigration.IsPending(directory));

            var error = Assert.Throws<InvalidDataException>(() => Migration(directory).Run());
            Assert.Contains(Defaults.InstanceFileName, error.Message, StringComparison.Ordinal);
            Assert.Contains(Defaults.LabKeyFileName, error.Message, StringComparison.Ordinal);
            Assert.Contains(directory, error.Message, StringComparison.Ordinal);
            AssertSameFiles(broken, directory);
            Assert.False(Directory.Exists(Path.Combine(directory, Defaults.LabsDirectoryName)));
            Assert.False(File.Exists(Path.Combine(directory, Defaults.ProfilesFileName)));
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void An_empty_logs_directory_and_no_packages_directory_still_move()
    {
        var (directory, labId, instanceId, _) = CreateLegacyLab();
        try
        {
            Directory.Delete(Path.Combine(directory, Defaults.PackagesDirectoryName), recursive: true);
            Directory.Delete(Path.Combine(directory, Defaults.LogsDirectoryName), recursive: true);
            Directory.CreateDirectory(Path.Combine(directory, Defaults.LogsDirectoryName));
            var originals = HashLabFiles(directory);
            Assert.Equal(Documents.Order(StringComparer.Ordinal), originals.Keys.Order(StringComparer.Ordinal));

            Assert.Equal(labId, Migration(directory).Run());

            var moved = Path.Combine(directory, Defaults.LabsDirectoryName, labId);
            AssertSameFiles(originals, moved);
            AssertRootIsClean(directory);

            // Opening the lab recreates the directories every writer expects.
            var opened = new ConsoleBootstrap(OptionsFor(directory), TestLogging.Factory).OpenExisting(labId);
            try
            {
                Assert.Equal(instanceId, opened.Instance.InstanceId);
                Assert.True(Directory.Exists(opened.Store.PackagesDirectory));
                Assert.True(Directory.Exists(opened.Store.LogsDirectory));
            }
            finally
            {
                opened.Vault!.Dispose();
                opened.Instance.Dispose();
            }
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task A_fresh_directory_is_left_alone_until_a_lab_is_created()
    {
        var directory = TestConsole.TempDirectory();
        try
        {
            Assert.False(ProfileMigration.IsPending(directory));
            Assert.Null(Migration(directory).Run());
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));

            var bootstrap = new ConsoleBootstrap(OptionsFor(directory), TestLogging.Factory);
            Assert.False(bootstrap.HasLab);
            Assert.Null(bootstrap.DefaultLabId);
            Assert.False(File.Exists(Path.Combine(directory, Defaults.ProfilesFileName)));

            await using var session = bootstrap.CreateLab("Room 214", "Viacheslav", Passphrase, "MacBook", out _);
            Assert.True(File.Exists(Path.Combine(directory, Defaults.ProfilesFileName)));
            var entry = Assert.Single(bootstrap.Profiles.Profiles);
            Assert.Equal((session.LabId, ProfileAccess.Administrator, ProfileAuthorization.Authorized, ProfileSource.Created),
                (entry.LabId, entry.Access, entry.Authorization, entry.Source));
            Assert.Equal(session.LabId, bootstrap.Profiles.LastUsedLabId);
            Assert.Equal(Path.Combine(directory, Defaults.LabsDirectoryName, session.LabId), session.Store.Directory);
            Assert.True(File.Exists(Path.Combine(session.Store.Directory, Defaults.LabKeyFileName)));
            Assert.False(File.Exists(Path.Combine(directory, Defaults.LabKeyFileName)), "nothing is written flat at the root any more");
            Assert.True(bootstrap.HasLab);
            Assert.Equal(session.LabId, bootstrap.DefaultLabId);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Two_data_directories_coexist_without_seeing_each_other()
    {
        var first = TestConsole.TempDirectory();
        var second = TestConsole.TempDirectory();
        try
        {
            var a = new ConsoleBootstrap(OptionsFor(first), TestLogging.Factory);
            var b = new ConsoleBootstrap(OptionsFor(second), TestLogging.Factory);
            string labA, labB;
            await using (var sessionA = a.CreateLab("Room A", "Viacheslav", Passphrase, "MacBook", out _))
            await using (var sessionB = b.CreateLab("Room B", "Viacheslav", Passphrase, "Windows desk PC", out _))
            {
                labA = sessionA.LabId;
                labB = sessionB.LabId;
            }

            Assert.NotEqual(labA, labB);
            Assert.Equal(labA, Assert.Single(new ProfileStore(first).Profiles).LabId);
            Assert.Equal(labB, Assert.Single(new ProfileStore(second).Profiles).LabId);
            Assert.True(Directory.Exists(Path.Combine(first, Defaults.LabsDirectoryName, labA)));
            Assert.False(Directory.Exists(Path.Combine(first, Defaults.LabsDirectoryName, labB)));
            Assert.False(Directory.Exists(Path.Combine(second, Defaults.LabsDirectoryName, labA)));

            var openedA = new ConsoleBootstrap(OptionsFor(first), TestLogging.Factory).OpenExisting();
            var openedB = new ConsoleBootstrap(OptionsFor(second), TestLogging.Factory).OpenExisting();
            try
            {
                Assert.Equal("Room A", openedA.Vault!.LabName);
                Assert.Equal("Room B", openedB.Vault!.LabName);
                Assert.Throws<InvalidDataException>(() => new ConsoleBootstrap(OptionsFor(first), TestLogging.Factory).OpenExisting(labB));
            }
            finally
            {
                openedA.Vault!.Dispose();
                openedA.Instance.Dispose();
                openedB.Vault!.Dispose();
                openedB.Instance.Dispose();
            }
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public async Task A_lab_that_is_already_saved_is_not_overwritten_by_a_backup_import()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var bootstrap = new ConsoleBootstrap(console.Session.Options, TestLogging.Factory);
        var backupPath = Path.Combine(TestConsole.TempDirectory(), "lab.lcbak");
        Assert.True(bootstrap.TryExportBackup(console.Session, backupPath, out var error), error);
        var instanceBefore = File.ReadAllText(console.Session.Store.InstancePath);

        var refused = Assert.Throws<InvalidDataException>(() => bootstrap.ImportBackup(ConsoleBootstrap.ReadBackup(backupPath), TestConsole.Passphrase, null, "Again"));
        Assert.Contains("already saved", refused.Message, StringComparison.Ordinal);
        Assert.Equal(instanceBefore, File.ReadAllText(console.Session.Store.InstancePath));
        Assert.Single(bootstrap.Profiles.Profiles);

        // On another device the same backup imports into its own labs/<lab_id>/ with a backup entry.
        var elsewhere = TestConsole.TempDirectory();
        try
        {
            var other = new ConsoleBootstrap(OptionsFor(elsewhere), TestLogging.Factory);
            await using var imported = other.ImportBackup(ConsoleBootstrap.ReadBackup(backupPath), TestConsole.Passphrase, null, "Windows desk PC");
            Assert.Equal(console.Session.LabId, imported.LabId);
            Assert.Equal(Path.Combine(elsewhere, Defaults.LabsDirectoryName, imported.LabId), imported.Store.Directory);
            var entry = Assert.Single(other.Profiles.Profiles);
            Assert.Equal((ProfileSource.Backup, ProfileAccess.Administrator, ProfileAuthorization.Authorized, imported.Instance.InstanceId),
                (entry.Source, entry.Access, entry.Authorization, entry.InstanceId));
            Assert.Equal(Assert.Single(bootstrap.Profiles.Profiles).AuthorityFingerprint, entry.AuthorityFingerprint);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task An_unlisted_lab_directory_blocks_a_backup_import_unless_it_holds_no_identity()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var bootstrap = new ConsoleBootstrap(console.Session.Options, TestLogging.Factory);
        var backupPath = Path.Combine(TestConsole.TempDirectory(), "lab.lcbak");
        Assert.True(bootstrap.TryExportBackup(console.Session, backupPath, out var error), error);

        var elsewhere = TestConsole.TempDirectory();
        try
        {
            var labDirectory = Path.Combine(elsewhere, Defaults.LabsDirectoryName, console.Session.LabId);
            var other = new ConsoleBootstrap(OptionsFor(elsewhere), TestLogging.Factory);

            // A directory with the lab's key that the index does not list is somebody's data.
            Directory.CreateDirectory(labDirectory);
            File.Copy(console.Session.Store.LabKeyPath, Path.Combine(labDirectory, Defaults.LabKeyFileName));
            var refused = Assert.Throws<InvalidDataException>(() => other.ImportBackup(ConsoleBootstrap.ReadBackup(backupPath), TestConsole.Passphrase, null, "Windows desk PC"));
            Assert.Contains(labDirectory, refused.Message, StringComparison.Ordinal);
            Assert.Contains(Defaults.ProfilesFileName, refused.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(labDirectory, Defaults.LabKeyFileName)));
            Assert.Empty(other.Profiles.Profiles);

            // Without a key or an instance it is a leftover, and the import proceeds over it.
            File.Delete(Path.Combine(labDirectory, Defaults.LabKeyFileName));
            File.WriteAllText(Path.Combine(labDirectory, "stray.txt"), "left behind");
            await using var imported = other.ImportBackup(ConsoleBootstrap.ReadBackup(backupPath), TestConsole.Passphrase, null, "Windows desk PC");
            Assert.Equal(labDirectory, imported.Store.Directory);
            Assert.False(File.Exists(Path.Combine(labDirectory, "stray.txt")));
            Assert.Single(other.Profiles.Profiles);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void Removing_a_lab_with_its_data_forgets_the_directory_and_the_index_entry()
    {
        var (directory, labId, _, _) = CreateLegacyLab();
        try
        {
            Migration(directory).Run();
            var profiles = new ProfileStore(directory);
            var moved = profiles.Directory(labId);

            profiles.Remove(labId, deleteData: true);
            Assert.False(Directory.Exists(moved));
            Assert.Empty(profiles.Profiles);
            Assert.Null(profiles.LastUsedLabId);
            Assert.Empty(new ProfileStore(directory).Profiles);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void A_keystore_that_refuses_to_forget_the_instance_key_does_not_stop_the_removal()
    {
        var (directory, labId, _, _) = CreateLegacyLab();
        try
        {
            Migration(directory).Run();
            var stubborn = new StubbornProtector();
            var profiles = new ProfileStore(directory, TestLogging.Factory.CreateLogger<ProfileStore>(), _ => stubborn);
            var moved = profiles.Directory(labId);

            profiles.Remove(labId, deleteData: true);
            Assert.Equal(1, stubborn.ForgetCalls);
            Assert.False(Directory.Exists(moved));
            Assert.Empty(profiles.Profiles);
            Assert.Empty(new ProfileStore(directory).Profiles);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void The_console_lock_refuses_a_second_opener_until_it_is_released()
    {
        var directory = TestConsole.TempDirectory();
        try
        {
            using (var held = ConsoleLock.TryAcquire(directory, out var error))
            {
                Assert.NotNull(held);
                Assert.Equal(string.Empty, error);
                Assert.True(File.Exists(ConsoleLock.PathFor(directory)));

                Assert.Null(ConsoleLock.TryAcquire(directory, out var refused));
                Assert.NotEqual(string.Empty, refused);
            }

            using var again = ConsoleLock.TryAcquire(directory, out _);
            Assert.NotNull(again);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    /// <summary>Copies the five documents, <c>packages/</c> and <c>logs/</c> of one lab directory into another, as a person would with Finder.</summary>
    private static void CopyRootTo(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var name in Documents)
        {
            var path = Path.Combine(source, name);
            if (File.Exists(path))
            {
                File.Copy(path, Path.Combine(destination, name), overwrite: true);
            }
        }

        foreach (var sub in new[] { Defaults.PackagesDirectoryName, Defaults.LogsDirectoryName })
        {
            var root = Path.Combine(source, sub);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, sub, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }

    private static string Snapshot(string directory) =>
        string.Join("\n", Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path))
            .Order(StringComparer.Ordinal));

    private sealed class SimulatedCrash : Exception;

    /// <summary>The macOS Keychain saying no (<c>SecItemDelete</c> failing): <c>Forget</c> throws.</summary>
    private sealed class StubbornProtector : ISecretProtector
    {
        public int ForgetCalls { get; private set; }

        public string Name => "stubborn";

        public bool IsAvailable => true;

        public ProtectedSecret Protect(string reference, ReadOnlySpan<byte> secret) => throw new NotSupportedException();

        public bool TryUnprotect(ProtectedSecret secret, out byte[] plaintext) => throw new NotSupportedException();

        public void Forget(ProtectedSecret secret)
        {
            ForgetCalls++;
            throw new InvalidOperationException("The macOS Keychain refused to remove the item (OSStatus -25300).");
        }
    }
}
