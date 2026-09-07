using Avalonia.Input;
using LabControl.Shared.Control;

namespace LabControl.Console.Services;

/// <summary>
/// Avalonia's <see cref="PhysicalKey"/> — the key by its position, whatever the teacher's
/// layout — to the Windows virtual-key and set-1 scan code the student PC expects (D-36).
/// Only keys that are sent as keys are here: shortcuts (Ctrl+C is Ctrl plus the key at the
/// C position, on any layout), editing and navigation keys, function keys and modifiers.
/// Letters typed without Ctrl/Alt/Win never come through here — they travel as text.
/// </summary>
public static class KeyMap
{
    private const int Extended = 0xE000;

    /// <summary>The Windows key code and scan code for a physical key; <c>null</c> for keys Windows has no name for.</summary>
    public static (int Vk, int Scan)? Lookup(PhysicalKey key) => key switch
    {
        // Letters: the VK is the ASCII letter, the scan code is the US position.
        PhysicalKey.A => ('A', 0x1E),
        PhysicalKey.B => ('B', 0x30),
        PhysicalKey.C => ('C', 0x2E),
        PhysicalKey.D => ('D', 0x20),
        PhysicalKey.E => ('E', 0x12),
        PhysicalKey.F => ('F', 0x21),
        PhysicalKey.G => ('G', 0x22),
        PhysicalKey.H => ('H', 0x23),
        PhysicalKey.I => ('I', 0x17),
        PhysicalKey.J => ('J', 0x24),
        PhysicalKey.K => ('K', 0x25),
        PhysicalKey.L => ('L', 0x26),
        PhysicalKey.M => ('M', 0x32),
        PhysicalKey.N => ('N', 0x31),
        PhysicalKey.O => ('O', 0x18),
        PhysicalKey.P => ('P', 0x19),
        PhysicalKey.Q => ('Q', 0x10),
        PhysicalKey.R => ('R', 0x13),
        PhysicalKey.S => ('S', 0x1F),
        PhysicalKey.T => ('T', 0x14),
        PhysicalKey.U => ('U', 0x16),
        PhysicalKey.V => ('V', 0x2F),
        PhysicalKey.W => ('W', 0x11),
        PhysicalKey.X => ('X', 0x2D),
        PhysicalKey.Y => ('Y', 0x15),
        PhysicalKey.Z => ('Z', 0x2C),

        PhysicalKey.Digit1 => ('1', 0x02),
        PhysicalKey.Digit2 => ('2', 0x03),
        PhysicalKey.Digit3 => ('3', 0x04),
        PhysicalKey.Digit4 => ('4', 0x05),
        PhysicalKey.Digit5 => ('5', 0x06),
        PhysicalKey.Digit6 => ('6', 0x07),
        PhysicalKey.Digit7 => ('7', 0x08),
        PhysicalKey.Digit8 => ('8', 0x09),
        PhysicalKey.Digit9 => ('9', 0x0A),
        PhysicalKey.Digit0 => ('0', 0x0B),

        PhysicalKey.Escape => (InputMessages.VkEscape, 0x01),
        PhysicalKey.Backspace => (InputMessages.VkBack, 0x0E),
        PhysicalKey.Tab => (InputMessages.VkTab, 0x0F),
        PhysicalKey.Enter => (InputMessages.VkReturn, 0x1C),
        PhysicalKey.Space => (InputMessages.VkSpace, 0x39),
        PhysicalKey.Minus => (InputMessages.VkOemMinus, 0x0C),
        PhysicalKey.Equal => (InputMessages.VkOemPlus, 0x0D),
        PhysicalKey.BracketLeft => (InputMessages.VkOem4, 0x1A),
        PhysicalKey.BracketRight => (InputMessages.VkOem6, 0x1B),
        PhysicalKey.Backslash => (InputMessages.VkOem5, 0x2B),
        PhysicalKey.Semicolon => (InputMessages.VkOem1, 0x27),
        PhysicalKey.Quote => (InputMessages.VkOem7, 0x28),
        PhysicalKey.Backquote => (InputMessages.VkOem3, 0x29),
        PhysicalKey.Comma => (InputMessages.VkOemComma, 0x33),
        PhysicalKey.Period => (InputMessages.VkOemPeriod, 0x34),
        PhysicalKey.Slash => (InputMessages.VkOem2, 0x35),
        PhysicalKey.IntlBackslash => (InputMessages.VkOem102, 0x56),

        PhysicalKey.F1 => (InputMessages.VkF1, 0x3B),
        PhysicalKey.F2 => (InputMessages.VkF1 + 1, 0x3C),
        PhysicalKey.F3 => (InputMessages.VkF1 + 2, 0x3D),
        PhysicalKey.F4 => (InputMessages.VkF1 + 3, 0x3E),
        PhysicalKey.F5 => (InputMessages.VkF1 + 4, 0x3F),
        PhysicalKey.F6 => (InputMessages.VkF1 + 5, 0x40),
        PhysicalKey.F7 => (InputMessages.VkF1 + 6, 0x41),
        PhysicalKey.F8 => (InputMessages.VkF1 + 7, 0x42),
        PhysicalKey.F9 => (InputMessages.VkF1 + 8, 0x43),
        PhysicalKey.F10 => (InputMessages.VkF1 + 9, 0x44),
        PhysicalKey.F11 => (InputMessages.VkF1 + 10, 0x57),
        PhysicalKey.F12 => (InputMessages.VkF1 + 11, 0x58),

        PhysicalKey.ShiftLeft => (InputMessages.VkLShift, 0x2A),
        PhysicalKey.ShiftRight => (InputMessages.VkRShift, 0x36),
        PhysicalKey.ControlLeft => (InputMessages.VkLControl, 0x1D),
        PhysicalKey.ControlRight => (InputMessages.VkRControl, Extended | 0x1D),
        PhysicalKey.AltLeft => (InputMessages.VkLMenu, 0x38),
        PhysicalKey.AltRight => (InputMessages.VkRMenu, Extended | 0x38),
        PhysicalKey.MetaLeft => (InputMessages.VkLWin, Extended | 0x5B),
        PhysicalKey.MetaRight => (InputMessages.VkRWin, Extended | 0x5C),
        PhysicalKey.ContextMenu => (InputMessages.VkApps, Extended | 0x5D),

        PhysicalKey.Insert => (InputMessages.VkInsert, Extended | 0x52),
        PhysicalKey.Delete => (InputMessages.VkDelete, Extended | 0x53),
        PhysicalKey.Home => (InputMessages.VkHome, Extended | 0x47),
        PhysicalKey.End => (InputMessages.VkEnd, Extended | 0x4F),
        PhysicalKey.PageUp => (InputMessages.VkPrior, Extended | 0x49),
        PhysicalKey.PageDown => (InputMessages.VkNext, Extended | 0x51),
        PhysicalKey.ArrowUp => (InputMessages.VkUp, Extended | 0x48),
        PhysicalKey.ArrowDown => (InputMessages.VkDown, Extended | 0x50),
        PhysicalKey.ArrowLeft => (InputMessages.VkLeft, Extended | 0x4B),
        PhysicalKey.ArrowRight => (InputMessages.VkRight, Extended | 0x4D),
        PhysicalKey.PrintScreen => (InputMessages.VkSnapshot, Extended | 0x37),
        PhysicalKey.ScrollLock => (InputMessages.VkScroll, 0x46),
        PhysicalKey.Pause => (InputMessages.VkPause, 0x45),

        PhysicalKey.NumPad0 => (InputMessages.VkNumpad0, 0x52),
        PhysicalKey.NumPad1 => (InputMessages.VkNumpad0 + 1, 0x4F),
        PhysicalKey.NumPad2 => (InputMessages.VkNumpad0 + 2, 0x50),
        PhysicalKey.NumPad3 => (InputMessages.VkNumpad0 + 3, 0x51),
        PhysicalKey.NumPad4 => (InputMessages.VkNumpad0 + 4, 0x4B),
        PhysicalKey.NumPad5 => (InputMessages.VkNumpad0 + 5, 0x4C),
        PhysicalKey.NumPad6 => (InputMessages.VkNumpad0 + 6, 0x4D),
        PhysicalKey.NumPad7 => (InputMessages.VkNumpad0 + 7, 0x47),
        PhysicalKey.NumPad8 => (InputMessages.VkNumpad0 + 8, 0x48),
        PhysicalKey.NumPad9 => (InputMessages.VkNumpad0 + 9, 0x49),
        PhysicalKey.NumPadDecimal => (InputMessages.VkDecimal, 0x53),
        PhysicalKey.NumPadAdd => (InputMessages.VkAdd, 0x4E),
        PhysicalKey.NumPadSubtract => (InputMessages.VkSubtract, 0x4A),
        PhysicalKey.NumPadMultiply => (InputMessages.VkMultiply, 0x37),
        PhysicalKey.NumPadDivide => (InputMessages.VkDivide, Extended | 0x35),
        PhysicalKey.NumPadEnter => (InputMessages.VkReturn, Extended | 0x1C),

        _ => null,
    };

