using System.Numerics;
using System.Security.Cryptography;

namespace LabControl.Shared.Identity;

/// <summary>
/// Point compression for NIST P-256. The discovery beacon must fit in 512 bytes
/// (PROTOCOL, "Discovery beacon"), and a compressed public key is 33 bytes instead of 65;
/// the BCL imports and exports affine coordinates only, so the conversion lives here.
/// </summary>
public static class P256
{
    public const int CompressedLength = 33;

    private const int CoordinateLength = 32;

    private static readonly BigInteger P = Parse("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
    private static readonly BigInteger B = Parse("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");
    private static readonly BigInteger A = P - 3;

    /// <summary>The 33-byte compressed form of a public key: <c>02|03</c> then X.</summary>
    public static byte[] Compress(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var x = parameters.Q.X ?? throw new ArgumentException("The key has no public point.", nameof(key));
        var y = parameters.Q.Y ?? throw new ArgumentException("The key has no public point.", nameof(key));

        var compressed = new byte[CompressedLength];
        compressed[0] = (byte)((y[^1] & 1) == 1 ? 0x03 : 0x02);
        x.CopyTo(compressed, 1);
        return compressed;
    }

    /// <summary>
    /// Rebuilds a verifying key from the compressed form. Returns <c>false</c> for anything
    /// that is not a point on the curve — a forged beacon must cost a parse, not a crash.
    /// </summary>
    public static bool TryDecompress(ReadOnlySpan<byte> compressed, out ECDsa key)
    {
        key = null!;

        if (compressed.Length != CompressedLength || (compressed[0] != 0x02 && compressed[0] != 0x03))
        {
            return false;
        }

        var x = new BigInteger(compressed[1..], isUnsigned: true, isBigEndian: true);
        if (x >= P)
        {
            return false;
        }

        // y^2 = x^3 + ax + b (mod p); p = 3 (mod 4), so the square root is a single power.
        var ySquared = (BigInteger.ModPow(x, 3, P) + A * x + B) % P;
        var y = BigInteger.ModPow(ySquared, (P + 1) / 4, P);

        if (BigInteger.ModPow(y, 2, P) != ySquared)
        {
            return false;
        }

        if ((y.IsEven ? 0 : 1) != (compressed[0] & 1))
        {
            y = P - y;
        }

        var candidate = ECDsa.Create();
        try
        {
            candidate.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = ToFixed(x), Y = ToFixed(y) },
            });
        }
        catch (CryptographicException)
        {
            candidate.Dispose();
            return false;
        }

        key = candidate;
        return true;
    }

    private static byte[] ToFixed(BigInteger value)
    {
        var bytes = new byte[CoordinateLength];
        value.TryWriteBytes(bytes, out var written, isUnsigned: true, isBigEndian: true);
        if (written == CoordinateLength)
        {
            return bytes;
        }

        // TryWriteBytes left-aligns; shift the digits into the low end of a fixed-width field.
        var padded = new byte[CoordinateLength];
        bytes.AsSpan(0, written).CopyTo(padded.AsSpan(CoordinateLength - written));
        return padded;
    }

    private static BigInteger Parse(string hex) =>
        new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);
}
