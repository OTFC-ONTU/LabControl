using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Discovery;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;

namespace LabControl.Shared.Identity;

/// <summary>
/// This teacher machine's console identity: a leaf certificate minted from the lab key,
/// its beacon endorsement, and the private key that never leaves the machine
/// (ARCHITECTURE §3.1). Minting one is the <i>only</i> step that needs the lab key, which
/// is why alternating between two teacher machines never asks for a passphrase (§3.7).
/// </summary>
public sealed class ConsoleInstance : IDisposable
{
    private ConsoleInstance(InstanceDocument document, X509Certificate2 certificate)
    {
        Document = document;
        Certificate = certificate;
    }

    /// <summary>The persisted form, ready for the caller to save.</summary>
    public InstanceDocument Document { get; }

    /// <summary>The leaf with its private key attached: the TLS server certificate.</summary>
    public X509Certificate2 Certificate { get; }

    public string LabId => Document.LabId;

    public string InstanceId => Document.InstanceId;

    public string InstanceName => Document.InstanceName;

    public byte[] Endorsement => Document.Endorsement;

    public string CertificateSerial => LabCertificates.SerialOf(Certificate);

    /// <summary>
    /// Mints a new console instance. Called on first run and again on every migration to
    /// another teacher machine — a new machine always gets its own identity, never a copy
    /// of the old one (§3.7, "what each keeps to itself").
    /// </summary>
    public static ConsoleInstance Mint(
        LabKey lab,
        string instanceName,
        ISecretProtector protector,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        var created = now ?? DateTimeOffset.UtcNow;
        var instanceId = Guid.NewGuid().ToString("d");

        using var key = LabCertificates.CreateKey();
        var certificate = LabCertificates.IssueConsoleInstance(
            lab.Authority, lab.LabId, instanceId, instanceName, key, created);

        var document = new InstanceDocument
        {
            LabId = lab.LabId,
            InstanceId = instanceId,
            InstanceName = instanceName,
            Certificate = certificate.Export(X509ContentType.Cert),
            Endorsement = Beacon.Endorse(lab, instanceId, P256.Compress(key)),
            PrivateKey = protector.Protect(ProtectionReference(instanceId), key.ExportPkcs8PrivateKey()),
            CreatedAtUnix = created.ToUnixTimeSeconds(),
        };

        return new ConsoleInstance(document, certificate.CopyWithPrivateKey(key));
    }

    /// <summary>
    /// Reopens a persisted instance. Uses the protector that <i>wrote</i> the key, not the
    /// one this machine would choose today, so installing a keyring never orphans a key.
    /// </summary>
    public static ConsoleInstance Open(InstanceDocument document)
    {
        if (!SecretProtector.For(document.PrivateKey).TryUnprotect(document.PrivateKey, out var pkcs8))
        {
            throw new InvalidDataException(
                $"This machine's console key could not be opened ({document.PrivateKey.Protector}). " +
                "Import the lab key again to mint a new console instance.");
        }

        var key = LabCertificates.CreateKey();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            using var certificate = X509CertificateLoader.LoadCertificate(document.Certificate);
            return new ConsoleInstance(document, certificate.CopyWithPrivateKey(key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
            key.Dispose();
        }
    }

    /// <summary>Builds a signed beacon for this moment. Cheap enough to do twice a second.</summary>
    public Beacon CreateBeacon(string host, int port, DateTimeOffset now, DateTimeOffset? takeOverAt = null)
    {
        using var key = Certificate.GetECDsaPrivateKey()
                        ?? throw new InvalidOperationException("This console instance has no usable private key.");

        var beacon = new Beacon
        {
            LabId = LabId,
            InstanceId = InstanceId,
            InstanceName = InstanceName,
            Host = host,
            Port = port,
            SentAtUnix = now.ToUnixTimeSeconds(),
            TakeAtUnix = takeOverAt?.ToUnixTimeSeconds() ?? 0,
            PublicKey = P256.Compress(key),
            Endorsement = Endorsement,
        };

        return beacon.SignWith(key);
    }

    public void Dispose() => Certificate.Dispose();

    /// <summary>Names the instance key inside the OS keystore.</summary>
    public static string ProtectionReference(string instanceId) => $"instance-{instanceId}";
}
