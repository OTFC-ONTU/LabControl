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

/// <summary>Opt-in only for atomic create-new resources carrying this installation's
/// unguessable identity in the same native creation operation. Equality alone is not proof.
/// This read-only check must verify the complete native identity and configuration.</summary>
public interface ISetupOwnedCreation
{
    bool ConfirmsOwnedCreation(byte[] expected);
}

/// <summary>Opt-in only for an additive value whose presence is useful even when Setup
/// cannot prove that it created it. After an interrupted absent-to-present apply, repair
/// may discard the pending ownership record and preserve the value as pre-existing.
/// Removal must then leave it in place.</summary>
public interface ISetupPreservableExistingValue { }

/// <summary>Opt-in for a native operation whose successful exact post-state is chosen
/// by Windows. The requested bytes still bound the operation; an accepted resolved value
/// is journaled only after read-back. A matching interrupted result is preserved without
/// ownership because completion was never recorded.</summary>
public interface ISetupResolvedAppliedValue
{
    bool AcceptsResolvedAppliedValue(byte[]? requested, byte[]? actual);
}

/// <summary>Write-ahead journal under Setup's private-directory/exclusive-lock scope.
/// All contents are protected before disk I/O. Missing history is an error, not an empty
/// baseline; initialize once before a positively identified fresh setup changes anything.</summary>
public sealed class SetupSettingsJournal(
    string dataDirectory, string installationId,
    Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
{
    public string FilePath { get; } = Path.Combine(dataDirectory, Defaults.SetupSettingsFileName);

    public void Validate() => _ = Load();

    /// <summary>Detect missing companion metadata without exposing protected setting values.</summary>
    public bool HasEntries(string prefix)
    {
        ValidateId(prefix);
        return Load().Entries.Any(entry => entry.Id.StartsWith(prefix, StringComparison.Ordinal));
    }
    public IReadOnlyList<string> SettingIds() => Load().Entries.Select(entry => entry.Id).ToArray();
    public IReadOnlyList<string> PendingRestorationIds() => Load().Entries
        .Where(entry => entry.Phase != SettingChangePhase.Restored).Select(entry => entry.Id).ToArray();

    public void InitializeNew()
    {
        ValidateInstallationId();
        Save(new JournalData { SchemaVersion = Defaults.SetupSettingsSchemaVersion, InstallationId = installationId, Entries = [] }, overwrite: false);
    }

    /// <summary>Read-only planning with the same ownership rules as Apply. Native
    /// activation is intentionally left to Apply even for confirmed persisted values.</summary>
    public SetupCheck Check(ISetupSetting setting, Func<byte[]?, byte[]?> selectDesired)
    {
        ValidateId(setting.Id);
        var data = Load();
        var entry = data.Entries.SingleOrDefault(e => e.Id == setting.Id);
        var current = setting.Read();
        var desired = selectDesired(current?.ToArray());
        if (entry is null)
            return new(Equal(current, desired) ? SetupStepStatus.AlreadyDone : SetupStepStatus.Needed);
        if (entry.Phase == SettingChangePhase.PendingApply
            && (CanPreserveAsExisting(setting, entry, current)
                || AcceptsResolvedAppliedValue(setting, entry.Applied, current)))
            return new(SetupStepStatus.Needed);
        if (!Equal(entry.Applied, desired) || entry.Phase is SettingChangePhase.PendingRestore or SettingChangePhase.Restored)
            return new(SetupStepStatus.Conflict, "Saved ownership does not permit this change.");
        if (entry.Phase == SettingChangePhase.Applied)
            return new(Equal(current, entry.Applied) ? SetupStepStatus.AlreadyDone : SetupStepStatus.Conflict,
                Equal(current, entry.Applied) ? "" : "A later change was preserved.");
        if (ConfirmsCreation(setting, entry, current))
            return new(SetupStepStatus.Needed);
        return new(Equal(current, entry.Original) ? SetupStepStatus.Needed : SetupStepStatus.Conflict,
            Equal(current, entry.Original) ? "" : "An interrupted change requires review.");
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
            if (entry.Phase == SettingChangePhase.PendingApply
                && (CanPreserveAsExisting(setting, entry, current)
                    || AcceptsResolvedAppliedValue(setting, entry.Applied, current)))
            {
                // A crash may have happened after the native effect but before its exact
                // representation and ownership could be recorded. Preserve the effect,
                // discard only the unconfirmed intent, and never adopt it on repair.
                data.Entries.Remove(entry);
                Save(data);
                return SettingChangeResult.Unchanged;
            }
            if (!Equal(entry.Applied, desired) || entry.Phase is SettingChangePhase.PendingRestore or SettingChangePhase.Restored)
                return SettingChangeResult.Conflict;
            if (entry.Phase == SettingChangePhase.Applied)
            {
                if (!Equal(current, entry.Applied)) return SettingChangeResult.Conflict;
                Activate(setting, entry.Applied);
                return SettingChangeResult.AlreadyApplied;
            }
            if (ConfirmsCreation(setting, entry, current))
            {
                entry.Phase = SettingChangePhase.Applied;
                Save(data);
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
        var actual = setting.Read();
        if (!Equal(actual, entry.Applied))
        {
            if (!AcceptsResolvedAppliedValue(setting, entry.Applied, actual)) return SettingChangeResult.Conflict;
            entry.Applied = actual?.ToArray();
        }
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
        if (entry.Phase == SettingChangePhase.PendingApply)
        {
            var pendingCurrent = setting.Read();
            if (CanPreserveAsExisting(setting, entry, pendingCurrent)
                || AcceptsResolvedAppliedValue(setting, entry.Applied, pendingCurrent))
            {
                data.Entries.Remove(entry);
                Save(data);
                return SettingChangeResult.Untracked;
            }
            if (!ConfirmsCreation(setting, entry, pendingCurrent)) return SettingChangeResult.Conflict;
            entry.Phase = SettingChangePhase.Applied;
            Save(data);
        }
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

    private static bool ConfirmsCreation(ISetupSetting setting, SettingEntry entry, byte[]? current) =>
        entry.Phase == SettingChangePhase.PendingApply && entry.Original is null && entry.Applied is not null
        && Equal(current, entry.Applied) && setting is ISetupOwnedCreation owned
        && owned.ConfirmsOwnedCreation(entry.Applied.ToArray()) && Equal(setting.Read(), entry.Applied);

    private static bool CanPreserveAsExisting(ISetupSetting setting, SettingEntry entry, byte[]? current) =>
        setting is ISetupPreservableExistingValue && entry.Phase == SettingChangePhase.PendingApply
        && entry.Original is null && entry.Applied is not null && Equal(current, entry.Applied);

    private static bool AcceptsResolvedAppliedValue(ISetupSetting setting, byte[]? requested, byte[]? actual) =>
        setting is ISetupResolvedAppliedValue resolved && resolved.AcceptsResolvedAppliedValue(
            requested?.ToArray(), actual?.ToArray());

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
