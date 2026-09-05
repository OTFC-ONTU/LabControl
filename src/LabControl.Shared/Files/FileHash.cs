using System.Security.Cryptography;

namespace LabControl.Shared.Files;

/// <summary>
/// The one spelling of a SHA-256 used for files everywhere: lowercase hexadecimal, no
/// separators. A file's hash is also its <c>PullFile</c> reference (D-31), so the console
/// and the agent must agree on it to the character.
/// </summary>
public static class FileHash
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256Hex(Stream stream) =>
        Convert.ToHexStringLower(SHA256.HashData(stream));

    public static string Sha256HexOfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256Hex(stream);
    }

    /// <summary>Two hashes are the same whatever the case they were spelled in.</summary>
    public static bool Matches(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
