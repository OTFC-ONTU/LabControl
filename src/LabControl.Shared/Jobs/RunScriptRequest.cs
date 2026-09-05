using System.Globalization;
using LabControl.Shared.Files;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Jobs;

public enum ScriptShell
{
    /// <summary><c>powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File</c> (Windows PowerShell 5.1).</summary>
    PowerShell = 0,

    /// <summary><c>cmd.exe /d /c</c>.</summary>
    Cmd = 1,
}

public enum ScriptRunAs
{
    /// <summary>As the service itself: SYSTEM, session 0, no desktop.</summary>
    System = 0,

    /// <summary>In the interactive session, as the account logged on there (the student).</summary>
    User = 1,
}

/// <summary>
/// The arguments of a <c>run_script</c> job (PROTOCOL, <c>Job</c>; D-31), parsed and
/// validated once so the console builds them and the agent reads them through the same
/// names. The script itself travels through <c>PullFile</c> under <see cref="Reference"/>.
/// </summary>
public sealed record RunScriptRequest(
    string Reference,
    string Sha256,
    ScriptShell Shell,
    ScriptRunAs RunAs,
    TimeSpan Timeout,
    string Name)
{
    public const string ReferenceKey = "ref";
    public const string Sha256Key = "sha256";
    public const string ShellKey = "shell";
    public const string RunAsKey = "as";
    public const string TimeoutKey = "timeout_s";

    /// <summary>A name for the log and the file on the PC; optional, never trusted as a path.</summary>
    public const string NameKey = "name";

    public const string PowerShellValue = "powershell";
    public const string CmdValue = "cmd";
    public const string SystemValue = "system";
    public const string UserValue = "user";

    /// <summary>The extension the interpreter expects; the file on the PC is named with it.</summary>
    public string Extension => Shell == ScriptShell.Cmd ? ".cmd" : ".ps1";

    /// <summary>The wire form: every value spelled the one way the agent parses.</summary>
    public Dictionary<string, string> ToArgs() => new(StringComparer.Ordinal)
    {
        [ReferenceKey] = Reference,
        [Sha256Key] = Sha256,
        [ShellKey] = Shell == ScriptShell.Cmd ? CmdValue : PowerShellValue,
        [RunAsKey] = RunAs == ScriptRunAs.User ? UserValue : SystemValue,
        [TimeoutKey] = ((int)Timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
        [NameKey] = Name,
    };

    /// <summary>Reads a job's arguments; a job this build cannot run is refused with the reason.</summary>
    public static bool TryParse(Job job, out RunScriptRequest request, out string error)
    {
        request = null!;
        error = string.Empty;

        if (job.Kind != Job.Types.Kind.RunScript)
        {
            error = $"job {job.Id} is a {job.Kind}, not a run_script";
            return false;
        }

        if (!job.Args.TryGetValue(ReferenceKey, out var reference) || string.IsNullOrWhiteSpace(reference))
        {
            error = $"the job has no '{ReferenceKey}' — nothing to pull";
            return false;
        }

        if (!job.Args.TryGetValue(Sha256Key, out var sha256) || !FileHash.LooksLikeSha256(sha256))
        {
            error = $"the job has no usable '{Sha256Key}'; the script cannot be verified";
            return false;
        }

        var shell = ScriptShell.PowerShell;
        if (job.Args.TryGetValue(ShellKey, out var shellText) && shellText.Length > 0)
        {
            switch (shellText.ToLowerInvariant())
            {
                case PowerShellValue: shell = ScriptShell.PowerShell; break;
                case CmdValue: shell = ScriptShell.Cmd; break;
                default:
                    error = $"'{shellText}' is not a shell this agent knows ({PowerShellValue} | {CmdValue})";
                    return false;
            }
        }

        var runAs = ScriptRunAs.System;
        if (job.Args.TryGetValue(RunAsKey, out var runAsText) && runAsText.Length > 0)
        {
            switch (runAsText.ToLowerInvariant())
            {
                case SystemValue: runAs = ScriptRunAs.System; break;
                case UserValue: runAs = ScriptRunAs.User; break;
                default:
                    error = $"'{runAsText}' is not a way to run this agent knows ({SystemValue} | {UserValue})";
                    return false;
            }
        }

        var timeout = Defaults.ScriptDefaultTimeout;
        if (job.Args.TryGetValue(TimeoutKey, out var timeoutText) && timeoutText.Length > 0)
        {
            if (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                error = $"'{timeoutText}' is not a timeout in seconds";
                return false;
            }

            timeout = TimeSpan.FromSeconds(seconds);
        }

        var name = job.Args.TryGetValue(NameKey, out var nameText) ? SafeName(nameText) : string.Empty;
        if (name.Length == 0)
        {
            name = "script";
        }

        request = new RunScriptRequest(reference.Trim(), sha256.ToLowerInvariant(), shell, runAs, timeout, name);
        return true;
    }

    /// <summary>A file name from whatever the console sent: letters, digits, dash, underscore, dot; nothing that walks directories.</summary>
    public static string SafeName(string text)
    {
        var chars = text.Trim()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            .Take(64)
            .ToArray();
        var name = new string(chars).Trim('.');
        return name;
    }
}
