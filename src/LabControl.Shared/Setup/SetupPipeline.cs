namespace LabControl.Shared.Setup;

public enum SetupStepStatus { Needed, AlreadyDone, Skipped, Conflict }
public sealed record SetupCheck(SetupStepStatus Status, string Detail = "");
public sealed record SetupStepResult(string Name, SetupStepStatus Status, bool Applied, string Detail);

public enum SetupDiagnosticCode
{
    DefenderReadFailed,
    DefenderReadUnstable,
    DefenderProviderDataInvalid,
    DefenderConcurrentChange,
    DefenderProviderCallFailed,
    DefenderProviderRejected,
    DefenderReadBackTimeout,
    HibernationFileProbeFailed,
    HibernationCapabilitiesUnavailable,
    HibernationRegistryReadFailed,
    HibernationRegistryValueUnsupported,
    HibernationStateInconsistent,
    HibernationReadUnstable,
    HibernationTransitionRejected,
    HibernationConcurrentChange,
    HibernationCommandFailed,
    HibernationPostStateInvalid,
}

/// <summary>A fixed, value-free diagnostic that is safe to place in setup.log.</summary>
public sealed class SetupDiagnosticException(SetupDiagnosticCode code, long? status = null) : IOException
{
    public SetupDiagnosticCode Code { get; } = code;

    /// <summary>A numeric status the native provider returned (an HRESULT or WMI return
    /// value), never configuration data. Printed as hex so the failing PC can be diagnosed
    /// from setup.log alone.</summary>
    public long? Status { get; } = status;

    public string Describe() => Status is { } status
        ? $"{Code}, status 0x{unchecked((uint)status):X8}" : Code.ToString();
}

public interface ISetupStep
{
    string Name { get; }
    SetupCheck Check();
    void Apply();
}

/// <summary>Stop at the first failure or conflict. Dry-run calls read-only checks only;
/// successful native writes must pass a fresh check before they are reported complete.</summary>
public static class SetupPipeline
{
    public static IReadOnlyList<SetupStepResult> Run(IEnumerable<ISetupStep> steps, bool dryRun,
        Action<SetupStepResult>? report = null)
    {
        var results = new List<SetupStepResult>();
        foreach (var step in steps)
        {
            SetupStepResult result;
            try
            {
                var check = step.Check();
                var applied = false;
                if (check.Status == SetupStepStatus.Needed && !dryRun)
                {
                    step.Apply();
                    applied = true;
                    check = step.Check();
                    if (check.Status == SetupStepStatus.Needed)
                        check = new(SetupStepStatus.Conflict, "Verification did not confirm the change.");
                }
                result = new(step.Name, check.Status, applied, check.Detail);
            }
            catch (SetupDiagnosticException error)
            {
                result = new(step.Name, SetupStepStatus.Conflict, false,
                    $"The step failed ({error.Describe()}). No further steps were run; repair can retry from recorded history.");
            }
            catch (Exception)
            {
                // Native exceptions can contain account/sign-in values. Steps return
                // deliberate safe diagnostics in Check; do not echo arbitrary exceptions.
                result = new(step.Name, SetupStepStatus.Conflict, false, "The step failed. No further steps were run; repair can retry from recorded history.");
            }
            results.Add(result);
            report?.Invoke(result);
            if (result.Status == SetupStepStatus.Conflict) break;
        }
        return results;
    }
}

public sealed record SetupArguments(bool DryRun, bool Uninstall, bool Rekey, bool RemoveStudent,
    bool ConfirmRemoveStudent, bool? CreateStudent, bool NoReboot, int? Number, string? Payload)
{
    public static SetupArguments Parse(string[] args)
    {
        bool dry = false, uninstall = false, rekey = false, remove = false, confirm = false, noReboot = false;
        bool? create = null;
        int? number = null;
        string? payload = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].ToLowerInvariant();
            if (!seen.Add(arg)) throw new ArgumentException("A setup option was supplied more than once.");
            switch (arg)
            {
                case "--dry-run": dry = true; break;
                case "--uninstall": uninstall = true; break;
                case "--rekey": rekey = true; break;
                case "--remove-student": remove = true; break;
                case "--confirm-remove-student": confirm = true; break;
                case "--no-reboot": noReboot = true; break;
                case "--create-student":
                case "--no-student":
                    if (create is not null) throw new ArgumentException("Choose only one student-account mode.");
                    create = arg == "--create-student";
                    break;
                case "--number":
                    if (++i >= args.Length || !int.TryParse(args[i], out var value) || value < 1 || value > Defaults.MaxStudentPcs)
                        throw new ArgumentException($"PC number must be between 1 and {Defaults.MaxStudentPcs}.");
                    number = value;
                    break;
                case "--payload":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i])) throw new ArgumentException("A payload directory is required.");
                    payload = Path.GetFullPath(args[i]);
                    break;
                default: throw new ArgumentException("An unknown setup option was supplied.");
            }
        }
        if (uninstall && rekey || (remove || confirm) && !uninstall || confirm && !remove
            || (uninstall || rekey) && (create is not null || number is not null))
            throw new ArgumentException("These setup options cannot be combined.");
        return new(dry, uninstall, rekey, remove, confirm, create, noReboot, number, payload);
    }
}
