using LabControl.Shared.Protocol;

namespace LabControl.Shared.Control;

/// <summary>
/// What the console sends and when (D-36): a mouse move is held and the latest wins, so a
/// pointer sweeping across the picture at the display's rate costs one message per flush
/// instead of one per pixel; everything else goes out at once, behind the move it depends on
/// (a click lands where the pointer is). The window flushes on a short timer while control is
/// on, so a held move is never older than that interval.
/// </summary>
public sealed class InputQueue
{
    private Input? _heldMove;

    /// <summary>A move is waiting for the next <see cref="Flush"/>.</summary>
    public bool HasHeld => _heldMove is not null;

    /// <summary>Hands back what should be sent now, in order; a move returns nothing and is held.</summary>
    public IReadOnlyList<Input> Offer(Input input)
    {
        if (input.Kind == Input.Types.Kind.MouseMove)
        {
            _heldMove = input;
            return [];
        }

        if (_heldMove is { } held)
        {
            _heldMove = null;
            return [held, input];
        }

        return [input];
    }

    /// <summary>The held move, if any; called on the timer.</summary>
    public Input? Flush()
    {
        var held = _heldMove;
        _heldMove = null;
        return held;
    }
}
