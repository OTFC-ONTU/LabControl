using Xunit;

using Google.Protobuf;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Tests;

/// <summary>
/// Enrolment (D-14), the self-healing machine list (§3.7) and jobs (PROTOCOL, <c>Job</c>) —
/// the state the console keeps about the lab, independently of how it is transported.
/// </summary>
public sealed class LabStateTests
{
    // ------------------------------------------------------------------ enrolment

    [Fact]
    public void A_code_enrolls_one_pc_and_is_then_burned()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var authority = new EnrollmentAuthority(new EnrollmentDocument { LabId = lab.LabId });

        var codes = authority.Generate(3, "stick-1", now);
        Assert.Equal(3, authority.UnusedCodeCount);

        var request = Request(lab.LabId, number: 7, codes[0]);
        var first = authority.Redeem(lab, request, now);

        Assert.True(first.Ok);
        Assert.NotNull(first.Certificate);
        Assert.Equal(2, authority.UnusedCodeCount);

        using (first.Certificate)
        {
            var trust = LabTrust.FromAuthority(LabTrustTests.PublicOnly(lab.Authority));
            Assert.True(trust.TryValidate(first.Certificate, LabRole.Agent, null, out var name, out _));
            Assert.Equal(7, name.Number);
        }

        // A second PC presenting the same code is refused, and the refusal names the PC
        // that already used it so the teacher can find the duplicated stick.
        var second = authority.Redeem(lab, Request(lab.LabId, number: 8, codes[0]), now);

