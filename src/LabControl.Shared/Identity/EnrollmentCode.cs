using System.Security.Cryptography;

namespace LabControl.Shared.Identity;

/// <summary>
/// A single-use code from the USB payload (D-14). It is not a secret worth protecting —
/// the worst a found stick allows is enrolling a bogus PC, which shows up in the console
/// as an unexpected machine — so it is short enough to retype and stored in the clear.
/// </summary>
public static class EnrollmentCode
{
    /// <summary>80 bits: far beyond guessing on a LAN, and 16 characters to read out loud.</summary>
    public const int CodeBytes = 10;

    public static string Generate() =>
        Base32Text.Group(Base32Text.Encode(RandomNumberGenerator.GetBytes(CodeBytes)), size: 4);

    /// <summary>
    /// Case- and separator-insensitive canonical form. Codes are compared canonically so
    /// that a teacher reading one off a printed sheet cannot fail on a missing dash.
    /// </summary>
    public static bool TryCanonicalize(string? text, out string canonical)
    {
        if (Base32Text.TryDecode(text, CodeBytes, out var bytes))
        {
            canonical = Base32Text.Encode(bytes);
            return true;
        }

        canonical = string.Empty;
        return false;
    }
}
