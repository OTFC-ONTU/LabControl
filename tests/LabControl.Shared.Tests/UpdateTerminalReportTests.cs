using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class UpdateTerminalReportTests
{
    [Fact]
    public void Locked_startup_read_is_retried_after_real_durable_rollback_without_reconnect()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var trial = new UpdateTrial(directory);
            var report = new UpdateTerminalReport();
            trial.Begin("job", "0.2.0", "0.1.0", DateTimeOffset.UtcNow.AddMinutes(12));
            Assert.True(trial.RollBack(DateTimeOffset.UtcNow, true, _ =>
            {
                Assert.Throws<IOException>(() => report.Read(trial.Read, "0.1.0"));
            }));
            var update = report.Read(trial.Read, "0.1.0");
            Assert.Equal(UpdateTerminalReport.RolledBackCode, update!.Code);
            Assert.Equal("0.2.0", update.Message);
            Assert.Null(report.Read(trial.Read, "0.1.0"));
            report.Reset();
            Assert.NotNull(report.Read(trial.Read, "0.1.0"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Pending_restore_and_wrong_running_binary_never_report_terminal_success()
    {
        var report = new UpdateTerminalReport();
        var state = new UpdateTrialDocument { JobId = "job", Version = "0.2.0", Previous = "0.1.0", Phase = UpdateTrialPhase.RollbackPending };
        Assert.Null(report.Read(() => state, "0.1.0"));
        state.Phase = UpdateTrialPhase.RolledBack;
        Assert.Null(report.Read(() => state, "0.2.0"));
        Assert.NotNull(report.Read(() => state, "0.1.0"));
        state.JobId = "another-job";
        Assert.NotNull(report.Read(() => state, "0.1.0"));
    }

    [Fact]
    public void Existing_stable_terminal_record_refreshes_without_new_acceptance()
    {
        var report = new UpdateTerminalReport();
        var state = new UpdateTrialDocument { JobId = "job", Version = "0.2.0", Previous = "0.1.0", Phase = UpdateTrialPhase.Stable };
        Assert.Equal(UpdateTerminalReport.StableCode, report.Read(() => state, "0.2.0")!.Code);
        Assert.Null(report.Read(() => state, "0.2.0"));
        report.Reset();
        Assert.Equal(UpdateTerminalReport.StableCode, report.Read(() => state, "0.2.0")!.Code);
    }
}
