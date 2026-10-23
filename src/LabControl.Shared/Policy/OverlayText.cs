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

/// <summary>What the exam strip shows (M6 portion 4, D-75): the teacher's line, the countdown when there is a timer, and a passing notice.</summary>
public sealed record ExamStripContent(string Message, long? RemainingSeconds, string Notice)
{
    /// <summary>Nothing to show: no timer, no message, no notice — the strip is hidden (owner decision 2026-10-23).</summary>
    public bool IsEmpty => RemainingSeconds is null && Message.Length == 0 && Notice.Length == 0;
}

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

        // TimeSpan.FromSeconds overflows past ~2.9e11 s; a limit that far off is "until lifted" in every practical sense.
        var seconds = Math.Clamp(remainingSeconds.Value, 0, (long)TimeSpan.MaxValue.TotalSeconds - 1);
        var remaining = TimeSpan.FromSeconds(seconds);
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
    /// The message as the agent keeps it and the helper draws it: at most
    /// <see cref="Defaults.OverlayMessageMaxLength"/> characters, never <c>null</c>. A
    /// console cannot make a PC allocate or wrap without bound.
    /// </summary>
    public static string ClampMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        if (message.Length <= Defaults.OverlayMessageMaxLength)
        {
            return message;
        }

        var cut = Defaults.OverlayMessageMaxLength;
        if (char.IsHighSurrogate(message[cut - 1]))
        {
            cut--;
        }

        return message[..cut];
    }

    /// <summary>
    /// The size a picture is actually composed at for a requested size: at least 1×1, at
    /// most <see cref="Defaults.OverlayMaxDimension"/> on each axis and
    /// <see cref="Defaults.OverlayMaxPixels"/> in all, the aspect kept — the helper's blit
    /// stretches it to the window, so a lied-about desktop costs a blurrier picture, not
    /// gigabytes.
    /// </summary>
    public static (int Width, int Height) ComposedSize(int width, int height)
    {
        width = Math.Clamp(width, 1, Defaults.OverlayMaxDimension);
        height = Math.Clamp(height, 1, Defaults.OverlayMaxDimension);
        var pixels = (long)width * height;
        if (pixels > Defaults.OverlayMaxPixels)
        {
            var scale = Math.Sqrt((double)Defaults.OverlayMaxPixels / pixels);
            width = Math.Max(1, (int)(width * scale));
            height = Math.Max(1, (int)(height * scale));
        }

        return (width, height);
    }

    /// <summary>
    /// Draws the overlay at the given size — the virtual desktop's, so one picture covers
    /// every monitor. Never throws on odd input: a size of zero yields a 1×1 picture, an
    /// absurd one is composed at <see cref="ComposedSize"/> and left to the blit to stretch,
    /// and a message longer than the screen stops at the bottom band rather than running
    /// under it.
    /// </summary>
    public static OverlayBitmap Compose(OverlayContent content, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(content);
        (width, height) = ComposedSize(width, height);

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

            // The bottom band: the PC's name at the left, the one honest line at the right.
            // Nothing above it may draw below `floor`, so a long message cannot push the
            // countdown off the screen or under the band.
            var bottom = height - margin;
            var floor = bottom - smallSize * 1.6f;

            var y = height * 0.28f;
            y = DrawCentered(canvas, paint, Text("LockTitle"), titleSize, Foreground, width, y, width - 2 * margin, floor);

            var clamped = ClampMessage(content.Message);
            var message = clamped.Length > 0 ? clamped : Text("LockDefaultMessage");
            y += messageSize * 0.8f;

            // The countdown keeps its place: the message may take the room above it only.
            var countdownSize = smallSize * 1.3f;
            var countdownRoom = smallSize * 1.6f + countdownSize * 1.3f;
            y = DrawCentered(canvas, paint, message, messageSize, Foreground, width, y, width - 2 * margin, floor - countdownRoom);

            y += smallSize * 1.6f;
            DrawCentered(canvas, paint, CountdownText(content.RemainingSeconds), countdownSize, Accent, width, y, width - 2 * margin, floor);

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

    // ------------------------------------------------------------------ the exam strip (portion 4)

    /// <summary>The lock's message when an exam's timer runs out (owner decision 2026-10-23, D-75).</summary>
    public static string TimeUpMessage => Text("ExamTimeUpMessage");

    /// <summary>The strip's passing line after the whitelist closed <paramref name="program"/>.</summary>
    public static string NotAllowedText(string program) => Text("ExamNotAllowedFormat", program);

    /// <summary>The strip's warning before the opening sweep closes <paramref name="programs"/>.</summary>
    public static string ClosingText(IEnumerable<string> programs, int seconds) => Text("ExamClosingFormat", string.Join(", ", programs.Distinct(StringComparer.OrdinalIgnoreCase)), seconds);

    /// <summary>The strip's countdown: <c>42:17 left</c>, or empty when the exam has no timer.</summary>
    public static string StripRemainingText(long? remainingSeconds)
    {
        if (remainingSeconds is null)
        {
            return string.Empty;
        }

        var seconds = Math.Clamp(remainingSeconds.Value, 0, (long)TimeSpan.MaxValue.TotalSeconds - 1);
        var remaining = TimeSpan.FromSeconds(seconds);
        var clock = remaining.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (long)remaining.TotalHours, remaining.Minutes, remaining.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", remaining.Minutes, remaining.Seconds);
        return Text("ExamRemainingFormat", clock);
    }

    /// <summary>
    /// The strip's size on a screen of <paramref name="screenWidth"/> × <paramref name="screenHeight"/>:
    /// one line of text high, a third of the width (at least 480 px), and wider when
    /// <paramref name="content"/> needs it, up to nine tenths of the screen — the sweep's warning
    /// ends with "save your work now", the part a student must read (VM drill 2026-10-23: at a
    /// third of 1312 px it was cut after the program's name).
    /// </summary>
    public static (int Width, int Height) StripSize(int screenWidth, int screenHeight, ExamStripContent? content = null)
    {
        screenWidth = Math.Clamp(screenWidth, 1, Defaults.OverlayMaxDimension);
        screenHeight = Math.Clamp(screenHeight, 1, Defaults.OverlayMaxDimension);
        var height = Math.Clamp(screenHeight / 22, 28, 96);
        var width = Math.Min(screenWidth, Math.Max(480, screenWidth / 3));
        if (content is not null && width < screenWidth)
        {
            try
            {
                var (size, margin) = (height * 0.46f, height * 0.45f);
                var remaining = StripRemainingText(content.RemainingSeconds);
                var line = content.Notice.Length > 0 ? content.Notice : ClampMessage(content.Message).Replace('\n', ' ');
                var needed = margin + Measure(Text("ExamTitle"), size) + margin + (line.Length > 0 ? Measure(line, size) : 0f)
                    + (remaining.Length > 0 ? margin + Measure(remaining, size) : 0f) + margin;
                // In steps of two heights, so a countdown's changing digits do not jiggle the strip every second.
                var step = height * 2;
                var stepped = (int)Math.Ceiling(needed / step) * step;
                width = Math.Max(width, Math.Min(stepped, screenWidth * 9 / 10));
            }
            catch (Exception)
            {
                // Measuring is a nicety: the third-of-the-screen strip still ellipsizes.
            }
        }

        return (width, height);
    }

    /// <summary>
    /// Draws the strip (D-75): <c>Exam</c> and the teacher's message at the left, the countdown
    /// at the right in the accent colour; a notice, while there is one, takes the message's
    /// place. One line — a long message is cut with an ellipsis, never wrapped over the
    /// student's work. Never throws on odd input.
    /// </summary>
    public static OverlayBitmap ComposeStrip(ExamStripContent content, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(content);
        (width, height) = ComposedSize(width, height);

        var bitmap = new SKBitmap(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(Background);
            using var paint = new SKPaint { IsAntialias = true };

            var size = height * 0.46f;
            var margin = height * 0.45f;
            var baseline = height * 0.5f + size * 0.36f;

            var remaining = StripRemainingText(content.RemainingSeconds);
            var remainingWidth = remaining.Length > 0 ? Measure(remaining, size) : 0f;
            if (remaining.Length > 0)
            {
                DrawRight(canvas, paint, remaining, size, Accent, width - margin, baseline);
            }

            var title = Text("ExamTitle");
            DrawLeft(canvas, paint, title, size, Muted, margin, baseline);
            var x = margin + Measure(title, size) + margin;
            var room = width - margin - x - (remainingWidth > 0 ? remainingWidth + margin : 0);
            var notice = content.Notice.Length > 0;
            var line = notice ? content.Notice : ClampMessage(content.Message).Replace('\n', ' ');
            if (room > size && line.Length > 0)
            {
                DrawLeft(canvas, paint, Ellipsize(line, size, room), size, notice ? Accent : Foreground, x, baseline);
            }

            return new OverlayBitmap(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static string Ellipsize(string text, float size, float maxWidth)
    {
        if (Measure(text, size) <= maxWidth)
        {
            return text;
        }

        var cut = text.Length;
        while (cut > 1 && Measure(text[..cut] + "…", size) > maxWidth)
        {
            cut = Math.Max(1, cut - Math.Max(1, cut / 8));
        }

        return text[..cut].TrimEnd() + "…";
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Draws wrapped, centred lines from <paramref name="y"/> down, stopping before a line's baseline would pass <paramref name="floor"/>; returns the next baseline.</summary>
    private static float DrawCentered(SKCanvas canvas, SKPaint paint, string text, float size, SKColor color, int width, float y, float maxWidth, float floor)
    {
        paint.Color = color;
        foreach (var line in Wrap(text, size, maxWidth))
        {
            if (y > floor)
            {
                break;
            }

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
