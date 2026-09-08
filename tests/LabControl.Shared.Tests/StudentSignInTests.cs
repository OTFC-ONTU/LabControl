using System.Text;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class StudentSignInTests : IDisposable
{
    private const string Sid = "S-1-5-21-11-22-33-1001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-tuple-" + Guid.NewGuid().ToString("N"));
    private readonly InstallationState _state;
    private readonly SetupSettingsJournal _journal;
    private readonly Store _store = new();
    private string? _sid = Sid;
    public StudentSignInTests()
    {
        Directory.CreateDirectory(_directory);
        _state = new(_directory);
        _state.Configure(false);
        _state.BeginStudentCreation(null);
        _state.CompleteStudentCreation(Sid);
        // The journal's encryption/storage contract is tested separately.
        _journal = new(_directory, _state.Read()!.InstallationId, b => b.ToArray(), b => b.ToArray());
        _journal.InitializeNew();
    }
    private StudentSignIn Component() => new(_state, () => _sid, () => _journal, _store, "PC-01");
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-5-21-11-22-33-500")]
    public void Sid_baseline_round_trips_before_enable_and_after_tuple_restore(string? original)
    {
        _store.AutoLogonSid = original;
        Assert.Equal(SettingChangeResult.Applied, Component().Apply());
        Assert.Equal(Sid, _store.AutoLogonSid);
        Assert.Equal(new[] { "sid", "tuple" }, _store.Order);
        Component().Apply();
        Assert.Equal(SettingChangeResult.Restored, Component().Restore());
        Assert.Equal(original, _store.AutoLogonSid);
        Assert.Equal(new[] { "sid", "tuple", "tuple", "sid" }, _store.Order);
    }

    [Fact]
    public void Legacy_tuple_keeps_its_original_bytes_and_never_adopts_current_SID()
    {
        var original = _store.Value;
        var legacy = new LegacyTuple(_store);
        var desired = new StudentSignInSnapshot("1", LabControl.Shared.Defaults.StudentAccountName, "PC-01", null,
            Encoding.Unicode.GetBytes(LabControl.Shared.Defaults.StudentDefaultPassword));
        _journal.Apply(legacy, LegacyTuple.Encode(desired));
        _store.AutoLogonSid = Sid; // Windows populated this after the older installation.
        var warnings = 0;
        var component = new StudentSignIn(_state, () => _sid, () => _journal, _store, "PC-01", () => warnings++);
        Assert.Equal(SettingChangeResult.AlreadyApplied, component.Apply());
        Assert.Equal(SettingChangeResult.Restored, component.Restore());
        Assert.True(original.SameAs(_store.Value));
        Assert.Equal(0, _store.SidReads);
        Assert.Equal(Sid, _store.AutoLogonSid);
        Assert.Equal(2, warnings);
        Assert.DoesNotContain("student.autologon-sid", _journal.SettingIds());
    }

    [Fact]
    public void Later_sid_edit_is_preserved_and_reports_conflict()
    {
        Component().Apply();
        _store.AutoLogonSid = "later-identity";
        Assert.Equal(SettingChangeResult.Conflict, Component().Apply());
        Assert.Equal(SettingChangeResult.Conflict, Component().Restore());
        Assert.Equal("later-identity", _store.AutoLogonSid);
        Assert.Contains("student.autologon-sid", _journal.PendingRestorationIds());
    }

    [Fact]
    public void Sid_only_partial_install_restores_without_a_tuple_record()
    {
        _store.AfterRead = () => throw new IOException("Failure before tuple journal write");
        Assert.Throws<IOException>(() => Component().Apply());
        Assert.Equal(new[] { "student.autologon-sid" }, _journal.PendingRestorationIds());
        _store.AfterRead = null;
        Assert.Equal(SettingChangeResult.Restored, Component().Restore());
        Assert.Null(_store.AutoLogonSid);
        Assert.Empty(_journal.PendingRestorationIds());
    }

    private sealed class LegacyTuple(Store store) : ISetupSetting
    {
        public string Id => "student.autologon-tuple";
        public static byte[] Encode(StudentSignInSnapshot snapshot) => [1, .. System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot)];
        public byte[] Read() => Encode(store.Value);
        public void Write(byte[]? value) => store.Value = System.Text.Json.JsonSerializer.Deserialize<StudentSignInSnapshot>(value!.AsSpan(1))!;
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("synthetic-original")]
    public void Complete_tuple_round_trips_and_repair_does_not_replace_original(string? password)
    {
        var original = new StudentSignInSnapshot("1", "personal", "OLD-PC", password,
            password is null ? null : Encoding.Unicode.GetBytes(password));
        _store.Value = original;
        Assert.Equal(SettingChangeResult.Applied, Component().Apply());
        Assert.Equal("student", _store.Value.User);
        Assert.Null(_store.Value.PlaintextPassword);
        Assert.Equal("PC-01", _store.Value.Domain);
        Assert.Equal(SettingChangeResult.AlreadyApplied, Component().Apply());
        Assert.Equal(1, _store.Writes);
        Assert.Equal(SettingChangeResult.Restored, Component().Restore());
        Assert.True(original.SameAs(_store.Value));
    }
    [Fact]
    public void Off_never_accesses_account_signin_or_journal()
    {
        _state.Configure(true, false);
        var component = new StudentSignIn(_state, () => throw new Exception(), () => throw new Exception(), _store, "");
        Assert.Null(component.Apply()); Assert.Null(component.Restore());
        Assert.Equal(0, _store.Reads);
        Assert.Equal(0, _store.SidReads);
    }
    [Fact]
    public void Replaced_account_refuses_all_native_access()
    {
        _sid = "S-1-5-21-11-22-33-1002";
        Assert.Throws<InvalidOperationException>(() => Component().Apply());
        Assert.Throws<InvalidOperationException>(() => Component().Restore());
        Assert.Equal(0, _store.Reads);
        Assert.Equal(0, _store.SidReads);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Any_later_member_edit_preserves_entire_tuple(int member)
    {
        Component().Apply();
        _store.Value = member switch
        {
            0 => _store.Value with { Enabled = "0" },
            1 => _store.Value with { User = "later" },
            2 => _store.Value with { Domain = "later" },
            3 => _store.Value with { PlaintextPassword = "synthetic-later" },
            4 => _store.Value with { Secret = Encoding.Unicode.GetBytes("synthetic-later") },
            _ => _store.Value with { LogonCount = new("dword", null, 1) },
        };
        Assert.Equal(SettingChangeResult.Conflict, Component().Apply());
        Assert.Equal(SettingChangeResult.Conflict, Component().Restore());
        Assert.Equal(1, _store.Writes);
    }
    [Fact]
    public void Interrupted_partial_native_apply_cannot_be_adopted_or_restored()
    {
        _store.PartialFailure = true;
        Assert.Throws<IOException>(() => Component().Apply());
        _store.PartialFailure = false;
        Assert.Equal(SettingChangeResult.Conflict, Component().Apply());
        Assert.Equal(SettingChangeResult.Conflict, Component().Restore());
    }
    [Fact]
    public void Account_replacement_between_read_and_write_is_refused()
    {
        _store.AfterRead = () => _sid = null;
        Assert.Throws<InvalidOperationException>(() => Component().Apply());
        Assert.Equal(0, _store.Writes);
    }
    [Fact]
    public void Membership_preparation_never_activates_and_final_call_rechecks_membership()
    {
        var native = new Activation();
        var component = new StudentAccountActivation(_state, native);
        Assert.True(component.PrepareMembership());
        Assert.Equal(0, native.Activations);
        Assert.True(component.ActivateAfterSuccessfulSetup());
        Assert.Equal(2, native.Memberships);
        Assert.Equal(1, native.Activations);
    }
    [Fact]
    public void Failed_group_verification_prevents_activation()
    {
        var native = new Activation { FailMembership = true };
        Assert.Throws<IOException>(() => new StudentAccountActivation(_state, native).ActivateAfterSuccessfulSetup());
        Assert.Equal(0, native.Activations);
    }
    [Fact]
    public void Off_skips_membership_and_activation_account_queries()
    {
        _state.Configure(true, false);
        var native = new Activation { FailLookup = true };
        var component = new StudentAccountActivation(_state, native);
        Assert.False(component.PrepareMembership());
        Assert.False(component.ActivateAfterSuccessfulSetup());
        Assert.Equal(0, native.Activations);
        Assert.Equal(0, native.Memberships);
    }
    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(uint.MaxValue)]
    public void Countdown_dword_is_removed_and_restored_with_all_bits(uint original)
    {
        var count = new StudentSignInCount("dword", null, original);
        _store.Value = _store.Value with { LogonCount = count };
        Assert.Equal(SettingChangeResult.Applied, Component().Apply());
        Assert.Null(_store.Value.LogonCount);
        Assert.Equal(SettingChangeResult.AlreadyApplied, Component().Apply());
        Assert.Equal(SettingChangeResult.Restored, Component().Restore());
        Assert.Equal(count, _store.Value.LogonCount);
    }

    [Theory]
    [InlineData("")] [InlineData("0001")] [InlineData("legacy text")]
    public void Countdown_string_is_restored_without_type_or_text_conversion(string original)
    {
        var count = new StudentSignInCount("string", original, null);
        _store.Value = _store.Value with { LogonCount = count };
        Assert.Equal(SettingChangeResult.Applied, Component().Apply());
        Assert.Null(_store.Value.LogonCount);
        Assert.Equal(SettingChangeResult.Restored, Component().Restore());
        Assert.Equal(count, _store.Value.LogonCount);
    }

    [Theory]
    [InlineData("binary", null, null)]
    [InlineData("dword", null, null)]
    [InlineData("string", null, null)]
    [InlineData("string", "1", 1u)]
    [InlineData("dword", "1", 1u)]
    public void Malformed_countdown_snapshot_is_refused_before_writes(string kind, string? text, uint? dword)
    {
        _store.Value = _store.Value with { LogonCount = new(kind, text, dword) };
        Assert.Throws<InvalidDataException>(() => Component().Apply());
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void Changed_groups_after_activation_cannot_report_success()
    {
        var native = new Activation { ChangedAfterActivation = true };
        Assert.Throws<IOException>(() => new StudentAccountActivation(_state, native).ActivateAfterSuccessfulSetup());
        Assert.Equal(1, native.Activations);
    }

    private sealed class Activation : IStudentAccountActivationSystem
    {
        public int Memberships; public int Activations;
        public bool FailMembership; public bool FailLookup; public bool ChangedAfterActivation;
        public void VerifyStandardUsersMembership(string sid)
        {
            Assert.Equal(Sid, sid);
            if (ChangedAfterActivation && Activations > 0) throw new IOException("Membership changed.");
        }
        public string? FindStudentSid() => FailLookup ? throw new Exception() : Sid;
        public void EnsureStandardUsersMembership(string sid) { Memberships++; if (FailMembership) throw new IOException(); Assert.Equal(Sid, sid); }
        public void Activate(string sid) { Assert.Equal(Sid, sid); Activations++; }
    }
    private sealed class Store : IStudentSignInStore
    {
        public string? AutoLogonSid;
        public int SidReads;
        public readonly List<string> Order = [];
        public string? ReadAutoLogonSid() { SidReads++; return AutoLogonSid; }
        public void WriteAutoLogonSid(string? expected, string? value)
        { Assert.Equal(expected, AutoLogonSid); AutoLogonSid = value; Order.Add("sid"); }
        public StudentSignInSnapshot Value = new(null, null, null, null, null);
        public int Reads; public int Writes; public bool PartialFailure; public Action? AfterRead;
        public StudentSignInSnapshot Read() { Reads++; var copy = Value with { Secret = Value.Secret?.ToArray() }; AfterRead?.Invoke(); return copy; }
        public void Write(StudentSignInSnapshot expected, StudentSignInSnapshot desired)
        {
            Assert.True(Value.SameAs(expected)); Writes++; Order.Add("tuple");
            if (PartialFailure) { Value = Value with { Enabled = "0" }; throw new IOException(); }
            Value = desired with { Secret = desired.Secret?.ToArray() };
        }
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
