using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Link;

/// <summary>
/// An <see cref="IAgentStore"/> laid out the way a real PC keeps it (ARCHITECTURE §5):
/// <c>agent.json</c>, <c>ca.crt</c>, <c>agent.key</c>, <c>agent.crt</c> in one directory.
/// The private key goes through <see cref="KeyProtection"/> on the way to and from disk,
/// which is where the real agent plugs in DPAPI; <c>FakeAgent</c> stores it as is.
/// </summary>
public sealed class DirectoryAgentStore : IAgentStore, IDisposable
{
    private readonly string _directory;
    private readonly KeyProtection _protection;

    private DirectoryAgentStore(string directory, KeyProtection protection, AgentConfigDocument config, X509Certificate2 authority, ECDsa key, X509Certificate2? certificate)
    {
        _directory = directory;
        _protection = protection;
        Config = config;
        Authority = authority;
        Key = key;
        Certificate = certificate;
    }

    public AgentConfigDocument Config { get; }

    public X509Certificate2 Authority { get; }

    public ECDsa Key { get; private set; }

    public X509Certificate2? Certificate { get; private set; }

    public string Directory => _directory;

    public static bool Exists(string directory) =>
        File.Exists(Path.Combine(directory, Defaults.AgentConfigFileName));

    /// <summary>Reopens an installed PC.</summary>
    public static DirectoryAgentStore Open(string directory, KeyProtection? protection = null)
    {
        protection ??= KeyProtection.None;

        var config = JsonStore.Load<AgentConfigDocument>(Path.Combine(directory, Defaults.AgentConfigFileName), AgentConfigDocument.Migrations);
        var authority = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directory, Defaults.CaCertificateFileName));

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = protection.Unprotect(File.ReadAllBytes(Path.Combine(directory, Defaults.AgentKeyFileName)));
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        var certificatePath = Path.Combine(directory, Defaults.AgentCertificateFileName);
        var certificate = File.Exists(certificatePath) ? X509CertificateLoader.LoadCertificateFromFile(certificatePath) : null;

        return new DirectoryAgentStore(directory, protection, config, authority, key, certificate);
    }

    /// <summary>
    /// What Setup does on a PC (INSTALLER.md step 4): fresh keypair, pinned authority, the
    /// configuration with one enrollment code, no certificate yet.
    /// </summary>
    public static DirectoryAgentStore Install(string directory, AgentConfigDocument config, X509Certificate2 authority, KeyProtection? protection = null)
    {
        protection ??= KeyProtection.None;
        System.IO.Directory.CreateDirectory(directory);

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteKey(directory, protection, key);
        File.WriteAllBytes(Path.Combine(directory, Defaults.CaCertificateFileName), authority.Export(X509ContentType.Cert));

        var store = new DirectoryAgentStore(directory, protection, config, X509CertificateLoader.LoadCertificate(authority.Export(X509ContentType.Cert)), key, null);
        store.SaveConfig();
        return store;
    }

    public void SaveConfig() =>
        JsonStore.Save(Path.Combine(_directory, Defaults.AgentConfigFileName), Config, AgentConfigDocument.Migrations, ownerOnly: true);

    public void InstallCertificate(ECDsa key, X509Certificate2 certificate)
    {
        var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));

        if (!ReferenceEquals(key, Key))
        {
            WriteKey(_directory, _protection, key);
            Key.Dispose();
            Key = key;
        }

        File.WriteAllBytes(Path.Combine(_directory, Defaults.AgentCertificateFileName), publicOnly.RawData);
        Certificate?.Dispose();
        Certificate = publicOnly;
    }

    /// <summary>Wipes the certificate, as <c>Setup.exe --rekey</c> does; the key and the id stay.</summary>
    public void ForgetCertificate()
    {
        var path = Path.Combine(_directory, Defaults.AgentCertificateFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Certificate?.Dispose();
        Certificate = null;
    }

    private static void WriteKey(string directory, KeyProtection protection, ECDsa key)
    {
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            var path = Path.Combine(directory, Defaults.AgentKeyFileName);
            File.WriteAllBytes(path, protection.Protect(pkcs8));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    public void Dispose()
    {
        Key.Dispose();
        Certificate?.Dispose();
        Authority.Dispose();
    }
}

/// <summary>How the agent's private key is transformed on its way to disk.</summary>
public sealed record KeyProtection(Func<byte[], byte[]> Protect, Func<byte[], byte[]> Unprotect)
{
    /// <summary>Plain PKCS#8 — for the simulator. A real PC uses DPAPI at machine scope (M2).</summary>
    public static readonly KeyProtection None = new(bytes => bytes, bytes => bytes);
}
