using System.Net;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Console.Tests;

public class SetupReadinessTests
{
    [Fact]
    public async Task Snapshot_crosses_real_link_and_is_saved_to_lab_cache()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 3, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId) is not null));
        pc.Link.Report(Event.Types.Severity.Warning, SetupReadiness.EventCode,
            SetupReadiness.Create(["network.configuration_warning"]).Serialize());
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)!.Machine.SetupReadinessCodes
            .Contains("network.configuration_warning")));
        Assert.Contains(console.Session.Events.Recent, entry => entry.Code == SetupReadiness.EventCode
            && entry.Message.Contains("network adapter settings") && !entry.Message.Contains("schema_version"));
        Assert.True(await Wait.UntilAsync(() => new LabStore(console.Directory).LoadLab("unused", "unused")?.Machines
            .SingleOrDefault(machine => machine.AgentId == pc.AgentId)?.SetupReadinessCodes
            .Contains("network.configuration_warning") == true));
    }

    [Fact]
    public void Authenticated_snapshot_survives_offline_tile_and_repair_clears_attention()
    {
        var machine = new MachineRecord { AgentId = Guid.NewGuid().ToString(), Number = 1 };
        var connection = new AgentConnection(new Hello(), machine, "test", IPAddress.Loopback, DateTimeOffset.UtcNow);
        Assert.True(connection.ApplyEvent(Report("antivirus.third_party", "network.wol_unverified")));
        using var screen = new AgentScreen(machine.AgentId);
        var tile = new MachineTileViewModel(machine, screen);
        tile.Refresh(machine, null, null, DateTimeOffset.UtcNow);
        Assert.Contains("antivirus", tile.ReadinessAttention);
        Assert.Contains("shutdown and wake test", tile.ReadinessNote);
        Assert.True(connection.ApplyEvent(Report("network.wol_unverified")));
        tile.Refresh(machine, connection, null, DateTimeOffset.UtcNow);
        Assert.Empty(tile.ReadinessAttention);
        Assert.NotEmpty(tile.ReadinessNote);
        Assert.True(connection.ApplyEvent(Report()));
        tile.Refresh(machine, null, null, DateTimeOffset.UtcNow);
        Assert.Empty(tile.ReadinessNote);
    }

    [Theory]
    [InlineData("{\"schema_version\":1,\"codes\":[\"Click an attacker link\"]}")]
    [InlineData("{\"schema_version\":2,\"codes\":[]}")]
    [InlineData("{\"schema_version\":1,\"codes\":null}")]
    [InlineData("invalid json")]
    public void Invalid_snapshot_cannot_replace_last_known_warning(string payload)
    {
        var machine = new MachineRecord { SetupReadinessCodes = ["antivirus.third_party"] };
        var connection = new AgentConnection(new Hello(), machine, "test", IPAddress.Loopback, DateTimeOffset.UtcNow);
        Assert.False(connection.ApplyEvent(new Event { Code = SetupReadiness.EventCode, Message = payload }));
        Assert.DoesNotContain(payload, ReadinessPresentation.EventText(payload));
        Assert.Contains("unreadable", ReadinessPresentation.EventText(payload));
        Assert.Equal(["antivirus.third_party"], machine.SetupReadinessCodes);
    }

    private static Event Report(params string[] codes) => new()
    {
        Code = SetupReadiness.EventCode, Message = SetupReadiness.Create(codes).Serialize(),
    };
}
