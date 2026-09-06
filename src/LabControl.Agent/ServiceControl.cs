using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LabControl.Shared;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Services;

namespace LabControl.Agent;

/// <summary>
/// The two things a push needs from the service manager (ARCHITECTURE §7.2, D-33): point
/// the <c>LabControl</c> service at another <c>agent.exe</c> — <c>ChangeServiceConfig</c>
/// on the binary path, nothing else touched — and stop-then-start it. The second runs in a
/// separate process (<c>agent.exe --restart-service</c>), because a service cannot outlive
/// its own stop to issue the start. Every call answers with <c>null</c> or the failure in
/// plain language; nothing here throws.
/// </summary>
internal static class ServiceControl
{
    /// <summary>Points the service at <paramref name="executable"/>; takes effect at the next start.</summary>
    public static string? SetBinaryPath(string executable)
    {
        try
        {
            using var manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT);
            if (manager.IsInvalid)
            {
                return $"OpenSCManager failed: {LastError()}";
            }

            using var service = PInvoke.OpenService(manager, Defaults.ServiceName, PInvoke.SERVICE_CHANGE_CONFIG);
            if (service.IsInvalid)
            {
                return $"OpenService({Defaults.ServiceName}) failed: {LastError()}";
            }

            // The overload without a tag id passes NULL for it, so the load-order tag is left
            // alone along with everything else but the binary path.
            var binaryPath = "\"" + executable + "\"";
            if (!PInvoke.ChangeServiceConfig(
                    service,
                    (ENUM_SERVICE_TYPE)PInvoke.SERVICE_NO_CHANGE,
                    (SERVICE_START_TYPE)PInvoke.SERVICE_NO_CHANGE,
                    (SERVICE_ERROR)PInvoke.SERVICE_NO_CHANGE,
                    binaryPath,
                    null,
                    null,
                    null,
                    null,
                    null))
            {
                return $"ChangeServiceConfig failed: {LastError()}";
            }

            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or InvalidOperationException)
        {
            return $"could not repoint the service: {ex.Message}";
        }
    }

    /// <summary>
    /// Starts <paramref name="executable"/> with <see cref="Defaults.RestartServiceSwitch"/>
    /// as a detached process that will stop this service and start it again. Returns the
    /// failure when the process could not be started at all.
    /// </summary>
    public static string? SpawnRestart(string executable)
    {
        try
        {
            var info = new ProcessStartInfo(executable, Defaults.RestartServiceSwitch)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.SystemDirectory,
            };

            using var process = Process.Start(info);
            return process is null ? "the restart process did not start" : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return $"could not start {executable} {Defaults.RestartServiceSwitch}: {ex.Message}";
        }
    }

    /// <summary>
    /// <c>agent.exe --restart-service</c>: stop the service, wait for <i>stopped</i>, start
    /// it. Run by the outgoing agent's own executable, which is known to work on this PC.
    /// </summary>
    public static string? Restart(Action<string> log)
    {
        try
        {
            using var manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT);
            if (manager.IsInvalid)
            {
                return $"OpenSCManager failed: {LastError()}";
            }

            using var service = PInvoke.OpenService(manager, Defaults.ServiceName, PInvoke.SERVICE_STOP | PInvoke.SERVICE_START | PInvoke.SERVICE_QUERY_STATUS);
            if (service.IsInvalid)
            {
                return $"OpenService({Defaults.ServiceName}) failed: {LastError()}";
            }

            var state = QueryState(service);
            if (state is null)
            {
                return $"QueryServiceStatus failed: {LastError()}";
            }

            if (state != SERVICE_STATUS_CURRENT_STATE.SERVICE_STOPPED && state != SERVICE_STATUS_CURRENT_STATE.SERVICE_STOP_PENDING)
            {
                log($"stopping the {Defaults.ServiceName} service");
                if (!PInvoke.ControlService(service, PInvoke.SERVICE_CONTROL_STOP, out _))
                {
                    return $"ControlService(stop) failed: {LastError()}";
                }
            }

            var deadline = DateTime.UtcNow + Defaults.ServiceStopTimeout;
            while (QueryState(service) is { } current && current != SERVICE_STATUS_CURRENT_STATE.SERVICE_STOPPED)
            {
                if (DateTime.UtcNow > deadline)
                {
                    return $"the service did not stop within {Defaults.ServiceStopTimeout.TotalSeconds:0} s (state {current})";
                }

                Thread.Sleep(500);
            }

            log($"starting the {Defaults.ServiceName} service");
            if (!PInvoke.StartService(service, null))
            {
                return $"StartService failed: {LastError()}";
            }

            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or InvalidOperationException)
        {
            return $"could not restart the service: {ex.Message}";
        }
    }

    private static SERVICE_STATUS_CURRENT_STATE? QueryState(SafeHandle service) =>
        PInvoke.QueryServiceStatus(service, out var status) ? status.dwCurrentState : null;

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;
}
