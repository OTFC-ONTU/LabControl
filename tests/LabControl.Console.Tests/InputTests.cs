using Avalonia.Input;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared.Control;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// Remote control (M3 portion 3, PROTOCOL "Input", D-36): the window's events become the
/// right messages, they reach a PC over the real link, Ctrl+Alt+Del is answered, and the
/// tile says why there is no picture when the PC gave a reason.
/// </summary>
public class InputTests
{
    [Fact]
    public void Text_travels_as_text_and_shortcuts_as_keys_by_physical_position()
    {
        var mac = new InputMapper(macOs: true);

        // Typing "a": the key event is nothing, the text is the letter.
        Assert.Null(mac.KeyDown(PhysicalKey.A));
        Assert.Equal("a", mac.Text("a")!.Text);
        Assert.Null(mac.KeyUp(PhysicalKey.A));

        // Ukrainian text is text too, whatever the PC's layout.
        Assert.Equal("Привіт, світ", mac.Text("Привіт, світ")!.Text);

        // ⌘C on the Mac is Ctrl down, the key at the C position down and up, Ctrl up — and no text.
        var control = mac.KeyDown(PhysicalKey.MetaLeft)!;
        Assert.Equal((InputMessages.VkLControl, true), (control.Vk, control.Pressed));
        var c = mac.KeyDown(PhysicalKey.C)!;
        Assert.Equal(('C', 0x2E, true), (c.Vk, c.Scan, c.Pressed));
        Assert.Null(mac.Text("c"));
        Assert.Null(mac.Text("с")); // Cyrillic es from a Ukrainian layout: the shortcut already went
        Assert.False(mac.KeyUp(PhysicalKey.C)!.Pressed);
        Assert.False(mac.KeyUp(PhysicalKey.MetaLeft)!.Pressed);
        Assert.Empty(mac.PressedKeys);

        // Command keys go as keys on their own; the extended ones carry the 0xE0 prefix.
        var enter = mac.KeyDown(PhysicalKey.Enter)!;
        Assert.Equal((InputMessages.VkReturn, 0x1C), (enter.Vk, enter.Scan));
        mac.KeyUp(PhysicalKey.Enter);
        var up = mac.KeyDown(PhysicalKey.ArrowUp)!;
        Assert.Equal((InputMessages.VkUp, 0xE048), (up.Vk, up.Scan));
        mac.KeyUp(PhysicalKey.ArrowUp);

        // Enter never travels as text.
        Assert.Null(mac.Text("\r"));

        // On Windows the Meta key is the Windows key.
        var windows = new InputMapper(macOs: false);
        Assert.Equal(InputMessages.VkLWin, windows.KeyDown(PhysicalKey.MetaLeft)!.Vk);
    }

    [Fact]
    public void Wheel_fractions_accumulate_and_release_all_lets_go_of_everything_held()
    {
        var mapper = new InputMapper(macOs: true);

        Assert.Equal(48, mapper.Wheel(0, 0.4, 0.5, 0.5)[0].Delta);
        Assert.Empty(mapper.Wheel(0, 0.005, 0.5, 0.5));
        var sent = mapper.Wheel(0, 0.7, 0.5, 0.5);
        Assert.Single(sent);
        Assert.Equal((InputMessages.VerticalWheel, 84), (sent[0].Button, sent[0].Delta));
        Assert.Equal(InputMessages.HorizontalWheel, mapper.Wheel(-1, 0, 0.5, 0.5)[0].Button);

        mapper.KeyDown(PhysicalKey.ShiftLeft);
        mapper.PointerButton(PointerUpdateKind.LeftButtonPressed, 0.3, 0.3);
        Assert.Null(mapper.PointerButton(PointerUpdateKind.Other, 0.3, 0.3));
        Assert.Equal([InputMessages.VkLShift], mapper.PressedKeys);
        Assert.Equal([InputMessages.LeftButton], mapper.PressedButtons);

        var released = mapper.ReleaseAll();
        Assert.Equal(2, released.Count);
        Assert.All(released, r => Assert.False(r.Pressed));
        Assert.Empty(mapper.PressedKeys);
        Assert.Empty(mapper.PressedButtons);
    }

