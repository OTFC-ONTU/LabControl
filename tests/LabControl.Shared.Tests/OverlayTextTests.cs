using LabControl.Shared.Policy;
using LabControl.Shared.Video;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>The lock picture the helper blits (M6, D-70), composed on the Mac.</summary>
public sealed class OverlayTextTests
{
    [Fact]
    public void The_picture_is_the_screens_size_in_bgra()
    {
        using var overlay = OverlayText.Compose(new OverlayContent("Look at the board", "PC-07", 125), 1366, 768);

        Assert.Equal(1366, overlay.Width);
        Assert.Equal(768, overlay.Height);
        Assert.Equal(JpegCodec.PixelFormat, overlay.Bitmap.ColorType);
        Assert.Equal(1366 * JpegCodec.BytesPerPixel, overlay.RowBytes);
        Assert.Equal(768 * overlay.RowBytes, overlay.Pixels.Length);

        using var tiny = OverlayText.Compose(new OverlayContent(string.Empty, string.Empty, null), 0, -3);
        Assert.Equal((1, 1), (tiny.Width, tiny.Height));
    }

    [Fact]
    public void Cyrillic_renders_as_something_other_than_the_background()
    {
        using var overlay = OverlayText.Compose(new OverlayContent("Дивіться на дошку, будь ласка", "ПК-07", null), 800, 450);

        // Count pixels that are not the background in the band where the message is drawn.
        var lit = 0;
        var band = overlay.Bitmap;
        for (var y = band.Height / 4; y < band.Height * 3 / 4; y += 2)
        {
            for (var x = 0; x < band.Width; x += 2)
            {
                var pixel = band.GetPixel(x, y);
                if (pixel.Red + pixel.Green + pixel.Blue > 3 * 0x60)
                {
                    lit++;
                }
            }
        }

        Assert.True(lit > 200, $"only {lit} lit pixels: Cyrillic text did not render");
    }

    [Fact]
    public void The_countdown_changes_at_a_second_boundary_and_says_when_it_never_ends()
    {
        Assert.NotEqual(OverlayText.CountdownText(61), OverlayText.CountdownText(60));
        Assert.Equal(OverlayText.CountdownText(60), OverlayText.CountdownText(60));
        Assert.Contains("1:00", OverlayText.CountdownText(60), StringComparison.Ordinal);
        Assert.Contains("1:01:05", OverlayText.CountdownText(3665), StringComparison.Ordinal);
        Assert.Contains("0:00", OverlayText.CountdownText(-4), StringComparison.Ordinal);
        Assert.DoesNotContain(":", OverlayText.CountdownText(null), StringComparison.Ordinal);

        Assert.Equal(10, OverlayText.Remaining(1_000, 2_000, 990));
        Assert.Equal(5, OverlayText.Remaining(0, 1_000, 995));
        Assert.Equal(0, OverlayText.Remaining(1_000, 2_000, 5_000));
        Assert.Null(OverlayText.Remaining(0, long.MaxValue, 5));

        using var a = OverlayText.Compose(new OverlayContent("m", "PC-01", 61), 320, 200);
        using var b = OverlayText.Compose(new OverlayContent("m", "PC-01", 60), 320, 200);
        Assert.False(a.Pixels.SequenceEqual(b.Pixels));
    }

    [Fact]
    public void An_absurd_size_is_composed_within_the_budget_and_a_long_message_is_clamped_not_thrown()
    {
        var (w, h) = OverlayText.ComposedSize(100_000, 100_000);
        Assert.True(w <= Defaults.OverlayMaxDimension && h <= Defaults.OverlayMaxDimension);
        Assert.True((long)w * h <= Defaults.OverlayMaxPixels);
        Assert.Equal((1, 1), OverlayText.ComposedSize(0, -3));
        Assert.Equal((1366, 768), OverlayText.ComposedSize(1366, 768));
        var (tw, th) = OverlayText.ComposedSize(11520, 2160);
        Assert.Equal((11520, 2160), (tw, th));
        var (ww, wh) = OverlayText.ComposedSize(16384, 16384);
        Assert.InRange((double)ww / wh, 0.99, 1.01);

        using var huge = OverlayText.Compose(new OverlayContent("m", "PC-01", 5), int.MaxValue, int.MaxValue);
        Assert.True((long)huge.Width * huge.Height <= Defaults.OverlayMaxPixels);

        var message = string.Join(' ', Enumerable.Repeat("слово", 800));
        Assert.True(message.Length > Defaults.OverlayMessageMaxLength);
        Assert.Equal(Defaults.OverlayMessageMaxLength, OverlayText.ClampMessage(message).Length);
        Assert.Equal(string.Empty, OverlayText.ClampMessage(null));
        var surrogate = new string('a', Defaults.OverlayMessageMaxLength - 1) + "\U0001F600zzz";
        Assert.Equal(Defaults.OverlayMessageMaxLength - 1, OverlayText.ClampMessage(surrogate).Length);

        // A wall of text and no spaces: composes at screen size, and the bottom band is still drawn.
        using var wall = OverlayText.Compose(new OverlayContent(message + new string('x', 3000), "PC-01", 61), 1366, 768);
        Assert.Equal(1366, wall.Width);
        Assert.Equal(OverlayText.CountdownText(long.MaxValue), OverlayText.CountdownText(long.MaxValue - 1));
        Assert.NotEmpty(OverlayText.CountdownText(long.MaxValue));
        Assert.NotEmpty(OverlayText.CountdownText(long.MinValue));
    }

    [Fact]
    public void The_exam_strip_widens_for_the_sweep_warning_and_stays_put_as_the_countdown_ticks()
    {
        // VM drill 2026-10-23: at a third of a 1312 px screen the warning was cut before "save your work".
        var plain = OverlayText.StripSize(1312, 805);
        var warning = new ExamStripContent("Test", 599, OverlayText.ClosingText(["Notepad.exe"], 10));
        var wide = OverlayText.StripSize(1312, 805, warning);
        Assert.True(wide.Width > plain.Width, $"{wide.Width} <= {plain.Width}");
        Assert.True(wide.Width <= 1312 * 9 / 10);
        Assert.Equal(plain.Height, wide.Height);
        Assert.Equal(wide, OverlayText.StripSize(1312, 805, warning with { RemainingSeconds = 588 }));

        // A long list still fits the screen, ellipsized; a short message keeps the third.
        var many = new ExamStripContent("", null, OverlayText.ClosingText(Enumerable.Range(1, 40).Select(n => $"program{n}.exe"), 10));
        Assert.Equal(1312 * 9 / 10, OverlayText.StripSize(1312, 805, many).Width);
        Assert.Equal(plain, OverlayText.StripSize(1312, 805, new ExamStripContent("Test", 60, "")));
    }
}
