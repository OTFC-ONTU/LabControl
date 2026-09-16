using System.Text.Json;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// A machine that records every call in order and answers what the test told it to
/// (M6, D-69). The order is the point: the journal must restore <i>before</i> it reads.
/// </summary>
internal sealed class FakeMachine : IMachineState
{
    public List<string> Calls { get; } = [];

    public Dictionary<string, Func<MachineResult>> Answers { get; } = [];

    public OverlaySession? Shown { get; private set; }

    public DisplacedSettings? Refined { get; private set; }

    public MachineResult RestoreDefaults() => Answer(nameof(RestoreDefaults));

    public MachineResult RefineRestore(DisplacedSettings displaced)
    {
        Refined = displaced;
        return Answer(nameof(RefineRestore));
    }

    public MachineResult Confirm() => Answer(nameof(Confirm));

    public MachineResult ApplyOverlay(OverlaySession session)
    {
        var result = Answer(nameof(ApplyOverlay));
        if (result.Succeeded)
        {
            Shown = session;
        }

        return result;
    }

    public MachineResult LiftOverlay()
    {
        var result = Answer(nameof(LiftOverlay));
        if (result.Succeeded)
        {
            Shown = null;
        }

        return result;
    }

    public MachineResult ApplyInternet(InternetSession session, DisplacedSettings displaced) => Answer(nameof(ApplyInternet));

    public MachineResult LiftInternet() => Answer(nameof(LiftInternet));

    public MachineResult ApplyExam(ExamSession session) => Answer(nameof(ApplyExam));

    public MachineResult LiftExam() => Answer(nameof(LiftExam));

    private MachineResult Answer(string call)
    {
        Calls.Add(call);
        return Answers.TryGetValue(call, out var answer) ? answer() : MachineResult.Done;
    }
}

/// <summary>The §0 state machine (D-69), run on the Mac against <see cref="FakeMachine"/>.</summary>
public sealed class PolicyJournalTests
{
    private const string LabId = "1c383cd3-0b7c-4d69-9a37-4bfd08bb6c2e";
    private const string Instance = "console-a";
    private const long Now = 1_800_000_000;

    private static readonly long Hour = (long)Defaults.LockHardLimit.TotalSeconds;

    private sealed class Rig
    {
        public FakeMachine Machine { get; } = new();

        public List<PolicyEvent> Events { get; } = [];

        public List<string> Reads { get; } = [];

        public PolicyDocument? Saved { get; private set; }

        public int Saves { get; private set; }

        public Func<string?> Reader { get; set; } = () => null;

        public Action<PolicyDocument> Writer { get; set; }

        public PolicyJournal Journal { get; }

        public Rig(string? document = null)
        {
            if (document is not null)
            {
                Reader = () => document;
            }

            Writer = saved =>
            {
                Saved = saved;
                Saves++;
            };

            Journal = new PolicyJournal(Machine, LabId, () =>
            {
                Machine.Calls.Add("read");
                Reads.Add("read");
                return Reader();
            }, saved => Writer(saved));
            Journal.Reported += Events.Add;
        }

        public IEnumerable<string> Codes => Events.Select(e => e.Code);
    }

    private static string Document(ClassroomPolicy policy, string labId = LabId, int? schema = null)
    {
        var json = JsonStore.Serialize(new PolicyDocument { LabId = labId, SavedAtUnix = Now, Policy = policy }, PolicyDocument.Migrations);
        return schema is null ? json : json.Replace($"\"schema_version\": {Defaults.PolicySchemaVersion}", $"\"schema_version\": {schema}", StringComparison.Ordinal);
    }

    private static ClassroomPolicy WithLock(long until, long hardLimit, string sessionId = "lock-1") => new()
    {
        Overlay = new OverlaySession { SessionId = sessionId, SetByInstance = Instance, SinceUnix = Now - 60, UntilUnix = until, HardLimitUnix = hardLimit, Message = "Look here" },
        Displaced = new DisplacedSettings(),
    };

    // ------------------------------------------------------------------ restore before read

