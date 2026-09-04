using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LabControl.Shared.Identity;

/// <summary>
/// The lab key, unlocked (ARCHITECTURE §3.2). Holding one of these means holding the
/// authority to mint a console instance, enrol a PC or revoke a certificate — nothing
/// else in LabControl needs it, which is why daily use never asks for a passphrase.
/// </summary>
public sealed class LabKey : IDisposable
{
    private readonly byte[] _masterKey;
    private bool _disposed;

    private LabKey(LabKeyDocument document, X509Certificate2 authority, byte[] masterKey)
    {
        Document = document;
        Authority = authority;
        _masterKey = masterKey;
    }

    /// <summary>
    /// The file as it stands, including any wrapping added or removed since unlocking.
    /// The caller saves it; this class never touches the disk.
    /// </summary>
    public LabKeyDocument Document { get; }

    /// <summary>The CA certificate with its private key attached, ready to sign.</summary>
    public X509Certificate2 Authority { get; }

    public string LabId => Document.LabId;

    public string LabName => Document.LabName;

    /// <summary>Who can open this lab, without opening it — see ARCHITECTURE §3.2.</summary>
    public IReadOnlyList<string> HolderNames =>
        Document.Wrappings.Where(w => w.Kind == KeyWrappingKind.Holder).Select(w => w.Name).ToArray();

    // ------------------------------------------------------------------ creation

