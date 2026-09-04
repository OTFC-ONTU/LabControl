using Xunit;

using System.Security.Cryptography;
using LabControl.Shared;
using LabControl.Shared.Discovery;
using LabControl.Shared.Identity;
using LabControl.Shared.Protection;

namespace LabControl.Shared.Tests;

/// <summary>
/// The beacon is what makes a change of teacher machine free (D-13): every PC follows a
/// new console because the lab key endorsed it, not because anybody visited the PC. So it
/// has to be verifiable offline, and forging one must be impossible from a student PC,
/// which knows only the public CA certificate.
/// </summary>
public sealed class BeaconTests
{
    [Fact]
    public void A_beacon_from_this_lab_verifies_against_the_pinned_ca_alone()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var instance = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), now);
        var pinned = LabTrustTests.PublicOnly(lab.Authority);

        var beacon = instance.CreateBeacon("192.168.1.23", Defaults.ConsolePort, now);
        Assert.True(Beacon.TryParse(beacon.ToDatagram(), out var received));
        Assert.True(received.TryVerify(pinned, lab.LabId, now, out var failure));

        Assert.Equal(BeaconFailure.None, failure);
        Assert.Equal("MacBook-2026", received.InstanceName);
        Assert.Equal($"192.168.1.23:{Defaults.ConsolePort}", received.Endpoint);
        Assert.Equal(Defaults.BeaconVersion, received.Version);
    }

    [Fact]
    public void A_beacon_fits_the_datagram_budget_even_with_a_long_instance_name()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var instance = ConsoleInstance.Mint(
            lab, "Teacher's desk PC in room 214", new FileSecretProtector(), now);

        var datagram = instance.CreateBeacon("255.255.255.255", Defaults.ConsolePort, now).ToDatagram();

        Assert.InRange(datagram.Length, 1, Defaults.BeaconMaxBytes);
    }

    [Fact]
    public void The_second_teacher_machine_is_followed_without_touching_a_pc()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();

        // The PC pinned this at install time and has never been touched since.
        var pinned = LabTrustTests.PublicOnly(lab.Authority);

        using var macBook = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), now);
        using var deskPc = ConsoleInstance.Mint(lab, "Lab PC", new FileSecretProtector(), now);

        Assert.True(macBook.CreateBeacon("192.168.1.23", Defaults.ConsolePort, now).TryVerify(pinned, lab.LabId, now, out _));
        Assert.True(deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, now).TryVerify(pinned, lab.LabId, now, out _));
        Assert.NotEqual(macBook.InstanceId, deskPc.InstanceId);
    }

    [Fact]
    public void A_beacon_endorsed_by_another_lab_is_rejected_without_a_connection_attempt()
    {
        var now = DateTimeOffset.UtcNow;
        using var ours = TestLab.Create();
        using var theirs = TestLab.Create();
        using var stranger = ConsoleInstance.Mint(theirs, "Someone else", new FileSecretProtector(), now);

        var beacon = stranger.CreateBeacon("192.168.1.99", Defaults.ConsolePort, now);

        // Same lab id claimed, endorsement from the wrong key: the classic forgery.
        beacon.LabId = ours.LabId;
        Assert.False(beacon.TryVerify(LabTrustTests.PublicOnly(ours.Authority), ours.LabId, now, out var failure));
        Assert.Equal(BeaconFailure.BadEndorsement, failure);
    }

    [Fact]
    public void A_beacon_that_names_a_different_lab_is_dropped_first()
    {
        var now = DateTimeOffset.UtcNow;
        using var ours = TestLab.Create();
        using var theirs = TestLab.Create();
        using var stranger = ConsoleInstance.Mint(theirs, "Another college", new FileSecretProtector(), now);

        var beacon = stranger.CreateBeacon("192.168.1.99", Defaults.ConsolePort, now);

        Assert.False(beacon.TryVerify(LabTrustTests.PublicOnly(ours.Authority), ours.LabId, now, out var failure));
        Assert.Equal(BeaconFailure.WrongLab, failure);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("name")]
    [InlineData("take")]
    public void Editing_any_signed_field_breaks_the_signature(string field)
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var instance = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), now);
        var beacon = instance.CreateBeacon("192.168.1.23", Defaults.ConsolePort, now);

        switch (field)
        {
            case "host": beacon.Host = "192.168.1.66"; break;
            case "port": beacon.Port = 4000; break;
            case "name": beacon.InstanceName = "Lab PC"; break;
            case "take": beacon.TakeAtUnix = now.ToUnixTimeSeconds(); break;
        }

        Assert.False(beacon.TryVerify(LabTrustTests.PublicOnly(lab.Authority), lab.LabId, now, out var failure));
        Assert.Equal(BeaconFailure.BadSignature, failure);
    }

    [Fact]
    public void A_replayed_beacon_goes_stale()
    {
        // Whole seconds: the beacon carries a Unix timestamp, so a fractional "now" would
        // make the boundary assertions a second off at random.
        var sent = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var lab = TestLab.Create();
        using var instance = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), sent);
        var beacon = instance.CreateBeacon("192.168.1.23", Defaults.ConsolePort, sent);
        var pinned = LabTrustTests.PublicOnly(lab.Authority);

        Assert.True(beacon.TryVerify(pinned, lab.LabId, sent + Defaults.BeaconMaxSkew, out _));
        Assert.False(beacon.TryVerify(pinned, lab.LabId, sent + Defaults.BeaconMaxSkew + TimeSpan.FromSeconds(1), out var late));
        Assert.Equal(BeaconFailure.StaleTimestamp, late);

        // A beacon from the future is just as suspect as one from the past.
        Assert.False(beacon.TryVerify(pinned, lab.LabId, sent - Defaults.BeaconMaxSkew - TimeSpan.FromSeconds(1), out var early));
        Assert.Equal(BeaconFailure.StaleTimestamp, early);
    }

    [Fact]
    public void Take_over_is_carried_in_the_signed_part_of_the_beacon()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var instance = ConsoleInstance.Mint(lab, "Lab PC", new FileSecretProtector(), now);

        var pressed = now.AddSeconds(-3);
        var beacon = instance.CreateBeacon("192.168.1.40", Defaults.ConsolePort, now, pressed);

        Assert.True(Beacon.TryParse(beacon.ToDatagram(), out var received));
        Assert.True(received.TryVerify(LabTrustTests.PublicOnly(lab.Authority), lab.LabId, now, out _));
        Assert.Equal(pressed.ToUnixTimeSeconds(), received.TakeAtUnix);
    }

    [Fact]
    public void Rubbish_on_the_beacon_port_costs_a_parse_and_nothing_else()
    {
        Assert.False(Beacon.TryParse("not json at all"u8, out _));
        Assert.False(Beacon.TryParse([], out _));
        Assert.False(Beacon.TryParse(new byte[Defaults.BeaconMaxBytes + 1], out _));

        Assert.True(Beacon.TryParse("{\"v\":2}"u8, out var empty));
        using var lab = TestLab.Create();
        Assert.False(empty.TryVerify(LabTrustTests.PublicOnly(lab.Authority), lab.LabId, DateTimeOffset.UtcNow, out var failure));
        Assert.Equal(BeaconFailure.Malformed, failure);
    }

    [Fact]
    public void A_compressed_public_key_survives_the_round_trip()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var key = LabCertificates.CreateKey();
            var compressed = P256.Compress(key);

            Assert.Equal(P256.CompressedLength, compressed.Length);
            Assert.True(P256.TryDecompress(compressed, out var restored));

            using (restored)
            {
                var data = RandomNumberGenerator.GetBytes(32);
                var signature = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                Assert.True(restored.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            }
        }
    }

    [Fact]
    public void A_point_that_is_not_on_the_curve_is_refused()
    {
        Assert.False(P256.TryDecompress(new byte[P256.CompressedLength], out _));
        Assert.False(P256.TryDecompress([0x04, .. new byte[32]], out _));
        Assert.False(P256.TryDecompress(new byte[8], out _));
    }
}