    [Theory]
    [InlineData("throws")]
    [InlineData("garbage")]
    [InlineData("nulls")]
    public void The_machine_is_restored_before_the_document_is_read_and_a_bad_document_only_costs_an_event(string flavour)
    {
        var rig = new Rig();
        rig.Reader = flavour switch
        {
            "throws" => () => throw new IOException("disk on fire"),
            "garbage" => () => "{ this is not json",
            _ => () => "{\"schema_version\":1,\"lab_id\":null,\"policy\":null}",
        };

        var outcome = rig.Journal.Start(Now);

        // RestoreDefaults ran first, before the reader was ever called, whatever the reader did.
        Assert.Equal(nameof(IMachineState.RestoreDefaults), rig.Machine.Calls[0]);
        Assert.Equal("read", rig.Machine.Calls[1]);
        Assert.True(outcome.MachineOpen);
        Assert.Empty(outcome.Reapplied);
        Assert.Contains(PolicyEvents.Unreadable, rig.Codes);
        Assert.DoesNotContain(PolicyEvents.RestoreFailed, rig.Codes);
        Assert.Null(rig.Saved!.Policy!.Overlay);
        Assert.Equal(1, rig.Saves);
    }

    [Fact]
    public void A_missing_document_is_a_fresh_install_not_a_problem()
    {
        var rig = new Rig();

        var outcome = rig.Journal.Start(Now);

        Assert.True(outcome.MachineOpen);
        Assert.Empty(rig.Events);
        Assert.Equal(LabId, rig.Saved!.LabId);
    }

