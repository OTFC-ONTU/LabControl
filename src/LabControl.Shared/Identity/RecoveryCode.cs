using System.Security.Cryptography;

namespace LabControl.Shared.Identity;

/// <summary>
/// The printable 128-bit code that opens the lab key when every passphrase has been
/// forgotten (ARCHITECTURE §3.2). Shown once, meant for a wallet or a safe; the console
/// can print a new one at any time, which invalidates the old sheet.
/// </summary>
public sealed class RecoveryCode : IEquatable<RecoveryCode>
{
    private RecoveryCode(byte[] bytes) => Bytes = bytes;

    /// <summary>The raw secret. Never logged, never written anywhere but the key file's wrapping.</summary>
    public byte[] Bytes { get; }

    /// <summary>The canonical, separator-free form; this is what the key derivation sees.</summary>
    public string Canonical => Base32Text.Encode(Bytes);

    public static RecoveryCode Generate() =>
        new(RandomNumberGenerator.GetBytes(Defaults.RecoveryCodeBytes));

    /// <summary>What the teacher is shown and asked to write down.</summary>
    public string ToPrintableString() => Base32Text.Group(Canonical);

    /// <summary>Accepts the printed form back in any case, with or without the dashes.</summary>
    public static bool TryParse(string? text, out RecoveryCode code)
    {
        if (Base32Text.TryDecode(text, Defaults.RecoveryCodeBytes, out var bytes))
        {
            code = new RecoveryCode(bytes);
            return true;
        }

        code = null!;
        return false;
    }

    public bool Equals(RecoveryCode? other) =>
        other is not null && CryptographicOperations.FixedTimeEquals(Bytes, other.Bytes);

    public override bool Equals(object? obj) => Equals(obj as RecoveryCode);

    public override int GetHashCode() => Canonical.GetHashCode(StringComparison.Ordinal);

    /// <summary>Deliberately does not reveal the code: it must never reach a log by accident.</summary>
    public override string ToString() => "<recovery code>";
}
