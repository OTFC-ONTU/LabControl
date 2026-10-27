using SkiaSharp;

namespace LabControl.Shared.Video.H264;

/// <summary>
/// One H.264 encoder for one picture size (D-91): BGRA in, one Annex B access unit out —
/// baseline profile, low latency, so every picture comes straight back. A new size or a new
/// bit rate is a new encoder. Not thread-safe; the producer's loop is its only caller.
/// </summary>
public interface IH264Encoder : IDisposable
{
    /// <summary>"videotoolbox", "mediafoundation", or a test's name — for the log.</summary>
    string Kind { get; }

    int Width { get; }

    int Height { get; }

    /// <summary>
    /// Encodes one picture of <see cref="Width"/>×<see cref="Height"/> BGRA pixels;
    /// <paramref name="keyframe"/> forces an IDR. Returns the access unit, with SPS and PPS in
    /// front of an IDR, or <c>null</c> when the encoder gave nothing back for this picture.
    /// Throws <see cref="H264Exception"/> when the encoder failed and must be replaced.
    /// </summary>
    byte[]? Encode(ReadOnlySpan<byte> bgra, int rowBytes, bool keyframe, out bool isKeyframe);
}

/// <summary>
/// One H.264 decoder for one stream (D-91): Annex B access units in order, BGRA pictures out.
/// Not thread-safe; one decode thread is its only caller.
/// </summary>
public interface IH264Decoder : IDisposable
{
    string Kind { get; }

    /// <summary>
    /// Decodes one access unit. The picture it returns belongs to the decoder and is good until
    /// the next call; <c>null</c> when this unit produced none. Throws
    /// <see cref="H264Exception"/> when the stream cannot be decoded — the caller drops the
    /// decoder and waits for an IDR.
    /// </summary>
    SKBitmap? Decode(ReadOnlySpan<byte> accessUnit);
}

/// <summary>An H.264 codec that failed; the message is for the log, the code for an event.</summary>
public sealed class H264Exception : Exception
{
    public H264Exception(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
