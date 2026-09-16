using System.Globalization;
using System.Resources;
using LabControl.Shared.Video;
using SkiaSharp;

namespace LabControl.Shared.Policy;

/// <summary>What the lock overlay shows: everything the composer needs, nothing about how.</summary>
/// <param name="Message">The teacher's message; the default text when empty.</param>
/// <param name="PcName">The PC's own name (<c>PC-07</c>), so a student knows which screen is which.</param>
/// <param name="RemainingSeconds">Seconds until the lock lifts, or <c>null</c> for "until lifted".</param>
public sealed record OverlayContent(string Message, string PcName, long? RemainingSeconds);

/// <summary>
/// A composed overlay picture: BGRA, premultiplied, top-down, tightly packed — what one
/// <c>StretchDIBits</c> in the helper paints, and what a simulator can show as its screen.
/// </summary>
public sealed class OverlayBitmap : IDisposable
{
    internal OverlayBitmap(SKBitmap bitmap)
    {
        Bitmap = bitmap;
    }

    public SKBitmap Bitmap { get; }

    public int Width => Bitmap.Width;

    public int Height => Bitmap.Height;

    public int RowBytes => Bitmap.RowBytes;

    /// <summary>The pixels, <see cref="Height"/> rows of <see cref="RowBytes"/> each.</summary>
    public ReadOnlySpan<byte> Pixels => Bitmap.GetPixelSpan();

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Composes the lock / countdown picture with SkiaSharp (M6, D-70): the title, the
/// teacher's message, the PC name, the countdown and the one line that tells the truth —
/// Ctrl+Alt+Del still works. The helper paints the result with a single blit on a thread
/// that is <b>not</b> the hook thread (a hook that takes longer than
/// <c>LowLevelHooksTimeout</c>, 300 ms, is silently dropped by Windows). Every string is a
/// resource in <c>OverlayStrings.resx</c>; the typeface is looked up per character so
/// Cyrillic renders wherever the default face lacks it.
/// </summary>
public static class OverlayText
{
    private static readonly ResourceManager Resources = new("LabControl.Shared.Policy.OverlayStrings", typeof(OverlayText).Assembly);

    private static readonly SKColor Background = new(0x10, 0x1C, 0x2C);
    private static readonly SKColor Foreground = new(0xF4, 0xF6, 0xF8);
    private static readonly SKColor Muted = new(0xA8, 0xB4, 0xC4);
    private static readonly SKColor Accent = new(0xF0, 0xB4, 0x2C);

    /// <summary>The words for the countdown line, so a test can prove they change at a second boundary.</summary>
    public static string CountdownText(long? remainingSeconds)
    {
        if (remainingSeconds is null)
        {
            return Text("CountdownUntilLifted");
        }

        var remaining = TimeSpan.FromSeconds(Math.Max(0, remainingSeconds.Value));
        var clock = remaining.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (long)remaining.TotalHours, remaining.Minutes, remaining.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", remaining.Minutes, remaining.Seconds);
        return Text("CountdownFormat", clock);
    }

    /// <summary>Seconds left at <paramref name="nowUnix"/> for a lock with these limits; <c>null</c> when it never ends on its own.</summary>
    public static long? Remaining(long untilUnix, long hardLimitUnix, long nowUnix)
    {
        var deadline = untilUnix > 0 ? Math.Min(untilUnix, hardLimitUnix) : hardLimitUnix;
        if (deadline >= long.MaxValue - 1)
        {
            return null;
        }

        return Math.Max(0, deadline - nowUnix);
    }

