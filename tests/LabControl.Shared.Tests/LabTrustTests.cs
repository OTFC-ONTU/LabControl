using Xunit;

using System.Security.Cryptography.X509Certificates;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Protection;

namespace LabControl.Shared.Tests;

/// <summary>
/// ARCHITECTURE §3.4: neither side trusts a public CA, a hostname or an IP address — only
/// the lab key. These are the checks that stand between the lab and a PC from another
/// lab, a revoked laptop, or a certificate pretending to be something it is not.
/// </summary>
public sealed class LabTrustTests
{
    [Fact]
    public void A_console_instance_minted_from_the_lab_key_is_accepted()
    {
        using var lab = TestLab.Create();
        using var instance = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector());
        var trust = LabTrust.FromAuthority(PublicOnly(lab.Authority));

        Assert.True(trust.TryValidate(instance.Certificate, LabRole.Console, null, out var name, out var failure));
        Assert.Equal(TrustFailure.None, failure);
        Assert.Equal(instance.InstanceId, name.Id);
        Assert.Equal(lab.LabId, name.LabId);
    }

    [Fact]
    public void An_agent_certificate_issued_from_a_csr_is_accepted_and_names_its_pc()
    {
        using var lab = TestLab.Create();
        var agentId = Guid.NewGuid().ToString("d");

        using var agentCertificate = IssueAgent(lab, agentId, number: 7);
        var trust = LabTrust.FromAuthority(PublicOnly(lab.Authority));

        Assert.True(trust.TryValidate(agentCertificate, LabRole.Agent, null, out var name, out var failure));
        Assert.Equal(TrustFailure.None, failure);
        Assert.Equal(agentId, name.Id);
        Assert.Equal(7, name.Number);
        Assert.Contains("PC-07", agentCertificate.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void A_certificate_from_another_lab_is_refused_by_name_not_by_a_tls_error()
    {
        using var ours = TestLab.Create();
        using var theirs = TestLab.Create();
        using var stranger = ConsoleInstance.Mint(theirs, "Someone else", new FileSecretProtector());

        var trust = LabTrust.FromAuthority(PublicOnly(ours.Authority));

        Assert.False(trust.TryValidate(stranger.Certificate, LabRole.Console, null, out var name, out var failure));
        Assert.Equal(TrustFailure.WrongLab, failure);
        Assert.Contains(theirs.LabId, LabTrust.Describe(failure, name), StringComparison.Ordinal);
    }

    [Fact]
    public void A_console_certificate_offered_where_an_agent_belongs_is_refused()
    {
        using var lab = TestLab.Create();
        using var instance = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector());
        var trust = LabTrust.FromAuthority(PublicOnly(lab.Authority));

        Assert.False(trust.TryValidate(instance.Certificate, LabRole.Agent, null, out _, out var failure));
        Assert.Equal(TrustFailure.WrongRole, failure);
    }

    [Fact]
    public void A_revoked_certificate_is_refused_and_says_why()
    {
        using var lab = TestLab.Create();
        using var instance = ConsoleInstance.Mint(lab, "Stolen laptop", new FileSecretProtector());
        var authority = PublicOnly(lab.Authority);
        var trust = LabTrust.FromAuthority(authority);

        var revocations = new RevocationSet();
        Assert.True(revocations.TryAdd(authority,
            RevocationSet.Create(lab, instance.CertificateSerial, "stolen", DateTimeOffset.UtcNow)));

        Assert.False(trust.TryValidate(instance.Certificate, LabRole.Console, revocations, out _, out var failure));
        Assert.Equal(TrustFailure.Revoked, failure);
    }

    [Fact]
    public void A_self_signed_impostor_does_not_chain_to_the_lab()
    {
        using var lab = TestLab.Create();
        var trust = LabTrust.FromAuthority(PublicOnly(lab.Authority));

        // Same lab id in the name, but signed by nobody the lab knows.
        using var impostorKey = LabCertificates.CreateKey();
        using var impostor = LabCertificates.CreateAuthority(lab.LabId, "not the lab", impostorKey, DateTimeOffset.UtcNow);
        using var leaf = LabCertificates.IssueConsoleInstance(
            impostor, lab.LabId, Guid.NewGuid().ToString("d"), "impostor", impostorKey, DateTimeOffset.UtcNow);

        Assert.False(trust.TryValidate(leaf, LabRole.Console, null, out _, out var failure));
        Assert.Equal(TrustFailure.NotIssuedByThisLab, failure);
    }

    [Fact]
    public void A_certificate_without_a_lab_name_is_malformed()
    {
        using var lab = TestLab.Create();
        var trust = LabTrust.FromAuthority(PublicOnly(lab.Authority));

        Assert.False(trust.TryValidate(null, LabRole.Agent, null, out _, out var failure));
        Assert.Equal(TrustFailure.Malformed, failure);
    }

    [Fact]
    public void A_leaf_never_outlives_the_authority_that_signed_it()
    {
        using var lab = TestLab.Create();
        var almostExpired = new DateTimeOffset(lab.Authority.NotAfter.ToUniversalTime(), TimeSpan.Zero).AddDays(-1);

        using var instance = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), almostExpired);

        Assert.True(instance.Certificate.NotAfter <= lab.Authority.NotAfter);
    }

    [Fact]
    public void A_leaf_never_starts_before_the_authority_that_signed_it()
    {
        using var lab = TestLab.Create();

        // Certificates are backdated to tolerate a student PC whose clock is a little
        // behind; that must never push a leaf outside its issuer's window.
        using var instance = ConsoleInstance.Mint(
            lab, "MacBook-2026", new FileSecretProtector(), DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.True(instance.Certificate.NotBefore >= lab.Authority.NotBefore);
        Assert.True(LabTrust.FromAuthority(PublicOnly(lab.Authority))
            .TryValidate(instance.Certificate, LabRole.Console, null, out _, out _));
    }

    [Fact]
    public void An_expired_certificate_is_refused_as_expired_not_as_a_stranger()
    {
        using var lab = TestLab.Create();
        using var instance = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector());
        var trust = LabTrust.FromAuthority(PublicOnly(lab.Authority));

        var afterExpiry = DateTimeOffset.UtcNow + Defaults.ConsoleCertificateLifetime + TimeSpan.FromDays(1);

        Assert.False(trust.TryValidate(instance.Certificate, LabRole.Console, null, out _, out var failure, afterExpiry));
        Assert.Equal(TrustFailure.Expired, failure);
    }

    [Fact]
    public void Serial_numbers_are_spelled_the_same_way_everywhere()
    {
        Assert.Equal("1A2B", LabCertificates.NormalizeSerial("001a2b"));
        Assert.Equal("1A2B", LabCertificates.NormalizeSerial("1A:2B"));
        Assert.Equal("0", LabCertificates.NormalizeSerial("0000"));
    }

    [Fact]
    public void The_lab_name_uri_round_trips()
    {
        var labId = Guid.NewGuid().ToString("d");
        var agentId = Guid.NewGuid().ToString("d");
        var name = LabName.ForAgent(labId, agentId, 14);

        Assert.True(LabName.TryParse(name.ToUri().ToString(), out var parsed));
        Assert.Equal(name, parsed);
        Assert.False(LabName.TryParse("https://example.com/agent/x/1", out _));
        Assert.False(LabName.TryParse($"labcontrol://{labId}/agent/{agentId}", out _));
    }

    internal static X509Certificate2 IssueAgent(LabKey lab, string agentId, int number)
    {
        using var key = LabCertificates.CreateKey();
        var csr = LabCertificates.CreateSigningRequest(key, string.Format(Defaults.MachineNameFormat, number));

        return LabCertificates.IssueAgentFromCsr(lab.Authority, lab.LabId, agentId, number, csr, DateTimeOffset.UtcNow);
    }

    /// <summary>What a student PC pins: the CA certificate with no private key attached.</summary>
    internal static X509Certificate2 PublicOnly(X509Certificate2 authority) =>
        X509CertificateLoader.LoadCertificate(authority.Export(X509ContentType.Cert));
}
