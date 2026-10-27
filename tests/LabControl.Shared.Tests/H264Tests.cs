using Xunit;

using LabControl.Shared.Video;
using LabControl.Shared.Video.H264;
using SkiaSharp;

namespace LabControl.Shared.Tests;

/// <summary>
/// The H.264 seam (M7 portion 6, D-91): the pure NV12 and Annex B conversions everywhere, and
/// the platform codec where this computer has one — VideoToolbox on the owner's Mac. Media
/// Foundation runs only on Windows; on Linux the codec tests skip.
/// </summary>
public sealed class H264Tests
{
    [Fact]
    public void Nv12_round_trip_keeps_the_colours_within_a_few_steps()
    {
        const int width = 64, height = 48;
        var bgra = Gradient(width, height, out var rowBytes);
        var nv12 = new byte[Nv12.Size(width, height)];
        Nv12.FromBgra(bgra, rowBytes, width, height, nv12, width, height);
        var back = new byte[rowBytes * height];
        Nv12.ToBgra(nv12, width, height, width, height, back, rowBytes);

        // Chroma is shared by a 2×2 block, so a smooth gradient survives closely.
        var worst = 0;
        for (var i = 0; i < back.Length; i++)
        {
            if (i % 4 == 3)
            {
                Assert.Equal(255, back[i]);
                continue;
            }

            worst = Math.Max(worst, Math.Abs(back[i] - bgra[i]));
        }

        Assert.True(worst <= 12, $"worst channel difference {worst}");
    }

    [Fact]
    public void Nv12_white_and_black_use_the_limited_range()
    {
        var bgra = new byte[2 * 2 * 4];
        Array.Fill(bgra, (byte)255);
        var nv12 = new byte[Nv12.Size(2, 2)];
        Nv12.FromBgra(bgra, 8, 2, 2, nv12, 2, 2);
        Assert.Equal(235, nv12[0]);
        Assert.Equal(128, nv12[4]);
        Assert.Equal(128, nv12[5]);

        Array.Clear(bgra);
        Nv12.FromBgra(bgra, 8, 2, 2, nv12, 2, 2);
        Assert.Equal(16, nv12[0]);
    }

    [Fact]
    public void Nv12_refuses_an_odd_size_or_a_short_buffer()
    {
        Assert.Throws<ArgumentException>(() => Nv12.FromBgra(new byte[3 * 2 * 4], 12, 3, 2, new byte[100], 3, 2));
        Assert.Throws<ArgumentException>(() => Nv12.FromBgra(new byte[4 * 4 * 4], 16, 4, 4, new byte[10], 4, 4));
        Assert.Throws<ArgumentException>(() => Nv12.ToBgra(new byte[24], 4, 4, 4, 4, new byte[10], 16));
    }

