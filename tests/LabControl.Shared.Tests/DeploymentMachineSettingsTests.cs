using System.Security.Cryptography;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class DeploymentMachineSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-machine-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly SetupSettingsJournal _journal;

    public DeploymentMachineSettingsTests()
    {
        Directory.CreateDirectory(_directory);
        _journal = new(_directory, Guid.NewGuid().ToString("D"), Protect, Unprotect);
        _journal.InitializeNew();
    }

    [Fact]
    public void Defender_add_repair_remove_owns_only_the_fixed_exclusion()
    {
        var store = new Defender();
        var setting = new DefenderExclusionSetting(store);
        Assert.Equal(SettingChangeResult.Applied, setting.Apply(_journal));
        Assert.Equal(Defaults.AgentInstallDirectory, store.Value);
        Assert.Equal(SettingChangeResult.AlreadyApplied, setting.Apply(_journal));
        Assert.Equal(1, store.Writes);
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(setting));
        Assert.Null(store.Value);
    }

    [Fact]
    public void Existing_equivalent_exclusion_is_preserved_without_ownership()
    {
        var existing = Defaults.AgentInstallDirectory.ToLowerInvariant() + "\\";
        var store = new Defender { Value = existing };
        var setting = new DefenderExclusionSetting(store);
        Assert.Equal(SettingChangeResult.Unchanged, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(setting));
        Assert.Equal(existing, store.Value);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Interrupted_defender_add_is_preserved_without_ownership_on_repair()
    {
        var store = new Defender { FailAfterWrite = true };
        var setting = new DefenderExclusionSetting(store);
        Assert.Throws<IOException>(() => setting.Apply(_journal));

        store.FailAfterWrite = false;
        Assert.Equal(SetupStepStatus.Needed, setting.Check(_journal).Status);
        Assert.Equal(SettingChangeResult.Unchanged, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(setting));
        Assert.Equal(Defaults.AgentInstallDirectory, store.Value);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void Later_exclusion_representation_edit_and_removal_are_conflicts()
    {
        var store = new Defender();
        var setting = new DefenderExclusionSetting(store);
        setting.Apply(_journal);
        store.Value = Defaults.AgentInstallDirectory + "\\";
        Assert.Equal(SettingChangeResult.Conflict, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
        store.Value = null;
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(setting));
    }

    [Fact]
    public void Defender_does_not_accept_an_unrelated_path_or_unknown_native_result()
    {
        Assert.False(DefenderExclusionSetting.IsInstallDirectory(Defaults.AgentInstallDirectory + "2"));
        Assert.False(DefenderExclusionSetting.IsInstallDirectory(Defaults.AgentInstallDirectory + "\\*"));
        Assert.Throws<InvalidDataException>(() => new DefenderExclusionSetting(new Defender { Value = @"C:\" }).Apply(_journal));
        Assert.Throws<IOException>(() => new DefenderExclusionSetting(new Defender { FailRead = true }).Apply(_journal));
    }

    [Theory]
    [InlineData(1, 0u)]
    [InlineData(2, 75u)]
    [InlineData(2, null)]
    public void Hibernation_restores_full_or_reduced_file_and_exact_size_metadata(byte type, uint? size)
    {
        var original = new HibernationState(true, type, 1, type, size);
        var store = new Hibernation { Value = original };
        var setting = new HibernationSetting(store);
        Assert.Equal(SettingChangeResult.Applied, setting.Apply(_journal));
        Assert.Equal(original with { FilePresent = false, NativeFileType = 0, Enabled = 0 }, store.Value);
        Assert.Equal(SettingChangeResult.AlreadyApplied, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(setting));
        Assert.Equal(original, store.Value);
    }

    [Fact]
    public void Hibernation_enabled_by_os_default_without_registry_value_is_disabled_and_restored()
    {
        // Factory-fresh classroom PC (D-65): active file, HibernateEnabled absent,
        // HiberFileType absent, HiberFileSizePercent=0.
        var original = new HibernationState(true, 2, null, null, 0);
        var store = new Hibernation { Value = original, NormalizeDisabledState = true };
        var setting = new HibernationSetting(store);
        Assert.Equal(SettingChangeResult.Applied, setting.Apply(_journal));
        Assert.Equal(new(false, 0, null, null, null), store.Value);
        Assert.Equal(SettingChangeResult.AlreadyApplied, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(setting));
        Assert.Equal(original, store.Value);
    }

    [Fact]
    public void Hibernation_explicitly_disabled_value_with_active_file_is_refused()
    {
        var store = new Hibernation { Value = new(true, 2, 0, 2, 50) };
        Assert.Throws<InvalidDataException>(() => new HibernationSetting(store).Apply(_journal));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Hibernation_off_before_install_is_never_owned()
    {
        var store = new Hibernation { Value = new(false, 0, 0, null, null) };
        var setting = new HibernationSetting(store);
        Assert.Equal(SettingChangeResult.Unchanged, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(setting));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Disabled_hibernation_ignores_undefined_file_types_when_no_file_exists()
    {
        Assert.True(HibernationSetting.IsDisabled(new(false, byte.MaxValue, null, uint.MaxValue, 0)));
        Assert.False(HibernationSetting.IsDisabled(new(true, byte.MaxValue, null, uint.MaxValue, 0)));
        Assert.False(HibernationSetting.IsDisabled(new(false, byte.MaxValue, 1, uint.MaxValue, 0)));
    }

    [Fact]
    public void Later_size_change_is_preserved_even_when_hibernation_remains_off()
    {
        var store = new Hibernation { Value = new(true, 2, 1, 2, 50) };
        var setting = new HibernationSetting(store);
        setting.Apply(_journal);
        store.Value = store.Value with { SizePercent = 75 };
        Assert.Equal(SettingChangeResult.Conflict, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
        Assert.Equal(75u, store.Value.SizePercent);
    }

    [Fact]
    public void Windows_normalized_hibernation_off_state_is_journaled_and_restored()
    {
        var original = new HibernationState(true, 2, 1, 2, 50);
        var store = new Hibernation { Value = original, NormalizeDisabledState = true };
        var setting = new HibernationSetting(store);
        Assert.Equal(SettingChangeResult.Applied, setting.Apply(_journal));
        Assert.Equal(new(false, 0, null, null, null), store.Value);
        Assert.Equal(SettingChangeResult.AlreadyApplied, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(setting));
        Assert.Equal(original, store.Value);
    }

    [Fact]
    public void Interrupted_normalized_hibernation_off_is_preserved_without_ownership()
    {
        var store = new Hibernation
        {
            Value = new(true, 2, 1, 2, 50),
            NormalizeDisabledState = true,
            FailAfterWrite = true,
        };
        var setting = new HibernationSetting(store);
        Assert.Throws<IOException>(() => setting.Apply(_journal));

        store.FailAfterWrite = false;
        Assert.Equal(SetupStepStatus.Needed, setting.Check(_journal).Status);
        Assert.Equal(SettingChangeResult.Unchanged, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(setting));
        Assert.Equal(new(false, 0, null, null, null), store.Value);
    }

    [Fact]
    public void Native_race_does_not_overwrite_a_new_exclusion()
    {
        var store = new Defender { ChangeBeforeWrite = true };
        Assert.Throws<IOException>(() => new DefenderExclusionSetting(store).Apply(_journal));
        Assert.Equal(0, store.Writes);
    }

    private sealed class Defender : IDefenderExclusionStore
    {
        public string? Value;
        public int Writes;
        public bool FailRead, ChangeBeforeWrite, FailAfterWrite;
        public string? Read() => FailRead ? throw new IOException() : Value;
        public void Write(string? expected, string? value)
        {
            if (ChangeBeforeWrite) Value = Defaults.AgentInstallDirectory + "\\";
            if (Value != expected) throw new IOException();
            Writes++;
            Value = value;
            if (FailAfterWrite) throw new IOException();
        }
    }

    private sealed class Hibernation : IHibernationSystem
    {
        public required HibernationState Value;
        public bool NormalizeDisabledState, FailAfterWrite;
        public int Writes;
        public HibernationState Read() => Value;
        public void Write(HibernationState expected, HibernationState value)
        {
            if (Value != expected) throw new IOException();
            Writes++;
            Value = value;
            if (NormalizeDisabledState && !value.FilePresent)
                Value = new(false, 0, null, null, null);
            if (FailAfterWrite) throw new IOException();
        }
    }

    private byte[] Protect(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. nonce, .. tag, .. ciphertext];
    }
    private byte[] Unprotect(byte[] bytes)
    {
        var plaintext = new byte[bytes.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext);
        return plaintext;
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
