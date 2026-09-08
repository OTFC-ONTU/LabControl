using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Lab;

/// <summary>
/// The signed envelope every offline file uses (M5, D-56 item 1): a domain-separated
/// P-256/SHA-256 signature in IEEE P1363 form over <i>domain + payload bytes</i>, the
/// pattern of <c>UpdateManifestSignature</c> (D-52). One domain per kind, so a signature
/// made for one file can never be replayed as another.
/// </summary>
public static class SignedEnvelope
{
    public static ReadOnlySpan<byte> LabFileDomain => "labcontrol/lab-file/v1\0"u8;

    public static ReadOnlySpan<byte> DeviceRequestDomain => "labcontrol/device-request/v1\0"u8;

    public static ReadOnlySpan<byte> DeviceGrantDomain => "labcontrol/device-grant/v1\0"u8;

    /// <summary>Serialises a payload to the exact bytes that are signed and carried.</summary>
    public static byte[] PayloadBytes<T>(T payload) => JsonSerializer.SerializeToUtf8Bytes(payload, JsonStore.Options);

    /// <summary>Signs with the lab key: lab files and grants.</summary>
    public static string Sign(ReadOnlySpan<byte> domain, LabKey key, ReadOnlySpan<byte> payload) =>
        Convert.ToBase64String(key.Sign(Prefixed(domain, payload)));

