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
    public void Writing_a_new_stick_voids_the_unused_codes_of_the_old_one()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var authority = new EnrollmentAuthority(new EnrollmentDocument { LabId = lab.LabId });

        var oldStick = authority.Generate(3, "stick-1", now);
        Assert.True(authority.Redeem(lab, Request(lab.LabId, 1, oldStick[0]), now).Ok);

        // The console writes a second stick: the two unused codes of the first are voided,
        // the burned one is left as history.
        Assert.Equal(2, authority.Supersede(now.AddMinutes(1)));
        var newStick = authority.Generate(2, "stick-2", now.AddMinutes(1));
        Assert.Equal(2, authority.UnusedCodeCount);

        var stale = authority.Redeem(lab, Request(lab.LabId, 2, oldStick[1]), now.AddMinutes(2));
        Assert.Equal(EnrollmentOutcome.VoidedCode, stale.Outcome);
        Assert.Contains("stick-1", stale.Message, StringComparison.Ordinal);

        // The burned code still reports who used it, not that it was voided.
        Assert.Equal(EnrollmentOutcome.BurnedCode, authority.Redeem(lab, Request(lab.LabId, 2, oldStick[0]), now).Outcome);

        Assert.True(authority.Redeem(lab, Request(lab.LabId, 2, newStick[0]), now.AddMinutes(2)).Ok);
        Assert.Equal(1, authority.UnusedCodeCount);
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

        // And now that it is linked here, the observation names this console, so nobody
        // else is credited with the PC (M5 §4.6, D-58).
        Assert.Empty(registry.ObservedElsewhere(otherInstance, now));
        Assert.Single(registry.ObservedElsewhere(thisInstance, now));
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
    public void The_banner_counts_only_the_pcs_the_other_teacher_machine_was_seen_taking()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        var thisInstance = Guid.NewGuid().ToString("d");
        var otherInstance = Guid.NewGuid().ToString("d");
        var observed = now.AddMinutes(-1).ToUnixTimeSeconds();

        var document = new LabDocument
        {
            LabId = lab.LabId,
            Machines =
            [
                new MachineRecord { AgentId = "a", Number = 1, LastInstanceId = thisInstance, LastInstanceObservedUnix = observed },
                new MachineRecord { AgentId = "b", Number = 2, LastInstanceId = otherInstance, LastInstanceObservedUnix = observed },
                new MachineRecord { AgentId = "c", Number = 3, LastInstanceId = otherInstance, LastInstanceObservedUnix = observed },
                // Never observed with anybody: a PC that is simply switched off (M5 §4.6).
                new MachineRecord { AgentId = "d", Number = 4 },
                // Observed with the other console, but hours ago: too old to still be shown.
                new MachineRecord
                {
                    AgentId = "e",
                    Number = 5,
                    LastInstanceId = otherInstance,
                    LastInstanceObservedUnix = now.Add(-Defaults.OwnershipObservationLifetime).AddMinutes(-1).ToUnixTimeSeconds(),
                },
            ],
        };

        var registry = new LabRegistry(document, LabTrustTests.PublicOnly(lab.Authority));

        Assert.Equal([2, 3], registry.ObservedElsewhere(otherInstance, now).Select(m => m.Number));
        Assert.Equal([1], registry.ObservedElsewhere(thisInstance, now).Select(m => m.Number));

        // "b" says Hello here: the strongest observation there is, and it supersedes the
        // other console's claim on it without anything having to be cleared by hand.
        registry.RecordHello(new Hello { AgentId = "b", Number = 2 }, "0A", thisInstance, now, out _);
        Assert.Equal([3], registry.ObservedElsewhere(otherInstance, now).Select(m => m.Number));
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
    public void New_pcs_never_land_on_a_tile_that_is_already_placed()
    {
        using var lab = TestLab.Create();
        var document = new LabDocument
        {
            Machines = Enumerable.Range(1, 14)
                .Select(n => new MachineRecord { AgentId = $"a{n}", Number = n })
                .ToList(),
            // The room was arranged by hand once: fourteen tiles in the default grid.
            Layout = Enumerable.Range(1, 14)
                .Select(n => new LayoutTile { Number = n, Column = (n - 1) % Defaults.DefaultTilesPerRow, Row = (n - 1) / Defaults.DefaultTilesPerRow })
                .ToList(),
        };
        var registry = new LabRegistry(document, LabTrustTests.PublicOnly(lab.Authority));

        // Sixteen more PCs enrol; none may cover an existing tile.
        foreach (var n in Enumerable.Range(15, 16))
        {
            document.Machines.Add(new MachineRecord { AgentId = $"a{n}", Number = n });
        }

        var layout = registry.EffectiveLayout();
        Assert.Equal(30, layout.Count);
        Assert.Equal(30, layout.Select(t => (t.Column, t.Row)).Distinct().Count());
        Assert.Equal((2, 2), layout.Where(t => t.Number == 15).Select(t => (t.Column, t.Row)).Single());

        // A layout that already holds a collision (saved by an older build) is untangled the
        // same way every time: the lower number keeps its cell.
        document.Layout.Add(new LayoutTile { Number = 20, Column = 0, Row = 0 });
        layout = registry.EffectiveLayout();
        Assert.Equal(30, layout.Select(t => (t.Column, t.Row)).Distinct().Count());
        Assert.Equal((0, 0), layout.Where(t => t.Number == 1).Select(t => (t.Column, t.Row)).Single());
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

        var first = ledger.Admit(job, "console-1");
        Assert.True(first.ShouldRun);

        // The console re-sent it while the PC was still working: do not reboot twice.
        var duplicate = ledger.Admit(job, "console-1");
        Assert.False(duplicate.ShouldRun);
        Assert.True(duplicate.DuplicateOfRunning);

        ledger.Complete(new JobResult { JobId = job.Id, Ok = true, Message = "rebooting" });

        var afterReconnect = ledger.Admit(job, "console-1");
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
        Assert.True(ledger.Admit(new Job { Id = "job-9" }, "").CachedResult is not null);
        Assert.True(ledger.Admit(new Job { Id = "job-0" }, "").ShouldRun);
    }

    [Fact]
    public void A_result_is_answered_only_to_the_instance_that_delivered_the_job()
    {
        // D-57 item 4: the ledger binds every job to the console instance that delivered it.
        var ledger = new LabControl.Shared.Link.JobLedger();
        var job = new Job { Id = Guid.NewGuid().ToString("d"), Kind = Job.Types.Kind.RunScript };

        Assert.True(ledger.Admit(job, "macbook").ShouldRun);
        Assert.Equal("macbook", ledger.DeliveringInstanceOf(job.Id));

        // Another console of the same lab re-sends the id while it runs: not a duplicate it may
        // wait for, not a second run — refused as another instance's job.
        var foreignWhileRunning = ledger.Admit(job, "lab-pc");
        Assert.False(foreignWhileRunning.ShouldRun);
        Assert.False(foreignWhileRunning.DuplicateOfRunning);
        Assert.Null(foreignWhileRunning.CachedResult);
        Assert.True(foreignWhileRunning.BelongsToAnotherInstance);
        Assert.Equal("macbook", foreignWhileRunning.OtherInstanceId);
        Assert.Equal(1, ledger.RunningCount);

        var entry = ledger.Complete(new JobResult { JobId = job.Id, Ok = true, Message = "secret output" });
        Assert.Equal("macbook", entry.InstanceId);
        Assert.Equal("macbook", ledger.DeliveringInstanceOf(job.Id));

        // Finished: the other instance still gets nothing of the result; the deliverer gets it, case-insensitively.
        var foreignAfter = ledger.Admit(job, "lab-pc");
        Assert.Null(foreignAfter.CachedResult);
        Assert.True(foreignAfter.BelongsToAnotherInstance);
        Assert.Equal("secret output", ledger.Admit(job, "MACBOOK").CachedResult!.Message);
        Assert.Null(ledger.DeliveringInstanceOf("unknown"));
    }

    [Fact]
    public void A_result_completed_without_an_admission_keeps_the_instance_it_is_given()
    {
        var ledger = new LabControl.Shared.Link.JobLedger();
        var entry = ledger.Complete(new JobResult { JobId = "j", Ok = true }, "macbook");
        Assert.Equal("macbook", entry.InstanceId);
        Assert.True(ledger.Admit(new Job { Id = "j" }, "lab-pc").BelongsToAnotherInstance);
        Assert.NotNull(ledger.Admit(new Job { Id = "j" }, "macbook").CachedResult);

        // A second completion of the same id keeps the first entry and its owner.
        var again = ledger.Complete(new JobResult { JobId = "j", Ok = false }, "lab-pc");
        Assert.Equal("macbook", again.InstanceId);
        Assert.True(again.Result.Ok);
    }

    [Fact]
    public void In_flight_jobs_are_restored_only_for_the_same_lab_and_instance()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new JobQueue("lab-a", "macbook");
        var delivered = queue.Create("pc-1", Job.Types.Kind.RunScript, now.AddMinutes(-20), agentOnline: true, args: new Dictionary<string, string> { ["name"] = "wait" }, batchId: "batch-1");
        var pending = queue.Create("pc-2", Job.Types.Kind.RunScript, now.AddMinutes(-20), agentOnline: false, batchId: "batch-1");
        Assert.Equal("lab-a", delivered.LabId);
        Assert.Equal("macbook", delivered.InstanceId);
        Assert.Single(queue.TakePending("pc-1", now.AddMinutes(-19)));
        queue.Progress(new JobProgress { JobId = delivered.Id, Percent = 40, Line = "half" }, now.AddMinutes(-18));
        Assert.Equal(JobState.Pending, pending.State);

        // Only the delivered job is in flight; the queued one is never carried over (D-57 item 2).
        var snapshot = queue.SnapshotInFlight();
        var row = Assert.Single(snapshot);
        Assert.Equal(delivered.Id, row.Id);
        Assert.Equal(JobState.Running, row.State);
        Assert.Equal(["half"], row.Output);
        Assert.Equal("wait", row.Args["name"]);
        Assert.Equal("lab-a", row.LabId);
        Assert.Equal("macbook", row.InstanceId);

        var foreignLab = new InFlightJob { Id = "x", AgentId = "pc-9", Kind = Job.Types.Kind.RunScript, LabId = "lab-b", InstanceId = "macbook", State = JobState.Delivered };
        var foreignInstance = new InFlightJob { Id = "y", AgentId = "pc-9", Kind = Job.Types.Kind.RunScript, LabId = "lab-a", InstanceId = "lab-pc", State = JobState.Delivered };
        var finished = new InFlightJob { Id = "z", AgentId = "pc-9", Kind = Job.Types.Kind.RunScript, LabId = "lab-a", InstanceId = "macbook", State = JobState.Succeeded };

        var next = new JobQueue("lab-a", "macbook");
        var seen = new List<JobRecord>();
        next.Updated += seen.Add;
        var restored = next.Restore([row, foreignLab, foreignInstance, finished], now, _ => null);
        var back = Assert.Single(restored.Resent);
        Assert.Empty(restored.Unknown);
        Assert.Equal(3, restored.Foreign);
        Assert.Same(back, Assert.Single(seen));                         // the jobs panel is told (S6)
        Assert.Equal(delivered.Id, back.Id);
        Assert.Equal(JobState.Running, back.State);
        Assert.True(back.RestoredFromDisk);
        Assert.Equal(now.ToUnixTimeSeconds(), back.LastActivityUnix);   // the inactivity clock restarts
        Assert.Equal(delivered.DeliveredAtUnix, back.DeliveredAtUnix);
        Assert.Equal("wait", back.Args["name"]);
        Assert.Equal(["half"], back.Output);
        Assert.Single(next.InFlight("pc-1"));
        Assert.Empty(next.TimeOutStale(now.AddSeconds(back.TimeoutSeconds - 1)));

        // A restored row is never written back: it has had its one re-send and must not
        // chase the teacher from lesson to lesson (D-57 item 4).
        Assert.Empty(next.SnapshotInFlight());

        // The snapshot row carries the lab and instance for the batch export.
        var log = Assert.Single(next.SnapshotBatch("batch-1"));
        Assert.Equal("lab-a", log.LabId);
        Assert.Equal("macbook", log.InstanceId);

        // A second result for a closed job changes nothing (the drained result and the
        // re-sent copy's cached answer both arrive after a reconnect).
        Assert.NotNull(next.Complete(new JobResult { JobId = back.Id, Ok = true, Message = "first" }, now));
        Assert.Null(next.Complete(new JobResult { JobId = back.Id, Ok = false, Message = "second" }, now));
        Assert.Equal("first", back.Message);
        Assert.True(back.Ok);
    }

    [Theory]
    [InlineData(Job.Types.Kind.Reboot)]
    [InlineData(Job.Types.Kind.Shutdown)]
    [InlineData(Job.Types.Kind.Logoff)]
    [InlineData(Job.Types.Kind.ResetProfile)]
    [InlineData(Job.Types.Kind.SelfUpdate)]
    [InlineData(Job.Types.Kind.Rekey)]
    [InlineData(Job.Types.Kind.SendFile)]
    [InlineData(Job.Types.Kind.InstallPackage)]
    [InlineData(Job.Types.Kind.CollectFiles)]
    public void A_saved_job_that_must_not_run_twice_comes_back_closed_as_outcome_unknown(Job.Types.Kind kind)
    {
        // *Shut down all* followed by Disconnect must never power the class off the next
        // morning: only run_script is ever sent again (D-57 item 4).
        var now = DateTimeOffset.UtcNow;
        var savedAt = now.AddMinutes(-1);
        var row = new InFlightJob
        {
            Id = "j", AgentId = "pc-1", Kind = kind, LabId = "lab-a", InstanceId = "macbook",
            State = JobState.Delivered, TimeoutSeconds = 600, DeliveredAtUnix = savedAt.ToUnixTimeSeconds(),
        };

        Assert.False(InFlightJobPolicy.IsResendable(kind));
        var queue = new JobQueue("lab-a", "macbook");
        var restored = queue.Restore([row], now, job => InFlightJobPolicy.RefuseReason(job, savedAt, now));

        Assert.Empty(restored.Resent);
        var closed = Assert.Single(restored.Unknown);
        Assert.Equal(JobState.TimedOut, closed.State);
        Assert.False(closed.Ok);
        Assert.Contains("Outcome unknown", closed.Message, StringComparison.Ordinal);
        Assert.Contains("never sent to a PC a second time", closed.Message, StringComparison.Ordinal);
        Assert.Empty(queue.InFlight("pc-1"));           // nothing is delivered to the PC
        Assert.Empty(queue.SnapshotInFlight());

        // A result that does arrive later still replaces it — the row is honest, not final.
        Assert.NotNull(queue.Complete(new JobResult { JobId = "j", Ok = true, Message = "done after all" }, now));
        Assert.Equal(JobState.Succeeded, closed.State);
    }

    [Fact]
    public void A_saved_row_older_than_its_own_bound_is_not_sent_again()
    {
        var now = DateTimeOffset.UtcNow;
        var script = new InFlightJob
        {
            Id = "j", AgentId = "pc-1", Kind = Job.Types.Kind.RunScript, LabId = "lab-a", InstanceId = "macbook",
            State = JobState.Delivered, TimeoutSeconds = 120,
        };

        // The bound is the job's own timeout or an hour, whichever is smaller.
        Assert.Equal(TimeSpan.FromMinutes(2), InFlightJobPolicy.MaxAgeOf(script));
        Assert.Equal(Defaults.InFlightJobsMaxAge, InFlightJobPolicy.MaxAgeOf(new InFlightJob { Kind = Job.Types.Kind.RunScript, TimeoutSeconds = 86_400 }));
        Assert.Null(InFlightJobPolicy.RefuseReason(script, now.AddMinutes(-1), now));

        var stale = InFlightJobPolicy.RefuseReason(script, now.AddMinutes(-3), now);
        Assert.NotNull(stale);
        Assert.Contains("longer than", stale, StringComparison.Ordinal);

        var queue = new JobQueue("lab-a", "macbook");
        var restored = queue.Restore([script], now, job => InFlightJobPolicy.RefuseReason(job, now.AddHours(-9), now));
        Assert.Empty(restored.Resent);
        Assert.Equal(JobState.TimedOut, Assert.Single(restored.Unknown).State);
    }

    [Fact]
    public void A_job_delivered_before_the_PC_booted_is_not_sent_again()
    {
        // The agent's ledger lives in its memory; a reboot takes it with it, so the re-sent
        // copy would run instead of being answered (D-57 item 4, D-32 item 7).
        var boot = DateTimeOffset.UtcNow.AddMinutes(-5);
        Assert.True(InFlightJobPolicy.RebootedSinceDelivery(boot.AddMinutes(-10).ToUnixTimeSeconds(), boot.ToUnixTimeSeconds()));
        Assert.False(InFlightJobPolicy.RebootedSinceDelivery(boot.AddMinutes(10).ToUnixTimeSeconds(), boot.ToUnixTimeSeconds()));

        // An agent that reports no boot time at all is never taken to have rebooted.
        Assert.False(InFlightJobPolicy.RebootedSinceDelivery(boot.ToUnixTimeSeconds(), 0));
        Assert.False(InFlightJobPolicy.RebootedSinceDelivery(0, boot.ToUnixTimeSeconds()));

        var now = DateTimeOffset.UtcNow;
        var queue = new JobQueue("lab-a", "macbook");
        var restored = queue.Restore([new InFlightJob
        {
            Id = "j", AgentId = "pc-1", Kind = Job.Types.Kind.RunScript, LabId = "lab-a", InstanceId = "macbook",
            State = JobState.Delivered, TimeoutSeconds = 600, DeliveredAtUnix = boot.AddMinutes(-10).ToUnixTimeSeconds(),
        }], now, _ => null);
        Assert.Single(restored.Resent);

        var closed = queue.CloseAsOutcomeUnknown("j", now, "the PC has restarted since, so it no longer remembers this job");
        Assert.NotNull(closed);
        Assert.Equal(JobState.TimedOut, closed.State);
        Assert.Contains("restarted since", closed.Message, StringComparison.Ordinal);
        Assert.Empty(queue.InFlight("pc-1"));
        Assert.Null(queue.CloseAsOutcomeUnknown("j", now, "again"));     // a closed row closes once
        Assert.Null(queue.CloseAsOutcomeUnknown("unknown-id", now, "no such job"));
    }

    [Fact]
    public void An_online_only_job_stays_online_only_when_it_is_restored()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new JobQueue("lab-a", "macbook");
        var job = queue.Create("pc-1", Job.Types.Kind.RunScript, now, agentOnline: true, delivery: JobDelivery.OnlineOnly);
        queue.TakePending("pc-1", now);
        var row = Assert.Single(queue.SnapshotInFlight());
        Assert.Equal(JobDelivery.OnlineOnly, row.Delivery);

        var next = new JobQueue("lab-a", "macbook");
        Assert.Equal(JobDelivery.OnlineOnly, Assert.Single(next.Restore([row], now, _ => null).Resent).Delivery);
        Assert.Equal(job.Id, row.Id);
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
