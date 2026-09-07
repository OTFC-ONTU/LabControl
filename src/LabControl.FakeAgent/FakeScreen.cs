using System.Globalization;
using LabControl.Shared.Control;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using SkiaSharp;

namespace LabControl.FakeAgent;

/// <summary>
/// A simulated student's desktop (M3): a coloured wallpaper with the PC's number, a clock
/// that ticks once a second, a window whose text grows as the "student types", and a box
/// that drifts about. Every change knows its own rectangle, so the frames it produces carry
/// honest dirty rectangles — what DXGI reports on a real PC, without a real PC. It is the
/// simulator's <see cref="IScreenSource"/>, so the same <see cref="ScreenProducer"/> that
/// runs in the session helper runs here. Since M3 portion 3 it also shows the teacher's
/// input (D-36): a pointer that follows the mouse, a ring where a button went down, and a
/// second editor line that shows what was typed — so remote control can be tried on the Mac.
/// </summary>
public sealed class FakeScreen : IScreenSource
{
    private static readonly SKColor[] Wallpapers =
    [
        new(0x1E, 0x3A, 0x5F), new(0x2E, 0x4A, 0x2E), new(0x5A, 0x2E, 0x3E), new(0x3E, 0x2E, 0x5A),
        new(0x2E, 0x4E, 0x5A), new(0x5A, 0x4A, 0x1E), new(0x3A, 0x3A, 0x3A), new(0x1E, 0x5A, 0x4A),
    ];

    private static readonly string[] Sentences =
    [
        "public static void Main(string[] args)",
        "for (int i = 0; i < n; i++) sum += a[i];",
        "print(\"Привіт, світ!\")",
        "SELECT name FROM students WHERE grade > 10;",
        "def fib(n): return n if n < 2 else fib(n-1) + fib(n-2)",
    ];

    private readonly int _number;
    private readonly bool _idle;
    private readonly SKBitmap _bitmap;
    private readonly SKCanvas _canvas;
    private readonly SKFont _titleFont;
    private readonly SKFont _bodyFont;
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private readonly Random _random;
    private readonly List<Rect> _pendingDirty = [];

    private readonly List<Input> _inputs = [];
    private readonly SKBitmap _underPointer;
    private SKRect? _pointerRect;
    private string _teacherText = string.Empty;
    private bool _teacherTextDirty;
    private SKRect? _ring;

    private string _clock = string.Empty;
    private int _typed;
    private int _sentence;
    private float _boxX;
    private float _boxY;
    private float _boxDx;
    private float _boxDy;
    private bool _drawn;

