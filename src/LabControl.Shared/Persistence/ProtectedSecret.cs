namespace LabControl.Shared.Persistence;

/// <summary>
/// A secret at rest, as it appears inside a document. What <see cref="Payload"/> means
/// depends on <see cref="Protector"/>: an OS keystore stores the secret itself and leaves
/// only a reference here, while DPAPI and the encrypted-file fallback store a blob.
/// </summary>
public sealed class ProtectedSecret
{
    /// <summary>Which protector wrote this: <c>keychain</c>, <c>dpapi</c>, <c>libsecret</c>, <c>file</c>.</summary>
    public string Protector { get; set; } = string.Empty;

    /// <summary>Names the item inside the keystore, and the context an AEAD blob is bound to.</summary>
    public string Reference { get; set; } = string.Empty;

    public byte[] Payload { get; set; } = [];

    /// <summary>Per-secret salt for the fallback's machine-bound key derivation.</summary>
    public byte[] Salt { get; set; } = [];
}
