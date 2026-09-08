using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.System.Services;

namespace LabControl.Setup;

/// <summary>The installer owns only a newly-created service. Existing service settings
/// are never adopted merely because its name matches.</summary>
internal sealed class WindowsSetupService(string installationId, string? installVersion = null) : ISetupSetting, ISetupOwnedCreation
{
    private byte[]? _last;
    private bool _read;
    public string Id => "machine.agent-service";

    private string Description => Defaults.ServiceDescription + " [" + installationId + "]";
    public byte[] Desired => Encoding.UTF8.GetBytes(Description);

    public byte[]? Read()
    {
        _read = false;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Defaults.SetupServiceRegistryKey);
        if (key is null) { _last = null; _read = true; return null; }
        if (key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string image
            || key.GetValue("ObjectName") is not string account || account != "LocalSystem"
            || key.GetValue("Start") is not int start || start != 2
            || key.GetValue("Type") is not int type || type != 16)
            throw new IOException("The existing service configuration is not managed by this installer.");
        // DisplayName is committed by CreateService together with the binary path.
        // Description is cosmetic and may be absent after a crash during creation.
        if (key.GetValue("DisplayName") is not string displayName || displayName != Description)
            throw new IOException("The service does not carry this installation's ownership identity.");
        var version = InstallLayout.Default.ReadCurrent();
        if (version is null || image != "\"" + InstallLayout.Default.AgentExecutable(version) + "\"")
            throw new IOException("The service target differs from the recorded active version.");
        _last = Desired;
        _read = true;
        return _last.ToArray();
    }

    public bool ConfirmsOwnedCreation(byte[] expected) => expected.AsSpan().SequenceEqual(Desired)
        && Read() is { } current && current.AsSpan().SequenceEqual(expected);

    public void Write(byte[]? value)
    {
        if (!_read) throw new InvalidOperationException("Read the service before changing it.");
        var expected = _last?.ToArray();
        var current = Read();
        if (!(expected is null ? current is null : current is not null && current.AsSpan().SequenceEqual(expected)))
            throw new IOException("The service configuration changed.");
        if (value is null)
        {
            Stop();
            Run("delete", Defaults.ServiceName);
            return;
        }
        if (!value.AsSpan().SequenceEqual(Desired) || installVersion is null || !InstallLayout.IsValidVersion(installVersion))
            throw new InvalidDataException("The service ownership or target is invalid.");
        if (current is not null) throw new IOException("An existing service cannot be replaced without an owned upgrade operation.");
        var command = "\"" + InstallLayout.Default.AgentExecutable(installVersion) + "\"";
        InstallLayout.Default.WriteCurrent(installVersion);
        Run("create", Defaults.ServiceName, "binPath=", command, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", Description);
        Run("description", Defaults.ServiceName, Description);
    }

    public static bool Exists()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Defaults.SetupServiceRegistryKey);
        return key is not null;
    }

    public static bool IsRunning()
    {
        using var manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT);
        if (manager.IsInvalid) throw NativeError();
        using var service = PInvoke.OpenService(manager, Defaults.ServiceName, PInvoke.SERVICE_QUERY_STATUS);
        if (service.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 1060) return false;
            throw NativeError();
        }
        if (!PInvoke.QueryServiceStatus(service, out var status)) throw NativeError();
        return status.dwCurrentState == SERVICE_STATUS_CURRENT_STATE.SERVICE_RUNNING;
    }

    public static void Start()
    {
        if (!IsRunning()) Run("start", Defaults.ServiceName);
        var timeout = DateTime.UtcNow + Defaults.ServiceStopTimeout;
        while (!IsRunning())
        {
            if (DateTime.UtcNow >= timeout) throw new IOException("The service did not start in time.");
            Thread.Sleep(250);
        }
    }

    public static void Stop()
    {
        using var manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT);
        if (manager.IsInvalid) throw NativeError();
        using var service = PInvoke.OpenService(manager, Defaults.ServiceName, PInvoke.SERVICE_QUERY_STATUS | PInvoke.SERVICE_STOP);
        if (service.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 1060) return;
            throw NativeError();
        }
        if (!PInvoke.QueryServiceStatus(service, out var status)) throw NativeError();
        if (status.dwCurrentState == SERVICE_STATUS_CURRENT_STATE.SERVICE_STOPPED) return;
        if (status.dwCurrentState != SERVICE_STATUS_CURRENT_STATE.SERVICE_STOP_PENDING
            && !PInvoke.ControlService(service, PInvoke.SERVICE_CONTROL_STOP, out _)) throw NativeError();
        var timeout = DateTime.UtcNow + Defaults.ServiceStopTimeout;
        while (true)
        {
            if (!PInvoke.QueryServiceStatus(service, out status)) throw NativeError();
            if (status.dwCurrentState == SERVICE_STATUS_CURRENT_STATE.SERVICE_STOPPED) return;
            if (DateTime.UtcNow >= timeout) throw new IOException("The service did not stop in time.");
            Thread.Sleep(250);
        }
    }

    public static void ConfigureRecovery()
    {
        var delay = ((int)Defaults.ServiceRestartDelay.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Run("failure", Defaults.ServiceName, "reset=", "86400", "command=", "", "actions=", "restart/" + delay);
        Run("failureflag", Defaults.ServiceName, "1");
    }

    private static void Run(params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)Defaults.ServiceStopTimeout.TotalMilliseconds))
        {
            process.Kill(true);
            throw new IOException("Service configuration timed out.");
        }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new IOException($"Service configuration failed ({process.ExitCode}).");
    }

    private static IOException NativeError() => new("The Windows service manager operation failed.");
}
