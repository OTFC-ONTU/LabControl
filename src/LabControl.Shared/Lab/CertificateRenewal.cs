using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Identity;

namespace LabControl.Shared.Lab;

/// <summary>Why a <c>Renew</c> call did or did not produce a certificate.</summary>
public enum RenewalOutcome
{
    Issued = 0,

    /// <summary>The lab key is locked on this console; the agent keeps its current certificate and asks again later.</summary>
    Closed = 1,

    /// <summary>The peer is not an agent of this lab, so there is nothing to re-issue.</summary>
    NotAnAgent = 2,

    /// <summary>The signing request is not a valid PKCS#10, or is not signed by its own key.</summary>
    BadRequest = 3,
}

public sealed record RenewalResult(RenewalOutcome Outcome, X509Certificate2? Certificate, string Message)
{
    public bool Ok => Outcome == RenewalOutcome.Issued;
}

/// <summary>
/// Re-issues an agent certificate over the existing mutual-TLS link (D-25), so that a
/// certificate reaching the end of its life never turns into a walk to the PC. The identity
/// being renewed is the one the peer <i>proved</i> on this connection — lab, agent id and
/// number come out of its certificate, never out of the request.
/// <para>
/// Signing needs the lab key, exactly like enrolment (D-24): a locked console answers
/// <see cref="RenewalOutcome.Closed"/> and the agent simply tries again later, which is why
/// renewal starts <see cref="Defaults.CertificateRenewalLeadTime"/> before expiry.
/// </para>
/// </summary>
public static class CertificateRenewal
{
    public static RenewalResult Renew(LabKey? lab, LabName peer, byte[] pkcs10, DateTimeOffset now)
    {
        if (peer.Role != LabRole.Agent)
        {
            return new RenewalResult(RenewalOutcome.NotAnAgent, null,
                "Only agent certificates are renewed over the link; a console instance is re-minted from the lab key.");
        }

        var who = string.Format(Defaults.MachineNameFormat, peer.Number);

        if (lab is null)
        {
            return new RenewalResult(RenewalOutcome.Closed, null,
                $"{who} asked to renew its certificate, but the lab key is locked on this console — " +
                "unlock it in Settings and the PC will ask again.");
        }

        if (!string.Equals(peer.LabId, lab.LabId, StringComparison.OrdinalIgnoreCase))
        {
            return new RenewalResult(RenewalOutcome.NotAnAgent, null,
                $"{who} belongs to lab {peer.LabId}, not this one.");
        }

        try
        {
            var certificate = LabCertificates.IssueAgentFromCsr(
                lab.Authority, lab.LabId, peer.Id, peer.Number, pkcs10, now);

            return new RenewalResult(RenewalOutcome.Issued, certificate, $"{who} renewed its certificate.");
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return new RenewalResult(RenewalOutcome.BadRequest, null,
                $"{who} sent a signing request this console could not read: {ex.Message}");
        }
    }
}
