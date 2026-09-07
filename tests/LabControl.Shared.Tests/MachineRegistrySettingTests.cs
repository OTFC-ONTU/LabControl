using System.Security.Cryptography;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class MachineRegistrySettingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-registry-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly SetupSettingsJournal _journal;

    public MachineRegistrySettingTests()
    {
        Directory.CreateDirectory(_directory);
        _journal = new(_directory, Guid.NewGuid().ToString("D"), Protect, Unprotect);
        _journal.InitializeNew();
    }

    [Theory]
    [InlineData(MachineRegistryPolicy.FastStartup, null, 0u)]
    [InlineData(MachineRegistryPolicy.FastStartup, 1u, 0u)]
    [InlineData(MachineRegistryPolicy.FastStartup, uint.MaxValue, 0u)]
    [InlineData(MachineRegistryPolicy.SoftwareSas, null, 1u)]
    [InlineData(MachineRegistryPolicy.SoftwareSas, 2u, 1u)]
    public void Apply_repair_and_restore_keep_original_bits_and_absence(MachineRegistryPolicy policy, uint? original, uint desired)
    {
        var store = new Store { Value = original };
        var setting = new MachineRegistrySetting(policy, store);
        Assert.Equal(SettingChangeResult.Applied, setting.Apply(_journal));
        Assert.Equal(desired, store.Value);
        Assert.Equal(SettingChangeResult.AlreadyApplied, setting.Apply(_journal));
        Assert.Equal(1, store.Writes);
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(setting));
        Assert.Equal(original, store.Value);
    }

    [Theory]
    [InlineData(MachineRegistryPolicy.FastStartup, 0u)]
    [InlineData(MachineRegistryPolicy.SoftwareSas, 1u)]
    [InlineData(MachineRegistryPolicy.SoftwareSas, 3u)]
    public void Existing_acceptable_policy_is_never_owned(MachineRegistryPolicy policy, uint original)
    {
        var store = new Store { Value = original };
        var setting = new MachineRegistrySetting(policy, store);
        Assert.Equal(SettingChangeResult.Unchanged, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(setting));
        Assert.Equal(original, store.Value);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Later_sas_broadening_is_a_conflict_not_new_ownership()
    {
        var store = new Store();
        var setting = new MachineRegistrySetting(MachineRegistryPolicy.SoftwareSas, store);
        setting.Apply(_journal);
        store.Value = 3;
        Assert.Equal(SettingChangeResult.Conflict, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
        Assert.Equal(3u, store.Value);
    }

    [Fact]
    public void Native_guard_preserves_change_after_the_journals_last_read()
    {
        var store = new Store { Value = 1, BeforeWrite = s => s.Value = 7 };
        var setting = new MachineRegistrySetting(MachineRegistryPolicy.FastStartup, store);
        Assert.Throws<IOException>(() => setting.Apply(_journal));
        Assert.Equal(7u, store.Value);
        Assert.Equal(0, store.Writes);
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
    }

    [Fact]
    public void Missing_history_prevents_even_policy_selection_reads()
    {
        File.Delete(_journal.FilePath);
        var store = new Store { FailRead = true };
        var setting = new MachineRegistrySetting(MachineRegistryPolicy.SoftwareSas, store);
        Assert.Throws<FileNotFoundException>(() => setting.Apply(_journal));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Failed_read_invalidates_previous_write_guard()
    {
        var store = new Store { Value = 1 };
        var setting = new MachineRegistrySetting(MachineRegistryPolicy.FastStartup, store);
        setting.Read();
        store.FailRead = true;
        Assert.Throws<IOException>(() => setting.Read());
        Assert.Throws<InvalidOperationException>(() => setting.Write(MachineRegistrySetting.Encode(0)));
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 4, 0 })]
    [InlineData(new byte[] { 1, 0, 0, 0, 0 })]
    public void Invalid_saved_native_type_or_length_never_reaches_store(byte[] value)
    {
        var store = new Store();
        var setting = new MachineRegistrySetting(MachineRegistryPolicy.FastStartup, store);
        setting.Read();
        Assert.Throws<InvalidDataException>(() => setting.Write(value));
        Assert.Equal(0, store.Writes);
    }

    private sealed class Store : IMachineRegistryStore
    {
        public uint? Value { get; set; }
        public int Writes { get; private set; }
        public bool FailRead { get; set; }
        public Action<Store>? BeforeWrite { get; init; }
        public uint? Read(MachineRegistryPolicy policy) => FailRead ? throw new IOException("read refused") : Value;
        public void Write(MachineRegistryPolicy policy, uint? expected, uint? value)
        {
            BeforeWrite?.Invoke(this);
            if (Value != expected) throw new IOException("concurrent change");
            Value = value;
            Writes++;
        }
    }

    private byte[] Protect(byte[] plain)
    {
        var bytes = new byte[28 + plain.Length];
        RandomNumberGenerator.Fill(bytes.AsSpan(0, 12));
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(bytes.AsSpan(0, 12), plain, bytes.AsSpan(28), bytes.AsSpan(12, 16));
        return bytes;
    }

    private byte[] Unprotect(byte[] bytes)
    {
        var plain = new byte[bytes.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
        return plain;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        Directory.Delete(_directory, recursive: true);
    }
}
