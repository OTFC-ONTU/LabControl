using System.Security.Cryptography.X509Certificates;

namespace LabControl.Shared.Identity;

/// <summary>
/// Makes a certificate-with-key usable by the TLS stack on every platform. A certificate
/// assembled in memory with <c>CopyWithPrivateKey</c> is fine for signing but not always
/// for a handshake — macOS in particular wants the key to have come through a PKCS#12
/// import — so both ends of the link pass their leaf through here once.
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
            var flags = OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.EphemeralKeySet
                : X509KeyStorageFlags.DefaultKeySet;

            return X509CertificateLoader.LoadPkcs12(pkcs12, password: null, flags);
        }
        finally
        {
            Array.Clear(pkcs12);
        }
    }
}
