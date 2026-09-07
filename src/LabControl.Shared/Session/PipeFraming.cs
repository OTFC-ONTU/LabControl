using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;

namespace LabControl.Shared.Session;

/// <summary>
/// The wire format between the agent service and the session helper: one protobuf message
/// per frame, preceded by its length as a 4-byte little-endian integer (PROTOCOL.md, "Agent
/// ↔ Session helper"). Shared by both processes so they can never disagree; tested on the
/// Mac over an ordinary pipe pair because nothing in it is Windows-specific.
/// </summary>
public static class PipeFraming
{
    public const int HeaderBytes = 4;

    /// <summary>Writes one frame. Callers serialise writes; the stream is not shared between writers.</summary>
    public static async ValueTask WriteAsync(Stream stream, IMessage message, CancellationToken token, int maxBytes = Defaults.SessionPipeMaxMessageBytes)
    {
        var size = message.CalculateSize();
        if (size > maxBytes)
        {
            throw new InvalidDataException($"A {message.Descriptor.Name} of {size} bytes exceeds the pipe's limit of {maxBytes} bytes.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(HeaderBytes + size);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, size);
            message.WriteTo(buffer.AsSpan(HeaderBytes, size));
            // No Flush: pipe writes are unbuffered in .NET, and on Windows PipeStream.Flush is
            // FlushFileBuffers, which blocks until the peer has READ everything — a handshake
            // that deadlocked the service and the helper when both wrote from inside their
            // read loops (PC-10, 2026-09-07; D-35 item 8).
            await stream.WriteAsync(buffer.AsMemory(0, HeaderBytes + size), token);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads one frame, or <c>null</c> when the other side closed the pipe cleanly between
    /// frames. A pipe that closes in the middle of a frame, or announces a frame larger than
    /// <paramref name="maxBytes"/>, is a protocol error and throws.
    /// </summary>
    public static async ValueTask<T?> ReadAsync<T>(Stream stream, MessageParser<T> parser, CancellationToken token, int maxBytes = Defaults.SessionPipeMaxMessageBytes)
        where T : class, IMessage<T>
    {
        var header = new byte[HeaderBytes];
        var got = await FillAsync(stream, header, token);
        if (got == 0)
        {
            return null;
        }

        if (got < HeaderBytes)
        {
            throw new EndOfStreamException("The pipe closed in the middle of a frame header.");
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 0 || size > maxBytes)
        {
            throw new InvalidDataException($"The pipe announced a frame of {size} bytes; the limit is {maxBytes}.");
        }

        var body = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            if (await FillAsync(stream, body.AsMemory(0, size), token) < size)
            {
                throw new EndOfStreamException("The pipe closed in the middle of a frame.");
            }

            return parser.ParseFrom(body, 0, size);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    /// <summary>Reads until the buffer is full or the stream ends; returns how many bytes arrived.</summary>
    private static async ValueTask<int> FillAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[filled..], token);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled;
    }
}
