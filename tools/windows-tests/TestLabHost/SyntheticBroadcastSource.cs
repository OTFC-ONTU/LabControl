using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using SkiaSharp;

/// <summary>
/// A drawn "teacher screen" for the disposable VM drill: large readable text, a clock with
/// seconds and a moving square, so a broadcast that freezes or tears is visible on the PC.
/// </summary>
internal sealed class SyntheticBroadcastSource : IScreenSource
{
    private const int Width = 1280;
    private const int Height = 720;
    private readonly SKBitmap _bitmap = new(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
    private readonly SKCanvas _canvas;
    private readonly SKFont _big = new(SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Bold), 64);
    private readonly SKFont _body = new(SKTypeface.FromFamilyName("Menlo"), 28);
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private DateTime _last = DateTime.MinValue;
    private long _frames;

    public SyntheticBroadcastSource() => _canvas = new SKCanvas(_bitmap);

    public string Kind => "synthetic";

    /// <summary>Set by <c>broadcast-freeze</c>: every capture fails, as a console whose own screen locked or slept.</summary>
    public static volatile bool Frozen;

    /// <summary>Frames the source draws per second; 10 unless <c>--synthetic-fps</c> asks for more (the M7 H.264 rate check, D-91).</summary>
    public static int FramesPerSecond { get; set; } = 10;

    public IScreenFrame? Acquire(TimeSpan timeout)
    {
        if (Frozen)
        {
            Thread.Sleep(timeout);
            throw new ScreenCaptureException("capture_failed", "the fixture froze the teacher's screen");
        }

        var wait = TimeSpan.FromMilliseconds(1000.0 / FramesPerSecond) - (DateTime.UtcNow - _last);
        if (wait > TimeSpan.Zero) Thread.Sleep(wait < timeout ? wait : timeout);
        _last = DateTime.UtcNow;
        _frames++;
        Draw();
        return new Frame(this);
    }

    private void Draw()
    {
        _canvas.Clear(new SKColor(0x1E, 0x3A, 0x5F));
        _paint.Color = SKColors.White;
        _canvas.DrawText("Трансляція екрана вчителя", 60, 140, _big, _paint);
        _paint.Color = new SKColor(0xFF, 0xD5, 0x4F);
        _canvas.DrawText(DateTime.Now.ToString("HH:mm:ss.f"), 60, 240, _big, _paint);
        _paint.Color = SKColors.White;
        _canvas.DrawText("M6 portion 2 VM drill — 11bdec8", 60, 320, _body, _paint);
        _canvas.DrawText($"frame #{_frames}", 60, 370, _body, _paint);
        _canvas.DrawText("public static void main(String[] args) { System.out.println(\"Привіт\"); }", 60, 440, _body, _paint);
        var x = 60 + (int)(_frames * 12 % (Width - 220));
        _paint.Color = new SKColor(0x4C, 0xAF, 0x50);
        _canvas.DrawRect(x, 520, 100, 100, _paint);
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
        _big.Dispose();
        _body.Dispose();
        _paint.Dispose();
    }

    private sealed class Frame(SyntheticBroadcastSource source) : IScreenFrame
    {
        public int Width => SyntheticBroadcastSource.Width;
        public int Height => SyntheticBroadcastSource.Height;
        public int RowBytes => source._bitmap.RowBytes;
        public ReadOnlySpan<byte> Pixels => source._bitmap.GetPixelSpan();
        public IReadOnlyList<Rect> Dirty { get; } = [new Rect { X = 0, Y = 0, Width = SyntheticBroadcastSource.Width, Height = SyntheticBroadcastSource.Height }];
        public void Dispose() { }
    }
}