    public FakeScreen(int number, int width, int height, bool idle)
    {
        _number = number;
        _idle = idle;
        Width = width;
        Height = height;
        _random = new Random(number);
        _bitmap = new SKBitmap(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));
        _canvas = new SKCanvas(_bitmap);
        _titleFont = new SKFont(SKTypeface.Default, height / 5f);
        _bodyFont = new SKFont(SKTypeface.Default, height / 30f);
        _boxX = width * 0.6f;
        _boxY = height * 0.55f;
        _boxDx = width / 240f;
        _boxDy = height / 320f;
        _sentence = number % Sentences.Length;
        _underPointer = new SKBitmap(new SKImageInfo(PointerSize, PointerSize, JpegCodec.PixelFormat, SKAlphaType.Premul));
    }

    private const int PointerSize = 24;

    /// <summary>Where the teacher's pointer is, normalised; <c>null</c> until it first moved.</summary>
    public (double X, double Y)? Pointer { get; private set; }

    /// <summary>What the teacher typed since the last Enter, as it stands on the second editor line.</summary>
    public string TeacherText
    {
        get
        {
            lock (this)
            {
                return _teacherText;
            }
        }
    }

    /// <summary>Inputs applied so far, by kind — for a status line and the tests.</summary>
    public int InputsApplied { get; private set; }

    /// <summary>The last button pressed (1 = left…) and the wheel's running total, for the tests.</summary>
    public int LastButton { get; private set; }

    public int WheelTotal { get; private set; }

    /// <summary>
    /// From the link's thread: queued, and drawn by the next <see cref="Advance"/> on the
    /// producer's thread — the bitmap has exactly one writer.
    /// </summary>
    public void Apply(Input input)
    {
        lock (this)
        {
            _inputs.Add(input);
        }
    }

    public int Width { get; }

    public int Height { get; }

    public string Kind => "simulated";

    /// <summary>The picture as it is now; valid until the next <see cref="Advance"/>.</summary>
    public SKBitmap Bitmap => _bitmap;

    /// <summary>
    /// The producer's look at the desktop: moves it on by one tick and hands out the bitmap
    /// with what changed. The simulator never waits — a tick is a tick.
    /// </summary>
    public IScreenFrame? Acquire(TimeSpan timeout)
    {
        IReadOnlyList<Rect> dirty;
        lock (this)
        {
            dirty = Advance(DateTimeOffset.UtcNow);
            MarkSent();
        }

        return new Frame(this, dirty);
    }

    private sealed class Frame(FakeScreen screen, IReadOnlyList<Rect> dirty) : IScreenFrame
    {
        public int Width => screen.Width;

        public int Height => screen.Height;

        public int RowBytes => screen._bitmap.RowBytes;

        public ReadOnlySpan<byte> Pixels => screen._bitmap.GetPixelSpan();

        public IReadOnlyList<Rect> Dirty => dirty;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Moves the desktop on by one tick and returns the tile-aligned rectangles that changed
    /// (merged with whatever earlier ticks left unsent). Empty when nothing moved — an idle
    /// PC changes only when its clock does.
    /// </summary>
    public IReadOnlyList<Rect> Advance(DateTimeOffset now)
    {
        var dirty = new List<Rect>();

        if (!_drawn)
        {
            DrawWallpaper();
            _drawn = true;
            dirty.Add(VideoGeometry.Whole(Width, Height));
        }

        var clock = now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (clock != _clock)
        {
            _clock = clock;
            dirty.Add(DrawClock());
        }

        if (!_idle)
        {
            dirty.Add(DrawTyping());
            dirty.Add(MoveBox());
        }

        dirty.AddRange(DrawInputs());

        var aligned = dirty
            .Where(r => r.Width > 0 && r.Height > 0)
            .Select(r => VideoGeometry.AlignToTiles(r, Width, Height))
            .Where(r => r.Width > 0 && r.Height > 0);

        _pendingDirty.AddRange(aligned);
        var merged = VideoGeometry.Merge(_pendingDirty);
        _pendingDirty.Clear();
        _pendingDirty.AddRange(merged);
        return merged;
    }

    /// <summary>The frame went out: nothing is owed any more.</summary>
    public void MarkSent() => _pendingDirty.Clear();

    /// <summary>What is still owed after a frame was refused, so the next attempt carries it too.</summary>
    public IReadOnlyList<Rect> Pending => _pendingDirty;

    /// <summary>The whole desktop scaled to thumbnail size; the caller disposes it.</summary>
    public SKBitmap Thumbnail()
    {
        var (width, height) = VideoGeometry.ThumbnailSize(Width, Height);
        return JpegCodec.Scale(_bitmap, width, height);
    }

    private void DrawWallpaper()
    {
        _paint.Color = Wallpapers[_number % Wallpapers.Length];
        _canvas.DrawRect(0, 0, Width, Height, _paint);

        // Taskbar.
        _paint.Color = new SKColor(0x10, 0x10, 0x10, 0xE0);
        _canvas.DrawRect(0, Height - Height / 18f, Width, Height / 18f, _paint);

        // The PC's number, big.
        _paint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x50);
        _canvas.DrawText(string.Format(CultureInfo.InvariantCulture, Shared.Defaults.MachineNameFormat, _number), Width * 0.05f, Height * 0.3f, SKTextAlign.Left, _titleFont, _paint);

        // The "editor" window.
        _paint.Color = new SKColor(0xF4, 0xF4, 0xF4);
        _canvas.DrawRoundRect(EditorRect(), 8, 8, _paint);
        _paint.Color = new SKColor(0xD0, 0xD0, 0xD0);
        var title = EditorRect();
        _canvas.DrawRect(title.Left, title.Top, title.Width, _bodyFont.Size * 1.6f, _paint);
        _paint.Color = SKColors.Black;
        _canvas.DrawText("Untitled — Notepad", title.Left + 8, title.Top + _bodyFont.Size * 1.15f, SKTextAlign.Left, _bodyFont, _paint);
    }

    private SKRect EditorRect() => new(Width * 0.05f, Height * 0.4f, Width * 0.55f, Height * 0.9f);

    private Rect DrawClock()
    {
        var height = Height / 18f;
        var rect = new SKRect(Width - Width * 0.12f, Height - height, Width, Height);
        _paint.Color = new SKColor(0x10, 0x10, 0x10);
        _canvas.DrawRect(rect, _paint);
        _paint.Color = SKColors.White;
        _canvas.DrawText(_clock, rect.Left + 8, rect.Bottom - height * 0.3f, SKTextAlign.Left, _bodyFont, _paint);
        return ToRect(rect);
    }

    private Rect DrawTyping()
    {
        var text = Sentences[_sentence];
        _typed++;
        if (_typed > text.Length + 10)
        {
            _typed = 0;
            _sentence = (_sentence + 1) % Sentences.Length;
            text = Sentences[_sentence];
        }

        var editor = EditorRect();
        var line = new SKRect(editor.Left + 4, editor.Top + _bodyFont.Size * 2.2f, editor.Right - 4, editor.Top + _bodyFont.Size * 3.6f);
        _paint.Color = new SKColor(0xF4, 0xF4, 0xF4);
        _canvas.DrawRect(line, _paint);
        _paint.Color = new SKColor(0x20, 0x20, 0x20);
        var shown = text[..Math.Min(_typed, text.Length)] + (_typed % 2 == 0 ? "▏" : string.Empty);
        _canvas.DrawText(shown, line.Left + 4, line.Bottom - _bodyFont.Size * 0.4f, SKTextAlign.Left, _bodyFont, _paint);
        return ToRect(line);
    }

    private Rect MoveBox()
    {
        var size = Height / 12f;
        var before = new SKRect(_boxX, _boxY, _boxX + size, _boxY + size);

        _boxX += _boxDx;
        _boxY += _boxDy;
        if (_boxX < Width * 0.58f || _boxX + size > Width - 8)
        {
            _boxDx = -_boxDx;
            _boxX += 2 * _boxDx;
        }

        if (_boxY < Height * 0.05f || _boxY + size > Height - Height / 18f - 8)
        {
            _boxDy = -_boxDy;
            _boxY += 2 * _boxDy;
        }

        var after = new SKRect(_boxX, _boxY, _boxX + size, _boxY + size);
        var union = SKRect.Union(before, after);

        // Restore the wallpaper under the old box, then draw the new one.
        _paint.Color = Wallpapers[_number % Wallpapers.Length];
        _canvas.DrawRect(union, _paint);
        _paint.Color = new SKColor((byte)(120 + _random.Next(100)), (byte)(120 + _random.Next(100)), (byte)(120 + _random.Next(100)));
        _canvas.DrawRoundRect(after, 6, 6, _paint);
        return ToRect(union);
    }

    // ------------------------------------------------------------------ the teacher's input

    private IEnumerable<Rect> DrawInputs()
    {
        List<Input> inputs;
        lock (this)
        {
            if (_inputs.Count == 0)
            {
                return [];
            }

            inputs = [.. _inputs];
            _inputs.Clear();
        }

        var dirty = new List<Rect>();
        foreach (var input in inputs)
        {
            InputsApplied++;
            switch (input.Kind)
            {
                case Input.Types.Kind.MouseMove:
                    dirty.AddRange(MovePointer(input.X, input.Y));
                    break;

                case Input.Types.Kind.MouseButton:
                    dirty.AddRange(MovePointer(input.X, input.Y));
                    if (input.Pressed)
                    {
                        LastButton = input.Button;
                        dirty.AddRange(DrawRing(input.X, input.Y));
                    }

                    break;

                case Input.Types.Kind.MouseWheel:
                    dirty.AddRange(MovePointer(input.X, input.Y));
                    WheelTotal += input.Delta;
                    break;

                case Input.Types.Kind.Text:
                    _teacherText += input.Text;
                    _teacherTextDirty = true;
                    break;

                case Input.Types.Kind.Key when input.Pressed && input.Vk == InputMessages.VkBack:
                    if (_teacherText.Length > 0)
                    {
                        _teacherText = _teacherText[..^1];
                        _teacherTextDirty = true;
                    }

                    break;

                case Input.Types.Kind.Key when input.Pressed && input.Vk == InputMessages.VkReturn:
                    _teacherText = string.Empty;
                    _teacherTextDirty = true;
                    break;
            }
        }

        if (_teacherTextDirty)
        {
            _teacherTextDirty = false;
            dirty.Add(DrawTeacherText());
        }

        return dirty;
    }

    /// <summary>The wallpaper (or whatever was there) comes back under the old pointer, and the pointer is drawn at the new place.</summary>
    private IEnumerable<Rect> MovePointer(double x, double y)
    {
        Pointer = (x, y);
        var px = (float)(x * (Width - 1));
        var py = (float)(y * (Height - 1));
        var rect = new SKRect(px, py, Math.Min(px + PointerSize, Width), Math.Min(py + PointerSize, Height));

        if (_pointerRect is { } old)
        {
            _canvas.DrawBitmap(_underPointer, new SKRect(0, 0, old.Width, old.Height), old);
            yield return ToRect(old);
        }

        // Keep what is under the new pointer, then draw the arrow.
        using (var under = new SKCanvas(_underPointer))
        {
            under.Clear();
            under.DrawBitmap(_bitmap, rect, new SKRect(0, 0, rect.Width, rect.Height));
        }

        using var path = new SKPath();
        path.MoveTo(px, py);
        path.LineTo(px, py + PointerSize * 0.8f);
        path.LineTo(px + PointerSize * 0.22f, py + PointerSize * 0.6f);
        path.LineTo(px + PointerSize * 0.55f, py + PointerSize * 0.55f);
        path.Close();
        _paint.Color = SKColors.White;
        _canvas.DrawPath(path, _paint);
        _paint.Color = SKColors.Black;
        _paint.Style = SKPaintStyle.Stroke;
        _paint.StrokeWidth = 1.5f;
        _canvas.DrawPath(path, _paint);
        _paint.Style = SKPaintStyle.Fill;

        _pointerRect = rect;
        yield return ToRect(rect);
    }

    private IEnumerable<Rect> DrawRing(double x, double y)
    {
        var px = (float)(x * (Width - 1));
        var py = (float)(y * (Height - 1));
        var radius = Height / 60f;
        var ring = new SKRect(px - radius - 2, py - radius - 2, px + radius + 2, py + radius + 2);

        _paint.Color = new SKColor(0xFF, 0xB0, 0x00);
        _paint.Style = SKPaintStyle.Stroke;
        _paint.StrokeWidth = 3;
        _canvas.DrawCircle(px, py, radius, _paint);
        _paint.Style = SKPaintStyle.Fill;
        _ring = ring;
        yield return ToRect(ring);
    }

    private Rect DrawTeacherText()
    {
        var editor = EditorRect();
        var line = new SKRect(editor.Left + 4, editor.Top + _bodyFont.Size * 3.8f, editor.Right - 4, editor.Top + _bodyFont.Size * 5.2f);
        _paint.Color = new SKColor(0xF4, 0xF4, 0xF4);
        _canvas.DrawRect(line, _paint);
        _paint.Color = new SKColor(0x00, 0x50, 0xA0);
        _canvas.DrawText(_teacherText, line.Left + 4, line.Bottom - _bodyFont.Size * 0.4f, SKTextAlign.Left, _bodyFont, _paint);
        return ToRect(line);
    }

    private static Rect ToRect(SKRect rect) => new()
    {
        X = (int)Math.Floor(rect.Left),
        Y = (int)Math.Floor(rect.Top),
        Width = (int)Math.Ceiling(rect.Right) - (int)Math.Floor(rect.Left),
        Height = (int)Math.Ceiling(rect.Bottom) - (int)Math.Floor(rect.Top),
    };

    public void Dispose()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
        _underPointer.Dispose();
        _titleFont.Dispose();
        _bodyFont.Dispose();
        _paint.Dispose();
    }
}
