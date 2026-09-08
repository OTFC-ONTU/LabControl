using System.Security.Cryptography;
using System.Text;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class StudentSignInSecretTests : IDisposable
{
    private const string Sid = "S-1-5-21-11-22-33-1001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-signin-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly SetupSettingsJournal _journal;
    private readonly InstallationState _state;
    private readonly Store _store = new();
    private string? _sid = Sid;
    private int _lookups;
    private int _journals;

    public StudentSignInSecretTests()
    {
        Directory.CreateDirectory(_directory);
        _state = new(_directory);
        _state.Configure(false);
        _state.BeginStudentCreation(null);
        _state.CompleteStudentCreation(Sid);
        _journal = new(_directory, _state.Read()!.InstallationId, Protect, Unprotect);
        _journal.InitializeNew();
    }

    private StudentSignInSecret Component() => new(_state,
        () => { _lookups++; return _sid; },
        () => { _journals++; return _journal; }, _store);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("synthetic-prior-value")]
    [InlineData("synthetic-ї\0-tail")]
    public void Apply_repair_restore_round_trip_without_plaintext_on_disk(string? original)
    {
        var prior = original is null ? null : Encoding.Unicode.GetBytes(original);
        _store.Value = prior;
        Assert.Equal(SettingChangeResult.Applied, Component().Apply());
        Assert.Equal(Encoding.Unicode.GetBytes(LabControl.Shared.Defaults.StudentDefaultPassword), _store.Value);
        Assert.Equal(SettingChangeResult.AlreadyApplied, Component().Apply());
        Assert.Equal(1, _store.Writes);
        if (prior is { Length: > 0 })
            Assert.DoesNotContain(Convert.ToBase64String(prior), File.ReadAllText(_journal.FilePath));
        Assert.Equal(SettingChangeResult.Restored, Component().Restore());
        Assert.Equal(prior, _store.Value);
        Assert.Equal(SettingChangeResult.Restored, Component().Restore());
        Assert.Equal(2, _store.Writes);
    }

    [Fact]
    public void Off_skips_all_account_secret_and_settings_history_access()
    {
        _state.Configure(true, false);
        File.Delete(_journal.FilePath);
        Assert.Null(Component().Apply());
        Assert.Null(Component().Restore());
        Assert.Equal(0, _lookups);
        Assert.Equal(0, _journals);
        Assert.Equal(0, _store.Reads);
        Assert.Equal(0, _store.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("S-1-5-21-11-22-33-1002")]
    public void Missing_or_replaced_account_prevents_secret_access(string? current)
    {
        _sid = current;
        Assert.Throws<InvalidOperationException>(() => Component().Apply());
        Assert.Throws<InvalidOperationException>(() => Component().Restore());
        Assert.Equal(0, _store.Reads);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void Missing_ownership_history_never_guesses_account_mode()
    {
        File.Delete(_state.FilePath);
        Assert.Throws<InvalidOperationException>(() => Component().Apply());
        Assert.Equal(0, _lookups);
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public void Missing_settings_history_prevents_lsa_access()
    {
        File.Delete(_journal.FilePath);
        Assert.Throws<FileNotFoundException>(() => Component().Apply());
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public void Account_replaced_during_read_prevents_mutation()
    {
        _store.AfterRead = () => _sid = null;
        Assert.Throws<InvalidOperationException>(() => Component().Apply());
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void Correct_existing_secret_is_not_owned()
    {
        _store.Value = Encoding.Unicode.GetBytes(LabControl.Shared.Defaults.StudentDefaultPassword);
        Assert.Equal(SettingChangeResult.Unchanged, Component().Apply());
        Assert.Equal(SettingChangeResult.Untracked, Component().Restore());
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void Later_secret_edit_is_preserved()
    {
        Component().Apply();
        var changed = Encoding.Unicode.GetBytes("synthetic-later-value");
        _store.Value = changed;
        Assert.Equal(SettingChangeResult.Conflict, Component().Apply());
        Assert.Equal(SettingChangeResult.Conflict, Component().Restore());
        Assert.Equal(changed, _store.Value);
    }

    [Fact]
    public void Failed_native_read_does_not_mean_absence()
    {
        _store.FailRead = true;
        Assert.Throws<IOException>(() => Component().Apply());
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void Malformed_native_secret_is_refused_and_buffer_cleared()
    {
        _store.Value = [7];
        Assert.Throws<InvalidDataException>(() => Component().Apply());
        Assert.Equal(0, _store.Writes);
        Assert.All(_store.ReturnedBuffers.SelectMany(b => b), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Native_write_guard_preserves_concurrent_edit()
    {
        _store.BeforeWrite = () => _store.Value = Encoding.Unicode.GetBytes("synthetic-race");
        Assert.Throws<IOException>(() => Component().Apply());
        Assert.Equal(0, _store.Writes);
        Assert.Equal(SettingChangeResult.Conflict, Component().Restore());
    }

    [Fact]
    public void Failed_apply_after_write_remains_ambiguous()
    {
        _store.FailAfterWrite = true;
        Assert.Throws<IOException>(() => Component().Apply());
        _store.FailAfterWrite = false;
        Assert.Equal(SettingChangeResult.Conflict, Component().Apply());
        Assert.Equal(SettingChangeResult.Conflict, Component().Restore());
    }

    [Fact]
    public void Interrupted_restore_can_finish_from_original_value()
    {
        _store.Value = Encoding.Unicode.GetBytes("synthetic-original");
        Component().Apply();
        _store.FailAfterWrite = true;
        Assert.Throws<IOException>(() => Component().Restore());
        _store.FailAfterWrite = false;
        Assert.Equal(SettingChangeResult.Restored, Component().Restore());
        Assert.Equal(2, _store.Writes);
    }

    [Fact]
    public void Adapter_owned_read_and_write_buffers_are_cleared()
    {
        _store.Value = Encoding.Unicode.GetBytes("synthetic-original");
        Component().Apply();
        Assert.All(_store.ReturnedBuffers.Concat(_store.WriteBuffers).SelectMany(b => b), b => Assert.Equal(0, b));
    }

    private sealed class Store : IStudentSignInSecretStore
    {
        public byte[]? Value { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool FailRead { get; set; }
        public bool FailAfterWrite { get; set; }
        public Action? BeforeWrite { get; set; }
        public Action? AfterRead { get; set; }
        public List<byte[]> ReturnedBuffers { get; } = [];
        public List<byte[]> WriteBuffers { get; } = [];
        public byte[]? Read()
        {
            Reads++;
            if (FailRead) throw new IOException();
            var copy = Value?.ToArray();
            if (copy is not null) ReturnedBuffers.Add(copy);
            AfterRead?.Invoke();
            return copy;
        }
        public void Write(byte[]? expected, byte[]? value)
        {
            BeforeWrite?.Invoke();
            if (!(Value is null ? expected is null : expected is not null && Value.AsSpan().SequenceEqual(expected)))
                throw new IOException();
            if (expected is not null) WriteBuffers.Add(expected);
            if (value is not null) WriteBuffers.Add(value);
            Value = value?.ToArray();
            Writes++;
            if (FailAfterWrite) throw new IOException();
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
