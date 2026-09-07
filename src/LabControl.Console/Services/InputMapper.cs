using Avalonia.Input;
using LabControl.Shared.Control;
using LabControl.Shared.Protocol;

namespace LabControl.Console.Services;

/// <summary>
/// Turns what the teacher does in the single-PC window into <c>Input</c> messages (D-36).
/// Text is sent as text — what the Mac's layout produced, dead keys and all — so the PC
/// types the same characters whatever its own layout; keys are sent as keys only when they
/// command (Enter, arrows, F-keys) or when Ctrl, Alt or Win is held, and then by physical
/// position, so Ctrl+C is Ctrl+C on a Ukrainian layout too. On macOS the ⌘ key is sent as
/// Ctrl, because that is what the teacher means by ⌘C; the Windows key has a button of its
/// own. Pressed keys and buttons are remembered, so control can end with everything
/// released. No Avalonia thread here: the window calls in, this hands messages back.
/// </summary>
public sealed class InputMapper
{
    private readonly bool _commandIsControl;
    private readonly HashSet<int> _pressedKeys = [];
    private readonly HashSet<int> _pressedButtons = [];
    private double _wheelRemainderX;
    private double _wheelRemainderY;

    public InputMapper(bool? macOs = null)
    {
        _commandIsControl = macOs ?? OperatingSystem.IsMacOS();
    }

    /// <summary>The ⌘ key is sent as Ctrl (macOS); on Windows and Linux ⌘/Win is the Windows key.</summary>
    public bool CommandIsControl => _commandIsControl;

    /// <summary>Keys sent down and not yet up, by virtual key.</summary>
    public IReadOnlyCollection<int> PressedKeys => _pressedKeys;

    public IReadOnlyCollection<int> PressedButtons => _pressedButtons;

    /// <summary>Ctrl, Alt or Win is down — what makes a letter a shortcut rather than text.</summary>
    public bool ShortcutModifierHeld => _pressedKeys.Overlaps([
        InputMessages.VkControl, InputMessages.VkLControl, InputMessages.VkRControl,
        InputMessages.VkMenu, InputMessages.VkLMenu, InputMessages.VkRMenu,
        InputMessages.VkLWin, InputMessages.VkRWin,
    ]);

    public Input? PointerMoved(double x, double y) => InputMessages.Move(x, y);

    /// <summary>A button went down or up; <c>null</c> for an update that is neither.</summary>
    public Input? PointerButton(PointerUpdateKind kind, double x, double y)
    {
        var (button, pressed) = kind switch
        {
            PointerUpdateKind.LeftButtonPressed => (InputMessages.LeftButton, true),
            PointerUpdateKind.LeftButtonReleased => (InputMessages.LeftButton, false),
            PointerUpdateKind.RightButtonPressed => (InputMessages.RightButton, true),
            PointerUpdateKind.RightButtonReleased => (InputMessages.RightButton, false),
            PointerUpdateKind.MiddleButtonPressed => (InputMessages.MiddleButton, true),
            PointerUpdateKind.MiddleButtonReleased => (InputMessages.MiddleButton, false),
            PointerUpdateKind.XButton1Pressed => (InputMessages.XButton1, true),
            PointerUpdateKind.XButton1Released => (InputMessages.XButton1, false),
            PointerUpdateKind.XButton2Pressed => (InputMessages.XButton2, true),
            PointerUpdateKind.XButton2Released => (InputMessages.XButton2, false),
            _ => (0, false),
        };

        if (button == 0)
        {
            return null;
        }

        if (pressed)
        {
            _pressedButtons.Add(button);
        }
        else
        {
            _pressedButtons.Remove(button);
        }

        return InputMessages.Button(button, pressed, x, y);
    }

