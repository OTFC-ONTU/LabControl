using System.Diagnostics;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Protection;

/// <summary>
/// Linux keyring through <c>secret-tool</c> (libsecret). The secret is handed over on
/// standard input and read back from standard output, never as a command-line argument.
/// Linux is the "nice to have" console platform, so an absent <c>secret-tool</c> is not an
/// error — <see cref="SecretProtector"/> falls back to <see cref="FileSecretProtector"/>.
/// </summary>
public sealed class SecretToolProtector : ISecretProtector
{
    public const string ProtectorName = ProtectorNames.LibSecret;

    private const string Executable = "secret-tool";
    private const string ServiceName = "LabControl";

    public string Name => ProtectorName;

    public bool IsAvailable =>
        OperatingSystem.IsLinux() && TryRun(["--version"], input: null, out _);

    public ProtectedSecret Protect(string reference, ReadOnlySpan<byte> secret)
    {
        var encoded = Convert.ToBase64String(secret);
        if (!TryRun(["store", "--label=LabControl", "service", ServiceName, "account", reference], encoded, out var error))
        {
            throw new InvalidOperationException($"secret-tool could not store '{reference}': {error}");
        }

        return new ProtectedSecret
        {
            Protector = ProtectorName,
            Reference = reference,
        };
    }

    public bool TryUnprotect(ProtectedSecret secret, out byte[] plaintext)
    {
        plaintext = [];
        if (!TryRun(["lookup", "service", ServiceName, "account", secret.Reference], input: null, out var output))
        {
            return false;
        }

        try
        {
            plaintext = Convert.FromBase64String(output.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Forget(ProtectedSecret secret) =>
        TryRun(["clear", "service", ServiceName, "account", secret.Reference], input: null, out _);

    private static bool TryRun(string[] arguments, string? input, out string output)
    {
        output = string.Empty;

        var startInfo = new ProcessStartInfo(Executable)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);

            output = process.ExitCode == 0 ? standardOutput : standardError;
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
