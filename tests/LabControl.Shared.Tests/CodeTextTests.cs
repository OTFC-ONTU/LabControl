using Xunit;

using LabControl.Shared;
using LabControl.Shared.Identity;

namespace LabControl.Shared.Tests;

/// <summary>
/// The recovery code and the enrollment codes are retyped by a human from a printed
/// sheet, so the encoding has to survive lower case, missing dashes and the digit/letter
/// look-alikes — and still refuse a code with a typo rather than decode a different one.
/// </summary>
public sealed class CodeTextTests
{
    [Fact]
    public void Base32_round_trips_arbitrary_bytes()
    {
        byte[] data = [0x00, 0x01, 0x7F, 0x80, 0xFF, 0xAB, 0xCD, 0xEF, 0x10, 0x20];

        Assert.True(Base32Text.TryDecode(Base32Text.Encode(data), data.Length, out var decoded));
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Look_alike_characters_read_back_as_digits()
    {
        var data = new byte[Defaults.RecoveryCodeBytes];
        var text = Base32Text.Encode(data);   // all zeros: "0000..."

        Assert.True(Base32Text.TryDecode(text.Replace('0', 'O'), data.Length, out var withLetterO));
        Assert.Equal(data, withLetterO);

        Assert.True(Base32Text.TryDecode(Base32Text.Encode([0x08, 0x42, 0x08, 0x42]).Replace('1', 'l'), 4, out _));
    }

    [Fact]
    public void A_code_with_the_wrong_length_or_a_stray_character_is_refused()
    {
        var text = Base32Text.Encode(new byte[Defaults.RecoveryCodeBytes]);

        Assert.False(Base32Text.TryDecode(text[..^1], Defaults.RecoveryCodeBytes, out _));
        Assert.False(Base32Text.TryDecode(text + "0", Defaults.RecoveryCodeBytes, out _));
        Assert.False(Base32Text.TryDecode(text[..^1] + "U", Defaults.RecoveryCodeBytes, out _));
        Assert.False(Base32Text.TryDecode(null, Defaults.RecoveryCodeBytes, out _));
    }

    [Fact]
    public void Padding_bits_that_are_not_zero_are_refused()
    {
        // 16 bytes encode to 26 characters, of which the last carries two padding bits.
        // A mistyped final character must fail rather than decode to a different secret.
        var text = Base32Text.Encode(new byte[Defaults.RecoveryCodeBytes]);

        Assert.Equal(26, text.Length);
        Assert.False(Base32Text.TryDecode(text[..^1] + "1", Defaults.RecoveryCodeBytes, out _));
    }

    [Fact]
    public void A_recovery_code_survives_the_trip_to_paper_and_back()
    {
        var code = RecoveryCode.Generate();
        var printed = code.ToPrintableString();

        Assert.Contains('-', printed);
        Assert.DoesNotContain("--", printed, StringComparison.Ordinal);
        Assert.True(RecoveryCode.TryParse(printed.ToLowerInvariant().Replace("-", " "), out var retyped));
        Assert.Equal(code, retyped);
    }

    [Fact]
    public void Two_recovery_codes_are_never_the_same()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => RecoveryCode.Generate().Canonical).ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_enrollment_code_is_readable_and_canonicalizes()
    {
        var code = EnrollmentCode.Generate();

        Assert.True(EnrollmentCode.TryCanonicalize(code, out var canonical));
        Assert.True(EnrollmentCode.TryCanonicalize(code.ToLowerInvariant().Replace("-", string.Empty), out var again));
        Assert.Equal(canonical, again);
        Assert.DoesNotContain('-', canonical);
        Assert.False(EnrollmentCode.TryCanonicalize("not a code", out _));
    }
}