    /// <summary>
    /// Wheel deltas in notches (a trackpad gives fractions): sent as whole wheel units, the
    /// way a precision touchpad reports them, with the fraction of a unit carried over so
    /// slow scrolling is not lost.
    /// </summary>
    public IReadOnlyList<Input> Wheel(double deltaX, double deltaY, double x, double y)
    {
        var result = new List<Input>(2);
        _wheelRemainderY += deltaY * InputMessages.WheelNotch;
        _wheelRemainderX += deltaX * InputMessages.WheelNotch;

        var vertical = (int)Math.Truncate(_wheelRemainderY);
        if (vertical != 0)
        {
            _wheelRemainderY -= vertical;
            result.Add(InputMessages.Wheel(vertical, horizontal: false, x, y));
        }

        var horizontal = (int)Math.Truncate(_wheelRemainderX);
        if (horizontal != 0)
        {
            _wheelRemainderX -= horizontal;
            result.Add(InputMessages.Wheel(horizontal, horizontal: true, x, y));
        }

        return result;
    }

    /// <summary>
    /// A key went down. Modifiers and command keys are sent as keys; a character key is
    /// sent as a key only under Ctrl/Alt/Win — otherwise the <c>TextInput</c> that follows
    /// carries it. <c>null</c> when nothing is sent.
    /// </summary>
    public Input? KeyDown(PhysicalKey key) => KeyEvent(key, pressed: true);

    public Input? KeyUp(PhysicalKey key) => KeyEvent(key, pressed: false);

    private Input? KeyEvent(PhysicalKey key, bool pressed)
    {
        var mapped = Translate(key);
        if (mapped is not { } m)
        {
            return null;
        }

        var isModifier = KeyMap.IsModifierKey(key);
        if (!isModifier && !KeyMap.IsCommandKey(key))
        {
            // A character key: a key event only as part of a shortcut. On release, send the
            // up only if the down went out, so a shortcut released after the modifier still ends.
            if (pressed ? !ShortcutModifierHeld : !_pressedKeys.Contains(m.Vk))
            {
                return null;
            }
        }

        if (pressed)
        {
            _pressedKeys.Add(m.Vk);
        }
        else
        {
            _pressedKeys.Remove(m.Vk);
        }

        return InputMessages.Key(m.Vk, m.Scan, pressed);
    }

    /// <summary>
    /// Text the platform produced: sent as it is unless a shortcut modifier is held (then the
    /// key event already went, and macOS' ⌥-characters must not be typed as well). Control
    /// characters never travel as text — Enter and Tab are keys.
    /// </summary>
    public Input? Text(string? text)
    {
        if (string.IsNullOrEmpty(text) || ShortcutModifierHeld)
        {
            return null;
        }

        var clean = new string(text.Where(c => c >= ' ' && c != '\x7F').ToArray());
        return clean.Length == 0 ? null : InputMessages.Text(clean);
    }

    /// <summary>Everything still down goes up: control ended, the window lost focus, or it closed.</summary>
    public IReadOnlyList<Input> ReleaseAll()
    {
        var result = new List<Input>();
        foreach (var vk in _pressedKeys)
        {
            result.Add(InputMessages.Key(vk, 0, pressed: false));
        }

        foreach (var button in _pressedButtons)
        {
            result.Add(InputMessages.Button(button, pressed: false, 0.5, 0.5));
        }

        _pressedKeys.Clear();
        _pressedButtons.Clear();
        return result;
    }

    /// <summary>A tap of the Windows key, for the toolbar button.</summary>
    public static IReadOnlyList<Input> WindowsKeyTap() =>
    [
        InputMessages.Key(InputMessages.VkLWin, 0xE05B, pressed: true),
        InputMessages.Key(InputMessages.VkLWin, 0xE05B, pressed: false),
    ];

    private (int Vk, int Scan)? Translate(PhysicalKey key)
    {
        // ⌘ on the Mac means Ctrl on the PC (⌘C copies there too); Ctrl on the Mac is Ctrl as well.
        if (_commandIsControl && key is PhysicalKey.MetaLeft or PhysicalKey.MetaRight)
        {
            return (InputMessages.VkLControl, 0x1D);
        }

        return KeyMap.Lookup(key);
    }
}
