namespace LabControl.Shared.Video.H264;

/// <summary>
/// BGRA ↔ NV12 for the Media Foundation codecs (D-91 item 1), which take and give NV12 only:
/// BT.601 limited range, what an H.264 stream without colour information is read as. Pure and
/// allocation-free; a 1080p picture is a few milliseconds. Chroma is the average of each 2×2
/// block, so the width and height must be even.
/// </summary>
public static class Nv12
{
    /// <summary>Bytes an NV12 picture of this size takes with rows <paramref name="stride"/> apart and the chroma plane right after <paramref name="planeHeight"/> luma rows.</summary>
    public static int Size(int stride, int planeHeight) => stride * planeHeight * 3 / 2;

    /// <summary>
    /// Converts <paramref name="width"/>×<paramref name="height"/> BGRA pixels into NV12 with
    /// luma rows <paramref name="stride"/> apart and the chroma plane at
    /// <c>stride × planeHeight</c>.
    /// </summary>
    public static unsafe void FromBgra(ReadOnlySpan<byte> bgra, int rowBytes, int width, int height, Span<byte> nv12, int stride, int planeHeight)
    {
        Check(width, height, stride, planeHeight);
        if (rowBytes < width * 4 || bgra.Length < (long)rowBytes * (height - 1) + width * 4)
        {
            throw new ArgumentException("The BGRA buffer is smaller than the picture.", nameof(bgra));
        }

        if (nv12.Length < Size(stride, planeHeight))
        {
            throw new ArgumentException("The NV12 buffer is smaller than the picture.", nameof(nv12));
        }

        fixed (byte* source = bgra)
        fixed (byte* target = nv12)
        {
            var chroma = target + stride * planeHeight;
            for (var y = 0; y < height; y += 2)
            {
                var row0 = source + y * rowBytes;
                var row1 = row0 + rowBytes;
                var luma0 = target + y * stride;
                var luma1 = luma0 + stride;
                var uv = chroma + y / 2 * stride;
                for (var x = 0; x < width; x += 2)
                {
                    var p00 = row0 + x * 4;
                    var p01 = p00 + 4;
                    var p10 = row1 + x * 4;
                    var p11 = p10 + 4;
                    luma0[x] = Luma(p00[2], p00[1], p00[0]);
                    luma0[x + 1] = Luma(p01[2], p01[1], p01[0]);
                    luma1[x] = Luma(p10[2], p10[1], p10[0]);
                    luma1[x + 1] = Luma(p11[2], p11[1], p11[0]);

                    var r = (p00[2] + p01[2] + p10[2] + p11[2] + 2) >> 2;
                    var g = (p00[1] + p01[1] + p10[1] + p11[1] + 2) >> 2;
                    var b = (p00[0] + p01[0] + p10[0] + p11[0] + 2) >> 2;
                    uv[x] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    uv[x + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        }
    }

    /// <summary>The reverse: NV12 laid out as <see cref="FromBgra"/> writes it into opaque BGRA.</summary>
    public static unsafe void ToBgra(ReadOnlySpan<byte> nv12, int stride, int planeHeight, int width, int height, Span<byte> bgra, int rowBytes)
    {
        Check(width, height, stride, planeHeight);
        if (nv12.Length < Size(stride, planeHeight))
        {
            throw new ArgumentException("The NV12 buffer is smaller than the picture.", nameof(nv12));
        }

        if (rowBytes < width * 4 || bgra.Length < (long)rowBytes * (height - 1) + width * 4)
        {
            throw new ArgumentException("The BGRA buffer is smaller than the picture.", nameof(bgra));
        }

        fixed (byte* source = nv12)
        fixed (byte* target = bgra)
        {
            var chroma = source + stride * planeHeight;
            for (var y = 0; y < height; y++)
            {
                var luma = source + y * stride;
                var uv = chroma + y / 2 * stride;
                var row = target + y * rowBytes;
                for (var x = 0; x < width; x++)
                {
                    var c = 298 * (luma[x] - 16);
                    var d = uv[x & ~1] - 128;
                    var e = uv[(x & ~1) + 1] - 128;
                    var pixel = row + x * 4;
                    pixel[0] = Clamp((c + 516 * d + 128) >> 8);
                    pixel[1] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
                    pixel[2] = Clamp((c + 409 * e + 128) >> 8);
                    pixel[3] = 255;
                }
            }
        }
    }

    private static byte Luma(int r, int g, int b) => (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static void Check(int width, int height, int stride, int planeHeight)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException($"NV12 needs an even, non-empty size, not {width}×{height}.");
        }

        if (stride < width || planeHeight < height)
        {
            throw new ArgumentException($"An NV12 plane of {stride}×{planeHeight} cannot hold {width}×{height}.");
        }
    }
}
