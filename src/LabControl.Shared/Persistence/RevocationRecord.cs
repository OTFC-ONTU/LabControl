using Google.Protobuf;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Persistence;

/// <summary>
/// A revocation entry as it is stored on disk. Identical in content to the wire message —
/// the entry is self-authenticating, so it needs no envelope of its own (D-21).
/// </summary>
public sealed class RevocationRecord
{
    public string Serial { get; set; } = string.Empty;

    public long RevokedAtUnix { get; set; }

    public string Reason { get; set; } = string.Empty;

    public byte[] Signature { get; set; } = [];

    public static RevocationRecord From(RevocationEntry entry) => new()
    {
        Serial = entry.Serial,
        RevokedAtUnix = entry.RevokedAtUnix,
        Reason = entry.Reason,
        Signature = entry.Signature.ToByteArray(),
    };

    public RevocationEntry ToEntry() => new()
    {
        Serial = Serial,
        RevokedAtUnix = RevokedAtUnix,
        Reason = Reason,
        Signature = ByteString.CopyFrom(Signature),
    };
}
