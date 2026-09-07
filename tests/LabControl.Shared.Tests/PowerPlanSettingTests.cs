using System.Security.Cryptography;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class PowerPlanSettingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-power-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly SetupSettingsJournal _journal;
    private readonly SystemStub _system = new();

    public PowerPlanSettingTests()
    {
        Directory.CreateDirectory(_directory);
        _journal = new(_directory, Guid.NewGuid().ToString("D"), Protect, Unprotect);
        _journal.InitializeNew();
    }

    private PowerPlanSetting Setting(PowerPlanPolicy policy = PowerPlanPolicy.Sleep) => new(policy, _system);

    [Theory]
    [InlineData(PowerPlanPolicy.Sleep, 1800u, 0u)]
    [InlineData(PowerPlanPolicy.Display, 600u, 1200u)]
    [InlineData(PowerPlanPolicy.Disk, uint.MaxValue, 0u)]
    public void Apply_repair_restore_keep_scheme_original_seconds_and_other_settings(PowerPlanPolicy policy, uint original, uint desired)
    {
        var scheme = _system.Active;
        _system.Values[policy] = original;
        var others = _system.Values.Where(p => p.Key != policy).ToArray();
        Assert.Equal(SettingChangeResult.Applied, Setting(policy).Apply(_journal));
        Assert.Equal(desired, _system.Values[policy]);
        Assert.Equal(desired, _system.Effective[policy]);
        Assert.Equal(SettingChangeResult.AlreadyApplied, Setting(policy).Apply(_journal));
        Assert.Equal(1, _system.Writes);
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(Setting(policy)));
        Assert.Equal(original, _system.Values[policy]);
        Assert.Equal(original, _system.Effective[policy]);
        Assert.Equal(scheme, _system.Active);
        Assert.All(others, p => Assert.Equal(p.Value, _system.Values[p.Key]));
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(Setting(policy)));
        Assert.Equal(2, _system.Writes);
    }

    [Fact]
    public void Existing_correct_setting_is_unowned_and_does_not_activate()
    {
        _system.Values[PowerPlanPolicy.Sleep] = 0;
        Assert.Equal(SettingChangeResult.Unchanged, Setting().Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(Setting()));
        Assert.Equal(0, _system.Writes);
        Assert.Equal(0, _system.Activations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Later_scheme_or_value_change_is_preserved(bool changeScheme)
    {
        Setting().Apply(_journal);
        if (changeScheme) _system.Active = Guid.NewGuid();
        else _system.Values[PowerPlanPolicy.Sleep] = 42;
        var active = _system.Active;
        Assert.Equal(SettingChangeResult.Conflict, Setting().Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(Setting()));
        Assert.Equal(active, _system.Active);
        Assert.Equal(1, _system.Writes);
        Assert.Equal(1, _system.Activations);
    }

    [Fact]
    public void Scheme_change_during_read_is_not_a_valid_snapshot()
    {
        _system.AfterRead = () => _system.Active = Guid.NewGuid();
        Assert.Throws<IOException>(() => Setting().Apply(_journal));
        Assert.Equal(0, _system.Writes);
        Assert.Equal(0, _system.Activations);
    }

    [Fact]
    public void Write_rechecks_identity_after_the_last_journal_read()
    {
        var setting = Setting();
        var snapshot = setting.Read();
        _system.Active = Guid.NewGuid();
        Assert.Throws<IOException>(() => setting.Write(snapshot));
        Assert.Equal(0, _system.Writes);
    }

    [Fact]
    public void Failed_read_discards_previous_write_guard()
    {
        var setting = Setting();
        var snapshot = setting.Read();
        _system.FailRead = true;
        Assert.Throws<IOException>(() => setting.Read());
        _system.FailRead = false;
        Assert.Throws<InvalidOperationException>(() => setting.Write(snapshot));
        Assert.Equal(0, _system.Writes);
    }

    [Fact]
    public void Missing_journal_prevents_native_reads()
    {
        File.Delete(_journal.FilePath);
        Assert.Throws<FileNotFoundException>(() => Setting().Apply(_journal));
        Assert.Equal(0, _system.Reads);
    }

    [Fact]
    public void Failed_apply_activation_is_ambiguous_and_never_adopted()
    {
        _system.FailActivation = true;
        Assert.Throws<IOException>(() => Setting().Apply(_journal));
        Assert.Equal(0u, _system.Values[PowerPlanPolicy.Sleep]);
        Assert.Equal(1800u, _system.Effective[PowerPlanPolicy.Sleep]);
        _system.FailActivation = false;
        Assert.Equal(SettingChangeResult.Conflict, Setting().Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(Setting()));
    }

    [Fact]
    public void Failed_restore_activation_is_retried_even_when_original_is_already_persisted()
    {
        Setting().Apply(_journal);
        _system.FailActivation = true;
        Assert.Throws<IOException>(() => _journal.Restore(Setting()));
        Assert.Equal(1800u, _system.Values[PowerPlanPolicy.Sleep]);
        Assert.Equal(0u, _system.Effective[PowerPlanPolicy.Sleep]);
        _system.FailActivation = false;
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(Setting()));
        Assert.Equal(1800u, _system.Effective[PowerPlanPolicy.Sleep]);
        Assert.Equal(2, _system.Writes);
    }

    [Fact]
    public void Repair_reactivates_a_confirmed_persisted_value_without_rewriting_it()
    {
        Setting().Apply(_journal);
        _system.Effective[PowerPlanPolicy.Sleep] = 1800;
        Assert.Equal(SettingChangeResult.AlreadyApplied, Setting().Apply(_journal));
        Assert.Equal(0u, _system.Effective[PowerPlanPolicy.Sleep]);
        Assert.Equal(1, _system.Writes);
    }

    [Fact]
    public void Repeated_restore_does_not_activate_a_later_user_choice()
    {
        Setting().Apply(_journal);
        _journal.Restore(Setting());
        _system.Active = Guid.NewGuid();
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(Setting()));
        Assert.Equal(2, _system.Activations);
    }

    [Fact]
    public void Scheme_change_after_write_prevents_activation_and_confirmation()
    {
        _system.AfterWrite = () => _system.Active = Guid.NewGuid();
        Assert.Throws<IOException>(() => Setting().Apply(_journal));
        Assert.Equal(0, _system.Activations);
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(Setting()));
    }

    [Fact]
    public void Activation_rechecks_value_before_calling_native_code()
    {
        var setting = Setting();
        var expected = setting.Read();
        _system.Values[PowerPlanPolicy.Sleep]++;
        Assert.Throws<IOException>(() => setting.Activate(expected));
        Assert.Equal(0, _system.Activations);
    }

    [Fact]
    public void Failed_write_can_retry_while_original_still_matches()
    {
        _system.FailWrite = true;
        Assert.Throws<IOException>(() => Setting().Apply(_journal));
        _system.FailWrite = false;
        Assert.Equal(SettingChangeResult.Applied, Setting().Apply(_journal));
        Assert.Equal(1, _system.Writes);
    }

    [Fact]
    public void Failed_apply_completion_save_does_not_claim_an_activated_value()
    {
        _system.AfterActivation = () => Directory.CreateDirectory(_journal.FilePath + ".tmp");
        Assert.True(Record.Exception(() => Setting().Apply(_journal)) is IOException or UnauthorizedAccessException);
        Directory.Delete(_journal.FilePath + ".tmp");
        _system.AfterActivation = null;
        Assert.Equal(0u, _system.Effective[PowerPlanPolicy.Sleep]);
        Assert.Equal(SettingChangeResult.Conflict, Setting().Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(Setting()));
    }

    [Fact]
    public void Failed_restore_completion_save_retries_activation_without_another_write()
    {
        Setting().Apply(_journal);
        _system.AfterActivation = () => Directory.CreateDirectory(_journal.FilePath + ".tmp");
        Assert.True(Record.Exception(() => _journal.Restore(Setting())) is IOException or UnauthorizedAccessException);
        Directory.Delete(_journal.FilePath + ".tmp");
        _system.AfterActivation = null;
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(Setting()));
        Assert.Equal(1800u, _system.Effective[PowerPlanPolicy.Sleep]);
        Assert.Equal(2, _system.Writes);
        Assert.Equal(3, _system.Activations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 0 })]
    [InlineData(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void Malformed_snapshot_never_reaches_native_mutation(byte[]? value)
    {
        var setting = Setting();
        setting.Read();
        Assert.Throws<InvalidDataException>(() => setting.Write(value));
        Assert.Throws<InvalidDataException>(() => setting.Activate(value));
        Assert.Equal(0, _system.Writes);
        Assert.Equal(0, _system.Activations);
    }

    private sealed class SystemStub : IPowerPlanSystem
    {
        public Guid Active { get; set; } = Guid.NewGuid();
        public Dictionary<PowerPlanPolicy, uint> Values { get; } = new()
        {
            [PowerPlanPolicy.Sleep] = 1800, [PowerPlanPolicy.Display] = 600, [PowerPlanPolicy.Disk] = 900
        };
        public Dictionary<PowerPlanPolicy, uint> Effective { get; } = new()
        {
            [PowerPlanPolicy.Sleep] = 1800, [PowerPlanPolicy.Display] = 600, [PowerPlanPolicy.Disk] = 900
        };
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }
        public bool FailActivation { get; set; }
        public Action? AfterRead { get; set; }
        public Action? AfterWrite { get; set; }
        public Action? AfterActivation { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int Activations { get; private set; }
        public Guid GetActiveScheme() => Active;
        public uint ReadAcSeconds(Guid scheme, PowerPlanPolicy policy)
        {
            Reads++;
            if (FailRead) throw new IOException("Read failed.");
            Assert.Equal(Active, scheme);
            var value = Values[policy];
            AfterRead?.Invoke();
            return value;
        }
        public void WriteAcSeconds(Guid scheme, PowerPlanPolicy policy, uint seconds)
        {
            if (FailWrite) throw new IOException("Write failed.");
            Assert.Equal(Active, scheme);
            Values[policy] = seconds;
            Writes++;
            AfterWrite?.Invoke();
        }
        public void ActivateScheme(Guid scheme)
        {
            if (FailActivation) throw new IOException("Activation failed.");
            Assert.Equal(Active, scheme);
            foreach (var pair in Values) Effective[pair.Key] = pair.Value;
            Activations++;
            AfterActivation?.Invoke();
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