    [Fact]
    public async Task Input_reaches_a_simulated_pc_and_ctrl_alt_del_is_answered_with_an_event()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1);
        var store = LabControl.FakeAgent.FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-07"), 7,
            LabControl.Shared.Setup.SetupPayload.Open(payload), System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var machine = new LabControl.FakeAgent.FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-07"));
        machine.Start();

        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(machine.AgentId) && machine.Desktop is not null, TimeSpan.FromSeconds(20)));
        var tile = new MachineTileViewModel(console.Session.Registry.Document.Machines.Single(m => m.AgentId == machine.AgentId), console.Session.Screens.Get(machine.AgentId));
        var posted = new List<Action>();
        var window = new ScreenViewModel(console.Session, tile, posted.Add, macOs: true);
        window.Open();
        window.Tick();
        Assert.True(window.CanControl, window.ControlProblem);

        // Nothing goes out until Control is on.
        window.Send(window.Mapper.PointerMoved(0.5, 0.5));
        window.FlushInput();
        Assert.Equal(0, window.InputsSent);

        window.IsControlling = true;
        window.Send(window.Mapper.PointerMoved(0.25, 0.75));
        window.FlushInput();
        window.Send(window.Mapper.PointerButton(PointerUpdateKind.LeftButtonPressed, 0.25, 0.75));
        window.Send(window.Mapper.PointerButton(PointerUpdateKind.LeftButtonReleased, 0.25, 0.75));
        window.Send(window.Mapper.Text("Привіт"));
        window.Send(window.Mapper.KeyDown(PhysicalKey.Backspace));
        window.Send(window.Mapper.KeyUp(PhysicalKey.Backspace));
        window.Send(window.Mapper.Wheel(0, -1, 0.25, 0.75));
        Assert.Equal(7, window.InputsSent);

        var desktop = machine.Desktop!;
        Assert.True(await Wait.UntilAsync(() => desktop.InputsApplied >= 7, TimeSpan.FromSeconds(10)), $"{desktop.InputsApplied} inputs applied");
        Assert.Equal((0.25, 0.75), desktop.Pointer);
        Assert.Equal(InputMessages.LeftButton, desktop.LastButton);
        Assert.Equal("Приві", desktop.TeacherText);
        Assert.Equal(-InputMessages.WheelNotch, desktop.WheelTotal);

        // Ctrl+Alt+Del: the service's job, answered as an event so the teacher sees it arrived.
        window.SendCtrlAltDelCommand.Execute(null);
        Assert.True(await Wait.UntilAsync(() => machine.SecureAttentionCount == 1));
        Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Any(e => e.Code == "input.sas")));

        // Turning control off releases what is held; a key still down goes up on the PC.
        window.Send(window.Mapper.KeyDown(PhysicalKey.ShiftLeft));
        var before = window.InputsSent;
        window.IsControlling = false;
        Assert.Equal(before + 1, window.InputsSent);
        Assert.Empty(window.Mapper.PressedKeys);

        window.Close();
        Assert.True(await Wait.UntilAsync(() => machine.Link.VideoControl is { Mode: VideoMode.Thumbnail }));
    }

    [Fact]
    public async Task A_capture_problem_the_pc_reports_is_the_reason_on_the_tile_until_it_recovers()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];
        await using var pc = TestAgent.Install(console, 3, code).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(pc.AgentId)));

        pc.Link.PublishSessionState(new SessionState { User = "student", SessionId = 1, HelperAlive = true });
        pc.Link.Report(Event.Types.Severity.Warning, "capture.no_duplication", "Cannot capture the screen: the display adapter offers no duplication.");
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)?.CaptureProblem == "no_duplication"));

        var machine = console.Session.Registry.Document.Machines.Single(m => m.AgentId == pc.AgentId);
        var tile = new MachineTileViewModel(machine, console.Session.Screens.Get(pc.AgentId));
        tile.Refresh(machine, console.Session.FindLinked(pc.AgentId), null, console.Session.Now);
        Assert.Equal("cannot capture: duplication unavailable", tile.PictureNote);
        Assert.False(tile.HelperDown);

        // The single-PC window says the same, and still allows control (the desktop is there, only the picture is not).
        var window = new ScreenViewModel(console.Session, tile, _ => { }, macOs: true);
        window.Tick();
        Assert.True(window.CanControl);
        Assert.Contains("duplication unavailable", window.Status);

        pc.Link.Report(Event.Types.Severity.Info, "capture.recovered", "Screen capture is back (dxgi).");
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)?.CaptureProblem is null));
        tile.Refresh(machine, console.Session.FindLinked(pc.AgentId), null, console.Session.Now);
        Assert.Equal("no picture yet", tile.PictureNote);

        // No interactive session at all: the tile says so, and control is off the table.
        pc.Link.PublishSessionState(new SessionState { User = string.Empty, SessionId = 0, HelperAlive = false });
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)?.NoSession == true));
        tile.Refresh(machine, console.Session.FindLinked(pc.AgentId), null, console.Session.Now);
        Assert.Equal("no user session", tile.PictureNote);
        Assert.False(tile.HelperDown);
        window.Tick();
        Assert.False(window.CanControl);
        Assert.Contains("nobody is logged on", window.Status);

        // The helper down with a session up is the old note.
        pc.Link.PublishSessionState(new SessionState { User = "student", SessionId = 1, HelperAlive = false });
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId) is { HelperAlive: false, NoSession: false }));
        tile.Refresh(machine, console.Session.FindLinked(pc.AgentId), null, console.Session.Now);
        Assert.Equal("session helper not running", tile.PictureNote);
        Assert.True(tile.HelperDown);
    }

    [Fact]
    public async Task Input_to_a_pc_that_is_not_linked_is_refused_and_an_old_agent_ignores_it()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];
        await using var pc = TestAgent.Install(console, 5, code).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(pc.AgentId)));

        // The scripted agent has no input handler: the message is delivered, logged and dropped, the link lives on.
        Assert.True(console.Session.SendInput(pc.AgentId, InputMessages.Text("hello")));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.True(console.Session.IsLinked(pc.AgentId));

        Assert.False(console.Session.SendInput("no-such-agent", InputMessages.Text("hello")));
    }
}