    /// <summary>
    /// Creates a brand-new lab: a P-256 authority, a random master key, the first holder's
    /// wrapping and the recovery code. Called exactly once per lab, by the first-run wizard.
    /// </summary>
    public static LabKey Create(
        string labName,
        string firstHolderName,
        string firstHolderPassphrase,
        out RecoveryCode recoveryCode,
        DateTimeOffset? now = null,
        int iterations = Defaults.KeyDerivationIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labName);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstHolderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstHolderPassphrase);

        var created = now ?? DateTimeOffset.UtcNow;
        var labId = Guid.NewGuid().ToString("d");
        var masterKey = RandomNumberGenerator.GetBytes(Defaults.MasterKeyBytes);

        using var key = LabCertificates.CreateKey();

        // CreateSelfSigned already carries the private key, so this certificate is the one
        // the LabKey hands out; only its public form is written to the file.
        var authority = LabCertificates.CreateAuthority(labId, labName, key, created);

        var document = new LabKeyDocument
        {
            LabId = labId,
            LabName = labName,
            CreatedAtUnix = created.ToUnixTimeSeconds(),
            Authority = authority.Export(X509ContentType.Cert),
            AuthorityPrivateKey = SealedSecret.Seal(masterKey, key.ExportPkcs8PrivateKey(), AuthorityContext(labId)),
        };

        recoveryCode = RecoveryCode.Generate();
        document.Wrappings.Add(Wrap(labId, KeyWrappingKind.Holder, firstHolderName, firstHolderPassphrase, masterKey, created, iterations));
        document.Wrappings.Add(Wrap(labId, KeyWrappingKind.Recovery, RecoveryWrappingName, recoveryCode.Canonical, masterKey, created, iterations));

        return new LabKey(document, authority, masterKey);
    }

    // ------------------------------------------------------------------ unlocking

    /// <summary>Opens the lab key with a named holder's passphrase.</summary>
    public static bool TryUnlock(LabKeyDocument document, string holderName, string passphrase, out LabKey key)
    {
        var wrapping = document.Wrappings.FirstOrDefault(
            w => w.Kind == KeyWrappingKind.Holder &&
                 string.Equals(w.Name, holderName, StringComparison.OrdinalIgnoreCase));

        return TryUnlockWith(document, wrapping, passphrase, out key);
    }

    /// <summary>
    /// Opens the lab key with a passphrase whose holder is not known — every holder is
    /// tried. Convenient on the import screen, where the teacher types a passphrase before
    /// the console has any idea whose it is.
    /// </summary>
    public static bool TryUnlock(LabKeyDocument document, string passphrase, out LabKey key)
    {
        foreach (var wrapping in document.Wrappings.Where(w => w.Kind == KeyWrappingKind.Holder))
        {
            if (TryUnlockWith(document, wrapping, passphrase, out key))
            {
                return true;
            }
        }

        key = null!;
        return false;
    }

    /// <summary>Opens the lab key with the printed recovery code.</summary>
    public static bool TryUnlock(LabKeyDocument document, RecoveryCode recoveryCode, out LabKey key)
    {
        var wrapping = document.Wrappings.FirstOrDefault(w => w.Kind == KeyWrappingKind.Recovery);
        return TryUnlockWith(document, wrapping, recoveryCode.Canonical, out key);
    }

    // ------------------------------------------------------------------ holders

    /// <summary>
    /// Adds a key holder. Requires an already-unlocked key, which is exactly the rule from
    /// ARCHITECTURE §3.2: adding a holder needs an existing holder's passphrase.
    /// </summary>
    public void AddHolder(string name, string passphrase, DateTimeOffset? now = null, int iterations = Defaults.KeyDerivationIterations)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);

        if (HolderNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"'{name}' is already a key holder of this lab.");
        }

        Document.Wrappings.Add(Wrap(LabId, KeyWrappingKind.Holder, name, passphrase, _masterKey, now ?? DateTimeOffset.UtcNow, iterations));
    }

    /// <summary>
    /// Removes a key holder. Their wrapping is dropped, which does not need their
    /// cooperation and takes effect the moment the file is saved. The last holder cannot be
    /// removed: a lab openable only by a sheet of paper in a drawer is a lab with no teacher.
    /// </summary>
    public void RemoveHolder(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var wrapping = Document.Wrappings.FirstOrDefault(
            w => w.Kind == KeyWrappingKind.Holder && string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"'{name}' is not a key holder of this lab.");

        if (HolderNames.Count == 1)
        {
            throw new InvalidOperationException(
                "The last key holder cannot be removed — add another holder first.");
        }

        Document.Wrappings.Remove(wrapping);
    }

    /// <summary>
    /// Prints a new recovery code and invalidates the old sheet, for "recovery code lost,
    /// passphrase known" (ARCHITECTURE §3.7.3, failure table).
    /// </summary>
    public RecoveryCode ResetRecoveryCode(DateTimeOffset? now = null, int iterations = Defaults.KeyDerivationIterations)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Document.Wrappings.RemoveAll(w => w.Kind == KeyWrappingKind.Recovery);

        var recoveryCode = RecoveryCode.Generate();
        Document.Wrappings.Add(Wrap(LabId, KeyWrappingKind.Recovery, RecoveryWrappingName, recoveryCode.Canonical,
            _masterKey, now ?? DateTimeOffset.UtcNow, iterations));

        return recoveryCode;
    }

    // ------------------------------------------------------------------ signing

    /// <summary>
    /// Signs with the lab key itself, IEEE P1363 fixed-width (64 bytes on P-256) so the
    /// beacon endorsement and revocation signatures have a constant size on the wire.
    /// </summary>
    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var key = Authority.GetECDsaPrivateKey()
                        ?? throw new InvalidOperationException("The lab authority has no usable private key.");

        return key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Verifies a lab-key signature against a CA certificate — no private key needed.</summary>
    public static bool Verify(X509Certificate2 authority, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = authority.GetECDsaPublicKey();
        if (key is null)
        {
            return false;
        }

        try
        {
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_masterKey);
        Authority.Dispose();
    }

    // ------------------------------------------------------------------ internals

    /// <summary>The name under which the recovery wrapping is listed; not a person.</summary>
    public const string RecoveryWrappingName = "Recovery code";

    private static string AuthorityContext(string labId) => $"labcontrol/authority/{labId}";

    private static string WrappingContext(string labId, string wrappingId) => $"labcontrol/wrapping/{labId}/{wrappingId}";

    private static KeyWrapping Wrap(
        string labId,
        KeyWrappingKind kind,
        string name,
        string secret,
        byte[] masterKey,
        DateTimeOffset now,
        int iterations)
    {
        var id = Guid.NewGuid().ToString("d");
        var salt = RandomNumberGenerator.GetBytes(Defaults.MasterKeyBytes);
        var derived = Derive(secret, salt, iterations);

        try
        {
            return new KeyWrapping
            {
                Id = id,
                Kind = kind,
                Name = name,
                CreatedAtUnix = now.ToUnixTimeSeconds(),
                Salt = salt,
                Iterations = iterations,
                Secret = SealedSecret.Seal(derived, masterKey, WrappingContext(labId, id)),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    private static bool TryUnlockWith(LabKeyDocument document, KeyWrapping? wrapping, string secret, out LabKey key)
    {
        key = null!;
        if (wrapping is null || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        var derived = Derive(secret, wrapping.Salt, wrapping.Iterations);
        byte[] masterKey;
        try
        {
            if (!wrapping.Secret.TryOpen(derived, WrappingContext(document.LabId, wrapping.Id), out masterKey))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }

        byte[] pkcs8;
        try
        {
            pkcs8 = document.AuthorityPrivateKey.Open(masterKey, AuthorityContext(document.LabId));
        }
        catch (CryptographicException)
        {
            // The wrapping opened but the authority did not: the file has been tampered with.
            CryptographicOperations.ZeroMemory(masterKey);
            throw new InvalidDataException(
                $"'{Defaults.LabKeyFileName}' opened with the right secret but its authority key is damaged.");
        }

        var privateKey = LabCertificates.CreateKey();
        try
        {
            privateKey.ImportPkcs8PrivateKey(pkcs8, out _);
            using var authority = X509CertificateLoader.LoadCertificate(document.Authority);
            key = new LabKey(document, authority.CopyWithPrivateKey(privateKey), masterKey);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
            privateKey.Dispose();
        }
    }

    private static byte[] Derive(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret.Normalize(NormalizationForm.FormC)),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            Defaults.MasterKeyBytes);
}
