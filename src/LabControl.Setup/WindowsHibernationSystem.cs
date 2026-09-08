using System.Diagnostics;
using System.Security;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.Win32;
using Windows.Win32;

namespace LabControl.Setup;

/// <summary>Use powercfg to create/remove Windows' hibernation file. Never delete it
/// directly or guess previous defaults. Full/reduced and size metadata must survive
/// the operation exactly; unexpected native side effects remain journal conflicts.</summary>
internal sealed class WindowsHibernationSystem : IHibernationSystem
{
    public HibernationState Read() => Guard(() =>
    {
        var first = ReadOnce();
        if (first != ReadOnce()) throw new IOException();
        return first;
    });

    public void Write(HibernationState expected, HibernationState value) => Guard(() =>
    {
        if (expected.FileType != value.FileType || expected.SizePercent != value.SizePercent
            || (value.FilePresent && (value.Enabled != 1 || value.NativeFileType is not (1 or 2)))
            || (!value.FilePresent && (value.Enabled != 0 || value.NativeFileType != 0)))
            throw new IOException();
        if (Read() != expected) throw new IOException();
        using var process = new Process { StartInfo = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, Defaults.PowerConfigurationExecutableName))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("/hibernate");
        process.StartInfo.ArgumentList.Add(value.FilePresent ? "on" : "off");
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)Defaults.SetupPowerCommandTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException();
        }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0 || Read() != value) throw new IOException();
        return true;
    });

    private static HibernationState ReadOnce()
    {
        if (!PInvoke.GetPwrCapabilities(out var capabilities)) throw new IOException();
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(Defaults.HibernationRegistryKey) ?? throw new IOException();
        var state = new HibernationState(capabilities.HiberFilePresent, capabilities.HiberFileType,
            Dword(Defaults.HibernateEnabledValue), Dword(Defaults.HibernationFileTypeValue), Dword(Defaults.HibernationFileSizeValue));
        if (state.Enabled is > 1 || (state.FilePresent && state.Enabled != 1)
            || (!state.FilePresent && state.Enabled == 1)) throw new IOException();
        return state;

        uint? Dword(string name)
        {
            if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            if (key.GetValueKind(name) != RegistryValueKind.DWord) throw new IOException();
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not int number
                || key.GetValueKind(name) != RegistryValueKind.DWord) throw new IOException();
            return unchecked((uint)number);
        }
    }

    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new IOException("The hibernation file or its type/size settings could not be read or changed safely; preserve the journal for review.");
        }
    }
}
