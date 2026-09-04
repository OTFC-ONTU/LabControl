using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Identity;

/// <summary>
/// Every revoked certificate this party has ever seen. Entries are self-authenticating —
/// signed by the lab key — so the list has no version and no owner and every party keeps
/// the <b>union</b> of what it has been shown (D-21). That is what lets two teacher
/// machines revoke independently and still converge, and what stops a console or an agent
/// from being talked into a revocation by a TLS peer alone.
/// </summary>
public sealed class RevocationSet
{
    private readonly Dictionary<string, RevocationEntry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The bytes the lab key signs: <c>serial|revoked_at_unix|reason</c>.</summary>
    public static byte[] SignedContent(string serial, long revokedAtUnix, string reason) =>
        Encoding.UTF8.GetBytes($"{LabCertificates.NormalizeSerial(serial)}|{revokedAtUnix}|{reason}");

    /// <summary>Creates a signed entry. Revoking already needs the lab key, so signing is free.</summary>
    public static RevocationEntry Create(LabKey lab, string serial, string reason, DateTimeOffset at)
    {
        var normalized = LabCertificates.NormalizeSerial(serial);
        var revokedAt = at.ToUnixTimeSeconds();

        return new RevocationEntry
        {
            Serial = normalized,
            RevokedAtUnix = revokedAt,
            Reason = reason,
            Signature = ByteString.CopyFrom(lab.Sign(SignedContent(normalized, revokedAt, reason))),
        };
    }

    /// <summary>An entry that does not verify against the pinned CA is dropped, not stored.</summary>
    public static bool Verify(X509Certificate2 authority, RevocationEntry entry) =>
        entry.Serial.Length > 0 &&
        LabKey.Verify(authority, SignedContent(entry.Serial, entry.RevokedAtUnix, entry.Reason), entry.Signature.Span);

    public IReadOnlyCollection<RevocationEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public IReadOnlyCollection<string> Serials
    {
        get
        {
            lock (_gate)
            {
                return _entries.Keys.ToArray();
            }
        }
    }

    /// <summary>
    /// Merges one entry after checking its signature. Returns <c>true</c> only when the
    /// entry was both valid and new, so the caller can log a genuine change and persist.
    /// </summary>
    public bool TryAdd(X509Certificate2 authority, RevocationEntry entry)
    {
        if (!Verify(authority, entry))
        {
            return false;
        }

        var serial = LabCertificates.NormalizeSerial(entry.Serial);
        lock (_gate)
        {
            if (_entries.ContainsKey(serial))
            {
                return false;
            }

            _entries[serial] = entry;
            return true;
        }
    }

    /// <summary>Merges a batch; returns the entries that were new.</summary>
    public IReadOnlyList<RevocationEntry> Merge(X509Certificate2 authority, IEnumerable<RevocationEntry> entries)
    {
        var added = new List<RevocationEntry>();
        foreach (var entry in entries)
        {
            if (TryAdd(authority, entry))
            {
                added.Add(entry);
            }
        }

        return added;
    }

    public bool IsRevoked(string serial)
    {
        var normalized = LabCertificates.NormalizeSerial(serial);
        lock (_gate)
        {
            return _entries.ContainsKey(normalized);
        }
    }

    /// <summary>The entries the other side is missing, given the serials it says it holds.</summary>
    public IReadOnlyList<RevocationEntry> Except(IEnumerable<string> serials)
    {
        var known = new HashSet<string>(serials.Select(LabCertificates.NormalizeSerial), StringComparer.Ordinal);
        lock (_gate)
        {
            return _entries.Where(pair => !known.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        }
    }
}