    [Fact]
    public void A_foreign_lab_a_newer_schema_and_a_null_displaced_are_all_unreadable_and_nothing_is_reapplied()
    {
        var foreign = new Rig(Document(WithLock(0, Now + Hour), labId: "another-lab"));
        var newer = new Rig(Document(WithLock(0, Now + Hour), schema: Defaults.PolicySchemaVersion + 1));
        var nullDisplaced = new Rig(Document(WithLock(0, Now + Hour)).Replace("\"displaced\": {", "\"displaced\": null, \"was\": {", StringComparison.Ordinal));

        foreach (var rig in new[] { foreign, newer, nullDisplaced })
        {
            var outcome = rig.Journal.Start(Now);
            Assert.True(outcome.MachineOpen);
            Assert.Empty(outcome.Reapplied);
            Assert.Single(rig.Events, e => e.Code == PolicyEvents.Unreadable);
            Assert.DoesNotContain(nameof(IMachineState.ApplyOverlay), rig.Machine.Calls);
            Assert.Null(rig.Machine.Shown);
        }

        Assert.Contains("another-lab", foreign.Events[0].Message, StringComparison.Ordinal);
        Assert.Contains((Defaults.PolicySchemaVersion + 1).ToString(), newer.Events[0].Message, StringComparison.Ordinal);
        Assert.Contains("displaced", nullDisplaced.Events[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"dns\": [", "\"dns\": null, \"was\": [", "displaced.dns")]
    [InlineData("\"session_id\": \"lock-1\"", "\"session_id\": null", "overlay.session_id")]
    [InlineData("\"servers\": [", "\"servers\": [null, ", "servers[0]")]
    [InlineData("\"profile_default_outbound\": {", "\"profile_default_outbound\": {\"public\": null, ", "profile_default_outbound.public")]
    [InlineData("\"set_by_instance\": \"console-a\"", "\"set_by_instance\": null", "overlay.set_by_instance")]
    [InlineData("\"message\": \"Look here\"", "\"message\": null", "overlay.message")]
    public void A_null_anywhere_the_schema_requires_a_value_makes_the_document_unreadable_and_nothing_throws(string find, string replaceWith, string named)
    {
        var policy = WithLock(0, Now + Hour);
        policy.Displaced = new DisplacedSettings
        {
            ProfileDefaultOutbound = { ["private"] = "block" },
            Dns = { new DisplacedDns { Interface = "Ethernet", Servers = { "10.0.0.1" } } },
            Applied = true,
        };
        var text = Document(policy);
        Assert.Contains(find, text, StringComparison.Ordinal);
        var rig = new Rig(text.Replace(find, replaceWith, StringComparison.Ordinal));

        var outcome = rig.Journal.Start(Now);

        // Restore already done before the read, nothing re-applied, the refine never asked for.
        Assert.Equal([nameof(IMachineState.RestoreDefaults), "read", nameof(IMachineState.Confirm)], rig.Machine.Calls);
        Assert.True(outcome.MachineOpen);
        Assert.Empty(outcome.Reapplied);
        var unreadable = Assert.Single(rig.Events, e => e.Code == PolicyEvents.Unreadable);
        Assert.Contains(named, unreadable.Message, StringComparison.Ordinal);
        Assert.Null(rig.Machine.Shown);
        Assert.Null(rig.Saved!.Policy!.Overlay);

        // And the state for the Welcome re-send is built without a throw.
        Assert.Equal(Overlay.Types.Kind.Unspecified, rig.Journal.OverlayStateOf(Now).Kind);
        Assert.Equal(Overlay.Types.Kind.Unspecified, rig.Journal.ReassertOverlay(Now).Kind);
        Assert.Equal(string.Empty, rig.Journal.NoteEnforcement(true, null!).Reason);
    }

    [Fact]
    public void A_document_whose_overlay_is_not_a_lock_is_dropped_not_reapplied()
    {
        var rig = new Rig(Document(WithLock(0, Now + Hour)).Replace("\"kind\": \"lock\"", "\"kind\": \"broadcast_start\"", StringComparison.Ordinal));

        var outcome = rig.Journal.Start(Now);

        Assert.Single(outcome.Dropped);
        Assert.Empty(outcome.Reapplied);
        Assert.DoesNotContain(nameof(IMachineState.ApplyOverlay), rig.Machine.Calls);
        Assert.Contains(PolicyEvents.ApplyFailed, rig.Codes);
        Assert.Null(rig.Saved!.Policy!.Overlay);
    }

    // ------------------------------------------------------------------ the clock

    [Fact]
    public void An_expired_lock_is_dropped_and_a_valid_one_is_reapplied()
    {
        var expired = new Rig(Document(WithLock(Now - 1, Now + Hour)));
        var valid = new Rig(Document(WithLock(Now + 600, Now + Hour)));

        var dropped = expired.Journal.Start(Now);
        var kept = valid.Journal.Start(Now);

        Assert.Empty(dropped.Reapplied);
        Assert.Single(dropped.Dropped);
        Assert.Null(expired.Machine.Shown);
        Assert.Contains(PolicyEvents.OverlayExpired, expired.Codes);
        Assert.Null(expired.Saved!.Policy!.Overlay);

        Assert.Single(kept.Reapplied);
        Assert.NotNull(valid.Machine.Shown);
        Assert.Equal("lock-1", valid.Machine.Shown!.SessionId);
        Assert.Contains(PolicyEvents.Reapplied, valid.Codes);
        Assert.Equal("lock-1", valid.Saved!.Policy!.Overlay!.SessionId);
    }

    [Fact]
    public void Until_zero_means_until_lifted_and_the_hard_limit_alone_decides()
    {
        var rig = new Rig(Document(WithLock(0, Now + 60)));

        Assert.Single(rig.Journal.Start(Now).Reapplied);
        Assert.Equal(Now + 60, rig.Journal.NextDeadline);

        var lifted = rig.Journal.Tick(Now + 59);
        Assert.Empty(lifted);
        Assert.NotNull(rig.Machine.Shown);

        lifted = rig.Journal.Tick(Now + 60);
        Assert.Single(lifted);
        Assert.Null(rig.Machine.Shown);
        Assert.Null(rig.Journal.NextDeadline);
        Assert.Contains(PolicyEvents.OverlayExpired, rig.Codes);
    }

    [Fact]
    public void Until_past_with_a_future_hard_limit_drops_and_the_reverse_drops_too()
    {
        var untilPast = new Rig(Document(WithLock(Now - 5, Now + Hour)));
        var limitPast = new Rig(Document(WithLock(Now + Hour, Now - 5)));

        Assert.Empty(untilPast.Journal.Start(Now).Reapplied);
        Assert.Empty(limitPast.Journal.Start(Now).Reapplied);
        Assert.Null(untilPast.Machine.Shown);
        Assert.Null(limitPast.Machine.Shown);
    }

    [Fact]
    public void The_stored_hard_limit_is_reclamped_on_every_reapply()
    {
        // A document with a hard limit thirty days out — hand-edited, or written by a build
        // with a longer ceiling — comes back at one hour from now.
        var thirtyDays = new Rig(Document(WithLock(0, Now + 30 * 24 * 3600)));
        Assert.Single(thirtyDays.Journal.Start(Now).Reapplied);
        Assert.Equal(Now + Hour, thirtyDays.Machine.Shown!.HardLimitUnix);
        Assert.Equal(Now + Hour, thirtyDays.Saved!.Policy!.Overlay!.HardLimitUnix);

        // The RTC moved back to 2001: the stored limit (2027) is far in the future relative to
        // the new "now", and must still become one hour from that now.
        var year2001 = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var clockBack = new Rig(Document(WithLock(0, Now + Hour)));
        Assert.Single(clockBack.Journal.Start(year2001).Reapplied);
        Assert.Equal(year2001 + Hour, clockBack.Machine.Shown!.HardLimitUnix);
        Assert.True(clockBack.Machine.Shown.HardLimitUnix <= year2001 + Hour);
    }

    // ------------------------------------------------------------------ failures

    [Fact]
    public void A_failed_refine_keeps_the_displaced_settings_and_the_machine_is_not_proven_open()
    {
        var policy = new ClassroomPolicy
        {
            Displaced = new DisplacedSettings
            {
                ProfileDefaultOutbound = { ["private"] = "block" },
                Dns = { new DisplacedDns { Interface = "Ethernet", Servers = { "10.0.0.1" } } },
                Applied = true,
            },
        };
        var rig = new Rig(Document(policy));
        rig.Machine.Answers[nameof(IMachineState.RefineRestore)] = () => MachineResult.Failed("netsh said no");

        var outcome = rig.Journal.Start(Now);

        Assert.False(outcome.MachineOpen);
        Assert.False(rig.Journal.MachineOpen);
        Assert.Contains(PolicyEvents.RestoreFailed, rig.Codes);
        Assert.Contains("netsh said no", rig.Events.Single(e => e.Code == PolicyEvents.RestoreFailed).Message, StringComparison.Ordinal);
        var kept = rig.Saved!.Policy!.Displaced!;
        Assert.Equal("block", kept.ProfileDefaultOutbound["private"]);
        Assert.Equal("10.0.0.1", kept.Dns[0].Servers[0]);

        // And a refine that succeeds clears them.
        var again = new Rig(Document(policy));
        Assert.True(again.Journal.Start(Now).MachineOpen);
        Assert.True(again.Saved!.Policy!.Displaced!.IsEmpty);
    }

    [Fact]
    public void A_refine_that_succeeds_but_a_read_back_that_fails_keeps_the_displaced_settings_too()
    {
        var policy = new ClassroomPolicy
        {
            Displaced = new DisplacedSettings { Dns = { new DisplacedDns { Interface = "Ethernet", Servers = { "10.0.0.1" } } }, Applied = true },
        };
        var rig = new Rig(Document(policy));
        rig.Machine.Answers[nameof(IMachineState.Confirm)] = () => MachineResult.Failed("still pointing at loopback");

        var outcome = rig.Journal.Start(Now);

        // The restore is not confirmed: the originals stay for the next pass, which confirms and clears them.
        Assert.False(outcome.MachineOpen);
        Assert.Equal("10.0.0.1", rig.Saved!.Policy!.Displaced!.Dns[0].Servers[0]);
        Assert.Equal("10.0.0.1", rig.Journal.Snapshot().Displaced!.Dns[0].Servers[0]);

        rig.Machine.Answers.Remove(nameof(IMachineState.Confirm));
        var next = new Rig(JsonStore.Serialize(rig.Saved, PolicyDocument.Migrations));
        Assert.True(next.Journal.Start(Now).MachineOpen);
        Assert.Contains(nameof(IMachineState.RefineRestore), next.Machine.Calls);
        Assert.True(next.Saved!.Policy!.Displaced!.IsEmpty);
    }

    [Fact]
    public void A_failed_lift_keeps_the_session_and_reports_the_machine_closed()
    {
        var rig = new Rig(Document(WithLock(Now + 10, Now + Hour)));
        Assert.True(rig.Journal.Start(Now).MachineOpen);
        rig.Machine.Answers[nameof(IMachineState.LiftOverlay)] = () => MachineResult.Failed("the helper is gone");

        var lifted = rig.Journal.Tick(Now + 10);

        Assert.Empty(lifted);
        Assert.False(rig.Journal.MachineOpen);
        Assert.NotNull(rig.Journal.Snapshot().Overlay);
        Assert.Contains(PolicyEvents.RestoreFailed, rig.Codes);

        // The next tick retries and succeeds.
        rig.Machine.Answers.Remove(nameof(IMachineState.LiftOverlay));
        Assert.Single(rig.Journal.Tick(Now + 11));
        Assert.Null(rig.Journal.Snapshot().Overlay);
    }

    [Fact]
    public void An_unlock_whose_lift_fails_keeps_the_lock_and_says_so()
    {
        var rig = new Rig();
        rig.Journal.Start(Now);
        rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "l1", Message = "x" }, Instance, Now);
        rig.Machine.Answers[nameof(IMachineState.LiftOverlay)] = () => MachineResult.Failed("no helper");

