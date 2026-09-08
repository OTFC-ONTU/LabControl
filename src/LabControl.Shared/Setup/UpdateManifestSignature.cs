using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Identity;

namespace LabControl.Shared.Setup;

/// <summary>Domain-separated signature of the exact manifest bytes, independent of transport.</summary>
public static class UpdateManifestSignature
{
    private static ReadOnlySpan<byte> Domain => "labcontrol/update-manifest/v1\0"u8;

    public static string Sign(LabKey key, ReadOnlySpan<byte> manifest) =>
        Convert.ToBase64String(key.Sign(Payload(manifest)));

    public static bool Verify(X509Certificate2 authority, ReadOnlySpan<byte> manifest, string signature)
    {
        // P-256 IEEE P1363 is exactly 64 bytes / 88 Base64 characters.
        if (signature.Length != 88) return false;
        Span<byte> decoded = stackalloc byte[64];
        return Convert.TryFromBase64String(signature, decoded, out var count) && count == 64
            && LabKey.Verify(authority, Payload(manifest), decoded);
    }

    private static byte[] Payload(ReadOnlySpan<byte> manifest)
    {
        var bytes = new byte[Domain.Length + manifest.Length];
        Domain.CopyTo(bytes);
        manifest.CopyTo(bytes.AsSpan(Domain.Length));
        return bytes;
    }
}
