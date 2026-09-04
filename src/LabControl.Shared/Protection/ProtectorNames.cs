namespace LabControl.Shared.Protection;

/// <summary>
/// The names written into <c>ProtectedSecret.protector</c>. They live outside the
/// protectors themselves because the selector has to name a Windows or macOS protector
/// while running on neither.
/// </summary>
public static class ProtectorNames
{
    /// <summary>macOS Keychain.</summary>
    public const string Keychain = "keychain";

    /// <summary>Windows DPAPI, current user.</summary>
    public const string Dpapi = "dpapi";

    /// <summary>Linux keyring through libsecret.</summary>
    public const string LibSecret = "libsecret";

    /// <summary>Encrypted file bound to this machine and user; the documented fallback.</summary>
    public const string File = "file";
}
