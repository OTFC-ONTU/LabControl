using LabControl.Shared.Persistence;

namespace LabControl.Shared.Protection;

/// <summary>
/// Protects the console instance's private key at rest (ARCHITECTURE §3.2). This is
/// deliberately <b>not</b> how the lab key is protected — that one is passphrase-wrapped so
/// it can be carried to another computer. The instance key never leaves its machine, so
/// the machine's own keystore is exactly the right place for it.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Stable identifier written into <see cref="ProtectedSecret.Protector"/>.</summary>
    string Name { get; }

    /// <summary>True when this protector can actually work on this machine right now.</summary>
    bool IsAvailable { get; }

    ProtectedSecret Protect(string reference, ReadOnlySpan<byte> secret);

    bool TryUnprotect(ProtectedSecret secret, out byte[] plaintext);

    /// <summary>Removes the secret. Called when an instance is retired, never on a read failure.</summary>
    void Forget(ProtectedSecret secret);
}