    /// <summary>
    /// Draws the overlay at the given size — the virtual desktop's, so one picture covers
    /// every monitor. Never throws on odd input: a size of zero yields a 1×1 picture.
    /// </summary>
    public static OverlayBitmap Compose(OverlayContent content, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(content);
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var bitmap = new SKBitmap(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(Background);

            var unit = Math.Min(width, height);
            var titleSize = unit / 14f;
            var messageSize = unit / 18f;
            var smallSize = unit / 28f;
            var margin = unit / 12f;

            using var paint = new SKPaint { IsAntialias = true };

            var y = height * 0.28f;
            y = DrawCentered(canvas, paint, Text("LockTitle"), titleSize, Foreground, width, y, width - 2 * margin);

            var message = content.Message.Length > 0 ? content.Message : Text("LockDefaultMessage");
            y += messageSize * 0.8f;
            y = DrawCentered(canvas, paint, message, messageSize, Foreground, width, y, width - 2 * margin);

            y += smallSize * 1.6f;
            y = DrawCentered(canvas, paint, CountdownText(content.RemainingSeconds), smallSize * 1.3f, Accent, width, y, width - 2 * margin);

            // The bottom band: the PC's name at the left, the one honest line at the right.
            var bottom = height - margin;
            DrawLeft(canvas, paint, Text("PcNameFormat", content.PcName), smallSize, Muted, margin, bottom);
            DrawRight(canvas, paint, Text("SecureAttentionNote"), smallSize, Muted, width - margin, bottom);

            return new OverlayBitmap(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ drawing

    private static float DrawCentered(SKCanvas canvas, SKPaint paint, string text, float size, SKColor color, int width, float y, float maxWidth)
    {
        paint.Color = color;
        foreach (var line in Wrap(text, size, maxWidth))
        {
            var lineWidth = Measure(line, size);
            DrawRun(canvas, paint, line, size, (width - lineWidth) / 2f, y);
            y += size * 1.3f;
        }

        return y;
    }

    private static void DrawLeft(SKCanvas canvas, SKPaint paint, string text, float size, SKColor color, float x, float y)
    {
        paint.Color = color;
        DrawRun(canvas, paint, text, size, x, y);
    }

    private static void DrawRight(SKCanvas canvas, SKPaint paint, string text, float size, SKColor color, float right, float y)
    {
        paint.Color = color;
        DrawRun(canvas, paint, text, size, right - Measure(text, size), y);
    }

    /// <summary>Draws a line one typeface run at a time, so a face without Cyrillic hands those characters to one that has them.</summary>
    private static void DrawRun(SKCanvas canvas, SKPaint paint, string text, float size, float x, float y)
    {
        foreach (var (run, typeface) in Runs(text))
        {
            using var font = new SKFont(typeface, size);
            canvas.DrawText(run, x, y, SKTextAlign.Left, font, paint);
            x += font.MeasureText(run);
        }
    }

    private static float Measure(string text, float size)
    {
        var width = 0f;
        foreach (var (run, typeface) in Runs(text))
        {
            using var font = new SKFont(typeface, size);
            width += font.MeasureText(run);
        }

        return width;
    }

    private static IEnumerable<string> Wrap(string text, float size, float maxWidth)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(candidate, size) > maxWidth)
                {
                    yield return line;
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            yield return line;
        }
    }

    /// <summary>
    /// Splits text into runs that one typeface can draw: the default face where it has the
    /// glyph, the font manager's match for the character otherwise. Characters nothing on
    /// the machine can draw stay with the default face and render as its missing glyph.
    /// </summary>
    private static IEnumerable<(string Run, SKTypeface Typeface)> Runs(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        var manager = SKFontManager.Default;
        var fallback = SKTypeface.Default;
        var start = 0;
        var current = TypefaceFor(manager, fallback, char.ConvertToUtf32(text, 0));
        var i = char.IsSurrogatePair(text, 0) ? 2 : 1;
        while (i < text.Length)
        {
            var codepoint = char.ConvertToUtf32(text, i);
            var next = TypefaceFor(manager, fallback, codepoint);
            if (!ReferenceEquals(next, current) && next.FamilyName != current.FamilyName)
            {
                yield return (text[start..i], current);
                start = i;
                current = next;
            }

            i += char.IsSurrogatePair(text, i) ? 2 : 1;
        }

        yield return (text[start..], current);
    }

    private static SKTypeface TypefaceFor(SKFontManager manager, SKTypeface fallback, int codepoint)
    {
        if (char.IsWhiteSpace((char)Math.Min(codepoint, char.MaxValue)) || fallback.ContainsGlyph(codepoint))
        {
            return fallback;
        }

        try
        {
            return manager.MatchCharacter(codepoint) ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static string Text(string name, params object?[] arguments)
    {
        string? template;
        try
        {
            template = Resources.GetString(name, CultureInfo.CurrentUICulture);
        }
        catch (MissingManifestResourceException)
        {
            template = null;
        }

        template ??= name;
        return arguments.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, arguments);
    }
}
