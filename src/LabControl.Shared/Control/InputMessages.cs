using LabControl.Shared.Protocol;

namespace LabControl.Shared.Control;

/// <summary>
/// The one vocabulary for <c>Input</c> messages (PROTOCOL "Input", M3 portion 3): what the
/// button numbers mean, what a wheel notch is, and the Windows virtual-key codes the console
/// names when it sends a key rather than text. The console builds the messages, the session
/// helper turns them into <c>SendInput</c>, and the simulator draws them — all three read
/// these constants, so a number never means two things.
/// </summary>
public static class InputMessages
{
    // Input.button on MOUSE_BUTTON.
    public const int LeftButton = 1;
    public const int RightButton = 2;
    public const int MiddleButton = 3;
    public const int XButton1 = 4;
    public const int XButton2 = 5;

    /// <summary>Input.button on MOUSE_WHEEL: 0 is the vertical wheel, 1 the horizontal one.</summary>
    public const int VerticalWheel = 0;
    public const int HorizontalWheel = 1;

    /// <summary>One click of a mouse wheel, in the units <c>Input.delta</c> carries (Windows' <c>WHEEL_DELTA</c>).</summary>
    public const int WheelNotch = 120;

    // Windows virtual-key codes for the keys the console sends by name. Letters and digits
    // are their ASCII codes; the rest are the VK_* values from winuser.h.
    public const int VkBack = 0x08;
    public const int VkTab = 0x09;
    public const int VkReturn = 0x0D;
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkPause = 0x13;
    public const int VkCapital = 0x14;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkPrior = 0x21;
    public const int VkNext = 0x22;
    public const int VkEnd = 0x23;
    public const int VkHome = 0x24;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;
    public const int VkSnapshot = 0x2C;
    public const int VkInsert = 0x2D;
    public const int VkDelete = 0x2E;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkApps = 0x5D;
    public const int VkNumpad0 = 0x60;
    public const int VkMultiply = 0x6A;
    public const int VkAdd = 0x6B;
    public const int VkSubtract = 0x6D;
    public const int VkDecimal = 0x6E;
    public const int VkDivide = 0x6F;
    public const int VkF1 = 0x70;
    public const int VkNumLock = 0x90;
    public const int VkScroll = 0x91;
    public const int VkLShift = 0xA0;
    public const int VkRShift = 0xA1;
    public const int VkLControl = 0xA2;
    public const int VkRControl = 0xA3;
    public const int VkLMenu = 0xA4;
    public const int VkRMenu = 0xA5;
    public const int VkOem1 = 0xBA;      // ;:
    public const int VkOemPlus = 0xBB;   // =+
    public const int VkOemComma = 0xBC;
    public const int VkOemMinus = 0xBD;
    public const int VkOemPeriod = 0xBE;
    public const int VkOem2 = 0xBF;      // /?
    public const int VkOem3 = 0xC0;      // `~
    public const int VkOem4 = 0xDB;      // [{
    public const int VkOem5 = 0xDC;      // \|
    public const int VkOem6 = 0xDD;      // ]}
    public const int VkOem7 = 0xDE;      // '"
    public const int VkOem102 = 0xE2;    // the extra key left of Z on ISO keyboards

    /// <summary>A key that is a modifier: the console tracks these to release them when control ends.</summary>
    public static bool IsModifier(int vk) => vk is VkShift or VkControl or VkMenu or VkLWin or VkRWin
        or VkLShift or VkRShift or VkLControl or VkRControl or VkLMenu or VkRMenu;

    public static Input Move(double x, double y) => new()
    {
        Kind = Input.Types.Kind.MouseMove,
        X = Clamp(x),
        Y = Clamp(y),
    };

    public static Input Button(int button, bool pressed, double x, double y) => new()
    {
        Kind = Input.Types.Kind.MouseButton,
        Button = button,
        Pressed = pressed,
        X = Clamp(x),
        Y = Clamp(y),
    };

    /// <summary><paramref name="delta"/> is in <see cref="WheelNotch"/> units, positive away from the user (up) or to the right.</summary>
    public static Input Wheel(int delta, bool horizontal, double x, double y) => new()
    {
        Kind = Input.Types.Kind.MouseWheel,
        Button = horizontal ? HorizontalWheel : VerticalWheel,
        Delta = delta,
        X = Clamp(x),
        Y = Clamp(y),
    };

    /// <summary>
    /// A key by its Windows virtual-key code. <paramref name="scan"/> is the set-1 scan code
    /// with an <c>0xE0</c> prefix for extended keys (<c>0xE048</c> is the up arrow); the helper
    /// passes it along so applications that read scan codes see the real key.
    /// </summary>
    public static Input Key(int vk, int scan, bool pressed) => new()
    {
        Kind = Input.Types.Kind.Key,
        Vk = vk,
        Scan = scan,
        Pressed = pressed,
    };

    /// <summary>Characters as typed, whatever the PC's keyboard layout: the helper injects them as Unicode.</summary>
    public static Input Text(string text) => new()
    {
        Kind = Input.Types.Kind.Text,
        Text = text,
    };

    /// <summary>The secure attention sequence; the service, not the helper, acts on it (<c>SendSAS</c>).</summary>
    public static Input CtrlAltDel() => new() { Kind = Input.Types.Kind.CtrlAltDel };

    /// <summary>Normalised coordinates never leave the screen, whatever the window did with the pointer.</summary>
    public static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