        Assert.Equal(EnrollmentOutcome.BurnedCode, second.Outcome);
        Assert.Contains("PC-07", second.Message, StringComparison.Ordinal);
        Assert.Null(second.Certificate);
    }

    [Fact]
    public void A_code_this_lab_never_issued_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var authority = new EnrollmentAuthority(new EnrollmentDocument { LabId = lab.LabId });
        authority.Generate(1, "stick-1", now);

        Assert.Equal(EnrollmentOutcome.UnknownCode,
            authority.Redeem(lab, Request(lab.LabId, 7, EnrollmentCode.Generate()), now).Outcome);
        Assert.Equal(EnrollmentOutcome.UnknownCode,
            authority.Redeem(lab, Request(lab.LabId, 7, "hello"), now).Outcome);
    }

    [Fact]
    public void A_pc_claiming_another_lab_or_an_impossible_number_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var authority = new EnrollmentAuthority(new EnrollmentDocument { LabId = lab.LabId });
        var codes = authority.Generate(4, "stick-1", now);

        Assert.Equal(EnrollmentOutcome.WrongLab,
            authority.Redeem(lab, Request(Guid.NewGuid().ToString("d"), 7, codes[0]), now).Outcome);
        Assert.Equal(EnrollmentOutcome.BadNumber,
            authority.Redeem(lab, Request(lab.LabId, 0, codes[1]), now).Outcome);

        var tooMany = authority.Redeem(lab, Request(lab.LabId, Defaults.MaxStudentPcs + 1, codes[2]), now);
        Assert.Equal(EnrollmentOutcome.BadNumber, tooMany.Outcome);
        Assert.Contains("D-17", tooMany.Message, StringComparison.Ordinal);

        // None of those spent a code.
        Assert.Equal(4, authority.UnusedCodeCount);
    }

    [Fact]
    public void An_unreadable_signing_request_gives_the_code_back()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var authority = new EnrollmentAuthority(new EnrollmentDocument { LabId = lab.LabId });
        var code = authority.Generate(1, "stick-1", now)[0];

        var request = Request(lab.LabId, 7, code);
        request.Csr = ByteString.CopyFrom([1, 2, 3]);

        Assert.Equal(EnrollmentOutcome.BadRequest, authority.Redeem(lab, request, now).Outcome);
        Assert.Equal(1, authority.UnusedCodeCount);
    }

    [Fact]
    public void A_pc_without_a_proper_agent_id_is_refused_before_a_code_is_spent()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var authority = new EnrollmentAuthority(new EnrollmentDocument { LabId = lab.LabId });
        var code = authority.Generate(1, "stick-1", now)[0];

        var request = Request(lab.LabId, 7, code);
        request.AgentId = "";

        Assert.Equal(EnrollmentOutcome.BadRequest, authority.Redeem(lab, request, now).Outcome);
        Assert.Equal(1, authority.UnusedCodeCount);
    }

    [Fact]
    public void A_certificate_is_renewed_over_the_link_for_the_identity_the_peer_proved()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var trust = LabTrust.FromAuthority(LabTrustTests.PublicOnly(lab.Authority));
        var agentId = Guid.NewGuid().ToString("d");

        using var current = LabTrustTests.IssueAgent(lab, agentId, number: 7);
        Assert.True(trust.TryValidate(current, LabRole.Agent, null, out var peer, out _));

        // Not yet due: the lead time is what gives the teacher time to unlock the lab key.
        Assert.False(LabCertificates.NeedsRenewal(current, now));
        Assert.True(LabCertificates.NeedsRenewal(current, now + Defaults.AgentCertificateLifetime - TimeSpan.FromDays(1)));

        using var freshKey = LabCertificates.CreateKey();
        var csr = LabCertificates.CreateSigningRequest(freshKey, "ignored");

        // Lab key locked: refused politely, the current certificate stays valid.
        Assert.Equal(RenewalOutcome.Closed, CertificateRenewal.Renew(null, peer, csr, now).Outcome);

        var renewed = CertificateRenewal.Renew(lab, peer, csr, now);
        Assert.True(renewed.Ok);
        using (renewed.Certificate)
        {
            Assert.True(trust.TryValidate(renewed.Certificate, LabRole.Agent, null, out var name, out _));
            Assert.Equal(agentId, name.Id);
            Assert.Equal(7, name.Number);
            Assert.NotEqual(LabCertificates.SerialOf(current), LabCertificates.SerialOf(renewed.Certificate!));
        }

        // A console instance is never renewed this way (it is re-minted from the lab key).
        using var console = ConsoleInstance.Mint(lab, "MacBook-2026", new LabControl.Shared.Protection.FileSecretProtector());
        Assert.True(trust.TryValidate(console.Certificate, LabRole.Console, null, out var consoleName, out _));
        Assert.Equal(RenewalOutcome.NotAnAgent, CertificateRenewal.Renew(lab, consoleName, csr, now).Outcome);
    }

    [Fact]
    public void A_console_with_the_lab_key_locked_enrolls_nobody()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var authority = new EnrollmentAuthority(new EnrollmentDocument { LabId = lab.LabId });
        var code = authority.Generate(1, "stick-1", now)[0];

        var result = authority.Redeem(null, Request(lab.LabId, 7, code), now);

        Assert.Equal(EnrollmentOutcome.Closed, result.Outcome);
        Assert.Equal(1, authority.UnusedCodeCount);
    }

    // ------------------------------------------------------------------ machine list

    [Fact]
    public void A_pc_this_console_has_never_seen_is_added_from_its_hello()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var registry = new LabRegistry(new LabDocument { LabId = lab.LabId }, LabTrustTests.PublicOnly(lab.Authority));

        var hello = new Hello
        {
            AgentId = Guid.NewGuid().ToString("d"),
            LabId = lab.LabId,
            Number = 7,
            Mac = "02:00:5E:00:00:07",
            AgentVersion = "0.1.0",
            ProtocolVersion = Defaults.ProtocolVersion,
        };

        var thisInstance = Guid.NewGuid().ToString("d");
        var machine = registry.RecordHello(hello, "00AB", thisInstance, now, out var isNew);

        Assert.True(isNew);
        Assert.Equal(7, machine.Number);
        Assert.Equal("AB", machine.CertificateSerial);
        Assert.Equal(thisInstance, machine.LastInstanceId);
        Assert.Single(registry.Document.Machines);

        // The same PC connecting again updates rather than duplicates.
        registry.RecordHello(hello, "00AB", thisInstance, now.AddMinutes(1), out var isNewAgain);
        Assert.False(isNewAgain);
        Assert.Single(registry.Document.Machines);
    }

    [Fact]
    public void A_pc_arriving_from_the_other_teacher_machine_records_that_machine()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var registry = new LabRegistry(new LabDocument { LabId = lab.LabId }, LabTrustTests.PublicOnly(lab.Authority));
        var thisInstance = Guid.NewGuid().ToString("d");
        var otherInstance = Guid.NewGuid().ToString("d");

        var hello = new Hello { AgentId = Guid.NewGuid().ToString("d"), Number = 3, PreviousInstanceId = otherInstance };
        registry.RecordHello(hello, "01", thisInstance, now, out _);

        var other = Assert.Single(registry.OtherTeacherMachines(thisInstance));
        Assert.Equal(otherInstance, other.InstanceId);
        Assert.False(other.IsThisMachine);

        // And now that it is linked here, nobody else holds it.
        Assert.Empty(registry.HeldElsewhere(thisInstance, []));
    }

    [Fact]
    public void A_reinstalled_pc_replaces_its_old_record_because_the_number_is_the_identity()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var registry = new LabRegistry(new LabDocument { LabId = lab.LabId }, LabTrustTests.PublicOnly(lab.Authority));
        var thisInstance = Guid.NewGuid().ToString("d");

        var oldAgent = Guid.NewGuid().ToString("d");
        registry.RecordHello(new Hello { AgentId = oldAgent, Number = 7 }, "0A", thisInstance, now, out _);

        (MachineRecord Old, MachineRecord New)? announced = null;
        registry.Replaced += (old, replacement) => announced = (old, replacement);

        // Windows was reinstalled and Setup ran again: new agent id, same sticker (D-25).
        var newAgent = Guid.NewGuid().ToString("d");
        var replacement = registry.RecordEnrollment(Request(lab.LabId, 7, "unused"), "0B", now.AddDays(30));

        var only = Assert.Single(registry.Document.Machines);
        Assert.Same(replacement, only);
        Assert.Equal(7, only.Number);
        Assert.NotEqual(oldAgent, only.AgentId);
        Assert.NotNull(announced);
        Assert.Equal(oldAgent, announced.Value.Old.AgentId);
        Assert.Same(replacement, announced.Value.New);
        Assert.Null(registry.FindByAgentId(oldAgent));

        // Renewal (D-25) only swaps the serial the console will accept next.
        Assert.Equal("B", registry.RecordRenewal(only.AgentId, "000B", now)!.CertificateSerial);
        _ = newAgent;
    }

    [Fact]
    public void The_banner_knows_which_pcs_the_other_teacher_machine_holds()
    {
        using var lab = TestLab.Create();
        var thisInstance = Guid.NewGuid().ToString("d");
        var otherInstance = Guid.NewGuid().ToString("d");

        var document = new LabDocument
        {
            LabId = lab.LabId,
            Machines =
            [
                new MachineRecord { AgentId = "a", Number = 1, LastInstanceId = thisInstance },
                new MachineRecord { AgentId = "b", Number = 2, LastInstanceId = otherInstance },
                new MachineRecord { AgentId = "c", Number = 3, LastInstanceId = otherInstance },
            ],
        };

        var registry = new LabRegistry(document, LabTrustTests.PublicOnly(lab.Authority));

        // "b" has since arrived here, so only "c" is still held by the other console.
        var held = registry.HeldElsewhere(thisInstance, ["a", "b"]);

        Assert.Equal(3, Assert.Single(held).Number);
    }

    [Fact]
    public void A_console_that_has_never_arranged_the_room_still_looks_right()
    {
        using var lab = TestLab.Create();
        var document = new LabDocument
        {
            Machines = Enumerable.Range(1, 14)
                .Select(n => new MachineRecord { AgentId = $"a{n}", Number = n })
                .ToList(),
        };

        var registry = new LabRegistry(document, LabTrustTests.PublicOnly(lab.Authority));
        var layout = registry.EffectiveLayout();

        Assert.Equal(14, layout.Count);
        Assert.Equal((0, 0), (layout[0].Column, layout[0].Row));
        Assert.Equal((0, 1), (layout[Defaults.DefaultTilesPerRow].Column, layout[Defaults.DefaultTilesPerRow].Row));

        // A hand-arranged tile wins over the default.
        registry.SetLayout([new LayoutTile { Number = 1, Column = 4, Row = 3 }]);
        Assert.Equal((4, 3), registry.EffectiveLayout()
            .Where(t => t.Number == 1)
            .Select(t => (t.Column, t.Row))
            .Single());
    }

    [Fact]
    public void A_hand_edited_lab_file_cannot_invent_a_revocation()
    {
        using var lab = TestLab.Create();
        var document = new LabDocument
        {
            LabId = lab.LabId,
            Revocations =
            [
                RevocationRecord.From(RevocationSet.Create(lab, "AAAA", "stolen", DateTimeOffset.UtcNow)),
                new RevocationRecord { Serial = "BBBB", Reason = "typed in by hand" },
            ],
        };

        var registry = new LabRegistry(document, LabTrustTests.PublicOnly(lab.Authority));

        Assert.True(registry.Revocations.IsRevoked("AAAA"));
        Assert.False(registry.Revocations.IsRevoked("BBBB"));
        Assert.Single(registry.Document.Revocations);
    }

    // ------------------------------------------------------------------ jobs

    [Fact]
    public void A_job_for_an_offline_pc_waits_and_runs_on_reconnect()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new JobQueue();

        var job = queue.Create("pc-07", Job.Types.Kind.RunScript, now, agentOnline: false);
        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(JobDelivery.Queued, job.Delivery);

        // Another PC connecting does not collect it.
        Assert.Empty(queue.TakePending("pc-08", now));

        var delivered = Assert.Single(queue.TakePending("pc-07", now.AddHours(2)));
        Assert.Equal(job.Id, delivered.Id);
        Assert.Equal(JobState.Delivered, delivered.State);

        queue.Progress(new JobProgress { JobId = job.Id, Percent = 40, Line = "installing" }, now);
        Assert.Equal(JobState.Running, job.State);
        Assert.Equal("installing", Assert.Single(job.Output));

        queue.Complete(new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = "done" }, now);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(100, job.Percent);
    }

    [Fact]
    public void A_shutdown_for_an_offline_pc_is_never_queued()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new JobQueue();

        var job = queue.Create("pc-07", Job.Types.Kind.Shutdown, now, agentOnline: false);
        Assert.Equal(JobDelivery.OnlineOnly, job.Delivery);
        Assert.Equal(JobState.NotDelivered, job.State);

        // Not even a minute later, when the PC happens to come back.
        Assert.Empty(queue.TakePending("pc-07", now.AddMinutes(1)));
    }

    [Fact]
    public void A_shutdown_still_pending_when_the_pc_drops_is_closed_not_kept()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new JobQueue();

        var shutdown = queue.Create("pc-07", Job.Types.Kind.Shutdown, now, agentOnline: true);
        var script = queue.Create("pc-07", Job.Types.Kind.RunScript, now, agentOnline: true);
        Assert.Equal(JobState.Pending, shutdown.State);

        var dropped = queue.AgentWentOffline("pc-07", now.AddSeconds(1));

        Assert.Same(shutdown, Assert.Single(dropped));
        Assert.Equal(JobState.NotDelivered, shutdown.State);
        Assert.Equal(JobState.Pending, script.State);
        Assert.Same(script, Assert.Single(queue.TakePending("pc-07", now.AddHours(1))));
    }

    [Fact]
    public void A_job_the_agent_went_silent_on_stops_spinning_but_a_talkative_one_does_not()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new JobQueue();

        var job = queue.Create("pc-07", Job.Types.Kind.InstallPackage, now, agentOnline: true, timeout: TimeSpan.FromSeconds(30));
        queue.TakePending("pc-07", now);

        Assert.Empty(queue.TimeOutStale(now.AddSeconds(20)));

        // A long installation that keeps reporting progress is never cut off.
        queue.Progress(new JobProgress { JobId = job.Id, Percent = 10 }, now.AddSeconds(25));
        Assert.Empty(queue.TimeOutStale(now.AddSeconds(50)));

        // One that stops talking is.
        Assert.Single(queue.TimeOutStale(now.AddSeconds(56)));
        Assert.Equal(JobState.TimedOut, job.State);
    }

    [Fact]
    public void A_batch_reports_one_row_per_pc()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new JobQueue();
        var batch = Guid.NewGuid().ToString("d");

        foreach (var pc in (string[])["pc-01", "pc-02", "pc-03"])
        {
            queue.Create(pc, Job.Types.Kind.SendFile, now, agentOnline: true, batchId: batch);
        }

        queue.Create("pc-01", Job.Types.Kind.Reboot, now, agentOnline: false);

        Assert.Equal(3, queue.Batch(batch).Count);
        Assert.Equal(4, queue.All().Count);
    }

    [Fact]
    public void A_re_sent_job_returns_the_cached_result_instead_of_running_twice()
    {
        var ledger = new LabControl.Shared.Link.JobLedger();
        var job = new Job { Id = Guid.NewGuid().ToString("d"), Kind = Job.Types.Kind.Reboot };

        var first = ledger.Admit(job);
        Assert.True(first.ShouldRun);

        // The console re-sent it while the PC was still working: do not reboot twice.
        var duplicate = ledger.Admit(job);
        Assert.False(duplicate.ShouldRun);
        Assert.True(duplicate.DuplicateOfRunning);

        ledger.Complete(new JobResult { JobId = job.Id, Ok = true, Message = "rebooting" });

        var afterReconnect = ledger.Admit(job);
        Assert.False(afterReconnect.ShouldRun);
        Assert.False(afterReconnect.DuplicateOfRunning);
        Assert.Equal("rebooting", afterReconnect.CachedResult!.Message);
    }

    [Fact]
    public void The_ledger_does_not_grow_without_bound()
    {
        var ledger = new LabControl.Shared.Link.JobLedger(capacity: 4);

        for (var i = 0; i < 10; i++)
        {
            ledger.Complete(new JobResult { JobId = $"job-{i}", Ok = true });
        }

        Assert.Equal(4, ledger.CompletedCount);
        Assert.True(ledger.Admit(new Job { Id = "job-9" }).CachedResult is not null);
        Assert.True(ledger.Admit(new Job { Id = "job-0" }).ShouldRun);
    }

    private static EnrollRequest Request(string labId, int number, string code)
    {
        using var key = LabCertificates.CreateKey();

        return new EnrollRequest
        {
            LabId = labId,
            AgentId = Guid.NewGuid().ToString("d"),
            Number = number,
            Hostname = string.Format(Defaults.MachineNameFormat, number),
            Mac = $"02:00:5E:00:00:{number:X2}",
            EnrollmentCode = code,
            Csr = ByteString.CopyFrom(LabCertificates.CreateSigningRequest(key, "whatever the pc asked for")),
            ProtocolVersion = Defaults.ProtocolVersion,
        };
    }
}
