using LabControl.Shared.Persistence;

namespace LabControl.Shared.Identity;

/// <summary>Which secret opens a wrapping of the master key.</summary>
public enum KeyWrappingKind
{
    Unknown = 0,

    /// <summary>A named person and their passphrase.</summary>
    Holder = 1,

    /// <summary>The printed recovery code. There is exactly one, and it can be reissued.</summary>
    Recovery = 2,
}

/// <summary>
/// One way to open the master key (ARCHITECTURE §3.2). Every wrapping is equal: there is
/// no owner wrapping that outranks the others, so an ill teacher cannot stop an exam.
/// </summary>
public sealed class KeyWrapping
{
    public string Id { get; set; } = string.Empty;

    public KeyWrappingKind Kind { get; set; }

    /// <summary>
    /// Stored in the clear on purpose: you must be able to see <i>who</i> can open the lab
    /// without opening it yourself.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public long CreatedAtUnix { get; set; }

    public byte[] Salt { get; set; } = [];

    public int Iterations { get; set; }

    /// <summary>The master key, sealed under the key derived from this holder's secret.</summary>
    public SealedSecret Secret { get; set; } = new();
}

/// <summary>
/// <c>lab-key.lck</c> — the lab's private certificate authority at rest. The only file
/// that must survive the loss of the teacher machine (D-13); useless to whoever finds it
/// without a holder's passphrase or the recovery code.
/// </summary>
public sealed class LabKeyDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.LabKeySchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.LabKeySchemaVersion;

    /// <summary>Identifies the lab, not the console. Survives every change of teacher machine.</summary>
    public string LabId { get; set; } = string.Empty;

    public string LabName { get; set; } = string.Empty;

    public long CreatedAtUnix { get; set; }

    /// <summary>The public CA certificate, DER. This is what the USB payload carries (D-14).</summary>
    public byte[] Authority { get; set; } = [];

    /// <summary>The CA private key (PKCS#8), sealed under the master key.</summary>
    public SealedSecret AuthorityPrivateKey { get; set; } = new();

    public List<KeyWrapping> Wrappings { get; set; } = [];
}
