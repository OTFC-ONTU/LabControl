using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Link;

/// <summary>
/// What a PC keeps on disk (ARCHITECTURE §3.3, §5): its configuration, the pinned CA, its
/// own keypair and — once enrolled — its certificate. The link library reads and updates
/// it; how the private key is protected is the host's business (DPAPI on a real PC, a
/// plain file for <c>FakeAgent</c>).
/// </summary>
public interface IAgentStore
{
    AgentConfigDocument Config { get; }

    /// <summary>The lab's public authority, pinned at install (D-14).</summary>
    X509Certificate2 Authority { get; }

    /// <summary>The PC's current private key: the one behind <see cref="Certificate"/>, or the one a CSR is made from.</summary>
    ECDsa Key { get; }

    /// <summary>The PC's certificate, or <c>null</c> until enrolment succeeds. Public part only; <see cref="Key"/> holds the private part.</summary>
    X509Certificate2? Certificate { get; }

    /// <summary>Persists <see cref="Config"/>.</summary>
    void SaveConfig();

    /// <summary>
    /// Installs the certificate the console issued, together with the key it certifies.
    /// Called after enrolment and after every renewal (D-25); the store owns the key from
    /// here on and disposes the previous one.
    /// </summary>
    void InstallCertificate(ECDsa key, X509Certificate2 certificate);
}
