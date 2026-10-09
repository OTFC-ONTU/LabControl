using System.Security.Cryptography;
using System.Text;
using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class SetupSettingsJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _installationId = Guid.NewGuid().ToString("D");
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private SetupSettingsJournal Open(string? installationId = null) => new(_directory, installationId ?? _installationId, Encrypt, Decrypt);

    public SetupSettingsJournalTests()
    {
        Directory.CreateDirectory(_directory);
        Open().InitializeNew();
    }

    [Theory]
    [InlineData(null, "enabled")]
    [InlineData("", "enabled")]
    [InlineData("old", null)]
    [InlineData("old", "new")]
    public void Apply_repair_and_removal_preserve_the_original_including_absence(string? original, string? desired)
    {
        var setting = new FakeSetting(original);
        Assert.Equal(SettingChangeResult.Applied, Open().Apply(setting, Bytes(desired)));
        Assert.Equal(SettingChangeResult.AlreadyApplied, Open().Apply(setting, Bytes(desired)));
        Assert.Equal(1, setting.Writes);
        Assert.Equal(SettingChangeResult.Restored, Open().Restore(setting));
        Assert.Equal(Bytes(original), setting.Value);
        Assert.Equal(SettingChangeResult.Restored, Open().Restore(setting));
        Assert.Equal(2, setting.Writes);
        Assert.Equal(SettingChangeResult.Conflict, Open().Apply(setting, Bytes(desired)));
    }

    [Fact]
    public void Preexisting_rule_or_exclusion_is_not_owned_or_removed()
    {
        var setting = new FakeSetting("enabled");
        Assert.Equal(SettingChangeResult.Unchanged, Open().Apply(setting, Bytes("enabled")));
        Assert.Equal(SettingChangeResult.Untracked, Open().Restore(setting));
        Assert.Equal(0, setting.Writes);
    }

    [Fact]
    public void Later_user_changes_are_preserved_on_repair_and_removal()
    {
        var setting = new FakeSetting("old");
        Open().Apply(setting, Bytes("setup"));
        setting.Value = Bytes("user");
        Assert.Equal(SettingChangeResult.Conflict, Open().Apply(setting, Bytes("setup")));
        Assert.Equal(SettingChangeResult.Conflict, Open().Restore(setting));
        Assert.Equal(Bytes("user"), setting.Value);
        Assert.Equal(1, setting.Writes);
    }

    [Fact]
    public void Journal_and_temporary_file_contain_only_ciphertext()
    {
        const string secret = "private-original-sign-in-state";
        var setting = new FakeSetting(secret);
        setting.BeforeWrite = () =>
        {
            var json = File.ReadAllText(Open().FilePath);
            Assert.DoesNotContain(secret, json);
            Assert.DoesNotContain(Convert.ToBase64String(Bytes(secret)!), json);
            var envelope = JsonStore.Load<SetupSettingsDocument>(Open().FilePath, SetupSettingsDocument.Migrations);
            Assert.Contains(Convert.ToBase64String(Bytes(secret)!), Encoding.UTF8.GetString(Decrypt(envelope.Payload)));
            // A failed completion rename leaves ciphertext in .tmp too.
            File.Delete(Open().FilePath);
            Directory.CreateDirectory(Open().FilePath);
        };
        Assert.ThrowsAny<IOException>(() => Open().Apply(setting, Bytes("new")));
        var temporary = File.ReadAllText(Open().FilePath + ".tmp");
        Assert.DoesNotContain(secret, temporary);
        Assert.DoesNotContain(Convert.ToBase64String(Bytes(secret)!), temporary);
    }

    [Fact]
    public void Failed_intent_save_prevents_native_write_and_does_not_lose_the_original()
    {
        var setting = new FakeSetting("old");
        Directory.CreateDirectory(Open().FilePath + ".tmp");
        Assert.Throws<UnauthorizedAccessException>(() => Open().Apply(setting, Bytes("new")));
        Assert.Equal(0, setting.Writes);
        Directory.Delete(Open().FilePath + ".tmp");
        Assert.Equal(SettingChangeResult.Applied, Open().Apply(setting, Bytes("new")));
        Open().Restore(setting);
        Assert.Equal(Bytes("old"), setting.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_apply_never_adopts_an_ambiguous_changed_value(bool changedBeforeFailure)
    {
        var setting = new FakeSetting("old");
        setting.BeforeWrite = () =>
        {
            if (changedBeforeFailure) setting.Value = Bytes("new");
            throw new IOException("native write interrupted");
        };
        Assert.Throws<IOException>(() => Open().Apply(setting, Bytes("new")));
        Assert.Equal(SettingChangeResult.Conflict, Open().Restore(setting));
        setting.BeforeWrite = null;
        Assert.Equal(changedBeforeFailure ? SettingChangeResult.Conflict : SettingChangeResult.Applied,
            Open().Apply(setting, Bytes("new")));
    }

    [Fact]
    public void Failed_completion_save_is_not_treated_as_confirmed_ownership()
    {
        var setting = new FakeSetting("old");
        setting.BeforeWrite = () => Directory.CreateDirectory(Open().FilePath + ".tmp");
        Assert.Throws<UnauthorizedAccessException>(() => Open().Apply(setting, Bytes("new")));
        Directory.Delete(Open().FilePath + ".tmp");
        setting.BeforeWrite = null;
        Assert.Equal(SettingChangeResult.Conflict, Open().Apply(setting, Bytes("new")));
        Assert.Equal(SettingChangeResult.Conflict, Open().Restore(setting));
        Assert.Equal(Bytes("new"), setting.Value);
    }

    [Fact]
    public void Interrupted_preservable_additive_value_is_dropped_from_ownership_and_left_in_place()
    {
        var setting = new PreservableSetting();
        Assert.Throws<IOException>(() => Open().Apply(setting, Bytes("present")));

        Assert.Equal(SetupStepStatus.Needed, Open().Check(setting, _ => Bytes("present")).Status);
        Assert.Equal(SettingChangeResult.Unchanged, Open().Apply(setting, Bytes("present")));
        Assert.Equal(SettingChangeResult.Untracked, Open().Restore(setting));
        Assert.Equal(Bytes("present"), setting.Value);
        Assert.Equal(1, setting.Writes);
    }

    [Fact]
    public void Removal_preserves_an_interrupted_preservable_additive_value_without_adopting_it()
    {
        var setting = new PreservableSetting();
        Assert.Throws<IOException>(() => Open().Apply(setting, Bytes("present")));

        Assert.Equal(SettingChangeResult.Untracked, Open().Restore(setting));
        Assert.Equal(Bytes("present"), setting.Value);
        Assert.Equal(1, setting.Writes);
    }

    [Fact]
    public void Native_resolved_apply_is_journaled_exactly_and_later_edits_conflict()
    {
        var setting = new ResolvedSetting();
        Assert.Equal(SettingChangeResult.Applied, Open().Apply(setting, Bytes("requested")));
        Assert.Equal(SettingChangeResult.AlreadyApplied, Open().Apply(setting, setting.Value));

        setting.Value = Bytes("later-edit");
        Assert.Equal(SettingChangeResult.Conflict, Open().Restore(setting));
    }

    [Fact]
    public void Interrupted_native_resolved_apply_is_preserved_without_ownership()
    {
        var setting = new ResolvedSetting { FailAfterWrite = true };
        Assert.Throws<IOException>(() => Open().Apply(setting, Bytes("requested")));

        setting.FailAfterWrite = false;
        Assert.Equal(SetupStepStatus.Needed, Open().Check(setting, _ => Bytes("requested")).Status);
        Assert.Equal(SettingChangeResult.Unchanged, Open().Apply(setting, Bytes("requested")));
        Assert.Equal(SettingChangeResult.Untracked, Open().Restore(setting));
        Assert.Equal(Bytes("resolved"), setting.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_restore_can_resume_without_reapplying_setup(bool restoredBeforeFailure)
    {
        var setting = new FakeSetting("old");
        Open().Apply(setting, Bytes("new"));
        setting.BeforeWrite = () =>
        {
            if (restoredBeforeFailure) setting.Value = Bytes("old");
            throw new IOException("restore interrupted");
        };
        Assert.Throws<IOException>(() => Open().Restore(setting));
        setting.BeforeWrite = null;
        Assert.Equal(SettingChangeResult.Conflict, Open().Apply(setting, Bytes("new")));
        Assert.Equal(SettingChangeResult.Restored, Open().Restore(setting));
        Assert.Equal(Bytes("old"), setting.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_restore_journal_save_keeps_recovery_possible(bool failCompletion)
    {
        var setting = new FakeSetting("old");
        Open().Apply(setting, Bytes("new"));
        if (failCompletion) setting.BeforeWrite = () => Directory.CreateDirectory(Open().FilePath + ".tmp");
        else Directory.CreateDirectory(Open().FilePath + ".tmp");
        Assert.Throws<UnauthorizedAccessException>(() => Open().Restore(setting));
        Assert.Equal(Bytes(failCompletion ? "old" : "new"), setting.Value);
        Directory.Delete(Open().FilePath + ".tmp");
        setting.BeforeWrite = null;
        Assert.Equal(SettingChangeResult.Restored, Open().Restore(setting));
        Assert.Equal(Bytes("old"), setting.Value);
        Assert.Equal(2, setting.Writes);
    }

    [Fact]
    public void Changes_between_restore_intent_and_write_are_preserved()
    {
        var setting = new FakeSetting("old");
        Open().Apply(setting, Bytes("new"));
        var reads = 0;
        setting.BeforeRead = () => { if (++reads == 2) setting.Value = Bytes("user"); };
        Assert.Equal(SettingChangeResult.Conflict, Open().Restore(setting));
        Assert.Equal(Bytes("user"), setting.Value);
        Assert.Equal(1, setting.Writes);
    }

    [Fact]
    public void Read_failure_is_never_absence_and_readback_mismatch_never_reports_success()
    {
        var setting = new FakeSetting("old") { BeforeRead = () => throw new IOException("read failed") };
        Assert.Throws<IOException>(() => Open().Apply(setting, null));
        Assert.Equal(0, setting.Writes);
        setting.BeforeRead = null;
        setting.IgnoreWrite = true;
        Assert.Equal(SettingChangeResult.Conflict, Open().Apply(setting, Bytes("new")));
        Assert.Equal(SettingChangeResult.Conflict, Open().Restore(setting));
    }

    [Fact]
    public void Change_between_intent_and_write_is_preserved()
    {
        var setting = new FakeSetting("old");
        var reads = 0;
        setting.BeforeRead = () => { if (++reads == 2) setting.Value = Bytes("user"); };
        Assert.Equal(SettingChangeResult.Conflict, Open().Apply(setting, Bytes("new")));
        Assert.Equal(0, setting.Writes);
        Assert.Equal(Bytes("user"), setting.Value);
    }

    [Fact]
    public void Missing_corrupt_future_or_other_installation_history_refuses_all_setting_access()
    {
        var setting = new FakeSetting("old") { BeforeRead = () => throw new Exception("must not query native state") };
        Assert.Throws<InvalidDataException>(() => Open(Guid.NewGuid().ToString("D")).Restore(setting));
        Assert.Throws<IOException>(() => Open().InitializeNew());
        File.WriteAllText(Open().FilePath, "{\"schema_version\":999,\"payload\":\"AA==\"}");
        Assert.Throws<SchemaVersionException>(() => Open().Apply(setting, null));
        JsonStore.Save(Open().FilePath, new SetupSettingsDocument { Payload = [1, 2, 3] }, SetupSettingsDocument.Migrations);
        Assert.Throws<InvalidDataException>(() => Open().Restore(setting));
        File.Delete(Open().FilePath);
        Assert.Throws<FileNotFoundException>(() => Open().Apply(setting, null));
        Assert.Throws<FileNotFoundException>(() => Open().Restore(setting));
        Assert.Equal(0, setting.Writes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema_version\":999,\"installation_id\":\"ID\",\"entries\":[]}")]
    [InlineData("{\"schema_version\":1,\"installation_id\":\"ID\",\"entries\":null}")]
    [InlineData("{\"schema_version\":1,\"installation_id\":\"ID\",\"entries\":[{\"id\":\"test.setting\",\"original\":null,\"applied\":\"AQ==\"}]}")]
    public void Invalid_inner_document_is_refused_without_exposing_plaintext(string json)
    {
        JsonStore.Save(Open().FilePath, new SetupSettingsDocument { Payload = Encrypt(Bytes(json.Replace("ID", _installationId))!) }, SetupSettingsDocument.Migrations);
        var error = Assert.Throws<InvalidDataException>(() => Open().Restore(new FakeSetting("old")));
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Interrupted_atomic_owned_creation_requires_proof_for_repair_and_removal(bool proof, bool removal)
    {
        var setting = new OwnedCreation { Proof = proof };
        Assert.Throws<IOException>(() => Open().Apply(setting, Bytes("owned")));
        Assert.Equal(proof ? SetupStepStatus.Needed : SetupStepStatus.Conflict,
            Open().Check(setting, _ => Bytes("owned")).Status);
        Assert.Equal(proof ? (removal ? SettingChangeResult.Restored : SettingChangeResult.AlreadyApplied) : SettingChangeResult.Conflict,
            removal ? Open().Restore(setting) : Open().Apply(setting, Bytes("owned")));
        Assert.Equal(proof && removal ? null : Bytes("owned"), setting.Value);
        Assert.Equal(proof && removal ? 2 : 1, setting.Writes);
    }

    private sealed class OwnedCreation : ISetupSetting, ISetupOwnedCreation
    {
        public string Id => "test.owned-resource";
        public bool Proof { get; set; }
        public byte[]? Value { get; private set; }
        public int Writes { get; private set; }
        public byte[]? Read() => Value?.ToArray();
        public bool ConfirmsOwnedCreation(byte[] expected) => Proof && expected.AsSpan().SequenceEqual(Value);
        public void Write(byte[]? value)
        {
            Value = value?.ToArray();
            if (++Writes == 1) throw new IOException("Crash after atomic native creation.");
        }
    }

    private sealed class PreservableSetting : ISetupSetting, ISetupPreservableExistingValue
    {
        public string Id => "test.preservable-resource";
        public byte[]? Value { get; private set; }
        public int Writes { get; private set; }
        public byte[]? Read() => Value?.ToArray();
        public void Write(byte[]? value)
        {
            Value = value?.ToArray();
            if (++Writes == 1) throw new IOException("Crash after the additive value appeared.");
        }
    }

    private sealed class ResolvedSetting : ISetupSetting, ISetupResolvedAppliedValue
    {
        public string Id => "test.resolved-resource";
        public byte[]? Value { get; set; } = Bytes("original");
        public bool FailAfterWrite { get; set; }
        public byte[]? Read() => Value?.ToArray();
        public void Write(byte[]? value)
        {
            Value = Bytes("resolved");
            if (FailAfterWrite) throw new IOException();
        }
        public bool AcceptsResolvedAppliedValue(byte[]? requested, byte[]? actual) =>
            requested.AsSpan().SequenceEqual(Bytes("requested")) && actual.AsSpan().SequenceEqual(Bytes("resolved"));
    }

    private static byte[]? Bytes(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value);

    private byte[] Encrypt(byte[] plaintext)
    {
        var bytes = new byte[28 + plaintext.Length];
        RandomNumberGenerator.Fill(bytes.AsSpan(0, 12));
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(bytes.AsSpan(0, 12), plaintext, bytes.AsSpan(28), bytes.AsSpan(12, 16));
        return bytes;
    }

    private byte[] Decrypt(byte[] bytes)
    {
        if (bytes.Length < 28) throw new CryptographicException();
        var plaintext = new byte[bytes.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext);
        return plaintext;
    }

    private sealed class FakeSetting(string? initial) : ISetupSetting
    {
        public string Id => "test.setting";
        public byte[]? Value { get; set; } = Bytes(initial);
        public int Writes { get; private set; }
        public Action? BeforeWrite { get; set; }
        public Action? BeforeRead { get; set; }
        public bool IgnoreWrite { get; set; }
        public byte[]? Read() { BeforeRead?.Invoke(); return Value?.ToArray(); }
        public void Write(byte[]? value)
        {
            Writes++;
            BeforeWrite?.Invoke();
            if (!IgnoreWrite) Value = value?.ToArray();
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        Directory.Delete(_directory, true);
    }
}
