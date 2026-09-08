using System.Security.Cryptography.X509Certificates;

namespace LabControl.Shared.Identity;

/// <summary>Why a peer certificate was refused; shown to the teacher as-is.</summary>
public enum TrustFailure
{
    None = 0,
    NotIssuedByThisLab = 1,
    WrongLab = 2,
    WrongRole = 3,
    Revoked = 4,
    Expired = 5,
    Malformed = 6,
}

/// <summary>
/// The only thing either side of a LabControl connection trusts: the lab's CA
/// (ARCHITECTURE §3.4). No public root, no hostname, no IP address — a certificate is
/// acceptable when it chains to the pinned authority, names this lab, claims the role the
/// caller expected, and is not revoked.
/// </summary>
public sealed class LabTrust
{
    private readonly X509Certificate2 _authority;

    public LabTrust(X509Certificate2 authority, string labId)
    {
        _authority = authority;
        LabId = labId;
    }

    /// <summary>Builds trust straight from the public CA certificate, which carries the lab id.</summary>
    public static LabTrust FromAuthority(X509Certificate2 authority)
    {
        if (!LabName.TryFromCertificate(authority, out var name) || name.Role != LabRole.Authority)
        {
            throw new ArgumentException(
                "This certificate is not a LabControl lab authority.", nameof(authority));
        }

        return new LabTrust(authority, name.LabId);
    }

    public string LabId { get; }

    public X509Certificate2 Authority => _authority;

    public bool TryValidate(
        X509Certificate2? peer,
        LabRole expectedRole,
        RevocationSet? revocations,
        out LabName name,
        out TrustFailure failure,
        DateTimeOffset? now = null)
    {
        name = null!;

        if (peer is null)
        {
            failure = TrustFailure.Malformed;
            return false;
        }

        if (!LabName.TryFromCertificate(peer, out name))
        {
            failure = TrustFailure.Malformed;
            return false;
        }

        if (!string.Equals(name.LabId, LabId, StringComparison.OrdinalIgnoreCase))
        {
            // A PC from another lab, or another lab's console. Say so plainly instead of
            // letting it fail as an opaque TLS error (PROTOCOL, "Versioning").
            failure = TrustFailure.WrongLab;
            return false;
        }

        if (name.Role != expectedRole)
        {
            failure = TrustFailure.WrongRole;
            return false;
        }

        if (revocations is not null && revocations.IsRevoked(LabCertificates.SerialOf(peer)))
        {
            failure = TrustFailure.Revoked;
            return false;
        }

        // A withdrawn teacher device is revoked by instance id as well as by serial (D-56
        // item 6), so a leaf renewed after the withdrawal is refused too.
        if (revocations is not null && name.Role == LabRole.Console && revocations.IsRevoked(LabCertificates.InstanceSerial(name.Id)))
        {
            failure = TrustFailure.Revoked;
            return false;
        }

        var at = now ?? DateTimeOffset.UtcNow;
        if (!ChainsToAuthority(peer, at))
        {
            failure = at < peer.NotBefore || at > peer.NotAfter
                ? TrustFailure.Expired
                : TrustFailure.NotIssuedByThisLab;
            return false;
        }

        failure = TrustFailure.None;
        return true;
    }

    private bool ChainsToAuthority(X509Certificate2 peer, DateTimeOffset at)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_authority);
        chain.ChainPolicy.VerificationTime = at.UtcDateTime;

        // The lab is offline by definition: there is no OCSP responder and no CRL to fetch,
        // and revocation is checked against the signed set above instead (D-21).
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        return chain.Build(peer);
    }

    /// <summary>Plain-language reason, for an event row or a tile tooltip.</summary>
    public static string Describe(TrustFailure failure, LabName? name = null) => failure switch
    {
        TrustFailure.None => "accepted",
        TrustFailure.NotIssuedByThisLab => "the certificate was not issued by this lab's key",
        TrustFailure.WrongLab => $"the certificate belongs to lab {name?.LabId ?? "?"}, not this one",
        TrustFailure.WrongRole => $"the certificate is a {name?.Role.ToString().ToLowerInvariant() ?? "?"} certificate",
        TrustFailure.Revoked => "the certificate has been revoked",
        TrustFailure.Expired => "the certificate has expired",
        _ => "the certificate could not be read",
    };
}
