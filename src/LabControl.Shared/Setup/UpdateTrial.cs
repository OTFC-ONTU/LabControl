using System.Text;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Setup;

public enum UpdateTrialPhase { OnProbation, Stable, RollbackPending, RolledBack }

public sealed class UpdateTrialDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(1);
    public int SchemaVersion { get; set; } = 1;
    public string JobId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Previous { get; set; } = string.Empty;
    public DateTimeOffset Deadline { get; set; }
    public UpdateTrialPhase Phase { get; set; }
    public bool RecoveryFinalized { get; set; }
}

/// <summary>Durable, process-locked probation decisions shared with the known-good
/// executable's external rollback command. Callbacks must be idempotent: a crash can
/// replay them, but cannot turn an unfinished rollback into an accepted update.</summary>
public sealed class UpdateTrial(string directory)
{
    private string StatePath => Path.Combine(directory, Defaults.UpdateTrialFileName);

    public static DateTimeOffset SchedulerDeadline(DateTimeOffset calculated) =>
        DateTimeOffset.FromUnixTimeSeconds(checked(calculated.ToUnixTimeSeconds() + 1));

    public UpdateTrialDocument? Read() => Locked(ReadCore);

    /// <summary>Hold through service removal so no new update or rollback can race
    /// uninstall. An unfinished update must settle before removal is authorized.</summary>
    public IDisposable AcquireRemovalLease(out UpdateTrialDocument? state)
    {
        Directory.CreateDirectory(directory);
        var lease = new FileStream(Path.Combine(directory, Defaults.UpdateTrialLockFileName),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            state = ReadCore();
            if (state?.Phase is UpdateTrialPhase.OnProbation or UpdateTrialPhase.RollbackPending)
                throw new InvalidOperationException("Wait for the update to be accepted or rolled back before uninstalling.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public static string RecoveryTaskName(UpdateTrialDocument state) => Defaults.UpdateRollbackTaskName + " " +
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(state.JobId)))[..16];

    public void Begin(string jobId, string version, string previous, DateTimeOffset deadline) => Locked(() =>
    {
        if (string.IsNullOrWhiteSpace(jobId) || !InstallLayout.IsValidVersion(version)
            || !InstallLayout.IsValidVersion(previous) || version == previous)
            throw new InvalidDataException("Invalid update trial identity.");
        var current = ReadCore();
        if (current?.Phase is UpdateTrialPhase.OnProbation or UpdateTrialPhase.RollbackPending)
            throw new InvalidOperationException("An update is already awaiting acceptance or rollback.");
        if (current is { RecoveryFinalized: false })
            throw new InvalidOperationException("The preceding update's recovery configuration must be finalized first.");
        Save(new UpdateTrialDocument { JobId = jobId, Version = version, Previous = previous,
            Deadline = deadline, Phase = UpdateTrialPhase.OnProbation });
        return true;
    });

    public bool Accept(string running, DateTimeOffset now, TimeSpan continuousLink) => Locked(() =>
    {
        var state = ReadCore();
        if (state is null || state.Phase != UpdateTrialPhase.OnProbation || state.Version != running
            || now >= state.Deadline || continuousLink < Defaults.UpdateProbation) return false;
        state.Phase = UpdateTrialPhase.Stable;
        Save(state);
        return true;
    });

    /// <summary>Reset native recovery and clean markers under the same lock as Begin.
    /// Incomplete cleanup retries safely and cannot disable a later trial's recovery.</summary>
    public bool FinalizeTerminal(string expectedJobId, Action<UpdateTrialDocument> finalize) => Locked(() =>
    {
        var state = ReadCore();
        if (state is null || state.JobId != expectedJobId || state.RecoveryFinalized
            || state.Phase is not (UpdateTrialPhase.Stable or UpdateTrialPhase.RolledBack)) return false;
        finalize(state);
        state.RecoveryFinalized = true;
        Save(state);
        return true;
    });

    /// <summary>Run by the previous binary after a deadline or service crash threshold.
    /// The rollback intent reaches disk before service/marker changes.</summary>
    public bool RollBack(DateTimeOffset now, bool crashThreshold, Action<UpdateTrialDocument> restore, string? expectedJobId = null) => Locked(() =>
    {
        var state = ReadCore();
        if (expectedJobId is not null && state?.JobId != expectedJobId) return false;
        if (state is null || state.Phase is UpdateTrialPhase.Stable or UpdateTrialPhase.RolledBack) return false;
        if (state.Phase == UpdateTrialPhase.OnProbation && !crashThreshold && now < state.Deadline) return false;
        state.Phase = UpdateTrialPhase.RollbackPending;
        Save(state);
        restore(state);
        state.Phase = UpdateTrialPhase.RolledBack;
        Save(state);
        return true;
    });

    private UpdateTrialDocument? ReadCore()
    {
        UpdateTrialDocument state;
        try { state = JsonStore.Load<UpdateTrialDocument>(StatePath, UpdateTrialDocument.Migrations); }
        catch (FileNotFoundException) { return null; }
        if (string.IsNullOrWhiteSpace(state.JobId) || !InstallLayout.IsValidVersion(state.Version)
            || !InstallLayout.IsValidVersion(state.Previous) || state.Version == state.Previous
            || !Enum.IsDefined(state.Phase) || state.Deadline == default)
            throw new InvalidDataException("The update trial record is invalid.");
        return state;
    }

    private void Save(UpdateTrialDocument state)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonStore.Serialize(state, UpdateTrialDocument.Migrations));
        var temporary = StatePath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, StatePath, overwrite: true);
    }

    private T Locked<T>(Func<T> action)
    {
        Directory.CreateDirectory(directory);
        using var lease = new FileStream(Path.Combine(directory, Defaults.UpdateTrialLockFileName),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return action();
    }
}