        var state = rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Unlock }, Instance, Now + 5);

        Assert.Equal(Overlay.Types.Kind.Lock, state.Kind);
        Assert.Equal("l1", state.SessionId);
        Assert.Contains("no helper", state.Problem, StringComparison.Ordinal);
        Assert.False(rig.Journal.MachineOpen);
    }

    [Fact]
    public void A_machine_that_throws_on_one_call_and_not_another_is_a_failure_of_that_call_only()
    {
        var rig = new Rig(Document(WithLock(0, Now + Hour)));
        rig.Machine.Answers[nameof(IMachineState.Confirm)] = () => throw new InvalidOperationException("boom");

        var outcome = rig.Journal.Start(Now);

        // Confirm threw: not open. RestoreDefaults and ApplyOverlay did not: the lock is back.
        Assert.False(outcome.MachineOpen);
        Assert.Single(outcome.Reapplied);
        Assert.NotNull(rig.Machine.Shown);
        var failure = rig.Events.Single(e => e.Code == PolicyEvents.RestoreFailed);
        Assert.Contains("boom", failure.Message, StringComparison.Ordinal);
        Assert.Contains(PolicyEvents.Reapplied, rig.Codes);

        // The throwing call was the only one that failed; a throwing apply drops only its session.
        var apply = new Rig(Document(WithLock(0, Now + Hour)));
        apply.Machine.Answers[nameof(IMachineState.ApplyOverlay)] = () => throw new InvalidOperationException("no desktop");
        var applied = apply.Journal.Start(Now);
        Assert.True(applied.MachineOpen);
        Assert.Empty(applied.Reapplied);
        Assert.Single(applied.Dropped);
        Assert.Contains(PolicyEvents.ApplyFailed, apply.Codes);
        Assert.Null(apply.Saved!.Policy!.Overlay);
    }

    [Fact]
    public void A_reader_that_throws_and_a_writer_that_throws_never_escape()
    {
        var rig = new Rig();
        rig.Reader = () => throw new UnauthorizedAccessException("locked");
        rig.Writer = _ => throw new IOException("disk full");
        rig.Journal.Reported += _ => throw new InvalidOperationException("a listener that misbehaves");

        var outcome = rig.Journal.Start(Now);

        Assert.True(outcome.MachineOpen);
        Assert.Contains(PolicyEvents.Unreadable, rig.Codes);
        Assert.Contains(PolicyEvents.SaveFailed, rig.Codes);
    }

    // ------------------------------------------------------------------ the document is saved whatever happened

    [Fact]
    public void The_document_is_saved_at_the_end_of_every_pass_and_carries_lab_and_schema()
    {
        var rig = new Rig();
        rig.Machine.Answers[nameof(IMachineState.RestoreDefaults)] = () => MachineResult.Failed("everything");

        rig.Journal.Start(Now);

        Assert.Equal(1, rig.Saves);
        Assert.Equal(Defaults.PolicySchemaVersion, rig.Saved!.SchemaVersion);
        Assert.Equal(LabId, rig.Saved.LabId);
        Assert.Equal(Now, rig.Saved.SavedAtUnix);
        Assert.NotNull(rig.Saved.Policy!.Displaced);
    }

    // ------------------------------------------------------------------ stop

    [Fact]
    public void Stop_restores_the_machine_and_does_not_clear_the_document()
    {
        var rig = new Rig(Document(WithLock(0, Now + Hour)));
        rig.Journal.Start(Now);
        var savesAfterStart = rig.Saves;
        rig.Machine.Calls.Clear();

        var outcome = rig.Journal.Stop(Now + 10);

        Assert.True(outcome.MachineOpen);
        Assert.Equal(nameof(IMachineState.RestoreDefaults), rig.Machine.Calls[0]);
        Assert.Contains(nameof(IMachineState.Confirm), rig.Machine.Calls);
        Assert.DoesNotContain("read", rig.Machine.Calls);
        Assert.Equal(savesAfterStart, rig.Saves);
        Assert.Equal("lock-1", rig.Saved!.Policy!.Overlay!.SessionId);

        // After a stop nothing is applied and nothing ticks.
        Assert.Contains(PolicyEvents.RefusedStopped, rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Lock }, Instance, Now + 11).Problem, StringComparison.Ordinal);
        Assert.Empty(rig.Journal.Tick(Now + 2 * Hour));

        // And the next start re-applies what the document kept.
        rig.Reader = () => Document(rig.Saved!.Policy!);
        Assert.Single(rig.Journal.Start(Now + 20).Reapplied);
    }

    // ------------------------------------------------------------------ SetOverlay

    [Fact]
    public void A_lock_is_clamped_to_one_hour_and_reported_as_held()
    {
        var rig = new Rig();
        rig.Journal.Start(Now);

        var state = rig.Journal.SetOverlay(new Overlay
        {
            Kind = Overlay.Types.Kind.Lock,
            SessionId = "lock-9",
            Message = "Дивіться на дошку",
            UntilUnix = Now + 30 * 24 * 3600,
            HardLimitUnix = Now + 30 * 24 * 3600,
        }, Instance, Now);

        Assert.Equal(Overlay.Types.Kind.Lock, state.Kind);
        Assert.Equal("lock-9", state.SessionId);
        Assert.Equal(Instance, state.SetByInstance);
        Assert.Equal(Now, state.SinceUnix);
        Assert.Equal(Now + 30 * 24 * 3600, state.UntilUnix);
        Assert.Equal(Now + Hour, state.HardLimitUnix);
        Assert.Equal(string.Empty, state.Problem);
        Assert.Equal("Дивіться на дошку", rig.Machine.Shown!.Message);
        Assert.Equal(Now + Hour, rig.Journal.NextDeadline);
        Assert.Equal("lock-9", rig.Saved!.Policy!.Overlay!.SessionId);

        // No hard limit proposed at all: the ceiling.
        var none = rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "lock-10" }, Instance, Now + 5);
        Assert.Equal(Now + 5 + Hour, none.HardLimitUnix);
        Assert.Equal(0, none.UntilUnix);
    }

    [Fact]
    public void A_lock_already_past_its_end_is_refused_without_touching_the_machine()
    {
        var rig = new Rig();
        rig.Journal.Start(Now);

        var state = rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "old", UntilUnix = Now - 1 }, Instance, Now);

        Assert.Equal(Overlay.Types.Kind.Unspecified, state.Kind);
        Assert.Equal(PolicyEvents.RefusedAlreadyEnded, state.Problem);
        Assert.DoesNotContain(nameof(IMachineState.ApplyOverlay), rig.Machine.Calls);
    }

    [Fact]
    public void An_unlock_lifts_and_an_unlock_with_nothing_up_is_harmless()
    {
        var rig = new Rig();
        rig.Journal.Start(Now);
        rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "l" }, Instance, Now);

        // Another console of the same lab may end it (owner decision 4).
        var state = rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Unlock }, "console-b", Now + 1);
        Assert.Equal(Overlay.Types.Kind.Unspecified, state.Kind);
        Assert.Equal(string.Empty, state.Problem);
        Assert.Null(rig.Machine.Shown);
        Assert.Null(rig.Saved!.Policy!.Overlay);

        var again = rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Unlock }, Instance, Now + 2);
        Assert.Equal(string.Empty, again.Problem);
        Assert.Equal(1, rig.Machine.Calls.Count(c => c == nameof(IMachineState.LiftOverlay)));
    }

    [Fact]
    public void A_failed_apply_is_reported_and_the_lock_is_not_held()
    {
        var rig = new Rig();
        rig.Journal.Start(Now);
        rig.Machine.Answers[nameof(IMachineState.ApplyOverlay)] = () => MachineResult.Failed("no interactive session");

        var state = rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "l" }, Instance, Now);

        Assert.Equal(Overlay.Types.Kind.Unspecified, state.Kind);
        Assert.Contains("no interactive session", state.Problem, StringComparison.Ordinal);
        Assert.Null(rig.Journal.Snapshot().Overlay);
        Assert.Contains(PolicyEvents.ApplyFailed, rig.Codes);
    }

    [Fact]
    public void A_broadcast_is_not_in_this_build_and_the_state_tells_the_truth_about_the_lock()
    {
        var rig = new Rig();
        rig.Journal.Start(Now);
        rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "l" }, Instance, Now);

        var state = rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.BroadcastStart }, Instance, Now + 1);

        Assert.Equal(Overlay.Types.Kind.Lock, state.Kind);
        Assert.NotEqual(string.Empty, state.Problem);
        Assert.NotNull(rig.Machine.Shown);
    }

    [Fact]
    public void The_helper_enforcement_and_a_reassert_feed_the_state_for_the_resend()
    {
        var rig = new Rig();
        rig.Journal.Start(Now);
        rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "l", UntilUnix = Now + 100 }, Instance, Now);
        rig.Machine.Calls.Clear();

        var noted = rig.Journal.NoteEnforcement(inputBlocked: false, "BlockInput fallback");
        Assert.False(noted.InputBlocked);
        Assert.Equal("BlockInput fallback", noted.Reason);
        Assert.Equal(Overlay.Types.Kind.Lock, noted.Kind);

        // A helper restarted at logon shows nothing: the service re-asserts.
        var reasserted = rig.Journal.ReassertOverlay(Now + 5);
        Assert.Equal(new[] { nameof(IMachineState.ApplyOverlay) }, rig.Machine.Calls);
        Assert.Equal("l", reasserted.SessionId);

        // Past its end, a re-assert shows nothing and leaves the lift to the tick.
        rig.Machine.Calls.Clear();
        rig.Journal.ReassertOverlay(Now + 100);
        Assert.Empty(rig.Machine.Calls);

        Assert.Equal(Overlay.Types.Kind.Lock, rig.Journal.OverlayStateOf(Now + 5).Kind);
    }

    [Fact]
    public void Exam_and_internet_sessions_that_this_build_cannot_enforce_are_dropped_not_kept()
    {
        var policy = new ClassroomPolicy
        {
            Exam = new ExamSession { SessionId = "e", SetByInstance = Instance, SinceUnix = Now - 60, HardLimitUnix = Now + 3600 },
            ExamInternet = new InternetSession { SessionId = "ei", Mode = InternetPolicy.Types.Mode.Blocked, HardLimitUnix = Now + 3600 },
            Internet = new InternetSession { SessionId = "i", Mode = InternetPolicy.Types.Mode.Whitelist, AllowedHosts = { "docs.oracle.com" }, HardLimitUnix = Now + 3600 },
            Displaced = new DisplacedSettings(),
        };
        var rig = new Rig(Document(policy));
        rig.Machine.Answers[nameof(IMachineState.ApplyExam)] = () => MachineResult.NotInThisBuild("exam mode");
        rig.Machine.Answers[nameof(IMachineState.ApplyInternet)] = () => MachineResult.NotInThisBuild("internet control");

        var outcome = rig.Journal.Start(Now);

        Assert.True(outcome.MachineOpen);
        Assert.Empty(outcome.Reapplied);
        Assert.Equal(3, outcome.Dropped.Count);
        Assert.True(rig.Journal.Snapshot().IsEmpty);
        Assert.Equal(2, rig.Events.Count(e => e.Code == PolicyEvents.ApplyFailed));
        Assert.Null(rig.Journal.NextDeadline);
    }

    [Fact]
    public void An_exam_internet_policy_is_clamped_to_the_exam_and_lifted_with_it()
    {
        var policy = new ClassroomPolicy
        {
            Exam = new ExamSession { SessionId = "e", SetByInstance = Instance, SinceUnix = Now - 60, UntilUnix = Now + 600, HardLimitUnix = Now + 1200 },
            ExamInternet = new InternetSession { SessionId = "ei", Mode = InternetPolicy.Types.Mode.Blocked, HardLimitUnix = Now + 30 * 3600 },
            Displaced = new DisplacedSettings(),
        };
        var rig = new Rig(Document(policy));

        var outcome = rig.Journal.Start(Now);

        Assert.Equal(2, outcome.Reapplied.Count);
        var held = rig.Journal.Snapshot();
        Assert.Equal(Now + 1200, held.ExamInternet!.HardLimitUnix);
        Assert.Equal(Now + 600, rig.Journal.NextDeadline);

        var lifted = rig.Journal.Tick(Now + 600);
        Assert.Equal(2, lifted.Count);
        Assert.Contains(nameof(IMachineState.LiftInternet), rig.Machine.Calls);
        Assert.Contains(nameof(IMachineState.LiftExam), rig.Machine.Calls);
        Assert.True(rig.Journal.Snapshot().IsEmpty);
    }
}

