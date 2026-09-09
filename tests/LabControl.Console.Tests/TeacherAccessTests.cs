using System.Net;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// Teacher access end to end (M5 portion 3, D-56, D-60): a device authorized through a lab
/// file, a request and a grant drives the room without ever holding the key; what it cannot
/// do answers by name; a withdrawal travels to the PCs and is shown as delivered, never
/// assumed; a backup upgrades the profile in place; a mixed batch imports what it can.
/// </summary>
public sealed class TeacherAccessTests
{
    [Fact]
    public async Task A_teacher_console_links_every_pc_and_runs_scripts_but_cannot_enrol_renew_update_revoke_or_export()
    {
        await using var admin = await TestConsole.CreateLabAsync("Admin MacBook");
        await using var teacher = await TestConsole.JoinAsTeacherAsync(admin, "Teacher laptop");

        Assert.False(teacher.Session.IsAdministrator);
        Assert.Null(teacher.Session.Vault);
        Assert.Equal(ProfileAccess.Teacher, teacher.Session.Access);
        Assert.Equal(ConsoleAccess.Teacher, LabName.AccessOf(teacher.Session.Instance.Certificate));
        Assert.Equal(admin.Session.LabId, teacher.Session.LabId);
        Assert.NotEqual(admin.Session.Instance.InstanceId, teacher.Session.Instance.InstanceId);

        // The administrator's device book names the teacher device as authorized.
        var book = admin.Session.Registry.Document.Instances.Single(i => i.InstanceId == teacher.Session.Instance.InstanceId);
        Assert.Equal(ProfileAccess.Teacher, book.Access);
        Assert.True(book.AuthorizedAtUnix > 0);
        Assert.Contains(admin.Session.Events.Recent, e => e.Code == "device.authorized");

        // The teacher's profile is authorized until the leaf runs out; the pending key is gone.
        var profile = teacher.Bootstrap.Profiles.Find(teacher.Session.LabId)!;
        Assert.Equal(ProfileAccess.Teacher, profile.Access);
        Assert.Equal(ProfileAuthorization.Authorized, profile.Authorization);
        Assert.Equal(teacher.Session.Instance.ExpiresAt.ToUnixTimeSeconds(), profile.AccessExpiresUnix);
        var access = teacher.Session.Store.LoadAccess()!;
        Assert.Equal(AccessState.Authorized, access.State);
        Assert.Null(access.PendingKey);

        var lab = admin.Session.Vault!.Peek()!;
        var agents = new List<TestAgent>();
        try
        {
            for (var n = 1; n <= Defaults.MaxStudentPcs; n++)
            {
                agents.Add(TestAgent.InstallEnrolled(lab, n, teacher.Port, pinHost: true).Start());
            }

            Assert.True(await Wait.UntilAsync(() => teacher.Session.Linked.Count == Defaults.MaxStudentPcs, TimeSpan.FromSeconds(20)),
                $"only {teacher.Session.Linked.Count} of {Defaults.MaxStudentPcs} linked");
            Assert.All(agents, a => Assert.Equal(ConsoleAccess.Teacher, a.Link.LinkedConsoleAccess));
            Assert.All(agents, a => Assert.Equal(teacher.Session.Instance.InstanceId, a.Link.LinkedInstanceId));

            // A script runs on every PC like on an administrator console.
            var script = new ScriptRecord { Id = "hello", Name = "Hello", Text = "Write-Host hello", Shell = Shared.Jobs.RunScriptRequest.PowerShellValue, TimeoutSeconds = 20 };
            var jobs = teacher.Session.RunScript(agents.Select(a => a.AgentId), script);
            Assert.Equal(Defaults.MaxStudentPcs, jobs.Count);
            Assert.True(await Wait.UntilAsync(() => jobs.All(j => j.State == JobState.Succeeded), TimeSpan.FromSeconds(20)),
                string.Join(", ", jobs.Select(j => j.State)));

            // self_update and rekey are refused by role before anything is pulled (D-56 item 5).
            var first = agents[0];
            var update = teacher.Session.CreateJobs([first.AgentId], Job.Types.Kind.SelfUpdate, new Dictionary<string, string> { ["version"] = "9.9.9" }).Single();
            var rekey = teacher.Session.CreateJobs([first.AgentId], Job.Types.Kind.Rekey).Single();
            Assert.True(await Wait.UntilAsync(() => update.State == JobState.Failed && rekey.State == JobState.Failed));
            Assert.Contains("teacher access", update.Message, StringComparison.Ordinal);
            Assert.Contains("teacher access", rekey.Message, StringComparison.Ordinal);
            Assert.True(await Wait.UntilAsync(() => teacher.Session.Events.Recent.Count(e => e.Code == "job.refused_by_role") >= 2));
            lock (first.Behaviour.JobsRun)
            {
                Assert.DoesNotContain(first.Behaviour.JobsRun, j => j.Kind is Job.Types.Kind.SelfUpdate or Job.Types.Kind.Rekey);
            }

            // Everything that needs the key answers "administrator" by name.
            Assert.False(teacher.Session.TryRevoke(agents[1].Store.Certificate!.SerialNumber, "test", out var revokeMessage));
            Assert.Contains("Administrator access needed", revokeMessage, StringComparison.Ordinal);
            Assert.False(teacher.Session.TryWithdrawDevice(admin.Session.Instance.InstanceId, "test", out _));
            Assert.Throws<InvalidOperationException>(() => teacher.Session.WritePayload(TestConsole.TempDirectory(), 2));
            Assert.Throws<InvalidOperationException>(() => teacher.Session.PushAgentBuild([first.AgentId], new AgentBuildStub().Build));
            var bootstrap = teacher.Bootstrap;
            Assert.False(bootstrap.TryExportBackup(teacher.Session, Path.Combine(TestConsole.TempDirectory(), "x.lcbak"), out var backupError));
            Assert.Contains("Administrator", backupError, StringComparison.Ordinal);
            Assert.False(bootstrap.TryExportLabFile(teacher.Session, Path.Combine(TestConsole.TempDirectory(), "x.lclab"), out var fileError));
            Assert.Contains("Administrator", fileError, StringComparison.Ordinal);
            Assert.Equal(BackupStatus.NotApplicable, bootstrap.CheckBackup(teacher.Session.InstanceDocument));
            Assert.False(bootstrap.SetupIsUnfinished(teacher.Session.InstanceDocument));

            // Renew: Closed, naming the administrator; the certificate stays.
            using var key = LabCertificates.CreateKey();
            var response = teacher.Session.Renew(first.Store.Certificate, new RenewRequest { Csr = ByteString.CopyFrom(LabCertificates.CreateSigningRequest(key, "PC-01")) });
            Assert.Empty(response.AgentCertificate);
            Assert.Contains("administrator", response.Refusal, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var agent in agents)
            {
                await agent.DisposeAsync();
            }
        }

        // Enrol: Closed, naming the administrator; the PC keeps waiting.
        await using var newcomer = TestAgent.Install(teacher, 7, "ABCDEFGHJKMNPQRSTVWX").Start();
        Assert.True(await Wait.UntilAsync(() => newcomer.Refusals.Count > 0));
        Assert.Contains("administrator", newcomer.Refusals[0], StringComparison.OrdinalIgnoreCase);
        Assert.False(newcomer.Link.IsEnrolled);

        // A PC due for renewal does not ask a teacher console at all (D-56 item 5).
        var due = TestAgent.InstallEnrolled(admin, 8, TimeSpan.FromDays(10));
        due.Store.Config.ConsolePort = teacher.Port;
        due.Store.SaveConfig();
        var serialBefore = LabCertificates.SerialOf(due.Store.Certificate!);
        await using (due.Start())
        {
            Assert.True(await Wait.UntilAsync(() => due.Link.State == LinkState.Linked));
            Assert.Equal(ConsoleAccess.Teacher, due.Link.LinkedConsoleAccess);
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            Assert.Equal(serialBefore, LabCertificates.SerialOf(due.Store.Certificate!));
            Assert.DoesNotContain(teacher.Session.Events.Recent, e => e.Code == "renew.refused");
        }
    }

