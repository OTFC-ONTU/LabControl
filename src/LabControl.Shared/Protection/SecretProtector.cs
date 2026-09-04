using LabControl.Shared.Persistence;

namespace LabControl.Shared.Protection;

/// <summary>
/// Chooses how this machine protects the console instance's private key: the OS keystore
/// when there is one, the encrypted-file fallback otherwise (ARCHITECTURE §3.2). A document
/// is always reopened with the protector that <i>wrote</i> it, so moving between them —
/// installing a keyring, say — never orphans a key.
/// </summary>
public static class SecretProtector
{
    /// <summary>
    /// Forces a protector by name, for development and for a machine whose keystore
    /// misbehaves. Values are the <c>ProtectorName</c> constants.
    /// </summary>
    public const string OverrideVariable = "LABCONTROL_SECRET_PROTECTOR";

    /// <summary>The protector a new secret should be written with on this machine.</summary>
    public static ISecretProtector ForCurrentPlatform()
    {
        var requested = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var forced = Create(requested.Trim());
            if (forced is not null && forced.IsAvailable)
            {
                return forced;
            }
        }

        foreach (var candidate in Candidates())
        {
            if (candidate.IsAvailable)
            {
                return candidate;
            }
        }

        return new FileSecretProtector();
    }

    /// <summary>The protector that wrote an existing secret, whatever this machine prefers today.</summary>
    public static ISecretProtector For(ProtectedSecret secret) =>
        Create(secret.Protector)
        ?? throw new InvalidDataException(
            $"'{secret.Protector}' is not a protector this build knows; the instance key cannot be opened.");

    private static IEnumerable<ISecretProtector> Candidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return new AppleKeychainSecretProtector();
        }

        if (OperatingSystem.IsWindows())
        {
            yield return new DpapiSecretProtector();
        }

        if (OperatingSystem.IsLinux())
        {
            yield return new SecretToolProtector();
        }
    }

    private static ISecretProtector? Create(string name) => name switch
    {
        ProtectorNames.Keychain when OperatingSystem.IsMacOS() => new AppleKeychainSecretProtector(),
        ProtectorNames.Dpapi when OperatingSystem.IsWindows() => new DpapiSecretProtector(),
        ProtectorNames.LibSecret when OperatingSystem.IsLinux() => new SecretToolProtector(),
        ProtectorNames.File => new FileSecretProtector(),
        _ => null,
    };
}
