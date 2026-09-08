using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class UpdateTrialTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-trial-tests", Guid.NewGuid().ToString("n"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private UpdateTrial Trial => new(_directory);

    private void Begin() => Trial.Begin("job", "0.2.0", "0.1.0", _now.AddMinutes(12));

    [Fact]
    public void Acceptance_requires_correct_version_continuous_link_and_unexpired_deadline()
    {
        Begin();
        Assert.False(Trial.Accept("0.1.0", _now.AddMinutes(10), TimeSpan.FromMinutes(10)));
        Assert.False(Trial.Accept("0.2.0", _now.AddMinutes(10), TimeSpan.FromMinutes(9)));
        Assert.False(Trial.Accept("0.2.0", _now.AddMinutes(12), TimeSpan.FromMinutes(12)));
        Assert.True(Trial.Accept("0.2.0", _now.AddMinutes(11), TimeSpan.FromMinutes(10)));
        Assert.False(Trial.RollBack(_now.AddHours(1), true, _ => throw new Exception()));
        Assert.Equal(UpdateTrialPhase.Stable, Trial.Read()!.Phase);
    }

    [Fact]
    public void Deadline_rolls_back_without_any_new_agent_participation()
    {
        Begin();
        Assert.False(Trial.RollBack(_now, false, _ => throw new Exception()));
        var calls = 0;
        Assert.True(Trial.RollBack(_now.AddMinutes(12), false, state =>
        {
            Assert.Equal("0.1.0", state.Previous);
            calls++;
        }));
        Assert.False(Trial.RollBack(_now.AddHours(1), true, _ => calls++));
        Assert.Equal(1, calls);
        Assert.Equal(UpdateTrialPhase.RolledBack, Trial.Read()!.Phase);
    }

    [Fact]
    public void Crash_recovery_does_not_wait_for_deadline()
    {
        Begin();
        Assert.True(Trial.RollBack(_now, true, _ => { }));
    }

    [Fact]
    public void Interrupted_rollback_is_replayed_and_cannot_be_accepted()
    {
        Begin();
        Assert.Throws<IOException>(() => Trial.RollBack(_now, true, _ => throw new IOException()));
        Assert.Equal(UpdateTrialPhase.RollbackPending, Trial.Read()!.Phase);
        Assert.False(Trial.Accept("0.2.0", _now.AddMinutes(11), TimeSpan.FromMinutes(10)));
        Assert.True(Trial.RollBack(_now, false, _ => { }));
    }

    [Fact]
    public void Second_update_cannot_replace_pending_trial()
    {
        Begin();
        Assert.Throws<InvalidOperationException>(() => Trial.Begin("next", "0.3.0", "0.2.0", _now.AddHours(1)));
        Assert.Equal("job", Trial.Read()!.JobId);
    }

    [Fact]
    public void Callback_holds_cross_process_lock()
    {
        Begin();
        Trial.RollBack(_now, true, _ => Assert.Throws<IOException>(() => Trial.Read()));
    }

    [Fact]
    public void Old_recovery_task_cannot_roll_back_a_later_trial()
    {
        Begin();
        Assert.True(Trial.Accept("0.2.0", _now.AddMinutes(11), TimeSpan.FromMinutes(10)));
        Trial.FinalizeTerminal("job", _ => { });
        Trial.Begin("new-job", "0.3.0", "0.2.0", _now.AddMinutes(30));
        Assert.False(Trial.RollBack(_now.AddHours(1), true, _ => throw new Exception(), "job"));
        Assert.False(Trial.RollBack(_now.AddHours(1), false, _ => throw new Exception(), "job"));
        Assert.Equal(UpdateTrialPhase.OnProbation, Trial.Read()!.Phase);
        Assert.True(Trial.RollBack(_now.AddHours(1), false, _ => { }, "new-job"));
    }

    [Fact]
    public void Mismatched_job_cannot_resume_interrupted_rollback()
    {
        Begin();
        Assert.Throws<IOException>(() => Trial.RollBack(_now, true, _ => throw new IOException(), "job"));
        Assert.False(Trial.RollBack(_now, true, _ => throw new Exception(), "old-job"));
        Assert.Equal(UpdateTrialPhase.RollbackPending, Trial.Read()!.Phase);
        Assert.True(Trial.RollBack(_now, false, _ => { }, "job"));
    }

    [Fact]
    public void Failed_terminal_cleanup_blocks_next_update_and_retries_under_lock()
    {
        Begin();
        Assert.True(Trial.Accept("0.2.0", _now.AddMinutes(11), TimeSpan.FromMinutes(10)));
        Assert.Throws<IOException>(() => Trial.FinalizeTerminal("job", _ => throw new IOException()));
        Assert.False(Trial.Read()!.RecoveryFinalized);
        Assert.Throws<InvalidOperationException>(() => Trial.Begin("next", "0.3.0", "0.2.0", _now.AddHours(1)));
        Assert.False(Trial.FinalizeTerminal("wrong", _ => throw new Exception()));
        Assert.True(Trial.FinalizeTerminal("job", _ =>
            Assert.Throws<IOException>(() => Trial.Begin("next", "0.3.0", "0.2.0", _now.AddHours(1)))));
        Assert.False(Trial.FinalizeTerminal("job", _ => throw new Exception()));
        Trial.Begin("next", "0.3.0", "0.2.0", _now.AddHours(1));
        Assert.False(Trial.FinalizeTerminal("job", _ => throw new Exception()));
    }

    [Fact]
    public void Removal_refuses_pending_update_and_releases_failed_lease()
    {
        Begin();
        Assert.Throws<InvalidOperationException>(() => Trial.AcquireRemovalLease(out _));
        Assert.Throws<IOException>(() => Trial.RollBack(_now, true, _ => throw new IOException()));
        Assert.Throws<InvalidOperationException>(() => Trial.AcquireRemovalLease(out _));
        Assert.True(Trial.RollBack(_now, true, _ => { }));
    }

    [Fact]
    public void Removal_lease_excludes_new_update_and_callback_until_disposed()
    {
        Begin();
        Trial.RollBack(_now, true, _ => { });
        Trial.FinalizeTerminal("job", _ => { });
        using (Trial.AcquireRemovalLease(out var state))
        {
            Assert.Equal(UpdateTrialPhase.RolledBack, state!.Phase);
            Assert.Throws<IOException>(() => Trial.Begin("next", "0.3.0", "0.1.0", _now.AddHours(1)));
            Assert.Throws<IOException>(() => Trial.RollBack(_now, true, _ => throw new Exception()));
        }
        Trial.Begin("next", "0.3.0", "0.1.0", _now.AddHours(1));
    }

    [Fact]
    public void Removal_before_first_update_also_excludes_trial_creation()
    {
        using (Trial.AcquireRemovalLease(out var state))
        {
            Assert.Null(state);
            Assert.Throws<IOException>(Begin);
        }
        Begin();
    }

    [Fact]
    public void Scheduler_deadline_is_rounded_up_and_not_earlier_than_durable_probation()
    {
        var calculated = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var deadline = UpdateTrial.SchedulerDeadline(calculated);
        Assert.True(deadline > calculated);
        Assert.Equal(0, deadline.Ticks % TimeSpan.TicksPerSecond);
        Trial.Begin("job", "0.2.0", "0.1.0", deadline);
        Assert.False(Trial.RollBack(deadline.AddTicks(-1), false, _ => throw new Exception()));
        Assert.True(Trial.RollBack(deadline, false, _ => { }));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
