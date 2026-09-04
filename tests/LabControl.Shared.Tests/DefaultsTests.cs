using Xunit;

using LabControl.Shared;

namespace LabControl.Shared.Tests;

/// <summary>
/// Defaults is referenced by every component, so its invariants are worth asserting:
/// a typo here would surface as a mysterious runtime failure on a student PC.
/// </summary>
public class DefaultsTests
{
    [Fact]
    public void Ports_are_distinct_and_in_the_private_range()
    {
        int[] ports = [Defaults.ConsolePort, Defaults.BeaconPort];

        Assert.Equal(ports.Length, ports.Distinct().Count());
        Assert.All(ports, port => Assert.InRange(port, 1024, 65535));
        Assert.Equal(9, Defaults.WolPort);
    }

    [Fact]
    public void Heartbeat_timeout_leaves_room_for_several_missed_beats()
    {
        // One missed heartbeat must not mark a working PC offline.
        Assert.True(Defaults.HeartbeatTimeout >= Defaults.HeartbeatInterval * 3);
    }

    [Fact]
    public void Reconnect_backoff_is_a_range()
    {
        Assert.True(Defaults.ReconnectDelayMin > TimeSpan.Zero);
        Assert.True(Defaults.ReconnectDelayMax > Defaults.ReconnectDelayMin);
    }

    [Fact]
    public void Beacon_is_sent_far_more_often_than_its_accepted_skew()
    {
        Assert.True(Defaults.BeaconInterval < Defaults.BeaconMaxSkew);
    }

    [Fact]
    public void Machine_name_format_pads_to_two_digits()
    {
        Assert.Equal("PC-07", string.Format(Defaults.MachineNameFormat, 7));
        Assert.Equal("PC-14", string.Format(Defaults.MachineNameFormat, 14));
        Assert.Equal("PC-30", string.Format(Defaults.MachineNameFormat, Defaults.MaxStudentPcs));
    }

    [Fact]
    public void Lab_is_designed_for_thirty_pcs()
    {
        // D-17: the first lab has 14, the design target is 30. Nothing may assume 14.
        Assert.Equal(30, Defaults.MaxStudentPcs);
    }

    [Fact]
    public void Console_data_directory_is_absolute()
    {
        Assert.True(Path.IsPathRooted(Defaults.ConsoleDataDirectory));
    }

    [Fact]
    public void Key_derivation_is_expensive_enough_to_be_worth_doing()
    {
        Assert.True(Defaults.KeyDerivationIterations >= 600_000);
        Assert.Equal(32, Defaults.MasterKeyBytes);
    }

    [Fact]
    public void Certificate_lifetimes_are_ordered_by_how_hard_they_are_to_replace()
    {
        // Re-minting a console leaf is a ten-minute job; re-issuing the CA means
        // visiting every PC. The easier something is to replace, the shorter it lives.
        Assert.True(Defaults.ConsoleCertificateLifetime < Defaults.AgentCertificateLifetime);
        Assert.True(Defaults.AgentCertificateLifetime < Defaults.LabAuthorityLifetime);
    }

    [Fact]
    public void Exam_hard_limit_is_longer_than_a_lesson_but_not_a_day()
    {
        // D-16: a crashed console must never strand a PC. The limit has to outlast a
        // real exam and still expire on the same day.
        Assert.True(Defaults.ExamHardLimit >= TimeSpan.FromHours(2));
        Assert.True(Defaults.ExamHardLimit <= TimeSpan.FromHours(8));
    }

    [Fact]
    public void Thumbnail_is_cheaper_than_full_video()
    {
        Assert.True(Defaults.ThumbnailJpegQuality < Defaults.FullJpegQuality);
        Assert.True(Defaults.ThumbnailFramesPerSecond < Defaults.FullFramesPerSecond);
    }
}