    [Fact]
    public void AnnexB_finds_units_behind_both_start_codes()
    {
        byte[] stream = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x68, 3, 0, 0, 0, 1, 0x65, 4, 5, 6];
        var units = AnnexB.Units(stream);
        Assert.Equal([(4, 3), (10, 2), (16, 4)], units);
        Assert.True(AnnexB.IsIdr(stream));
        var sets = AnnexB.ParameterSets(stream);
        Assert.NotNull(sets);
        Assert.Equal(new byte[] { 0x67, 1, 2 }, sets.Value.Sps);
        Assert.Equal(new byte[] { 0x68, 3 }, sets.Value.Pps);
    }

    [Fact]
    public void AnnexB_to_length_prefixed_leaves_the_parameter_sets_out_and_back_again()
    {
        byte[] stream = [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 2, 0, 0, 0, 1, 0x65, 9, 9, 0, 0, 1, 0x41, 7];
        var avcc = AnnexB.ToLengthPrefixed(stream);
        Assert.Equal(new byte[] { 0, 0, 0, 3, 0x65, 9, 9, 0, 0, 0, 2, 0x41, 7 }, avcc);

        var back = new List<byte>();
        Assert.True(AnnexB.AppendLengthPrefixed(avcc, back));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65, 9, 9, 0, 0, 0, 1, 0x41, 7 }, back.ToArray());
        Assert.False(AnnexB.AppendLengthPrefixed(new byte[] { 0, 0, 0, 9, 1 }, back));
        Assert.False(AnnexB.IsIdr(new byte[] { 0, 0, 1, 0x41, 7 }));
    }

    [Fact]
    public void The_platform_codec_round_trips_a_picture()
    {
        if (!H264Codecs.CanEncode || !H264Codecs.CanDecode)
        {
            Assert.Skip($"no H.264 here: {H264Codecs.WhyNoEncoder ?? H264Codecs.WhyNoDecoder}");
        }

        const int width = 320, height = 240;
        using var encoder = H264Codecs.CreateEncoder(width, height, 30, 2 * 1024 * 1024);
        using var decoder = H264Codecs.CreateDecoder();
        var bgra = Gradient(width, height, out var rowBytes);

        var first = encoder.Encode(bgra, rowBytes, keyframe: true, out var isKeyframe);
        Assert.NotNull(first);
        Assert.True(isKeyframe);
        Assert.True(AnnexB.IsIdr(first));
        Assert.NotNull(AnnexB.ParameterSets(first));

        var picture = decoder.Decode(first);
        Assert.NotNull(picture);
        Assert.Equal(width, picture.Width);
        Assert.Equal(height, picture.Height);
        AssertClose(bgra, rowBytes, picture, 24);

        // Deltas: a moving white square, each picture decoded right behind its access unit.
        for (var i = 1; i <= 5; i++)
        {
            DrawSquare(bgra, rowBytes, 20 * i, 40);
            var unit = encoder.Encode(bgra, rowBytes, keyframe: false, out isKeyframe);
            Assert.NotNull(unit);
            Assert.False(isKeyframe);
            Assert.False(AnnexB.IsIdr(unit));
            picture = decoder.Decode(unit);
            Assert.NotNull(picture);
        }

        AssertClose(bgra, rowBytes, picture, 40);

        // A forced IDR in the middle of the stream.
        var idr = encoder.Encode(bgra, rowBytes, keyframe: true, out isKeyframe);
        Assert.True(isKeyframe);
        Assert.NotNull(AnnexB.ParameterSets(idr!));

        // A decoder that joins on that IDR needs nothing before it.
        using var late = H264Codecs.CreateDecoder();
        Assert.NotNull(late.Decode(idr));
    }

    [Fact]
    public void A_decoder_without_an_idr_first_refuses()
    {
        if (!H264Codecs.CanEncode || !H264Codecs.CanDecode)
        {
            Assert.Skip($"no H.264 here: {H264Codecs.WhyNoEncoder ?? H264Codecs.WhyNoDecoder}");
        }

        using var encoder = H264Codecs.CreateEncoder(64, 64, 30, 512 * 1024);
        var bgra = Gradient(64, 64, out var rowBytes);
        encoder.Encode(bgra, rowBytes, keyframe: true, out _);
        DrawSquare(bgra, rowBytes, 8, 8);
        var delta = encoder.Encode(bgra, rowBytes, keyframe: false, out _);

        using var decoder = H264Codecs.CreateDecoder();
        if (OperatingSystem.IsMacOS())
        {
            Assert.Throws<H264Exception>(() => decoder.Decode(delta));
        }
    }

    internal static byte[] Gradient(int width, int height, out int rowBytes)
    {
        rowBytes = width * 4;
        var pixels = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = y * rowBytes + x * 4;
                pixels[at] = (byte)(x * 255 / width);
                pixels[at + 1] = (byte)(y * 255 / height);
                pixels[at + 2] = 128;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    private static void DrawSquare(byte[] pixels, int rowBytes, int left, int top)
    {
        for (var y = top; y < top + 16; y++)
        {
            for (var x = left; x < left + 16; x++)
            {
                var at = y * rowBytes + x * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = 255;
            }
        }
    }

    private static void AssertClose(byte[] expected, int rowBytes, SKBitmap actual, int tolerance)
    {
        long total = 0;
        var samples = 0;
        for (var y = 0; y < actual.Height; y += 7)
        {
            for (var x = 0; x < actual.Width; x += 7)
            {
                var pixel = actual.GetPixel(x, y);
                var at = y * rowBytes + x * 4;
                total += Math.Abs(pixel.Blue - expected[at]) + Math.Abs(pixel.Green - expected[at + 1]) + Math.Abs(pixel.Red - expected[at + 2]);
                samples += 3;
            }
        }

        var mean = (double)total / samples;
        Assert.True(mean <= tolerance, $"mean channel difference {mean:0.0}");
    }
}
