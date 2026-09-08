using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class StudentSessionSettingsTests : IDisposable
{
    private const string Student = "S-1-5-21-1-2-3-1001";
    private const string Admin = "S-1-5-21-1-2-3-1002";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-hygiene-" + Guid.NewGuid().ToString("N"));
    private readonly InstallationState _state;
    private readonly SetupSettingsJournal _journal;
    private readonly Visibility _visibility = new();
    private readonly Policies _policies = new();
    public StudentSessionSettingsTests()
    {
        Directory.CreateDirectory(_directory);
        _state = new(_directory); _state.Configure(false);
        _state.BeginStudentCreation(null); _state.CompleteStudentCreation(Student);
        _journal = new(_directory, _state.Read()!.InstallationId, b => b.ToArray(), b => b.ToArray()); _journal.InitializeNew();
    }
    private AdministratorVisibility AdminComponent() => new(_state, () => Student, () => _journal, _visibility);
    private StudentSessionSettings SessionComponent() => new(_state, () => Student, () => _journal, _policies);
    [Theory]
    [InlineData(StudentSessionPolicy.PrivacyExperience)] [InlineData(StudentSessionPolicy.EdgeFirstRun)]
    [InlineData(StudentSessionPolicy.OneDriveSignInNotifications)]
    public void Session_policy_round_trips_absent_value(StudentSessionPolicy policy)
    {
        Assert.Equal(SettingChangeResult.Applied, SessionComponent().Apply(policy));
        Assert.Equal(1u, _policies.Value);
        Assert.Equal(SettingChangeResult.AlreadyApplied, SessionComponent().Apply(policy));
        Assert.Equal(SettingChangeResult.Restored, SessionComponent().Restore(policy));
        Assert.Null(_policies.Value);
    }
    [Fact]
    public void Later_session_policy_edit_is_preserved()
    {
        SessionComponent().Apply(StudentSessionPolicy.PrivacyExperience);
        _policies.Value = 17;
        Assert.Equal(SettingChangeResult.Conflict, SessionComponent().Restore(StudentSessionPolicy.PrivacyExperience));
        Assert.Equal(17u, _policies.Value);
    }
    [Fact]
    public void Restore_admin_as_different_operator_uses_recorded_sid_without_current_lookup()
    {
        Assert.Equal(SettingChangeResult.Applied, AdminComponent().Apply());
        Assert.Equal(Admin, _state.Read()!.HiddenAdministratorSid);
        _visibility.Current = "S-1-5-21-1-2-3-1003";
        Assert.Equal(SettingChangeResult.AlreadyApplied, AdminComponent().Apply());
        Assert.Equal(SettingChangeResult.Restored, AdminComponent().Restore());
        Assert.Null(_visibility.Value.Value);
        Assert.Equal(1, _visibility.CurrentLookups);
        Assert.All(_visibility.Targets, sid => Assert.Equal(Admin, sid));
    }
    [Fact]
    public void Restore_admin_does_not_access_deleted_or_replaced_student()
    {
        Assert.Equal(SettingChangeResult.Applied, AdminComponent().Apply());
        var recovery = new AdministratorVisibility(_state,
            () => throw new InvalidOperationException("Student SAM must not be accessed during admin recovery."),
            () => _journal, _visibility);
        Assert.Equal(SettingChangeResult.Restored, recovery.Restore());
        Assert.Null(_visibility.Value.Value);
        Assert.Equal(1, _visibility.CurrentLookups);
        Assert.All(_visibility.Targets, sid => Assert.Equal(Admin, sid));
    }

    [Fact]
    public void Recovery_preserves_later_admin_edit_without_student_access()
    {
        AdminComponent().Apply();
        _visibility.Value = _visibility.Value with { Value = 1 };
        var recovery = new AdministratorVisibility(_state, () => throw new Exception(), () => _journal, _visibility);
        Assert.Equal(SettingChangeResult.Conflict, recovery.Restore());
        Assert.Equal(1u, _visibility.Value.Value);
        Assert.Equal(1, _visibility.Writes);
    }

    [Fact]
    public void Renamed_administrator_is_preserved()
    {
        AdminComponent().Apply();
        _visibility.Value = _visibility.Value with { AccountName = "Renamed" };
        Assert.Equal(SettingChangeResult.Conflict, AdminComponent().Restore());
        Assert.Equal(1, _visibility.Writes);
    }
    [Fact]
    public void Later_visibility_edit_is_preserved()
    {
        AdminComponent().Apply(); _visibility.Value = _visibility.Value with { Value = 2 };
        Assert.Equal(SettingChangeResult.Conflict, AdminComponent().Restore());
        Assert.Equal(2u, _visibility.Value.Value);
    }
    [Fact]
    public void Off_skips_every_student_admin_native_and_journal_access()
    {
        _state.Configure(true, false);
        var admin = new AdministratorVisibility(_state, () => throw new Exception(), () => throw new Exception(), _visibility);
        var settings = new StudentSessionSettings(_state, () => throw new Exception(), () => throw new Exception(), _policies);
        Assert.Null(admin.Apply()); Assert.Null(admin.Restore());
        Assert.Null(settings.Apply(StudentSessionPolicy.PrivacyExperience));
        Assert.Null(settings.Restore(StudentSessionPolicy.EdgeFirstRun));
        Assert.Equal(0, _visibility.CurrentLookups); Assert.Empty(_visibility.Targets); Assert.Equal(0, _policies.Reads);
        Assert.Equal(0, _visibility.PromptReads);
    }
    [Fact]
    public void Replaced_student_refuses_hygiene_and_visibility()
    {
        var admin = new AdministratorVisibility(_state, () => null, () => _journal, _visibility);
        var settings = new StudentSessionSettings(_state, () => null, () => _journal, _policies);
        Assert.Throws<InvalidOperationException>(() => admin.Apply());
        Assert.Throws<InvalidOperationException>(() => settings.Apply(StudentSessionPolicy.EdgeFirstRun));
        Assert.Equal(0, _visibility.CurrentLookups); Assert.Equal(0, _policies.Reads);
    }
    [Fact]
    public void Hidden_target_is_write_once_and_cannot_be_student()
    {
        Assert.Throws<InvalidOperationException>(() => _state.RecordHiddenAdministrator(Student));
        _state.RecordHiddenAdministrator(Admin);
        Assert.Throws<InvalidOperationException>(() => _state.RecordHiddenAdministrator("S-1-5-21-1-2-3-1003"));
    }
    [Fact]
    public void Interrupted_visibility_apply_is_not_adopted()
    {
        _visibility.FailAfterWrite = true;
        Assert.Throws<IOException>(() => AdminComponent().Apply());
        _visibility.FailAfterWrite = false;
        Assert.Equal(SettingChangeResult.Conflict, AdminComponent().Apply());
        Assert.Equal(SettingChangeResult.Conflict, AdminComponent().Restore());
    }
    [Fact]
    public void Credential_entry_is_verified_before_hiding_and_restored_after_unhiding()
    {
        _visibility.Prompt = 1;
        Assert.Equal(SettingChangeResult.Applied, AdminComponent().Apply());
        Assert.Equal(new[] { "prompt:0", "visibility:0" }, _visibility.Mutations);
        Assert.Equal(SettingChangeResult.Restored, AdminComponent().Restore());
        Assert.Equal(new[] { "prompt:0", "visibility:0", "visibility:absent", "prompt:1" }, _visibility.Mutations);
    }

    [Fact]
    public void Failed_credential_policy_never_hides_the_administrator()
    {
        _visibility.FailPrompt = true;
        Assert.Throws<IOException>(() => AdminComponent().Apply());
        Assert.Null(_visibility.Value.Value);
        Assert.Equal(0, _visibility.Writes);
    }

    [Fact]
    public void Visibility_conflict_keeps_credential_entry_available()
    {
        AdminComponent().Apply();
        _visibility.Value = _visibility.Value with { AccountName = "Renamed" };
        Assert.Equal(SettingChangeResult.Conflict, AdminComponent().Restore());
        Assert.Equal(0u, _visibility.Prompt);
    }

    [Fact]
    public void Later_credential_policy_edit_is_preserved_after_admin_unhide()
    {
        AdminComponent().Apply();
        _visibility.Prompt = 7;
        Assert.Equal(SettingChangeResult.Conflict, AdminComponent().Restore());
        Assert.Null(_visibility.Value.Value);
        Assert.Equal(7u, _visibility.Prompt);
    }

    [Fact]
    public void Already_hidden_admin_is_not_owned()
    {
        _visibility.Value = _visibility.Value with { Value = 0 };
        Assert.Equal(SettingChangeResult.Unchanged, AdminComponent().Apply());
        Assert.Equal(SettingChangeResult.Restored, AdminComponent().Restore());
        Assert.Equal(0, _visibility.Writes);
        Assert.Null(_visibility.Prompt);
    }
    private sealed class Policies : IStudentSessionPolicyStore
    {
        public uint? Value; public int Reads;
        public uint? Read(StudentSessionPolicy policy) { Reads++; return Value; }
        public void Write(StudentSessionPolicy policy, uint? expected, uint? desired) { Assert.Equal(Value, expected); Value = desired; }
    }
    private sealed class Visibility : IAdministratorVisibilityStore
    {
        public uint? Prompt; public int PromptReads; public bool FailPrompt;
        public List<string> Mutations = [];
        public uint? ReadCredentialPrompt(string sid) { Targets.Add(sid); PromptReads++; return Prompt; }
        public void WriteCredentialPrompt(string sid, uint? expected, uint? desired)
        {
            Targets.Add(sid); Assert.Equal(Prompt, expected);
            if (FailPrompt) throw new IOException();
            Prompt = desired; Mutations.Add("prompt:" + (desired?.ToString() ?? "absent"));
        }
        public string Current = Admin; public int CurrentLookups; public int Writes; public bool FailAfterWrite;
        public List<string> Targets = [];
        public AdministratorVisibilitySnapshot Value = new("Teacher", null);
        public string CurrentLocalAdministratorSid() { CurrentLookups++; return Current; }
        public AdministratorVisibilitySnapshot Read(string sid) { Targets.Add(sid); return Value; }
        public void Write(string sid, AdministratorVisibilitySnapshot expected, AdministratorVisibilitySnapshot desired)
        {
            Targets.Add(sid); Assert.Equal(expected, Value); Value = desired; Writes++;
            Mutations.Add("visibility:" + (desired.Value?.ToString() ?? "absent"));
            if (FailAfterWrite) throw new IOException();
        }
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