    [Fact]
    public async Task Withdrawing_a_device_revokes_it_by_instance_the_pending_list_shrinks_as_pcs_confirm_and_its_link_is_refused()
    {
        await using var admin = await TestConsole.CreateLabAsync("Admin MacBook");
        await using var teacher = await TestConsole.JoinAsTeacherAsync(admin, "Teacher laptop");
        var lab = admin.Session.Vault!.Peek()!;
        var teacherId = teacher.Session.Instance.InstanceId;
        var instanceSerial = LabCertificates.InstanceSerial(teacherId);

        // PC-01 linked to the administrator; PC-02 linked once and now offline; PC-03 linked to the teacher device itself.
        await using var online = TestAgent.InstallEnrolled(lab, 1, admin.Port, pinHost: true).Start();
        var offline = TestAgent.InstallEnrolled(lab, 2, admin.Port, pinHost: true).Start();
        await using var withTeacher = TestAgent.InstallEnrolled(lab, 3, teacher.Port, pinHost: true).Start();
        Assert.True(await Wait.UntilAsync(() => admin.Session.Linked.Count == 2, TimeSpan.FromSeconds(15)));
        Assert.True(await Wait.UntilAsync(() => teacher.Session.Linked.Count == 1, TimeSpan.FromSeconds(15)));
        await offline.Link.DisposeAsync();
        Assert.True(await Wait.UntilAsync(() => admin.Session.Linked.Count == 1));

        // The device was once issued another leaf (an earlier request): its history must be revoked too.
        const string earlierSerial = "ABCDEF0123";
        admin.Session.Registry.Persist(document => document.Instances.Single(i => i.InstanceId == teacherId).CertificateSerials.Add(earlierSerial));

        Assert.True(admin.Session.TryWithdrawDevice(teacherId, "left the school", out var message), message);
        Assert.Contains("withdrawn", message, StringComparison.Ordinal);
        Assert.True(admin.Session.Registry.Revocations.IsRevoked(instanceSerial));
        Assert.True(admin.Session.Registry.Revocations.IsRevoked(teacher.Session.Instance.CertificateSerial));
        Assert.True(admin.Session.Registry.Revocations.IsRevoked(earlierSerial));
        Assert.True(admin.Session.Registry.Document.Instances.Single(i => i.InstanceId == teacherId).RevokedAtUnix > 0);
        Assert.Contains(admin.Session.Events.Recent, e => e.Code == "device.withdrawn");
        Assert.False(admin.Session.TryWithdrawDevice(admin.Session.Instance.InstanceId, "self", out _));

        // The linked PC confirms; the offline one is pending, with when it was last seen.
        Assert.True(await Wait.UntilAsync(() => admin.Session.DeliveryOf(instanceSerial).Delivered == 1, TimeSpan.FromSeconds(10)));
        var delivery = admin.Session.DeliveryOf(instanceSerial);
        Assert.Equal(2, delivery.Total);
        Assert.False(delivery.IsComplete);
        var pending = Assert.Single(delivery.Pending);
        Assert.Equal(2, pending.Number);
        Assert.True(pending.LastSeenUnix > 0);
        Assert.Contains(instanceSerial, online.Store.Config.Revocations.Select(r => r.Serial));
        Assert.Contains(instanceSerial, admin.Session.Registry.Document.Machines.Single(m => m.Number == 1).RevocationSerialsSeen);

        // The offline PC comes back, answers the Revocation it is handed with what it holds, and the list is complete.
        var returned = TestAgent.Open(offline.Store).Start();
        try
        {
            Assert.True(await Wait.UntilAsync(() => admin.Session.DeliveryOf(instanceSerial).IsComplete, TimeSpan.FromSeconds(15)));
            Assert.Contains(instanceSerial, returned.Store.Config.Revocations.Select(r => r.Serial));
        }
        finally
        {
            await returned.DisposeAsync();
        }

        // A PC running an agent older than M5 confirms the leaf serial of the same withdrawal but can
        // never hold the instance: entry — shown as "cannot hold", never as pending or complete.
        var leafSerial = LabCertificates.NormalizeSerial(teacher.Session.Instance.CertificateSerial);
        admin.Session.Registry.Persist(document => document.Machines.Add(new MachineRecord { AgentId = "pre-m5", Number = 9, LastSeenUnix = 1, RevocationSerialsSeen = [leafSerial] }));
        var stale = admin.Session.DeliveryOf(instanceSerial);
        Assert.False(stale.IsComplete);
        Assert.Empty(stale.Pending);
        Assert.Equal(9, Assert.Single(stale.CannotHold).Number);
        Assert.Equal(2, stale.Delivered);
        Assert.Equal(3, stale.Total);
        Assert.True(admin.Session.DeliveryOf(leafSerial).IsComplete);
        admin.Session.Registry.Persist(document => document.Machines.RemoveAll(m => m.AgentId == "pre-m5"));

        // A PC that holds the entry refuses the withdrawn console, and would refuse a renewed leaf too.
        var store = online.Store;
        await online.Link.DisposeAsync();
        store.Config.ConsolePort = teacher.Port;
        store.SaveConfig();
        var refusing = TestAgent.Open(store).Start();
        try
        {
            Assert.True(await Wait.UntilAsync(() => refusing.Refusals.Count > 0, TimeSpan.FromSeconds(15)));
            Assert.Contains("revoked", refusing.Refusals[0], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(teacher.Session.Linked, c => c.Number == 1);
        }
        finally
        {
            // The store is disposed with `online`; only the second link is ours.
            await refusing.Link.DisposeAsync();
        }

        // A later lab file carries the entry; the teacher console learns of its own withdrawal and cannot reopen the lab.
        var labFile = Path.Combine(TestConsole.TempDirectory(), "after.lclab");
        Assert.True(admin.Bootstrap.TryExportLabFile(admin.Session, labFile, out var error), error);
        var teacherBootstrap = teacher.Bootstrap;
        var imports = new LabImports(teacherBootstrap, _ => Task.FromResult<BackupSecret?>(null), "Teacher laptop", null,
            id => string.Equals(id, teacher.Session.LabId, StringComparison.OrdinalIgnoreCase) ? teacher.Session : null);
        var result = Assert.Single(await imports.ImportAsync([labFile]));
        Assert.True(result.Ok, result.Message);
        Assert.True(teacher.Session.Registry.Revocations.IsRevoked(instanceSerial));
        Assert.Contains(teacher.Session.Events.Recent, e => e.Code == "access.withdrawn");

        // The withdrawn console pushed the entry to its own PC, which now knows its console is
        // withdrawn and leaves at once (D-56 item 6): a withdrawn console keeps no live link.
        Assert.True(await Wait.UntilAsync(() => teacher.Session.Linked.Count == 0 && withTeacher.Link.State != LinkState.Linked, TimeSpan.FromSeconds(10)),
            $"teacher still holds {teacher.Session.Linked.Count} PC(s); PC-03 is {withTeacher.Link.State}");
        Assert.Contains(instanceSerial, withTeacher.Store.Config.Revocations.Select(r => r.Serial));
        Assert.Null(withTeacher.Link.LinkedInstanceId);

        await teacher.Session.CloseAsync("test");
        var refused = Assert.Throws<InvalidDataException>(() => teacherBootstrap.OpenExisting(teacher.Session.LabId));
        Assert.Contains("withdrew", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ProfileAuthorization.Revoked, teacherBootstrap.Profiles.Find(teacher.Session.LabId)!.Authorization);
        Assert.Equal(AccessState.Revoked, teacher.Session.Store.LoadAccess()!.State);

        // A new request from the withdrawn device carries a fresh identity, so the old entry cannot be replayed against it.
        var requestPath = Path.Combine(TestConsole.TempDirectory(), "again.lcreq");
        imports.Devices.WriteRequest(teacher.Session.LabId, requestPath);
        var reread = teacher.Session.Store.LoadAccess()!;
        Assert.NotEqual(teacherId, reread.InstanceId);
        Assert.Equal(AccessState.RequestPending, reread.State);
        Assert.Equal(ProfileAuthorization.RequestPending, teacherBootstrap.Profiles.Find(teacher.Session.LabId)!.Authorization);
    }

    [Fact]
    public async Task A_backup_upgrades_a_teacher_profile_to_administrator_keeping_its_instance_and_logs_with_dormant_codes()
    {
        await using var admin = await TestConsole.CreateLabAsync("Admin MacBook");
        var teacher = await TestConsole.JoinAsTeacherAsync(admin, "Teacher laptop");
        var labId = admin.Session.LabId;
        var instanceId = teacher.Session.Instance.InstanceId;
        teacher.Session.Events.Info("test.marker", "written before the upgrade");
        var logFiles = Directory.GetFiles(teacher.Session.Store.LogsDirectory);
        Assert.NotEmpty(logFiles);
        await teacher.Session.CloseAsync("test");

        admin.IssueCodes(3);
        var backupPath = Path.Combine(TestConsole.TempDirectory(), "room.lcbak");
        Assert.True(admin.Bootstrap.TryExportBackup(admin.Session, backupPath, out var error), error);

        var bootstrap = teacher.Bootstrap;
        var imports = new LabImports(bootstrap, _ => Task.FromResult<BackupSecret?>(new BackupSecret(TestConsole.Passphrase, null)), "Teacher laptop");
        var result = Assert.Single(await imports.ImportAsync([backupPath]));
        Assert.True(result.Ok, result.Message);
        Assert.Contains("administrator", result.Message, StringComparison.OrdinalIgnoreCase);

        var profile = bootstrap.Profiles.Find(labId)!;
        Assert.Equal(ProfileAccess.Administrator, profile.Access);
        Assert.Equal(ProfileAuthorization.Authorized, profile.Authorization);
        Assert.Equal(instanceId, profile.InstanceId);
        Assert.Equal(0, profile.AccessExpiresUnix);

        var store = bootstrap.StoreFor(labId);
        Assert.True(store.HasLabKey);
        Assert.Equal(instanceId, store.LoadInstance()!.InstanceId);
        Assert.All(logFiles, f => Assert.True(File.Exists(f)));
        Assert.Contains("written before the upgrade", File.ReadAllText(logFiles[0]), StringComparison.Ordinal);

        // The imported codes sleep (D-60); the enrollment document names the stick and its issuer.
        var enrollment = new EnrollmentAuthority(store.LoadEnrollment(labId));
        Assert.Equal(3, enrollment.DormantCodeCount);
        Assert.Equal(0, enrollment.UnusedCodeCount);

        // The lab opens as an administrator, still serving with the teacher leaf until it is re-minted.
        var opened = bootstrap.OpenExisting(labId);
        Assert.NotNull(opened.Vault);
        Assert.Equal(instanceId, opened.Instance.InstanceId);
        Assert.Equal(ConsoleAccess.Teacher, LabName.AccessOf(opened.Instance.Certificate));
        Assert.True(opened.Vault!.TryUnlock(TestConsole.Passphrase));
        var reminted = bootstrap.Remint(opened.Vault, opened.Document);
        Assert.Equal(instanceId, reminted.InstanceId);
        Assert.Equal(ConsoleAccess.Administrator, LabName.AccessOf(reminted.Certificate));
        opened.Instance.Dispose();

        await using var session = bootstrap.Build(opened, reminted);
        await session.StartAsync();
        Assert.True(session.IsAdministrator);
        Assert.Equal(3, session.Enrollment.DormantCodeCount);
        Assert.Equal(3, session.Enrollment.ActivateDormant());
        Assert.Equal(3, session.Enrollment.UnusedCodeCount);

        // A second backup is refused: the profile is an administrator one now.
        var again = Assert.Single(await imports.ImportAsync([backupPath]));
        Assert.False(again.Ok);
        await teacher.DisposeAsync();
    }

    [Fact]
    public async Task A_mixed_batch_imports_the_good_files_and_reports_each_one()
    {
        await using var labA = await TestConsole.CreateLabAsync("Console A");
        await using var labB = await TestConsole.CreateLabAsync("Console B");
        var files = TestConsole.TempDirectory();

        var fileA = Path.Combine(files, "a.lclab");
        Assert.True(labA.Bootstrap.TryExportLabFile(labA.Session, fileA, out var error), error);
        var backupB = Path.Combine(files, "b.lcbak");
        Assert.True(labB.Bootstrap.TryExportBackup(labB.Session, backupB, out error), error);
        var corrupt = Path.Combine(files, "c.lclab");
        File.WriteAllText(corrupt, File.ReadAllText(fileA).Replace("\"signature\": \"", "\"signature\": \"AAAA", StringComparison.Ordinal));
        var notes = Path.Combine(files, "notes.txt");
        File.WriteAllText(notes, "not a lab");

        // A request for lab A, made on a device that only holds the lab file.
        var device = new ConsoleBootstrap(new ConsoleOptions { DataDirectory = TestConsole.TempDirectory(), Port = 0, BindAddress = IPAddress.Loopback, BeaconPort = TestConsole.BeaconPort },
            TestLogging.Factory, () => new FileSecretProtector());
        var imports = new LabImports(device, _ => Task.FromResult<BackupSecret?>(new BackupSecret(TestConsole.Passphrase, null)), "Device");

        var results = await imports.ImportAsync([fileA, backupB, corrupt, notes]);
        Assert.Equal(4, results.Count);
        Assert.True(results[0].Ok, results[0].Message);
        Assert.True(results[1].Ok, results[1].Message);
        Assert.False(results[2].Ok);
        Assert.Contains("not signed", results[2].Message, StringComparison.Ordinal);
        Assert.False(results[3].Ok);

        Assert.Equal(ProfileAccess.Teacher, device.Profiles.Find(labA.Session.LabId)!.Access);
        Assert.Equal(ProfileAuthorization.NeedsAuthorization, device.Profiles.Find(labA.Session.LabId)!.Authorization);
        Assert.Equal(ProfileAccess.Administrator, device.Profiles.Find(labB.Session.LabId)!.Access);
        Assert.Throws<InvalidDataException>(() => device.OpenExisting(labA.Session.LabId));

        // A grant cannot land before a request exists; a request cannot be approved where the key is not.
        var requestPath = Path.Combine(files, "device.lcreq");
        imports.Devices.WriteRequest(labA.Session.LabId, requestPath);
        Assert.Equal(ProfileAuthorization.RequestPending, device.Profiles.Find(labA.Session.LabId)!.Authorization);
        var elsewhere = Assert.Single(await imports.ImportAsync([requestPath]));
        Assert.False(elsewhere.Ok);
        Assert.Contains("holds that lab's key", elsewhere.Message, StringComparison.Ordinal);

        // Lab A's console approves it through the same batch importer (its session is active, key unlocked).
        var adminImports = new LabImports(labA.Bootstrap, _ => Task.FromResult<BackupSecret?>(null), "Console A", null,
            id => string.Equals(id, labA.Session.LabId, StringComparison.OrdinalIgnoreCase) ? labA.Session : null,
            _ => Task.FromResult<BackupSecret?>(new BackupSecret(TestConsole.Passphrase, null)));
        var approved = Assert.Single(await adminImports.ImportAsync([requestPath]));
        Assert.True(approved.Ok, approved.Message);
        var grantPath = Path.ChangeExtension(requestPath, Defaults.DeviceGrantFileExtension);
        Assert.True(File.Exists(grantPath));

        // The same lab file again refreshes rather than duplicates; the grant authorizes; a wrong-lab grant is refused.
        var second = await imports.ImportAsync([fileA, grantPath, fileA]);
        Assert.True(second[0].Ok, second[0].Message);
        Assert.True(second[1].Ok, second[1].Message);
        Assert.True(second[2].Ok, second[2].Message);
        Assert.Equal(2, device.Profiles.Profiles.Count);
        Assert.Equal(ProfileAuthorization.Authorized, device.Profiles.Find(labA.Session.LabId)!.Authorization);

        // Same lab id, different key: a forged authority for lab A's id, signed by its own key — refused, nothing changed.
        using var forgedKey = LabCertificates.CreateKey();
        using var forgedCa = LabCertificates.CreateAuthority(labA.Session.LabId, "A?", forgedKey, DateTimeOffset.UtcNow);
        var forgedPayload = new LabFilePayload
        {
            LabId = labA.Session.LabId,
            LabName = "A?",
            Authority = forgedCa.Export(X509ContentType.Cert),
            AuthorityFingerprint = ProfileRecord.AuthorityFingerprintOf(forgedCa.RawData),
            SnapshotVersion = long.MaxValue,
        };
        var forgedBytes = SignedEnvelope.PayloadBytes(forgedPayload);
        var foreign = new LabFileDocument
        {
            LabId = labA.Session.LabId,
            LabName = "A?",
            Payload = Convert.ToBase64String(forgedBytes),
            Signature = SignedEnvelope.Sign(SignedEnvelope.LabFileDomain, forgedKey, forgedBytes),
        };
        var foreignPath = Path.Combine(files, "foreign.lclab");
        File.WriteAllText(foreignPath, LabFile.Serialize(foreign));
        var refused = Assert.Single(await imports.ImportAsync([foreignPath]));
        Assert.False(refused.Ok);
        Assert.Contains("different key", refused.Message, StringComparison.Ordinal);
        Assert.Equal("Test lab", device.Profiles.Find(labA.Session.LabId)!.LabName);

        var opened = device.OpenExisting(labA.Session.LabId);
        Assert.Null(opened.Vault);
        opened.Instance.Dispose();
    }

    [Fact]
    public async Task Welcome_announces_the_access_the_console_leaf_carries_and_the_certificate_still_decides()
    {
        // Welcome.console_access (M5, D-58, PROTOCOL "M5 additions" item 1) is additive and
        // informational: it says what the serving console's own leaf says, so an agent can
        // name the access level without re-parsing the certificate. What a job is refused on
        // stays the validated leaf, which is why both are asserted separately here.
        await using var admin = await TestConsole.CreateLabAsync("Admin MacBook");
        await using var teacher = await TestConsole.JoinAsTeacherAsync(admin, "Teacher laptop");
        var lab = admin.Session.Vault!.Peek()!;

        await using var onAdmin = TestAgent.InstallEnrolled(lab, 1, admin.Port, pinHost: true).Start();
        await using var onTeacher = TestAgent.InstallEnrolled(lab, 2, teacher.Port, pinHost: true).Start();

        Assert.True(await Wait.UntilAsync(() => admin.Session.Linked.Count == 1 && teacher.Session.Linked.Count == 1, TimeSpan.FromSeconds(20)));

        Assert.Equal(ConsoleAccess.Administrator, onAdmin.Link.AnnouncedConsoleAccess);
        Assert.Equal(ConsoleAccess.Administrator, onAdmin.Link.LinkedConsoleAccess);
        Assert.Equal(ConsoleAccess.Teacher, onTeacher.Link.AnnouncedConsoleAccess);
        Assert.Equal(ConsoleAccess.Teacher, onTeacher.Link.LinkedConsoleAccess);

        // The announcement comes off the console's own leaf, so it can never claim more than
        // the OU an agent reads out of the same certificate.
        Assert.Equal(LabName.AccessOf(admin.Session.Instance.Certificate), onAdmin.Link.AnnouncedConsoleAccess);
        Assert.Equal(LabName.AccessOf(teacher.Session.Instance.Certificate), onTeacher.Link.AnnouncedConsoleAccess);

        // A console that lies in Welcome changes nothing: the refusal is decided by the leaf.
        teacher.Session.BeforeWelcome = welcome => welcome.ConsoleAccess = Welcome.Types.ConsoleAccess.Administrator;
        await using var lied = TestAgent.InstallEnrolled(lab, 3, teacher.Port, pinHost: true).Start();
        Assert.True(await Wait.UntilAsync(() => teacher.Session.Linked.Count == 2, TimeSpan.FromSeconds(20)));

        Assert.Equal(ConsoleAccess.Administrator, lied.Link.AnnouncedConsoleAccess);
        Assert.Equal(ConsoleAccess.Teacher, lied.Link.LinkedConsoleAccess);

        var rekey = teacher.Session.CreateJobs([lied.AgentId], Job.Types.Kind.Rekey).Single();
        Assert.True(await Wait.UntilAsync(() => rekey.State == JobState.Failed, TimeSpan.FromSeconds(20)), rekey.State.ToString());
        Assert.Contains("teacher access", rekey.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lab_file_is_refused_over_an_occupied_directory_and_removing_a_teacher_profile_forgets_its_pending_key()
    {
        await using var admin = await TestConsole.CreateLabAsync("Admin MacBook");
        var labId = admin.Session.LabId;
        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        Assert.True(admin.Bootstrap.TryExportLabFile(admin.Session, labFile, out var error), error);

        var directory = TestConsole.TempDirectory();
        var device = new ConsoleBootstrap(new ConsoleOptions { DataDirectory = directory, Port = 0, BindAddress = IPAddress.Loopback, BeaconPort = TestConsole.BeaconPort },
            TestLogging.Factory, () => new FileSecretProtector());
        var imports = new LabImports(device, _ => Task.FromResult<BackupSecret?>(null), "Device");

        // labs/<lab_id>/ exists with somebody's instance.json but no index entry: refused, nothing touched.
        var labDirectory = Path.Combine(directory, Defaults.LabsDirectoryName, labId);
        Directory.CreateDirectory(labDirectory);
        File.Copy(admin.Session.Store.InstancePath, Path.Combine(labDirectory, Defaults.InstanceFileName));
        var refused = Assert.Single(await imports.ImportAsync([labFile]));
        Assert.False(refused.Ok, refused.Message);
        Assert.Contains(labDirectory, refused.Message, StringComparison.Ordinal);
        Assert.Empty(device.Profiles.Profiles);
        Assert.True(File.Exists(Path.Combine(labDirectory, Defaults.InstanceFileName)));
        Assert.False(File.Exists(Path.Combine(labDirectory, Defaults.AccessFileName)));

        // A leftover directory without a key, an instance or an access document is just a directory.
        File.Delete(Path.Combine(labDirectory, Defaults.InstanceFileName));
        File.WriteAllText(Path.Combine(labDirectory, "stray.txt"), "left behind");
        var added = Assert.Single(await imports.ImportAsync([labFile]));
        Assert.True(added.Ok, added.Message);
        Assert.Equal(ProfileAccess.Teacher, device.Profiles.Find(labId)!.Access);
        Assert.Equal(labDirectory, device.StoreFor(labId).Directory);

        // A request pending: the pending key lives in the keystore, referenced only by access.json.
        imports.Devices.WriteRequest(labId, Path.Combine(files, "device.lcreq"));
        var pending = device.StoreFor(labId).LoadAccess()!.PendingKey;
        Assert.NotNull(pending);
        Assert.EndsWith("-pending", pending!.Reference, StringComparison.Ordinal);

        // Removing the profile with its data forgets that key too — there is no instance key yet to forget.
        var forgetting = new CountingProtector();
        var profiles = new ProfileStore(directory, TestLogging.Factory.CreateLogger("profiles"), _ => forgetting);
        profiles.Remove(labId, deleteData: true);
        Assert.Equal([pending.Reference], forgetting.Forgotten);
        Assert.False(Directory.Exists(labDirectory));
        Assert.Empty(profiles.Profiles);
        Assert.Empty(new ProfileStore(directory).Profiles);
    }

    private sealed class CountingProtector : ISecretProtector
    {
        public List<string> Forgotten { get; } = [];

        public string Name => "counting";

        public bool IsAvailable => true;

        public ProtectedSecret Protect(string reference, ReadOnlySpan<byte> secret) => throw new NotSupportedException();

        public bool TryUnprotect(ProtectedSecret secret, out byte[] plaintext) => throw new NotSupportedException();

        public void Forget(ProtectedSecret secret) => Forgotten.Add(secret.Reference);
    }

    /// <summary>A minimal build folder for the refusal check: the files are never read on a teacher console.</summary>
    private sealed class AgentBuildStub
    {
        public AgentBuildStub()
        {
            var folder = TestConsole.TempDirectory();
            File.WriteAllBytes(Path.Combine(folder, Defaults.AgentExecutableName), new byte[16]);
            File.WriteAllText(Path.Combine(folder, Defaults.SessionExecutableName), "helper");
            Assert.True(Shared.Setup.AgentBuild.TryLoad(folder, "0.1.0", out var build, out var error), error);
            Build = build;
        }

        public Shared.Setup.AgentBuild Build { get; }
    }
}
