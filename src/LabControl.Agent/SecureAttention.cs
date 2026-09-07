using System.Security;
using LabControl.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Windows.Win32;

namespace LabControl.Agent;

/// <summary>
/// Ctrl+Alt+Del on the student's PC, raised by the service (PROTOCOL "Input", D-36). Only a
/// service or Winlogon may call <c>SendSAS</c>, and only when the
/// <c>SoftwareSASGeneration</c> policy allows services — so the value is checked, and set to
/// "services" if nothing allows them yet, right before the call. The helper cannot do this:
/// it is SYSTEM but not a service, and <c>SendInput</c> cannot fake the sequence by design.
/// </summary>
internal static class SecureAttention
{
    /// <summary><c>null</c> when the sequence was sent; otherwise why not, in plain words.</summary>
    public static string? Send(ILogger log)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(Defaults.SoftwareSasPolicyKey, writable: true);
            if (key is null)
            {
                return $@"cannot open HKLM\{Defaults.SoftwareSasPolicyKey}";
            }

            var current = key.GetValue(Defaults.SoftwareSasPolicyValue) as int?;
            if (current is not (1 or 3))
            {
                key.SetValue(Defaults.SoftwareSasPolicyValue, 1, RegistryValueKind.DWord);
                log.LogInformation("enabled the Software SAS policy for services ({Value} was {Was})", Defaults.SoftwareSasPolicyValue, current?.ToString() ?? "unset");
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return $"cannot enable the Software SAS policy: {ex.Message}";
        }

        try
        {
            PInvoke.SendSAS(false);
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return $"SendSAS is not available on this Windows: {ex.Message}";
        }
    }
}
