using System.Security.Cryptography;
using System.Text;

namespace LabControl.Shared.Identity;

/// <summary>
/// A blob sealed with AES-256-GCM (ARCHITECTURE §3.2). The associated data binds the
/// ciphertext to where it belongs — the lab it was made for, the wrapping it lives in — so
/// a blob lifted out of one file cannot be pasted into another.
/// </summary>
public sealed class SealedSecret
{
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    public byte[] Nonce { get; set; } = [];

    public byte[] Ciphertext { get; set; } = [];

    public byte[] Tag { get; set; } = [];

    public static SealedSecret Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(associatedData));

        return new SealedSecret { Nonce = nonce, Ciphertext = ciphertext, Tag = tag };
    }

    /// <summary>Opens the blob, or throws <see cref="CryptographicException"/> if the key or the context is wrong.</summary>
    public byte[] Open(ReadOnlySpan<byte> key, string associatedData)
    {
        var plaintext = new byte[Ciphertext.Length];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(Nonce, Ciphertext, Tag, plaintext, Encoding.UTF8.GetBytes(associatedData));

        return plaintext;
    }

    /// <summary>Opens the blob, reporting a wrong key as <c>false</c> rather than an exception.</summary>
    public bool TryOpen(ReadOnlySpan<byte> key, string associatedData, out byte[] plaintext)
    {
        try
        {
            plaintext = Open(key, associatedData);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = [];
            return false;
        }
    }
}