/// <summary>The ownership gate (D-69): who may set or lift a policy on this PC.</summary>
public sealed class OwnershipGateTests
{
    [Fact]
    public void A_bound_unrevoked_console_passes_and_everything_else_is_refused()
    {
        using var lab = TestLab.Create();
        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        var revocations = new RevocationSet();

        Assert.Null(OwnershipGate.Refusal("inst-a", "inst-a", "00AABB", revocations));
        Assert.Null(OwnershipGate.Refusal("inst-a", "INST-A", null, revocations));

        // Fails closed: no validated instance, no authority — where LeaveIfConsoleRevoked does nothing.
        Assert.Equal(PolicyEvents.RefusedUnbound, OwnershipGate.Refusal(null, null, "00AABB", revocations));
        Assert.Equal(PolicyEvents.RefusedUnbound, OwnershipGate.Refusal("inst-a", null, "00AABB", revocations));
        Assert.Equal(PolicyEvents.RefusedUnbound, OwnershipGate.Refusal("inst-a", "inst-b", "00AABB", revocations));

        Assert.True(revocations.TryAdd(pinned, RevocationSet.Create(lab, "00AABB", "stolen laptop", DateTimeOffset.UtcNow)));
        Assert.Equal(PolicyEvents.RefusedRevoked, OwnershipGate.Refusal("inst-a", "inst-a", "00AABB", revocations));
        Assert.Null(OwnershipGate.Refusal("inst-a", "inst-a", "00AABC", revocations));

        Assert.True(revocations.TryAdd(pinned, RevocationSet.Create(lab, LabCertificates.InstanceSerial("inst-a"), "withdrawn", DateTimeOffset.UtcNow)));
        Assert.Equal(PolicyEvents.RefusedWithdrawn, OwnershipGate.Refusal("inst-a", "inst-a", "00AABC", revocations));
        Assert.Equal(PolicyEvents.RefusedWithdrawn, OwnershipGate.Refusal("INST-A", "inst-a", null, revocations));
    }
}

