using LabControl.Shared.Control;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>The input vocabulary and the console's send queue (PROTOCOL "Input", D-36).</summary>
public class InputTests
{
    [Fact]
    public void Coordinates_are_normalised_and_never_leave_the_screen()
    {
        Assert.Equal((0.25, 0.5), (InputMessages.Move(0.25, 0.5).X, InputMessages.Move(0.25, 0.5).Y));
        Assert.Equal((0, 1), (InputMessages.Move(-3, 7).X, InputMessages.Move(-3, 7).Y));
        Assert.Equal(0, InputMessages.Button(InputMessages.LeftButton, true, double.NaN, 0).X);
    }

    [Fact]
    public void Each_kind_carries_what_the_helper_needs()
    {
        var button = InputMessages.Button(InputMessages.RightButton, pressed: true, 0.1, 0.2);
        Assert.Equal((Input.Types.Kind.MouseButton, InputMessages.RightButton, true), (button.Kind, button.Button, button.Pressed));

        var wheel = InputMessages.Wheel(-InputMessages.WheelNotch, horizontal: true, 0, 0);
        Assert.Equal((Input.Types.Kind.MouseWheel, InputMessages.HorizontalWheel, -120), (wheel.Kind, wheel.Button, wheel.Delta));

        var key = InputMessages.Key(InputMessages.VkUp, 0xE048, pressed: false);
        Assert.Equal((Input.Types.Kind.Key, 0x26, 0xE048, false), (key.Kind, key.Vk, key.Scan, key.Pressed));

        Assert.Equal("Привіт", InputMessages.Text("Привіт").Text);
        Assert.Equal(Input.Types.Kind.CtrlAltDel, InputMessages.CtrlAltDel().Kind);
        Assert.True(InputMessages.IsModifier(InputMessages.VkLControl));
        Assert.False(InputMessages.IsModifier('A'));
    }

    [Fact]
    public void The_queue_holds_the_latest_move_and_sends_everything_else_at_once_in_order()
    {
        var queue = new InputQueue();

        Assert.Empty(queue.Offer(InputMessages.Move(0.1, 0.1)));
        Assert.Empty(queue.Offer(InputMessages.Move(0.2, 0.2)));
        Assert.True(queue.HasHeld);

        // A click goes out behind the move it depends on — the latest one, not the first.
        var sent = queue.Offer(InputMessages.Button(InputMessages.LeftButton, true, 0.2, 0.2));
        Assert.Equal(2, sent.Count);
        Assert.Equal((Input.Types.Kind.MouseMove, 0.2), (sent[0].Kind, sent[0].X));
        Assert.Equal(Input.Types.Kind.MouseButton, sent[1].Kind);
        Assert.False(queue.HasHeld);

        // Text without a move ahead of it goes alone; the timer flush finds nothing.
        Assert.Single(queue.Offer(InputMessages.Text("a")));
        Assert.Null(queue.Flush());

        // A move waits for the flush.
        queue.Offer(InputMessages.Move(0.3, 0.3));
        Assert.Equal(0.3, queue.Flush()!.X);
        Assert.Null(queue.Flush());
    }
}
