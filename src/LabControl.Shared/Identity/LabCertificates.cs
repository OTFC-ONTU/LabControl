using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LabControl.Shared.Identity;

/// <summary>
/// Mints everything the lab key signs: the authority itself, a console instance, and an
/// agent (ARCHITECTURE §3.1). All keys are ECDSA P-256 — small certificates, fast
/// handshakes on a ten-year-old lab PC, and nothing to configure.
/// </summary>
public static class LabCertificates
{
    private static readonly HashAlgorithmName Hash = HashAlgorithmName.SHA256;

    /// <summary>
    /// Certificates are backdated by this much. Student PCs on an isolated LAN have no time
    /// source beyond their own battery-backed clock, so a PC a couple of minutes behind the
    /// console must not reject a freshly issued certificate as "not valid yet".
    /// </summary>
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(5);

    /// <summary>Fresh P-256 keypair. The caller owns it.</summary>
    public static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>
    /// The lab's private certificate authority. Self-signed, long-lived: it is the one
    /// thing that must survive the teacher machine (D-13).
    /// </summary>
    public static X509Certificate2 CreateAuthority(string labId, string labName, ECDsa key, DateTimeOffset now)
    {
        var request = new CertificateRequest(
            SubjectFor(labName, "LabControl Lab"), key, Hash);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
            critical: true));
        request.CertificateExtensions.Add(SubjectAlternativeName(LabName.ForAuthority(labId)));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        return request.CreateSelfSigned(now - ClockSkewAllowance, now + Defaults.LabAuthorityLifetime);
    }

    /// <summary>
    /// A console instance leaf. It is both a TLS server (agents dial in) and a TLS client
    /// (nothing today, but a console that ever dials out must not need a reissue).
    /// </summary>
    public static X509Certificate2 IssueConsoleInstance(
        X509Certificate2 authority,
        string labId,
        string instanceId,
        string instanceName,
        ECDsa instanceKey,
        DateTimeOffset now)
    {
        var request = new CertificateRequest(
            SubjectFor(instanceName, Defaults.ConsoleOrganizationalUnit), instanceKey, Hash);

        Decorate(request, LabName.ForConsole(labId, instanceId),
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement,
            OidServerAuthentication, OidClientAuthentication);

        return Sign(request, authority, now, Defaults.ConsoleCertificateLifetime);
    }

    /// <summary>
    /// A teacher device's leaf (M5, D-56 items 4–5), issued from the PKCS#10 the device
    /// wrote into its request: the <b>same</b> SAN URI and key usages as a console instance,
    /// so every agent already in the field links to it, and <c>OU=LabControl Teacher</c> so
    /// an M5 agent knows it may not sign updates or re-key. Only the public key is taken from
    /// the request; the name and the lifetime are the administrator's decision.
    /// </summary>
    public static X509Certificate2 IssueTeacherDevice(
        X509Certificate2 authority,
        string labId,
        string instanceId,
        string instanceName,
        byte[] pkcs10,
        DateTimeOffset now)
    {
        var incoming = CertificateRequest.LoadSigningRequest(pkcs10, Hash);

        var request = new CertificateRequest(
            SubjectFor(instanceName, Defaults.TeacherOrganizationalUnit), incoming.PublicKey, Hash);

        Decorate(request, LabName.ForConsole(labId, instanceId),
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement,
            OidServerAuthentication, OidClientAuthentication);

        return Sign(request, authority, now, Defaults.TeacherCertificateLifetime);
    }

    /// <summary>The PKCS#10 a teacher device puts into its request (D-56 item 4); <c>CN</c> = the device name.</summary>
    public static byte[] CreateDeviceSigningRequest(ECDsa key, string instanceName) =>
        new CertificateRequest(SubjectFor(instanceName, Defaults.TeacherOrganizationalUnit), key, Hash)
            .CreateSigningRequest();

    /// <summary>
    /// Reads the public key out of a PKCS#10 after checking its self-signature; throws on
    /// anything else, including a key on any curve but P-256 — the only curve LabControl
    /// signs with, so a request cannot make the lab certify something it never verifies.
    /// </summary>
    public static ECDsa PublicKeyOfSigningRequest(byte[] pkcs10)
    {
        var incoming = CertificateRequest.LoadSigningRequest(pkcs10, Hash);
        var key = incoming.PublicKey.GetECDsaPublicKey()
                  ?? throw new CryptographicException("The signing request does not carry an ECDSA key.");

        var curve = key.ExportParameters(false).Curve;
        if (!curve.IsNamed || !string.Equals(curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal)
            && !string.Equals(curve.Oid.FriendlyName, ECCurve.NamedCurves.nistP256.Oid.FriendlyName, StringComparison.OrdinalIgnoreCase))
        {
            key.Dispose();
            throw new CryptographicException("The signing request carries a key that is not on the P-256 curve.");
        }

        return key;
    }

    /// <summary>
    /// An agent leaf, issued from the PKCS#10 request the PC generated during enrollment
    /// (D-14). Only the public key is taken from the request: the subject, the extensions
    /// and the lifetime are the console's decision, never the applicant's.
    /// </summary>
    public static X509Certificate2 IssueAgentFromCsr(
        X509Certificate2 authority,
        string labId,
        string agentId,
        int number,
        byte[] pkcs10,
        DateTimeOffset now,
        TimeSpan? lifetime = null)
    {
        // The default load path verifies the request's self-signature, which proves the PC
        // holds the private key for the public key it is asking us to certify.
        var incoming = CertificateRequest.LoadSigningRequest(pkcs10, Hash);

        var request = new CertificateRequest(
            SubjectFor(string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, number), "LabControl Agent"),
            incoming.PublicKey,
            Hash);

        Decorate(request, LabName.ForAgent(labId, agentId, number),
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement,
            OidClientAuthentication);

        // A shorter lifetime exists only for exercising renewal against FakeAgent (D-27).
        return Sign(request, authority, now, lifetime ?? Defaults.AgentCertificateLifetime);
    }

    /// <summary>The PKCS#10 an agent sends at enrollment. Its subject is ignored by the console.</summary>
    public static byte[] CreateSigningRequest(ECDsa key, string commonName) =>
        new CertificateRequest(SubjectFor(commonName, "LabControl Agent"), key, Hash)
            .CreateSigningRequest();

    /// <summary>
    /// Certificate serials as everything else in LabControl spells them: uppercase hex, no
    /// separators, leading zeros trimmed. Revocation matches on this string, so the two
    /// sides must not disagree about the spelling.
    /// </summary>
    public static string NormalizeSerial(string serial)
    {
        // The instance pseudo-serial (D-56 item 6) is not hex: keep the prefix, lower-case the id.
        if (serial.StartsWith(Defaults.InstanceRevocationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Defaults.InstanceRevocationPrefix + serial[Defaults.InstanceRevocationPrefix.Length..].Trim().ToLowerInvariant();
        }

        var trimmed = serial.Replace(":", string.Empty, StringComparison.Ordinal)
                            .Replace(" ", string.Empty, StringComparison.Ordinal)
                            .ToUpperInvariant()
                            .TrimStart('0');

        return trimmed.Length == 0 ? "0" : trimmed;
    }

    public static string SerialOf(X509Certificate2 certificate) => NormalizeSerial(certificate.SerialNumber);

    /// <summary>The pseudo-serial that revokes a console instance across renewals: <c>instance:&lt;id&gt;</c> (D-56 item 6).</summary>
    public static string InstanceSerial(string instanceId) => NormalizeSerial(Defaults.InstanceRevocationPrefix + instanceId);

    /// <summary>
    /// True once a leaf has less than <see cref="Defaults.CertificateRenewalLeadTime"/> left
    /// (D-25). Both sides use the same rule: the agent to know when to ask, the console to
    /// know when to show the "certificates need renewing" banner.
    /// </summary>
    public static bool NeedsRenewal(X509Certificate2 certificate, DateTimeOffset now) =>
        new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero) - now
        < Defaults.CertificateRenewalLeadTime;

    private const string OidServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private const string OidClientAuthentication = "1.3.6.1.5.5.7.3.2";

    private static X500DistinguishedName SubjectFor(string commonName, string organizationalUnit) =>
        new X500DistinguishedNameBuilder()
            .AddCommonNameAndUnit(commonName, organizationalUnit)
            .Build();

    private static void Decorate(
        CertificateRequest request,
        LabName name,
        X509KeyUsageFlags keyUsage,
        params string[] extendedKeyUsages)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsage, critical: true));

        var oids = new OidCollection();
        foreach (var oid in extendedKeyUsages)
        {
            oids.Add(new Oid(oid));
        }

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
        request.CertificateExtensions.Add(SubjectAlternativeName(name));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
    }

    private static X509Extension SubjectAlternativeName(LabName name)
    {
        var builder = new SubjectAlternativeNameBuilder();
        builder.AddUri(name.ToUri());
        return builder.Build();
    }

    private static X509Certificate2 Sign(
        CertificateRequest request,
        X509Certificate2 authority,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
                authority, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        // A leaf must live inside its issuer's window at both ends. Outliving the authority
        // validates today and fails silently in five years, in the middle of a lesson;
        // starting before it is refused outright, which the five-minute backdate below can
        // otherwise cause for a certificate minted in the same second as the lab.
        var authorityNotBefore = new DateTimeOffset(authority.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        var authorityNotAfter = new DateTimeOffset(authority.NotAfter.ToUniversalTime(), TimeSpan.Zero);

        var notBefore = now - ClockSkewAllowance;
        if (notBefore < authorityNotBefore)
        {
            notBefore = authorityNotBefore;
        }

        var notAfter = now + lifetime;
        if (notAfter > authorityNotAfter)
        {
            notAfter = authorityNotAfter;
        }

        return request.Create(authority, notBefore, notAfter, NewSerialNumber());
    }

    /// <summary>128 random bits, forced positive — serials are signed integers on the wire.</summary>
    private static byte[] NewSerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        serial[0] |= 0x40;
        return serial;
    }
}

file static class X500Builder
{
    public static X500DistinguishedNameBuilder AddCommonNameAndUnit(
        this X500DistinguishedNameBuilder builder, string commonName, string organizationalUnit)
    {
        builder.AddOrganizationName("LabControl");
        builder.AddOrganizationalUnitName(organizationalUnit);
        builder.AddCommonName(commonName);
        return builder;
    }
}
