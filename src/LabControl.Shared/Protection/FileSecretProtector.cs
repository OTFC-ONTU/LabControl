using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Protection;

/// <summary>
/// The fallback for machines with no usable keystore. AES-256-GCM under a key derived from
/// something specific to this machine and this user account, so a copied data directory is
/// useless on another computer.
/// <para>
/// It is honestly weaker than a keystore: anything running as this user on this machine can
/// re-derive the key. That is the trade — a console that cannot start because a keyring
/// daemon is missing is worse for this lab than a key protected by the file system, and the
/// instance key is reissuable from the lab key in any case (ARCHITECTURE §3.1).
/// </para>
/// </summary>
public sealed class FileSecretProtector : ISecretProtector
{
    public const string ProtectorName = ProtectorNames.File;

    private const int NonceBytes = SealedSecret.NonceBytes;
    private const int TagBytes = SealedSecret.TagBytes;

    public string Name => ProtectorName;

    public bool IsAvailable => true;

    public ProtectedSecret Protect(string reference, ReadOnlySpan<byte> secret)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var key = DeriveKey(reference, salt);

        try
        {
            return new ProtectedSecret
            {
                Protector = ProtectorName,
                Reference = reference,
                Salt = salt,
                Payload = Pack(SealedSecret.Seal(key, secret, Context(reference))),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public bool TryUnprotect(ProtectedSecret secret, out byte[] plaintext)
    {
        plaintext = [];
        if (!Unpack(secret.Payload, out var sealedSecret))
        {
            return false;
        }

        var key = DeriveKey(secret.Reference, secret.Salt);
        try
        {
            return sealedSecret.TryOpen(key, Context(secret.Reference), out plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Forget(ProtectedSecret secret)
    {
        // The blob lives inside the document; deleting the document is what forgets it.
    }

    private static string Context(string reference) => $"labcontrol/protected/{reference}";

    private static byte[] DeriveKey(string reference, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes($"{MachineIdentity()} {Environment.UserName} {reference}"),
            salt,
            // Not a passphrase: the input already has machine entropy, and the console must
            // start quickly. This stretches against a copied file, not against guessing.
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            Defaults.MasterKeyBytes);

    /// <summary>
    /// Something stable and specific to this computer. Every source is best-effort; the
    /// machine name is the floor, which still stops a data directory copied to another PC.
    /// </summary>
    private static string MachineIdentity()
    {
        string[] machineIdFiles = ["/etc/machine-id", "/var/lib/dbus/machine-id"];
        foreach (var path in machineIdFiles)
        {
            try
            {
                if (File.Exists(path))
                {
                    var id = File.ReadAllText(path).Trim();
                    if (id.Length > 0)
                    {
                        return id;
                    }
                }
            }
            catch (IOException)
            {
                // Unreadable is the same as absent here.
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var uuid = ReadMacPlatformUuid();
            if (uuid is not null)
            {
                return uuid;
            }
        }

        return Environment.MachineName;
    }

    private static string? ReadMacPlatformUuid()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5_000);

            const string marker = "\"IOPlatformUUID\" = \"";
            var start = output.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = output.IndexOf('"', start);
            return end > start ? output[start..end] : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static byte[] Pack(SealedSecret sealedSecret)
    {
        var packed = new byte[sealedSecret.Nonce.Length + sealedSecret.Tag.Length + sealedSecret.Ciphertext.Length];
        sealedSecret.Nonce.CopyTo(packed, 0);
        sealedSecret.Tag.CopyTo(packed, sealedSecret.Nonce.Length);
        sealedSecret.Ciphertext.CopyTo(packed, sealedSecret.Nonce.Length + sealedSecret.Tag.Length);
        return packed;
    }

    private static bool Unpack(byte[] packed, out SealedSecret sealedSecret)
    {
        const int header = NonceBytes + TagBytes;
        sealedSecret = null!;
        if (packed.Length < header)
        {
            return false;
        }

        sealedSecret = new SealedSecret
        {
            Nonce = packed[..NonceBytes],
            Tag = packed[NonceBytes..header],
            Ciphertext = packed[header..],
        };

        return true;
    }
}
