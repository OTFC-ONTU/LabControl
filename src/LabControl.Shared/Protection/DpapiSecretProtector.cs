using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Protection;

/// <summary>
/// Windows DPAPI, scoped to the current user (ARCHITECTURE §3.2). The blob lives inside
/// <c>instance.json</c> and can only be opened by this Windows account on this machine, so
/// a copied <c>%APPDATA%\LabControl</c> is inert.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public const string ProtectorName = ProtectorNames.Dpapi;

    public string Name => ProtectorName;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public ProtectedSecret Protect(string reference, ReadOnlySpan<byte> secret) => new()
    {
        Protector = ProtectorName,
        Reference = reference,
        Payload = ProtectedData.Protect(secret.ToArray(), Entropy(reference), DataProtectionScope.CurrentUser),
    };

    public bool TryUnprotect(ProtectedSecret secret, out byte[] plaintext)
    {
        try
        {
            plaintext = ProtectedData.Unprotect(secret.Payload, Entropy(secret.Reference), DataProtectionScope.CurrentUser);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = [];
            return false;
        }
    }

    public void Forget(ProtectedSecret secret)
    {
        // The blob lives inside the document; deleting the document is what forgets it.
    }

    /// <summary>Binds the blob to the secret it belongs to, so two instances cannot be swapped.</summary>
    private static byte[] Entropy(string reference) =>
        Encoding.UTF8.GetBytes($"labcontrol/protected/{reference}");
}