    /// <summary>
    /// A key that is sent as a key even without a modifier held: it edits, navigates or
    /// commands rather than producing a character. Everything else travels as text unless
    /// Ctrl, Alt or Win is down.
    /// </summary>
    public static bool IsCommandKey(PhysicalKey key) => key switch
    {
        PhysicalKey.Escape or PhysicalKey.Backspace or PhysicalKey.Tab or PhysicalKey.Enter or PhysicalKey.NumPadEnter
            or PhysicalKey.Insert or PhysicalKey.Delete or PhysicalKey.Home or PhysicalKey.End
            or PhysicalKey.PageUp or PhysicalKey.PageDown
            or PhysicalKey.ArrowUp or PhysicalKey.ArrowDown or PhysicalKey.ArrowLeft or PhysicalKey.ArrowRight
            or PhysicalKey.F1 or PhysicalKey.F2 or PhysicalKey.F3 or PhysicalKey.F4 or PhysicalKey.F5 or PhysicalKey.F6
            or PhysicalKey.F7 or PhysicalKey.F8 or PhysicalKey.F9 or PhysicalKey.F10 or PhysicalKey.F11 or PhysicalKey.F12
            or PhysicalKey.PrintScreen or PhysicalKey.ScrollLock or PhysicalKey.Pause or PhysicalKey.ContextMenu => true,
        _ => false,
    };

    /// <summary>Shift, Ctrl, Alt and the Win/⌘ keys.</summary>
    public static bool IsModifierKey(PhysicalKey key) => key is PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight
        or PhysicalKey.ControlLeft or PhysicalKey.ControlRight
        or PhysicalKey.AltLeft or PhysicalKey.AltRight
        or PhysicalKey.MetaLeft or PhysicalKey.MetaRight;
}
