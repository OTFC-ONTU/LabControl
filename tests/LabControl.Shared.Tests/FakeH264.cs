using LabControl.Shared.Video;
using LabControl.Shared.Video.H264;
using SkiaSharp;

namespace LabControl.Shared.Tests;

/// <summary>
/// A stand-in H.264 encoder for the producer and hub tests on every OS (D-91): an IDR is
/// <c>SPS PPS IDR</c> and a P-frame one slice, each carrying a running number, so a test can
/// tell which picture a frame was without any real codec.
/// </summary>
public sealed class FakeH264Encoder(int width, int height) : IH264Encoder
{
    public string Kind => "fake";

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Encoded { get; private set; }

    /// <summary>Ignore keyframe requests after the first picture, like an MFT without <c>ICodecAPI</c>.</summary>
    public bool IgnoresKeyframeRequests { get; init; }

    /// <summary>Throw on this call (1-based); 0 never.</summary>
    public int FailOn { get; init; }

    public bool Disposed { get; private set; }

    public byte[]? Encode(ReadOnlySpan<byte> bgra, int rowBytes, bool keyframe, out bool isKeyframe)
    {
        Encoded++;
        if (FailOn > 0 && Encoded == FailOn)
        {
            throw new H264Exception("the fake encoder failed");
        }

        isKeyframe = Encoded == 1 || (keyframe && !IgnoresKeyframeRequests);
        var n = (byte)Encoded;
        return isKeyframe
            ? [0, 0, 0, 1, 0x67, 0x42, n, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 0, 1, 0x65, n]
            : [0, 0, 0, 1, 0x41, n];
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A stand-in decoder: an IDR or a slice is a grey picture of the size it was made with.</summary>
public sealed class FakeH264Decoder(int width, int height) : IH264Decoder
{
    private readonly SKBitmap _picture = new(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));
    private bool _started;

    public string Kind => "fake";

    public int Decoded { get; private set; }

    public SKBitmap? Decode(ReadOnlySpan<byte> accessUnit)
    {
        if (AnnexB.IsIdr(accessUnit))
        {
            _started = true;
        }
        else if (!_started)
        {
            throw new H264Exception("no IDR yet");
        }

        Decoded++;
        _picture.Erase(new SKColor((byte)Decoded, 128, 128));
        return _picture;
    }

    public void Dispose() => _picture.Dispose();
}
