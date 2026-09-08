using System.Security.Cryptography;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class UpdateActiveHoursSettingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-active-hours-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly SetupSettingsJournal _journal;

    public UpdateActiveHoursSettingTests()
    {
        Directory.CreateDirectory(_directory);
        _journal = new(_directory, Guid.NewGuid().ToString("D"), Protect, Unprotect);
        _journal.InitializeNew();
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(0u, 8u, 17u)]
    [InlineData(1u, 0u, 23u)]
    [InlineData(uint.MaxValue, uint.MaxValue, null)]
    public void Apply_repair_restore_preserve_original_tuple(uint? enabled, uint? start, uint? end)
    {
        var original = new UpdateActiveHours(enabled, start, end);
        var store = new Store { Value = original };
        Assert.Equal(SettingChangeResult.Applied, new UpdateActiveHoursSetting(store).Apply(_journal));
        Assert.Equal(new UpdateActiveHours(1, 7, 20), store.Value);
        Assert.Equal(SettingChangeResult.AlreadyApplied, new UpdateActiveHoursSetting(store).Apply(_journal));
        Assert.Equal(1, store.Writes);
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(new UpdateActiveHoursSetting(store)));
        Assert.Equal(original, store.Value);
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(new UpdateActiveHoursSetting(store)));
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public void Existing_correct_tuple_is_not_owned()
    {
        var store = new Store { Value = new(1, 7, 20) };
        var setting = new UpdateActiveHoursSetting(store);
        Assert.Equal(SettingChangeResult.Unchanged, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(setting));
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(0u, 7u, 20u)]
    [InlineData(1u, 8u, 20u)]
    [InlineData(1u, 7u, null)]
    public void Any_later_member_edit_preserves_entire_tuple(uint? enabled, uint? start, uint? end)
    {
        var store = new Store();
        var setting = new UpdateActiveHoursSetting(store);
        setting.Apply(_journal);
        var edited = store.Value = new(enabled, start, end);
        Assert.Equal(SettingChangeResult.Conflict, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
        Assert.Equal(edited, store.Value);
        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_apply_is_never_adopted(bool fullWrite)
    {
        var store = new Store { FailWrite = true, FullWriteBeforeFailure = fullWrite };
        var setting = new UpdateActiveHoursSetting(store);
        Assert.Throws<IOException>(() => setting.Apply(_journal));
        var partial = store.Value;
        store.FailWrite = false;
        Assert.Equal(SettingChangeResult.Conflict, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
        Assert.Equal(partial, store.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_restore_accepts_only_complete_original(bool fullWrite)
    {
        var original = new UpdateActiveHours(0, 8, 17);
        var store = new Store { Value = original };
        var setting = new UpdateActiveHoursSetting(store);
        setting.Apply(_journal);
        store.FailWrite = true;
        store.FullWriteBeforeFailure = fullWrite;
        Assert.Throws<IOException>(() => _journal.Restore(setting));
        var partial = store.Value;
        store.FailWrite = false;
        Assert.Equal(fullWrite ? SettingChangeResult.Restored : SettingChangeResult.Conflict, _journal.Restore(setting));
        Assert.Equal(partial, store.Value);
    }

    [Fact]
    public void Missing_history_prevents_native_reads()
    {
        File.Delete(_journal.FilePath);
        var store = new Store();
        Assert.Throws<FileNotFoundException>(() => new UpdateActiveHoursSetting(store).Apply(_journal));
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public void Failed_read_invalidates_previous_guard()
    {
        var store = new Store();
        var setting = new UpdateActiveHoursSetting(store);
        setting.Read();
        store.FailRead = true;
        Assert.Throws<IOException>(() => setting.Read());
        Assert.Throws<InvalidOperationException>(() => setting.Write(UpdateActiveHoursSetting.Encode(new(1, 7, 20))));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Native_guard_refuses_concurrent_changes()
    {
        var store = new Store { ConcurrentEdit = true };
        var setting = new UpdateActiveHoursSetting(store);
        Assert.Throws<IOException>(() => setting.Apply(_journal));
        Assert.Equal(new UpdateActiveHours(1, 9, 21), store.Value);
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Invalid_snapshots_do_not_reach_native_write(int variant)
    {
        byte[]? bytes = UpdateActiveHoursSetting.Encode(new(null, null, null));
        switch (variant)
        {
            case 0: bytes = null; break;
            case 1: bytes = []; break;
            case 2: bytes[0] = 2; break;
            case 3: bytes[6] = 1; break;
            case 4: bytes[12] = 1; break; // Noncanonical absent slot.
        }
        var store = new Store();
        var setting = new UpdateActiveHoursSetting(store);
        setting.Read();
        Assert.Throws<InvalidDataException>(() => setting.Write(bytes));
        Assert.Equal(0, store.Writes);
    }

    private sealed class Store : IUpdateActiveHoursStore
    {
        public UpdateActiveHours Value { get; set; } = new(null, null, null);
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }
        public bool FullWriteBeforeFailure { get; set; }
        public bool ConcurrentEdit { get; init; }
        public UpdateActiveHours Read()
        {
            Reads++;
            return FailRead ? throw new IOException() : Value;
        }
        public void Write(UpdateActiveHours expected, UpdateActiveHours value)
        {
            if (ConcurrentEdit) Value = new(1, 9, 21);
            if (Value != expected) throw new IOException();
            Writes++;
            Value = FailWrite && !FullWriteBeforeFailure ? Value with { Start = value.Start } : value;
            if (FailWrite) throw new IOException();
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
