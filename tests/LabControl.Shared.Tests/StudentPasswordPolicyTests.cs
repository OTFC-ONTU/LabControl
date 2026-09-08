using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class StudentPasswordPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-policy-test-" + Guid.NewGuid().ToString("N"));
    private SetupSettingsJournal Journal => new(_directory, "00000000-0000-0000-0000-000000000001", x => x.ToArray(), x => x.ToArray());
    public StudentPasswordPolicyTests() { Directory.CreateDirectory(_directory); Journal.InitializeNew(); }

    [Fact]
    public void Fallback_restores_original_tuple_and_preserves_later_changes()
    {
        var store = new Store { Value = new(14, true) };
        var policy = new StudentPasswordPolicy(store);
        Assert.Equal(SettingChangeResult.Applied, policy.Apply(Journal));
        Assert.Equal(new PasswordPolicySnapshot(1, false), store.Value);
        Assert.Equal(SettingChangeResult.AlreadyApplied, policy.Apply(Journal));
        store.Value = new(10, true);
        Assert.Equal(SettingChangeResult.Conflict, policy.Restore(Journal));
        Assert.Equal(new PasswordPolicySnapshot(10, true), store.Value);
        store.Value = new(1, false);
        Assert.Equal(SettingChangeResult.Restored, policy.Restore(Journal));
        Assert.Equal(new PasswordPolicySnapshot(14, true), store.Value);
    }

    [Theory]
    [InlineData("[System Access]\nMinimumPasswordLength=7")]
    [InlineData("[System Access]\nMinimumPasswordLength=7\nPasswordComplexity=2")]
    [InlineData("[System Access]\nMinimumPasswordLength=7\nMinimumPasswordLength=1\nPasswordComplexity=0")]
    [InlineData("[Other]\nMinimumPasswordLength=7\nPasswordComplexity=0")]
    public void Unsupported_export_is_rejected(string text) => Assert.Throws<InvalidDataException>(() => StudentPasswordPolicy.ParseExport(text));

    [Fact]
    public void Template_changes_only_two_policy_fields_and_roundtrips()
    {
        var snapshot = new PasswordPolicySnapshot(14, true);
        var text = StudentPasswordPolicy.Configuration(snapshot);
        Assert.Equal(snapshot, StudentPasswordPolicy.ParseExport(text));
        Assert.DoesNotContain("student", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordHistorySize", text);
    }

    [Fact]
    public void Password_rejection_retries_once_after_fallback_and_records_only_successful_sid()
    {
        var state = new InstallationState(_directory);
        var account = new Account();
        var calls = 0;
        var provision = new StudentAccountProvisioning(state, account, () =>
        { calls++; Assert.True(state.Read()!.StudentCreationPending); account.Reject = false; });
        Assert.Equal(StudentAccountAction.Create, provision.Prepare(false));
        Assert.Equal(1, calls);
        Assert.Equal(2, account.Calls);
        Assert.Equal(account.Sid, state.Read()!.CreatedStudentSid);
    }

    [Fact]
    public void Opt_out_never_invokes_password_fallback_or_account_adapter()
    {
        var account = new Account();
        var provision = new StudentAccountProvisioning(new InstallationState(_directory), account, () => throw new IOException());
        Assert.Equal(StudentAccountAction.Skip, provision.Prepare(false, false));
        Assert.Equal(0, account.Lookups);
        Assert.Equal(0, account.Calls);
    }

    [Fact]
    public void Failed_fallback_does_not_retry_creation()
    {
        var account = new Account();
        var provision = new StudentAccountProvisioning(new InstallationState(_directory), account, () => throw new IOException());
        Assert.Throws<IOException>(() => provision.Prepare(false));
        Assert.Equal(1, account.Calls);
        Assert.Null(new InstallationState(_directory).Read()!.CreatedStudentSid);
    }

    private sealed class Store : IPasswordPolicyStore
    {
        public PasswordPolicySnapshot Value;
        public PasswordPolicySnapshot Read() => Value;
        public void Write(PasswordPolicySnapshot expected, PasswordPolicySnapshot desired)
        { Assert.Equal(expected, Value); Value = desired; }
    }
    private sealed class Account : IStudentAccountSystem
    {
        public string? Sid;
        public bool Reject = true;
        public int Lookups, Calls;
        public string? FindStudentSid() { Lookups++; return Sid; }
        public string CreateDisabledStudent()
        {
            Calls++;
            if (Reject) throw new StudentPasswordPolicyException();
            return Sid = "S-1-5-21-1-2-3-1001";
        }
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
