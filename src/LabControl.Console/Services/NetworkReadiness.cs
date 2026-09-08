using System.ComponentModel;
using System.Diagnostics;
using LabControl.Shared;
using LabControl.Shared.Packaging;

namespace LabControl.Console.Services;

/// <summary>Where the console stands with the local firewall (D-59 item 2).</summary>
public enum NetworkReadinessState
{
    /// <summary>Not Windows, so there is nothing to ask for and no banner is ever shown.</summary>
    NotApplicable = 0,

    /// <summary>Nothing has been checked yet.</summary>
    Unknown = 1,

    /// <summary>Both ports are reachable — by this installer's rules or by somebody else's.</summary>
    Allowed = 2,

    /// <summary>At least one port is blocked; the banner offers <i>Allow…</i>.</summary>
    Missing = 3,

    /// <summary>The teacher declined the Windows prompt, or the helper is not there. The banner turns into the two commands.</summary>
    Denied = 4,

    /// <summary>The check or the change failed. Same diagnostic; the console keeps running.</summary>
    Failed = 5,
}

/// <summary>
/// What the console is allowed to know about the firewall. Read-only inspection and one
/// elevated request; everything else belongs to <c>LabControl.ConsoleSetup.exe</c>.
/// Faked in the tests, which is why the console never calls the COM layer directly.
/// </summary>
public interface IConsoleFirewallProbe
{
    /// <summary>False on macOS and Linux: <see cref="NetworkReadiness"/> then does nothing at all.</summary>
    bool IsSupported { get; }

    /// <summary>Reads the rules. Throws when Windows will not answer.</summary>
    ConsoleFirewallStatus Check();

    /// <summary>The installer beside this console, or <c>null</c> when it was not installed from one.</summary>
    string? FindHelper();

    /// <summary>Runs the helper elevated and waits. False when the teacher declined or it failed.</summary>
    bool RequestElevated(string helper);
}

/// <summary>
/// The <i>LAN access</i> banner's state machine (D-59 item 2). On Windows, activating a lab
/// checks whether inbound TCP <see cref="Defaults.ConsolePort"/> and UDP
/// <see cref="Defaults.BeaconPort"/> are allowed on the Private and Domain profiles; when
/// they are not, the banner offers <i>Allow…</i>, which runs the installer's
/// <c>--firewall</c> elevated and re-checks. A refused elevation is not an error the teacher
/// can do nothing about: the banner then shows the exact two <c>netsh</c> lines.
///
/// It never touches Defender, never adds an exclusion and never disables anything — those
/// belong to the student PCs and to <c>D-15</c>, not here.
/// </summary>
public sealed class NetworkReadiness(IConsoleFirewallProbe probe)
{
    private readonly object _gate = new();

    /// <summary>The real machine: the COM firewall on Windows, nothing anywhere else.</summary>
    public static NetworkReadiness ForThisMachine() => new(new WindowsConsoleFirewallProbe());

    public NetworkReadinessState State { get; private set; } =
        probe.IsSupported ? NetworkReadinessState.Unknown : NetworkReadinessState.NotApplicable;

    /// <summary>The exact commands a teacher can run by hand; only filled in for <see cref="NetworkReadinessState.Denied"/> and <see cref="NetworkReadinessState.Failed"/>.</summary>
    public IReadOnlyList<string> Diagnostic { get; private set; } = [];

    /// <summary>Raised whenever <see cref="State"/> changed. On whatever thread did the work; the UI posts.</summary>
    public event Action? Changed;

    /// <summary>True while the teacher should be told something; <see cref="NetworkReadinessState.Allowed"/> is silent.</summary>
    public bool HasBanner => State is NetworkReadinessState.Missing or NetworkReadinessState.Denied or NetworkReadinessState.Failed;

    /// <summary>Reads the rules once. Safe to call again at any time.</summary>
    public void Check()
    {
        if (!probe.IsSupported)
        {
            Move(NetworkReadinessState.NotApplicable, []);
            return;
        }

        try
        {
            Move(probe.Check().Allowed ? NetworkReadinessState.Allowed : NetworkReadinessState.Missing, []);
        }
        catch (Exception error) when (error is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            Move(NetworkReadinessState.Failed, ConsoleFirewallRules.NetshLines());
        }
    }

    /// <summary>
    /// The banner's <i>Allow…</i>: run the installer elevated for its one privileged step, then
    /// look again. Whatever happens, the console keeps working — an unreachable console is a
    /// classroom problem, not a crash.
    /// </summary>
    public void Allow()
    {
        if (!probe.IsSupported)
        {
            Move(NetworkReadinessState.NotApplicable, []);
            return;
        }

        string? helper;
        try
        {
            helper = probe.FindHelper();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            helper = null;
        }

        if (helper is null)
        {
            Move(NetworkReadinessState.Denied, ConsoleFirewallRules.NetshLines());
            return;
        }

        bool granted;
        try
        {
            granted = probe.RequestElevated(helper);
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException)
        {
            Move(NetworkReadinessState.Failed, ConsoleFirewallRules.NetshLines());
            return;
        }

        if (!granted)
        {
            Move(NetworkReadinessState.Denied, ConsoleFirewallRules.NetshLines());
            return;
        }

        // The helper said it succeeded; only the rules themselves prove it.
        Check();
        if (State != NetworkReadinessState.Allowed)
        {
            Move(NetworkReadinessState.Failed, ConsoleFirewallRules.NetshLines());
        }
    }

    /// <summary>The COM read and the elevation prompt both block; keep them off the UI thread.</summary>
    public Task CheckAsync() => Task.Run(Check);

    public Task AllowAsync() => Task.Run(Allow);

    private void Move(NetworkReadinessState state, IReadOnlyList<string> diagnostic)
    {
        lock (_gate)
        {
            if (State == state && Diagnostic.Count == diagnostic.Count)
            {
                return;
            }

            State = state;
            Diagnostic = diagnostic;
        }

        Changed?.Invoke();
    }
}

/// <summary>The real probe: read-only COM on Windows, and nothing at all anywhere else.</summary>
public sealed class WindowsConsoleFirewallProbe : IConsoleFirewallProbe
{
    public bool IsSupported => WindowsConsoleFirewall.IsSupported;

    public ConsoleFirewallStatus Check() => WindowsConsoleFirewall.Check();

    /// <summary>
    /// <c>LabControl.ConsoleSetup.exe</c> sits beside the installed console (D-59 item 1).
    /// A console started some other way — a development run, a copied publish directory —
    /// simply has no helper, and the banner falls back to the <c>netsh</c> lines.
    /// </summary>
    public string? FindHelper()
    {
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) })
        {
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, Defaults.ConsoleSetupExecutableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public bool RequestElevated(string helper)
    {
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = true,
            Verb = "runas",
            ArgumentList = { Defaults.ConsoleSetupFirewallSwitch, Defaults.ConsoleSetupElevatedSwitch },
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // The teacher answered No to the Windows prompt, or policy forbids elevation.
            return false;
        }
    }
}
