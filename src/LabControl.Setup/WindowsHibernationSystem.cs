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
        if (first != ReadOnce()) throw Diagnostic(SetupDiagnosticCode.HibernationReadUnstable);
        return first;
    });

    public void Write(HibernationState expected, HibernationState value) => Guard(() =>
    {
        var disabling = expected.FilePresent && !value.FilePresent;
        var restoring = !expected.FilePresent && value.FilePresent;
        if ((!disabling && !restoring)
            || disabling && (expected.FileType != value.FileType || expected.SizePercent != value.SizePercent
                || value.Enabled != 0 || value.NativeFileType != 0)
            || restoring && (!HibernationSetting.IsDisabled(expected)
                || value.Enabled is not (null or 1) || value.NativeFileType is not (1 or 2)
                || value.SizePercent is > 100))
            throw Diagnostic(SetupDiagnosticCode.HibernationTransitionRejected);
        if (Read() != expected) throw Diagnostic(SetupDiagnosticCode.HibernationConcurrentChange);
        RunPowerConfiguration(value.FilePresent ? ["/hibernate", "on"] : ["/hibernate", "off"]);
        var actual = Read();
        if (restoring && actual != value)
        {
            RestoreMetadata(actual, value);
            actual = Read();
        }
        if (value.FilePresent ? actual != value : !HibernationSetting.IsDisabled(actual))
            throw Diagnostic(SetupDiagnosticCode.HibernationPostStateInvalid);
        return true;
    });

    private static void RestoreMetadata(HibernationState expected, HibernationState value)
    {
        if (!expected.FilePresent || expected.Enabled != 1 || value.NativeFileType is not (1 or 2))
            throw Diagnostic(SetupDiagnosticCode.HibernationTransitionRejected);
        if (value.NativeFileType == 1)
        {
            RunPowerConfiguration(["/hibernate", "/size", "0"]);
            RunPowerConfiguration(["/hibernate", "/type", "reduced"]);
        }
        else
        {
            RunPowerConfiguration(["/hibernate", "/type", "full"]);
            RunPowerConfiguration(["/hibernate", "/size", (value.SizePercent ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            RunPowerConfiguration(["/hibernate", "/type", "full"]);
        }
        var afterCommands = ReadOnce();
        if (!afterCommands.FilePresent || afterCommands.Enabled != 1)
            throw Diagnostic(SetupDiagnosticCode.HibernationPostStateInvalid);

        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(Defaults.HibernationRegistryKey, writable: true)
            ?? throw Diagnostic(SetupDiagnosticCode.HibernationRegistryReadFailed);
        if (ReadOnce() != afterCommands) throw Diagnostic(SetupDiagnosticCode.HibernationConcurrentChange);
        WriteDword(key, Defaults.HibernationFileTypeValue, value.FileType);
        WriteDword(key, Defaults.HibernationFileSizeValue, value.SizePercent);
        // powercfg /hibernate on writes HibernateEnabled=1; an originally absent value is
        // removed again so the PC returns to its OS-default representation exactly.
        WriteDword(key, Defaults.HibernateEnabledValue, value.Enabled);
        key.Flush();
    }

    private static void WriteDword(RegistryKey key, string name, uint? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, unchecked((int)value.Value), RegistryValueKind.DWord);
    }

    private static void RunPowerConfiguration(IReadOnlyList<string> arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, Defaults.PowerConfigurationExecutableName))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)Defaults.SetupPowerCommandTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw Diagnostic(SetupDiagnosticCode.HibernationCommandFailed);
        }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw Diagnostic(SetupDiagnosticCode.HibernationCommandFailed);
    }

    private static HibernationState ReadOnce()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(Defaults.HibernationRegistryKey)
                ?? throw Diagnostic(SetupDiagnosticCode.HibernationRegistryReadFailed);
            var filePresent = HibernationFilePresent();
            byte nativeFileType = 0;
            if (filePresent)
            {
                if (!PInvoke.GetPwrCapabilities(out var capabilities))
                    throw Diagnostic(SetupDiagnosticCode.HibernationCapabilitiesUnavailable);
                // A protected/residual filesystem object can remain visible even when
                // Windows reports that no active hibernation file exists. The path probe
                // decides whether native capabilities are needed; the successful native
                // result remains authoritative for active state and restorable type.
                filePresent = capabilities.HiberFilePresent;
                if (filePresent) nativeFileType = capabilities.HiberFileType;
            }
            var state = new HibernationState(filePresent, nativeFileType,
                Dword(Defaults.HibernateEnabledValue), Dword(Defaults.HibernationFileTypeValue), Dword(Defaults.HibernationFileSizeValue));
            // An absent HibernateEnabled value means the OS default (HibernateEnabledDefault):
            // Windows keeps an active hibernation file without ever writing the value on a
            // factory-fresh PC, and normalizes a completed "off" to the same absent value.
            // Only an explicit value that contradicts the native file state is inconsistent.
            if (state.Enabled is > 1 || (state.FilePresent && state.Enabled == 0)
                || (!state.FilePresent && state.Enabled == 1))
                throw Diagnostic(SetupDiagnosticCode.HibernationStateInconsistent);
            return state;

            uint? Dword(string name)
            {
                if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
                if (key.GetValueKind(name) != RegistryValueKind.DWord)
                    throw Diagnostic(SetupDiagnosticCode.HibernationRegistryValueUnsupported);
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not int number
                    || key.GetValueKind(name) != RegistryValueKind.DWord)
                    throw Diagnostic(SetupDiagnosticCode.HibernationRegistryValueUnsupported);
                return unchecked((uint)number);
            }
        }
        catch (SetupDiagnosticException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw Diagnostic(SetupDiagnosticCode.HibernationRegistryReadFailed);
        }
    }

    private static bool HibernationFilePresent()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrEmpty(root)) throw Diagnostic(SetupDiagnosticCode.HibernationFileProbeFailed);
        try
        {
            _ = File.GetAttributes(Path.Combine(root, Defaults.HibernationFileName));
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw Diagnostic(SetupDiagnosticCode.HibernationFileProbeFailed);
        }
    }

    private static SetupDiagnosticException Diagnostic(SetupDiagnosticCode code) => new(code);

    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (SetupDiagnosticException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new IOException("The hibernation file or its type/size settings could not be read or changed safely; preserve the journal for review.");
        }
    }
}
