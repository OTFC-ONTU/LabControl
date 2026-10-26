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

    /// <summary>
    /// When the machine last booted as this trial knew it, Unix seconds; 0 in a record from before
    /// D-83. A newer boot seen by the new version is a reboot inside the probation.
    /// </summary>
    public long BootUnix { get; set; }

    /// <summary>How many times a reboot has moved <see cref="Deadline"/> on (D-83), at most <see cref="Defaults.UpdateProbationRebootExtensions"/>.</summary>
    public int Extensions { get; set; }
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
            Deadline = deadline, Phase = UpdateTrialPhase.OnProbation, BootUnix = CurrentBootUnix() });
        return true;
    });

    /// <summary>When this machine booted, Unix seconds, from its uptime.</summary>
    public static long CurrentBootUnix() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Environment.TickCount64 / 1000;

    /// <summary>
    /// D-83: the new version, starting after a reboot inside its probation, gets a fresh probation
    /// — the teacher's reboot is not a failed release (three PCs rolled back in the lab,
    /// 2026-10-20, rebooted 10 s too late to fit ten continuous minutes before the deadline).
    /// Returns the deadline to move the external task to, or <c>null</c> when nothing changes: no
    /// trial on probation for <paramref name="running"/>, no reboot since the trial last knew
    /// (a boot within <see cref="Defaults.UpdateProbationBootTolerance"/> is the same boot), or
    /// <see cref="Defaults.UpdateProbationRebootExtensions"/> already used. A record from before
    /// D-83 has no boot: it is stamped with this one and not extended. The deadline only ever
    /// moves later. The caller moves the task <b>first</b> and then calls <see cref="Extend"/>,
    /// so a crash in between leaves a task that still rolls back — later, never not at all.
    /// </summary>
    public DateTimeOffset? ExtensionFor(string running, long bootUnix, DateTimeOffset now) => Locked(() =>
    {
        var state = ReadCore();
        if (state is null || state.Phase != UpdateTrialPhase.OnProbation || state.Version != running)
            return (DateTimeOffset?)null;
        if (state.BootUnix == 0)
        {
            state.BootUnix = bootUnix;
            Save(state);
            return null;
        }

        if (bootUnix - state.BootUnix <= Defaults.UpdateProbationBootTolerance.TotalSeconds
            || state.Extensions >= Defaults.UpdateProbationRebootExtensions)
            return null;
        var extended = SchedulerDeadline(now + Defaults.UpdateProbationWindow);
        return extended > state.Deadline ? extended : null;
    });

    /// <summary>Records a moved deadline and the boot that earned it (D-83); refuses one earlier than the current deadline.</summary>
    public bool Extend(string jobId, DateTimeOffset deadline, long bootUnix) => Locked(() =>
    {
        var state = ReadCore();
        if (state is null || state.JobId != jobId || state.Phase != UpdateTrialPhase.OnProbation || deadline <= state.Deadline
            || state.Extensions >= Defaults.UpdateProbationRebootExtensions)
            return false;
        state.Deadline = deadline;
        state.BootUnix = bootUnix;
        state.Extensions++;
        Save(state);
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
