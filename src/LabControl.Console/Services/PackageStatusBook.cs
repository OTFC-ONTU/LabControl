using LabControl.Shared.Lab;
using LabControl.Shared.Packages;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>Where one PC stands with one package, as the <i>Packages</i> tab shows it (D-89).</summary>
public enum PcPackageState
{
    /// <summary>Nothing heard yet, or only about an older version.</summary>
    Unknown,
    Installed,
    RebootRequired,
    Missing,
    Failed,
    Busy,
}

/// <summary>
/// The console's memory of what each PC said about each package (M7 portion 4, D-89): fed by
/// every finished <c>install_package</c> job, kept in <c>package-status.json</c>, read by
/// <i>Install on all missing</i> and the tab. An answer about another installer (an older
/// version of the entry) counts as nothing known about this one.
/// </summary>
public sealed class PackageStatusBook
{
    /// <summary>The outcome recorded when the console stopped waiting for a PC's answer.</summary>
    public const string ConsoleTimedOut = "console.timed_out";

    private readonly LabStore _store;
    private readonly JobQueue _jobs;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly PackageStatusDocument _document;

    public PackageStatusBook(LabStore store, string labId, JobQueue jobs, ILogger log)
    {
        _store = store;
        _jobs = jobs;
        _log = log;
        PackageStatusDocument? document = null;
        try
        {
            document = store.LoadPackageStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            // A cache: lost, it is rebuilt by the next check.
            log.LogWarning("package status unreadable, starting empty: {Message}", ex.Message);
        }

        _document = document ?? new PackageStatusDocument { LabId = labId };
        jobs.Updated += OnJobUpdated;
    }

    /// <summary>Raised after a PC's answer changed what is known; on whichever thread the result arrived.</summary>
    public event Action? Changed;

    public void Detach() => _jobs.Updated -= OnJobUpdated;

    private void OnJobUpdated(JobRecord job)
    {
        if (job.Kind != Job.Types.Kind.InstallPackage)
        {
            return;
        }

        if (!job.IsFinished)
        {
            Changed?.Invoke();
            return;
        }

        var outcome = job.Outcome.Length > 0 ? job.Outcome
            : job.State == JobState.TimedOut ? ConsoleTimedOut
            : job.State == JobState.Failed ? PackageOutcomes.Failed
            : string.Empty;
        if (outcome.Length == 0 || !job.Args.TryGetValue("package", out var package) || !job.Args.TryGetValue("sha256", out var sha))
        {
            Changed?.Invoke();
            return;
        }

        lock (_gate)
        {
            var entry = _document.Packages.FirstOrDefault(p => p.Package == package);
            if (entry is null)
            {
                entry = new PackageStatusEntry { Package = package };
                _document.Packages.Add(entry);
            }

            entry.Pcs.RemoveAll(p => string.Equals(p.AgentId, job.AgentId, StringComparison.OrdinalIgnoreCase));
            entry.Pcs.Add(new PcPackageStatus { AgentId = job.AgentId, Outcome = outcome, Sha256 = sha, Message = job.Message, AtUnix = job.CompletedAtUnix });
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>The last answer from <paramref name="agentId"/> about <paramref name="package"/>, whatever installer it was about.</summary>
    public PcPackageStatus? Last(string package, string agentId)
    {
        lock (_gate)
        {
            return _document.Packages.FirstOrDefault(p => p.Package == package)?.Pcs
                .FirstOrDefault(p => string.Equals(p.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Where <paramref name="agentId"/> stands with this entry's current installer.</summary>
    public PcPackageState StateOf(PackageEntry entry, string agentId)
    {
        if (_jobs.All().Any(j => j.Kind == Job.Types.Kind.InstallPackage && !j.IsFinished
                && string.Equals(j.AgentId, agentId, StringComparison.OrdinalIgnoreCase)
                && j.Args.GetValueOrDefault("package") == entry.Id))
        {
            return PcPackageState.Busy;
        }

        var last = Last(entry.Id, agentId);
        if (last is null) return PcPackageState.Unknown;
        var sameInstaller = string.Equals(last.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase);
        return last.Outcome switch
        {
            PackageOutcomes.Installed when sameInstaller => PcPackageState.Installed,
            PackageOutcomes.RebootRequired when sameInstaller => PcPackageState.RebootRequired,
            // A check or a skipped install found the program by its rule; which version it is the rule cannot say.
            PackageOutcomes.Present => PcPackageState.Installed,
            PackageOutcomes.Absent => PcPackageState.Missing,
            PackageOutcomes.Installed or PackageOutcomes.RebootRequired => PcPackageState.Unknown,
            _ when sameInstaller => PcPackageState.Failed,
            _ => PcPackageState.Unknown,
        };
    }

    /// <summary>True once any PC installed this entry's current installer — the test install has been done.</summary>
    public bool EverInstalled(PackageEntry entry)
    {
        lock (_gate)
        {
            return _document.Packages.FirstOrDefault(p => p.Package == entry.Id)?.Pcs.Any(p =>
                p.Outcome is PackageOutcomes.Installed or PackageOutcomes.RebootRequired
                && string.Equals(p.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)) == true;
        }
    }

    /// <summary>Forgets a deleted entry.</summary>
    public void Forget(string package)
    {
        lock (_gate)
        {
            if (_document.Packages.RemoveAll(p => p.Package == package) > 0)
            {
                Save();
            }
        }

        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            _store.SavePackageStatus(_document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("package status not saved: {Message}", ex.Message);
        }
    }
}
