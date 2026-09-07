using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Lab;

/// <summary>
/// Exports and imports the one file that moves a lab to another teacher machine
/// (ARCHITECTURE §3.6, D-26). Export needs the lab key unlocked — sealing the payload
/// needs the master key — and import needs whatever opens the lab key: a holder's
/// passphrase or the recovery code. The archive is useless on its own.
/// </summary>
public static class LabBackup
{
    private static string Context(string labId) => $"labcontrol/backup/{labId}";

    public static BackupDocument Export(LabKey lab, LabDocument labDocument, IReadOnlyDictionary<string, string> catalog, string exportedBy, DateTimeOffset now,
        EnrollmentDocument? enrollment = null, ScriptsDocument? scripts = null)
    {
        var payload = new BackupPayload { Lab = labDocument, Catalog = new Dictionary<string, string>(catalog), Enrollment = enrollment, Scripts = scripts };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonStore.Options);

        return new BackupDocument
        {
            LabId = lab.LabId,
            LabName = lab.LabName,
            ExportedAtUnix = now.ToUnixTimeSeconds(),
            ExportedBy = exportedBy,
            LabKey = lab.Document,
            Payload = lab.Seal(bytes, Context(lab.LabId)),
        };
    }

    /// <summary>Serializes an export to the text that goes into the <c>.lcbak</c> file.</summary>
    public static string Serialize(BackupDocument backup) => JsonStore.Serialize(backup, BackupDocument.Migrations);

    /// <summary>Parses a backup file without opening it: the lab name and the holder names are in the clear.</summary>
    public static BackupDocument Parse(string json, string fileName) =>
        JsonStore.Parse<BackupDocument>(json, fileName, BackupDocument.Migrations);

    /// <summary>Opens the sealed payload with an unlocked lab key that came from the same document.</summary>
    public static BackupPayload Open(BackupDocument backup, LabKey lab)
    {
        if (!string.Equals(lab.LabId, backup.LabId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("This lab key does not belong to the lab in the backup.");
        }

        byte[] bytes;
        try
        {
            bytes = lab.Open(backup.Payload, Context(backup.LabId));
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("The backup's contents do not match its lab key; the file is damaged.", ex);
        }

        try
        {
            return JsonSerializer.Deserialize<BackupPayload>(bytes, JsonStore.Options)
                   ?? throw new InvalidDataException("The backup holds no lab data.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>A file name that says which lab and when: <c>ОНТФК lab 214 2026-09-04.lcbak</c>.</summary>
    public static string SuggestFileName(string labName, DateTimeOffset now)
    {
        var safe = new StringBuilder();
        foreach (var c in labName)
        {
            safe.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        return $"{safe} {now:yyyy-MM-dd}{Defaults.BackupFileExtension}";
    }
}