    /// <summary>Signs with a device key: requests.</summary>
    public static string Sign(ReadOnlySpan<byte> domain, ECDsa key, ReadOnlySpan<byte> payload) =>
        Convert.ToBase64String(key.SignData(Prefixed(domain, payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static bool Verify(ReadOnlySpan<byte> domain, X509Certificate2 authority, ReadOnlySpan<byte> payload, string signature) =>
        TryDecode(signature, out var decoded) && LabKey.Verify(authority, Prefixed(domain, payload), decoded);

    public static bool Verify(ReadOnlySpan<byte> domain, ECDsa key, ReadOnlySpan<byte> payload, string signature)
    {
        if (!TryDecode(signature, out var decoded))
        {
            return false;
        }

        try
        {
            return key.VerifyData(Prefixed(domain, payload), decoded, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Decodes the envelope's payload field, or throws <see cref="InvalidDataException"/> naming the file.</summary>
    public static byte[] DecodePayload(SignedEnvelopeDocument document, string fileName)
    {
        try
        {
            return Convert.FromBase64String(document.Payload);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"'{fileName}' carries a payload that is not base64.", ex);
        }
    }

    public static T ParsePayload<T>(byte[] bytes, string fileName)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonStore.Options)
                   ?? throw new InvalidDataException($"'{fileName}' carries an empty payload.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{fileName}' carries a payload that is not valid JSON: {ex.Message}", ex);
        }
    }

    private static bool TryDecode(string signature, out byte[] decoded)
    {
        // P-256 IEEE P1363 is exactly 64 bytes / 88 base64 characters.
        decoded = new byte[64];
        return signature.Length == 88 && Convert.TryFromBase64String(signature, decoded, out var count) && count == 64;
    }

    private static byte[] Prefixed(ReadOnlySpan<byte> domain, ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[domain.Length + payload.Length];
        domain.CopyTo(bytes);
        payload.CopyTo(bytes.AsSpan(domain.Length));
        return bytes;
    }
}

/// <summary>What a merge of a snapshot into a saved lab changed (D-56 item 3), for the import row and the log.</summary>
public sealed record LabFileMergeReport(int MachinesAdded, int MachinesUpdated, int MachinesKept, bool LayoutReplaced, int RevocationsAdded, bool OlderSnapshot);

/// <summary>
/// The routine lab file (M5, D-56 item 2): the public CA, the roster, the layout, the
/// revocations and the script library, signed by the lab key and produced only by an
/// explicit <i>Export lab file…</i>. It carries no key, no code and no instance: whoever
/// finds one on a stick learns the room's PC list and nothing that lets them join it.
/// </summary>
public static class LabFile
{
    /// <summary>Builds the payload from a console's own documents; <paramref name="snapshotVersion"/> is the caller's monotonic counter.</summary>
    public static LabFilePayload Snapshot(LabKey lab, LabDocument labDocument, ScriptsDocument? scripts, string issuedByInstanceId, string issuedByInstanceName, long snapshotVersion, DateTimeOffset now)
    {
        var authority = lab.Document.Authority;
        return new LabFilePayload
        {
            LabId = lab.LabId,
            LabName = lab.LabName,
            IssuedAtUnix = now.ToUnixTimeSeconds(),
            IssuedByInstanceId = issuedByInstanceId,
            IssuedByInstanceName = issuedByInstanceName,
            Authority = authority,
            AuthorityFingerprint = ProfileRecord.AuthorityFingerprintOf(authority),
            SnapshotVersion = snapshotVersion,
            Roster = labDocument.Machines.Select(m => new RosterEntry
            {
                AgentId = m.AgentId,
                Number = m.Number,
                Hostname = m.Hostname,
                Mac = m.Mac,
                LastIp = m.LastIp,
                CertificateSerial = m.CertificateSerial,
                CertificateNotAfterUnix = m.CertificateNotAfterUnix,
            }).ToList(),
            Layout = labDocument.Layout.Select(t => new LayoutTile { Number = t.Number, Column = t.Column, Row = t.Row }).ToList(),
            Revocations = labDocument.Revocations.Select(r => new RevocationRecord { Serial = r.Serial, RevokedAtUnix = r.RevokedAtUnix, Reason = r.Reason, Signature = r.Signature }).ToList(),
            Scripts = scripts is null ? null : CopyScripts(scripts),
        };
    }

    /// <summary>The next <c>snapshot_version</c> for a console that last stamped <paramref name="previous"/>: monotonic here, roughly time-ordered across consoles.</summary>
    public static long NextSnapshotVersion(long previous, DateTimeOffset now) => Math.Max(previous + 1, now.ToUnixTimeSeconds());

    /// <summary>Signs a payload into the document that becomes the <c>.lclab</c> file.</summary>
    public static LabFileDocument Export(LabKey lab, LabFilePayload payload)
    {
        var bytes = SignedEnvelope.PayloadBytes(payload);
        return new LabFileDocument
        {
            LabId = payload.LabId,
            LabName = payload.LabName,
            Payload = Convert.ToBase64String(bytes),
            Signature = SignedEnvelope.Sign(SignedEnvelope.LabFileDomain, lab, bytes),
        };
    }

    public static string Serialize(LabFileDocument document) => JsonStore.Serialize(document, LabFileDocument.Migrations);

    /// <summary>Parses the envelope without verifying it: the lab name is in the clear for the results row.</summary>
    public static LabFileDocument Parse(string json, string fileName)
    {
        var document = JsonStore.Parse<LabFileDocument>(json, fileName, LabFileDocument.Migrations);
        if (!string.Equals(document.Kind, LabFileDocument.KindValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{fileName}' is a {Describe(document.Kind)}, not a lab file.");
        }

        return document;
    }

    /// <summary>
    /// Verifies and opens a lab file. On first import the authority inside the file is
    /// trusted (there is nothing else yet); on re-import <paramref name="pinnedAuthority"/>
    /// must be the same certificate, or the file is "the same lab id with a different key".
    /// </summary>
    public static LabFilePayload Open(LabFileDocument document, string fileName, X509Certificate2? pinnedAuthority = null)
    {
        var bytes = SignedEnvelope.DecodePayload(document, fileName);
        var payload = SignedEnvelope.ParsePayload<LabFilePayload>(bytes, fileName);

        using var authority = LoadAuthority(payload.Authority, payload.LabId, fileName, pinnedAuthority);
        if (!SignedEnvelope.Verify(SignedEnvelope.LabFileDomain, authority, bytes, document.Signature))
        {
            throw new InvalidDataException($"'{fileName}' is not signed by the key of lab \"{payload.LabName}\"; the file is damaged or was altered.");
        }

        if (!string.Equals(payload.LabId, document.LabId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{fileName}' names lab {document.LabId} outside and {payload.LabId} inside.");
        }

        return payload;
    }

    /// <summary>
    /// Loads and checks the public CA a payload carries: it must be a LabControl authority
    /// for the payload's lab, and equal the pinned one when there is one.
    /// </summary>
    public static X509Certificate2 LoadAuthority(byte[] der, string labId, string fileName, X509Certificate2? pinnedAuthority)
    {
        X509Certificate2 authority;
        try
        {
            authority = X509CertificateLoader.LoadCertificate(der);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException($"'{fileName}' carries an authority certificate that cannot be read.", ex);
        }

        if (!LabName.TryFromCertificate(authority, out var name) || name.Role != LabRole.Authority || !string.Equals(name.LabId, labId, StringComparison.OrdinalIgnoreCase))
        {
            authority.Dispose();
            throw new InvalidDataException($"'{fileName}' carries a certificate that is not the authority of lab {labId}.");
        }

        if (pinnedAuthority is not null && !pinnedAuthority.RawData.AsSpan().SequenceEqual(authority.RawData))
        {
            authority.Dispose();
            throw new InvalidDataException($"'{fileName}' is for lab {labId} but signed by a different key than the one saved here; the same lab id with a different key is refused.");
        }

        return authority;
    }

    /// <summary>
    /// The merge that cannot go backwards (D-56 item 3). An older snapshot — one at or below
    /// the newest applied here — contributes nothing but its revocations, which are a union
    /// anyway. A newer one adds PCs and refreshes the ones this console has never seen linked;
    /// a PC this console has itself seen keeps its number, name, address and serial as
    /// observed, and only fields that are empty here are filled in. A PC whose certificate
    /// this console holds revoked is never (re-)added: the local revocation outranks the
    /// roster. The layout is replaced only by a newer snapshot; revocations are unioned after
    /// their signatures verify; the instance list, the access level and the local history
    /// are not touched. The caller holds whatever lock guards <paramref name="local"/> and persists it.
    /// </summary>
    public static LabFileMergeReport Merge(LabDocument local, LabFilePayload snapshot, X509Certificate2 authority, RevocationSet revocations, DateTimeOffset now)
    {
        int added = 0, updated = 0, kept = 0;
        var older = snapshot.SnapshotVersion <= local.ImportedSnapshotVersion;

        // Revocations first: an entry the snapshot brings may name a PC in its own roster.
        var newEntries = revocations.Merge(authority, snapshot.Revocations.Select(r => r.ToEntry()));
        foreach (var entry in newEntries)
        {
            local.Revocations.Add(RevocationRecord.From(entry));
        }

        if (older)
        {
            return new LabFileMergeReport(0, 0, 0, false, newEntries.Count, true);
        }

        foreach (var entry in snapshot.Roster)
        {
            if (entry.AgentId.Length == 0 || entry.Number is < 1 || entry.Number > Defaults.MaxStudentPcs)
            {
                continue;
            }

            if (entry.CertificateSerial.Length > 0 && revocations.IsRevoked(entry.CertificateSerial))
            {
                // This console holds that PC's certificate revoked — a PC it removed, or one the
                // lab key struck: a roster cannot bring it back.
                kept++;
                continue;
            }

            var byId = local.Machines.FirstOrDefault(m => string.Equals(m.AgentId, entry.AgentId, StringComparison.OrdinalIgnoreCase));
            var byNumber = local.Machines.FirstOrDefault(m => m.Number == entry.Number);

            if (byId is not null)
            {
                if (byId.LastSeenUnix != 0)
                {
                    // Seen linked here: its number and everything observed on the link stay;
                    // only what this console never learned is filled in.
                    if (Fill(byId, entry))
                    {
                        updated++;
                    }
                    else
                    {
                        kept++;
                    }

                    continue;
                }

                if (byNumber is not null && !ReferenceEquals(byNumber, byId))
                {
                    if (byNumber.LastSeenUnix != 0)
                    {
                        // The snapshot moves this agent onto a number a PC seen here holds: the
                        // number is the identity (D-25), and the observed PC keeps it.
                        kept++;
                        continue;
                    }

                    // Neither record was ever seen linked: the snapshot's view wins.
                    local.Machines.Remove(byNumber);
                }

                Update(byId, entry);
                updated++;
                continue;
            }

            if (byNumber is not null)
            {
                if (byNumber.LastSeenUnix != 0)
                {
                    // The number is the identity (D-25), and this console has itself seen the PC
                    // that holds it: a snapshot cannot take that away.
                    kept++;
                    continue;
                }

                byNumber.AgentId = entry.AgentId;
                Update(byNumber, entry);
                updated++;
                continue;
            }

            var machine = new MachineRecord { AgentId = entry.AgentId, Number = entry.Number };
            Update(machine, entry);
            local.Machines.Add(machine);
            added++;
        }

        local.Machines.Sort((a, b) => a.Number.CompareTo(b.Number));

        var layoutReplaced = false;
        if (snapshot.Layout.Count > 0)
        {
            local.Layout = snapshot.Layout.Select(t => new LayoutTile { Number = t.Number, Column = t.Column, Row = t.Row }).ToList();
            layoutReplaced = true;
        }

        local.ImportedSnapshotVersion = snapshot.SnapshotVersion;

        return new LabFileMergeReport(added, updated, kept, layoutReplaced, newEntries.Count, false);
    }

    /// <summary>A fresh <c>lab.json</c> for a lab this device has never seen: the snapshot and nothing else.</summary>
    public static LabDocument NewLabDocument(LabFilePayload snapshot, X509Certificate2 authority)
    {
        var document = new LabDocument { LabId = snapshot.LabId, LabName = snapshot.LabName };
        Merge(document, snapshot, authority, new RevocationSet(), DateTimeOffset.UtcNow);
        return document;
    }

    /// <summary>A file name that says which lab and when: <c>ОНТФК lab 214 2026-09-08.lclab</c>.</summary>
    public static string SuggestFileName(string labName, DateTimeOffset now) =>
        $"{SafeName(labName)} {now:yyyy-MM-dd}{Defaults.LabFileExtension}";

    /// <summary>Replaces path-unsafe characters, keeping the rest of a room name intact.</summary>
    public static string SafeName(string name)
    {
        var safe = new StringBuilder();
        foreach (var c in name)
        {
            safe.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        return safe.Length == 0 ? "lab" : safe.ToString();
    }

    internal static string Describe(string kind) => kind switch
    {
        LabFileDocument.KindValue => "lab file",
        DeviceRequestDocument.KindValue => "device request",
        DeviceGrantDocument.KindValue => "device grant",
        _ => kind.Length == 0 ? "file of no kind" : $"'{kind}' file",
    };

    /// <summary>A record this console never saw linked takes the snapshot's view of it.</summary>
    private static void Update(MachineRecord machine, RosterEntry entry)
    {
        machine.Number = entry.Number;
        if (entry.Hostname.Length > 0)
        {
            machine.Hostname = entry.Hostname;
        }

        if (entry.Mac.Length > 0)
        {
            machine.Mac = entry.Mac;
        }

        if (!string.IsNullOrEmpty(entry.LastIp))
        {
            machine.LastIp = entry.LastIp;
        }

        if (entry.CertificateSerial.Length > 0)
        {
            machine.CertificateSerial = LabCertificates.NormalizeSerial(entry.CertificateSerial);
            machine.CertificateNotAfterUnix = entry.CertificateNotAfterUnix;
        }
    }

    /// <summary>A record seen linked here only gains what it lacks; returns whether anything was filled in.</summary>
    private static bool Fill(MachineRecord machine, RosterEntry entry)
    {
        var changed = false;
        if (machine.Hostname.Length == 0 && entry.Hostname.Length > 0)
        {
            machine.Hostname = entry.Hostname;
            changed = true;
        }

        if (machine.Mac.Length == 0 && entry.Mac.Length > 0)
        {
            machine.Mac = entry.Mac;
            changed = true;
        }

        if (string.IsNullOrEmpty(machine.LastIp) && !string.IsNullOrEmpty(entry.LastIp))
        {
            machine.LastIp = entry.LastIp;
            changed = true;
        }

        return changed;
    }

    private static ScriptsDocument CopyScripts(ScriptsDocument scripts) =>
        JsonSerializer.Deserialize<ScriptsDocument>(JsonSerializer.SerializeToUtf8Bytes(scripts, JsonStore.Options), JsonStore.Options) ?? new ScriptsDocument();
}
