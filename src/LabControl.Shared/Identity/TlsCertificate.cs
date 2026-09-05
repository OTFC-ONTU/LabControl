using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace LabControl.Shared.Identity;

/// <summary>
/// Makes a certificate-with-key usable by the TLS stack on every platform. A certificate
/// assembled in memory with <c>CopyWithPrivateKey</c> is fine for signing but not always
/// for a handshake — macOS wants the key to have come through a PKCS#12 import, and
/// Windows SChannel cannot authenticate with an <i>ephemeral</i> key at all: it answers
/// "the credentials supplied to the package were not recognized" (seen on the first VM
/// run of M2, D-29). So both ends of the link pass their leaf through here once, and on
/// Windows the key lands in a CNG container for the lifetime of the object — the machine
/// store for a service running as SYSTEM, the user store for the console.
/// </summary>
public static class TlsCertificate
{
    public static X509Certificate2 ForTls(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            throw new ArgumentException("A TLS identity needs its private key.", nameof(certificate));
        }

        var pkcs12 = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pkcs12, password: null, StorageFlags());
        }
        finally
        {
            Array.Clear(pkcs12);
        }
    }

    private static X509KeyStorageFlags StorageFlags()
    {
        if (!OperatingSystem.IsWindows())
        {
            return X509KeyStorageFlags.DefaultKeySet;
        }

        // Never EphemeralKeySet on Windows: SChannel refuses it for client and server auth.
        // The container is deleted when the certificate is disposed (no PersistKeySet).
        return RunsAsSystem() ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.DefaultKeySet;
    }

    private static bool RunsAsSystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException)
        {
            return false;
        }
    }
}
