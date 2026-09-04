using Xunit;

using Google.Protobuf;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Tests;

/// <summary>
/// D-21: revocation entries are signed by the lab key and merged as a <b>set</b>, with no
/// version and no owner. That is what lets two teacher machines revoke independently, lets
/// a console learn from the first agent that connects what the other console revoked, and
/// stops anyone being talked into a revocation by a TLS peer alone.
/// </summary>
public sealed class RevocationTests
{
    [Fact]
    public void An_entry_signed_by_the_lab_key_is_accepted_by_anyone_holding_the_ca()
    {
        using var lab = TestLab.Create();
        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        var entry = RevocationSet.Create(lab, "00AABB", "stolen laptop", DateTimeOffset.UtcNow);

        var set = new RevocationSet();
        Assert.True(set.TryAdd(pinned, entry));
        Assert.True(set.IsRevoked("aabb"));
        Assert.True(set.IsRevoked("00AABB"));
        Assert.False(set.IsRevoked("00AABC"));
    }

    [Fact]
    public void A_forged_entry_offered_by_a_peer_is_ignored()
    {
        using var lab = TestLab.Create();
        using var otherLab = TestLab.Create();
        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        var set = new RevocationSet();

        var unsigned = new RevocationEntry { Serial = "AABB", RevokedAtUnix = 1, Reason = "made up" };
        var wrongKey = RevocationSet.Create(otherLab, "AABB", "made up", DateTimeOffset.UtcNow);
        var edited = RevocationSet.Create(lab, "AABB", "stolen", DateTimeOffset.UtcNow);
        edited.Serial = "CCDD";   // same signature, different serial

        Assert.False(set.TryAdd(pinned, unsigned));
        Assert.False(set.TryAdd(pinned, wrongKey));
        Assert.False(set.TryAdd(pinned, edited));
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void Two_teacher_machines_that_revoked_separately_converge_on_the_union()
    {
        using var lab = TestLab.Create();
        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        var now = DateTimeOffset.UtcNow;

        var macBook = new RevocationSet();
        var deskPc = new RevocationSet();

        var revokedOnMac = RevocationSet.Create(lab, "AAAA", "lost", now);
        var revokedOnDesk = RevocationSet.Create(lab, "BBBB", "stolen", now);
        macBook.TryAdd(pinned, revokedOnMac);
        deskPc.TryAdd(pinned, revokedOnDesk);

        // An agent carries what it holds to whichever console it meets next.
        var agent = new RevocationSet();
        agent.Merge(pinned, macBook.Entries);
        agent.Merge(pinned, deskPc.Entries);

        var newToDesk = deskPc.Merge(pinned, agent.Entries);

        Assert.Equal("AAAA", Assert.Single(newToDesk).Serial);
        Assert.Equal(2, deskPc.Count);
        Assert.True(deskPc.IsRevoked("AAAA"));
        Assert.True(deskPc.IsRevoked("BBBB"));

        // Merging again changes nothing: the set converges, in any order.
        Assert.Empty(deskPc.Merge(pinned, agent.Entries));
    }

    [Fact]
    public void A_console_only_sends_the_entries_the_agent_is_missing()
    {
        using var lab = TestLab.Create();
        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        var now = DateTimeOffset.UtcNow;

        var console = new RevocationSet();
        console.TryAdd(pinned, RevocationSet.Create(lab, "AAAA", "lost", now));
        console.TryAdd(pinned, RevocationSet.Create(lab, "BBBB", "stolen", now));

        var missing = console.Except(["aaaa"]);

        Assert.Equal("BBBB", Assert.Single(missing).Serial);
        Assert.Empty(console.Except(["AAAA", "BBBB"]));
    }

    [Fact]
    public void An_entry_survives_the_trip_through_lab_json_and_the_wire()
    {
        using var lab = TestLab.Create();
        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        var entry = RevocationSet.Create(lab, "00FF", "stolen laptop", DateTimeOffset.UtcNow);

        var document = new LabDocument { Revocations = [RevocationRecord.From(entry)] };
        var reloaded = JsonStore.Parse<LabDocument>(
            JsonStore.Serialize(document, LabDocument.Migrations), "lab.json", LabDocument.Migrations);

        var restored = Assert.Single(reloaded.Revocations).ToEntry();
        Assert.True(RevocationSet.Verify(pinned, restored));

        // And through protobuf, which is how it reaches an agent.
        var overTheWire = RevocationEntry.Parser.ParseFrom(restored.ToByteArray());
        Assert.True(RevocationSet.Verify(pinned, overTheWire));
    }

    [Fact]
    public void Revoking_a_stolen_console_stops_it_at_the_next_agent_that_connects()
    {
        using var lab = TestLab.Create();
        var pinned = LabTrustTests.PublicOnly(lab.Authority);
        using var stolen = ConsoleInstance.Mint(lab, "Stolen laptop", new FileSecretProtector());

        var agentView = new RevocationSet();
        var trust = LabTrust.FromAuthority(pinned);

        Assert.True(trust.TryValidate(stolen.Certificate, LabRole.Console, agentView, out _, out _));

        agentView.TryAdd(pinned, RevocationSet.Create(lab, stolen.CertificateSerial, "stolen", DateTimeOffset.UtcNow));

        Assert.False(trust.TryValidate(stolen.Certificate, LabRole.Console, agentView, out _, out var failure));
        Assert.Equal(TrustFailure.Revoked, failure);
    }
}
