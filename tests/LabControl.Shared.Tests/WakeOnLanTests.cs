using Xunit;

using System.Net;
using LabControl.Shared;
using LabControl.Shared.Power;

namespace LabControl.Shared.Tests;

/// <summary>The magic packet and where it goes (ARCHITECTURE §6, ROADMAP M2).</summary>
public sealed class WakeOnLanTests
{
    [Fact]
    public void A_magic_packet_is_six_ff_bytes_then_the_mac_sixteen_times()
    {
        var packet = WakeOnLan.MagicPacket("00:11:22:AA:BB:CC");

        Assert.Equal(WakeOnLan.MagicPacketBytes, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0xAA, 0xBB, 0xCC }, packet.Skip(6 + i * 6).Take(6));
        }
    }

    [Theory]
    [InlineData("00:11:22:aa:bb:cc")]
    [InlineData("00-11-22-AA-BB-CC")]
    [InlineData("001122AABBCC")]
    public void Every_common_mac_spelling_is_accepted(string mac)
    {
        Assert.True(WakeOnLan.TryParseMac(mac, out var address));
        Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0xAA, 0xBB, 0xCC }, address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a mac")]
    [InlineData("00:11:22:AA:BB")]
    public void Nonsense_is_refused_not_guessed(string mac)
    {
        Assert.False(WakeOnLan.TryParseMac(mac, out _));
        Assert.Throws<FormatException>(() => WakeOnLan.MagicPacket(mac));
    }

    [Fact]
    public void Destinations_include_the_limited_broadcast_and_the_last_address()
    {
        var destinations = WakeOnLan.Destinations("192.168.1.77");

        Assert.Contains(new IPEndPoint(IPAddress.Broadcast, Defaults.WolPort), destinations);
        Assert.Contains(new IPEndPoint(IPAddress.Parse("192.168.1.77"), Defaults.WolPort), destinations);
        Assert.Equal(destinations.Count, destinations.Distinct().Count());
    }

    [Fact]
    public void A_missing_or_loopback_last_address_adds_nothing()
    {
        Assert.DoesNotContain(WakeOnLan.Destinations(null), d => IPAddress.IsLoopback(d.Address));
        Assert.DoesNotContain(WakeOnLan.Destinations("127.0.0.1"), d => IPAddress.IsLoopback(d.Address));
        Assert.DoesNotContain(WakeOnLan.Destinations("garbage"), d => d.Address.ToString() == "garbage");
    }
}