/// <summary>The bounded named mutex every pass holds (D-69 item 4), owned by a thread of its own so any thread may release it.</summary>
public sealed class PolicyMutexTests
{
    [Fact]
    public void A_second_holder_waits_out_the_bound_and_gets_nothing_until_the_first_lets_go()
    {
        var name = Defaults.PolicyMutexName + "." + Guid.NewGuid().ToString("n");
        using (var first = PolicyMutex.TryAcquire(TimeSpan.Zero, name))
        {
            Assert.Equal(PolicyMutexOutcome.Acquired, first.Outcome);
            Assert.True(first.Acquired);

            using var busy = PolicyMutex.TryAcquire(TimeSpan.FromMilliseconds(100), name);
            Assert.Equal(PolicyMutexOutcome.Busy, busy.Outcome);
            Assert.False(busy.Acquired);
        }

        using var second = PolicyMutex.TryAcquire(TimeSpan.FromSeconds(5), name);
        Assert.True(second.Acquired);
    }

    [Fact]
    public async Task A_lease_disposed_on_another_thread_still_releases_the_mutex()
    {
        // The async OnStart/OnStop shape: taken on one pool thread, disposed on another.
        var name = Defaults.PolicyMutexName + "." + Guid.NewGuid().ToString("n");
        var lease = await Task.Run(() => PolicyMutex.TryAcquire(TimeSpan.Zero, name));
        Assert.True(lease.Acquired);
        await Task.Yield();
        var disposer = new Thread(lease.Dispose);
        disposer.Start();
        disposer.Join();

        using var next = PolicyMutex.TryAcquire(TimeSpan.Zero, name);
        Assert.True(next.Acquired);
        next.Dispose();
        next.Dispose();
    }

    [Fact]
    public void An_abandoned_mutex_is_acquired_not_refused()
    {
        // The previous holder died mid-pass: a thread takes the raw mutex and ends without releasing.
        var name = Defaults.PolicyMutexName + "." + Guid.NewGuid().ToString("n");
        Mutex? raw = null;
        var holder = new Thread(() =>
        {
            raw = new Mutex(false, name);
            Assert.True(raw.WaitOne(TimeSpan.Zero));
        });
        holder.Start();
        holder.Join();

        using var lease = PolicyMutex.TryAcquire(TimeSpan.FromSeconds(5), name);
        Assert.Equal(PolicyMutexOutcome.Acquired, lease.Outcome);
        raw!.Dispose();
    }

    [Fact]
    public void A_mutex_that_cannot_be_created_is_reported_not_thrown()
    {
        // A name no platform accepts: far past every limit.
        using var lease = PolicyMutex.TryAcquire(TimeSpan.Zero, Defaults.PolicyMutexName + "." + new string('x', 5000));
        Assert.Equal(PolicyMutexOutcome.Unavailable, lease.Outcome);
        Assert.NotEmpty(lease.Problem);
        Assert.False(lease.Acquired);
    }
}
