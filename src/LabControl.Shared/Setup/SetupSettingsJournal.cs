using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Setup;

public enum SettingChangeResult { Applied, AlreadyApplied, Unchanged, Restored, Untracked, Conflict }
public enum SettingChangePhase { PendingApply, Applied, PendingRestore, Restored }

/// <summary>One code-defined setting, never a path/command supplied by journal data.
/// Null means absent; non-null bytes canonically encode both native type and value.
/// Reads must throw on failure. Writes must report errors without including values.
/// Adapters must recheck identity and guard against concurrent native changes.</summary>
public interface ISetupSetting
{
    string Id { get; }
    byte[]? Read();
    void Write(byte[]? value);
}

/// <summary>For settings whose persisted value needs a separate native activation.
/// Activation must recheck the expected identity/value, never change the saved value,
/// and be safe to repeat after a crash. Errors must leave completion unconfirmed.</summary>
public interface ISetupSettingActivation
{
    void Activate(byte[]? expected);
}

/// <summary>Write-ahead journal under Setup's private-directory/exclusive-lock scope.
/// All contents are protected before disk I/O. Missing history is an error, not an empty
/// baseline; initialize once before a positively identified fresh setup changes anything.</summary>
public sealed class SetupSettingsJournal(
    string dataDirectory, string installationId,
    Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
{
    public string FilePath { get; } = Path.Combine(dataDirectory, Defaults.SetupSettingsFileName);

    public void InitializeNew()
    {
        ValidateInstallationId();
        Save(new JournalData { SchemaVersion = Defaults.SetupSettingsSchemaVersion, InstallationId = installationId, Entries = [] }, overwrite: false);
    }

    public SettingChangeResult Apply(ISetupSetting setting, byte[]? desired) =>
        ApplyFromCurrent(setting, _ => desired);

    /// <summary>Select an acceptable desired value only after history is validated.
    /// The selector must not mutate native state.</summary>
    public SettingChangeResult ApplyFromCurrent(ISetupSetting setting, Func<byte[]?, byte[]?> selectDesired)
    {
        ValidateId(setting.Id);
        var data = Load();
        var entry = data.Entries.SingleOrDefault(e => e.Id == setting.Id);
        var current = setting.Read();
        var desired = selectDesired(current?.ToArray());
        if (entry is not null)
        {
            if (!Equal(entry.Applied, desired) || entry.Phase is SettingChangePhase.PendingRestore or SettingChangePhase.Restored)
                return SettingChangeResult.Conflict;
            if (entry.Phase == SettingChangePhase.Applied)
            {
                if (!Equal(current, entry.Applied)) return SettingChangeResult.Conflict;
                Activate(setting, entry.Applied);
                return SettingChangeResult.AlreadyApplied;
            }
            // A write may have happened before the completion record was saved. Equality
            // with desired is not proof that Setup made it: preserve ambiguous history.
            if (!Equal(current, entry.Original)) return SettingChangeResult.Conflict;
        }
        else
        {
            if (Equal(current, desired)) return SettingChangeResult.Unchanged;
            entry = new SettingEntry { Id = setting.Id, Original = current?.ToArray(), Applied = desired?.ToArray(), Phase = SettingChangePhase.PendingApply };
            data.Entries.Add(entry);
            Save(data);
        }

        if (!Equal(setting.Read(), entry.Original)) return SettingChangeResult.Conflict;
        setting.Write(entry.Applied?.ToArray());
        if (!Equal(setting.Read(), entry.Applied)) return SettingChangeResult.Conflict;
        Activate(setting, entry.Applied);
        entry.Phase = SettingChangePhase.Applied;
        Save(data);
        return SettingChangeResult.Applied;
    }

    public SettingChangeResult Restore(ISetupSetting setting)
    {
        ValidateId(setting.Id);
        var data = Load();
        var entry = data.Entries.SingleOrDefault(e => e.Id == setting.Id);
        if (entry is null) return SettingChangeResult.Untracked;
        if (entry.Phase == SettingChangePhase.Restored)
        {
            if (setting is ISetupSettingActivation)
            {
                if (!Equal(setting.Read(), entry.Original)) return SettingChangeResult.Conflict;
                Activate(setting, entry.Original);
            }
            return SettingChangeResult.Restored;
        }
        if (entry.Phase == SettingChangePhase.PendingApply) return SettingChangeResult.Conflict;
        var current = setting.Read();
        if (Equal(current, entry.Original))
        {
            Activate(setting, entry.Original);
            entry.Phase = SettingChangePhase.Restored;
            Save(data);
            return SettingChangeResult.Restored;
        }
        if (!Equal(current, entry.Applied)) return SettingChangeResult.Conflict;
        entry.Phase = SettingChangePhase.PendingRestore;
        Save(data);
        if (!Equal(setting.Read(), entry.Applied)) return SettingChangeResult.Conflict;
        setting.Write(entry.Original?.ToArray());
        if (!Equal(setting.Read(), entry.Original)) return SettingChangeResult.Conflict;
        Activate(setting, entry.Original);
        entry.Phase = SettingChangePhase.Restored;
        Save(data);
        return SettingChangeResult.Restored;
    }

    private JournalData Load()
    {
        ValidateInstallationId();
        var envelope = JsonStore.Load<SetupSettingsDocument>(FilePath, SetupSettingsDocument.Migrations);
        if (envelope.Payload is not { Length: > 0 }) throw InvalidJournal();
        byte[] plaintext;
        try { plaintext = unprotect(envelope.Payload); }
        catch (CryptographicException) { throw InvalidJournal(); }
        try
        {
            JournalData data;
            try
            {
                data = JsonSerializer.Deserialize<JournalData>(plaintext, JsonStore.Options) ?? throw InvalidJournal();
            }
            // Do not attach parser errors: a property name or value may contain a secret.
            catch (JsonException) { throw InvalidJournal(); }
            if (data.SchemaVersion != Defaults.SetupSettingsSchemaVersion || data.InstallationId != installationId
                || data.Entries is null || data.Entries.Any(e => e is null)
                || data.Entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != data.Entries.Count)
                throw InvalidJournal();
            foreach (var entry in data.Entries)
            {
                ValidateId(entry.Id);
                if (!Enum.IsDefined(entry.Phase) || Equal(entry.Original, entry.Applied)) throw InvalidJournal();
            }
            return data;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void Save(JournalData data, bool overwrite = true)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(data, JsonStore.Options);
        byte[] encrypted;
        try { encrypted = protect(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SetupSettingsDocument { Payload = encrypted }, JsonStore.Options);
        var temporary = FilePath + ".tmp";
        // The private directory must already exist. Flush the intent before invoking the
        // adapter; only ciphertext is ever written, with private permissions at creation.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, FilePath, overwrite);
    }

    private void ValidateInstallationId()
    {
        if (!Guid.TryParseExact(installationId, "D", out var id) || id == Guid.Empty) throw InvalidJournal();
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128
            || id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')))
            throw InvalidJournal();
    }

    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static void Activate(ISetupSetting setting, byte[]? expected)
    {
        if (setting is ISetupSettingActivation activation) activation.Activate(expected?.ToArray());
    }
    private static InvalidDataException InvalidJournal() => new("Setup settings history is invalid or cannot be decrypted; preserve settings for manual review.");

    private sealed class JournalData
    {
        public required int SchemaVersion { get; set; }
        public required string InstallationId { get; set; }
        public required List<SettingEntry> Entries { get; set; }
    }

    private sealed class SettingEntry
    {
        public required string Id { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public required byte[]? Original { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public required byte[]? Applied { get; set; }
        public required SettingChangePhase Phase { get; set; }
    }
}
