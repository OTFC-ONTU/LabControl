using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class StudentAccountProvisioningTests : IDisposable
{
    private const string OwnedSid = "S-1-5-21-100-200-300-1001";
    private const string OtherSid = "S-1-5-21-100-200-300-1002";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-account-" + Guid.NewGuid().ToString("N"));
    private InstallationState State => new(_directory);

    private StudentAccountProvisioning Open(FakeAccounts accounts) => new(State, accounts);

    [Fact]
    public void Intent_precedes_creation_and_repair_performs_no_account_mutation()
    {
        var accounts = new FakeAccounts();
        accounts.BeforeCreate = () =>
        {
            Assert.True(State.Read()!.StudentCreationPending);
            Assert.Null(State.Read()!.CreatedStudentSid);
        };
        Assert.Equal(StudentAccountAction.Create, Open(accounts).Prepare(false));
        Assert.Equal(OwnedSid, State.Read()!.CreatedStudentSid);
        Assert.False(State.Read()!.StudentCreationPending);
        Assert.Equal(StudentAccountAction.AlreadyManaged, Open(accounts).Prepare(true));
        Assert.Equal(1, accounts.Creations);
    }

    [Fact]
    public void Opt_out_and_repair_skip_even_a_failing_account_lookup()
    {
        var accounts = new FakeAccounts { BeforeLookup = () => throw new IOException("lookup unavailable") };
        Assert.Equal(StudentAccountAction.Skip, Open(accounts).Prepare(false, false));
        Assert.Equal(StudentAccountAction.Skip, Open(accounts).Prepare(true));
        Assert.Equal(0, accounts.Lookups);
        Assert.Equal(0, accounts.Creations);
    }

    [Fact]
    public void Missing_legacy_mode_and_lookup_errors_never_authorize_creation()
    {
        var accounts = new FakeAccounts();
        Assert.Throws<InvalidOperationException>(() => Open(accounts).Prepare(true));
        Assert.Equal(0, accounts.Lookups);
        accounts.BeforeLookup = () => throw new IOException("lookup unavailable");
        Assert.Throws<IOException>(() => Open(accounts).Prepare(true, true));
        Assert.Equal(0, accounts.Creations);
        Assert.False(State.Read()!.StudentCreationPending);
    }

    [Fact]
    public void Existing_or_replaced_accounts_are_never_modified()
    {
        var accounts = new FakeAccounts { Sid = OtherSid };
        Assert.Throws<InvalidOperationException>(() => Open(accounts).Prepare(false));
        Assert.Equal(0, accounts.Creations);
        Assert.Null(State.Read()!.CreatedStudentSid);
        accounts.Sid = null;
        Open(accounts).Prepare(true);
        accounts.Sid = OtherSid;
        Assert.Throws<InvalidOperationException>(() => Open(accounts).Prepare(true));
        Assert.Equal(1, accounts.Creations);
        Assert.Equal(OwnedSid, State.Read()!.CreatedStudentSid);
    }

    [Fact]
    public void Concurrent_creator_or_ambiguous_native_result_is_not_adopted_on_retry()
    {
        var accounts = new FakeAccounts();
        accounts.BeforeCreate = () =>
        {
            accounts.Sid = OtherSid;
            throw new InvalidOperationException("already exists or read-back could not be verified");
        };
        Assert.Throws<InvalidOperationException>(() => Open(accounts).Prepare(false));
        Assert.True(State.Read()!.StudentCreationPending);
        Assert.Null(State.Read()!.CreatedStudentSid);
        Assert.Throws<InvalidOperationException>(() => Open(accounts).Prepare(true));
        Assert.Equal(1, accounts.Creations);
        Assert.Equal(OtherSid, accounts.Sid);
    }

    [Fact]
    public void Failed_intent_write_prevents_the_native_creation_call()
    {
        var accounts = new FakeAccounts();
        accounts.BeforeLookup = () => Directory.CreateDirectory(State.FilePath + ".tmp");
        Assert.True(Record.Exception(() => Open(accounts).Prepare(false)) is IOException or UnauthorizedAccessException);
        Assert.Equal(0, accounts.Creations);
        Assert.False(State.Read()!.StudentCreationPending);
    }

    [Fact]
    public void Failed_sid_write_leaves_the_account_unowned_and_never_deletes_it()
    {
        var accounts = new FakeAccounts
        {
            BeforeCreate = () => Directory.CreateDirectory(State.FilePath + ".tmp"),
        };
        Assert.True(Record.Exception(() => Open(accounts).Prepare(false)) is IOException or UnauthorizedAccessException);
        Assert.Equal(OwnedSid, accounts.Sid);
        Assert.True(State.Read()!.StudentCreationPending);
        Assert.Null(State.Read()!.CreatedStudentSid);
        Directory.Delete(State.FilePath + ".tmp");
        Assert.Throws<InvalidOperationException>(() => Open(accounts).Prepare(true));
        Assert.Equal(1, accounts.Creations);
    }

    [Fact]
    public void Policy_failure_can_retry_only_while_the_account_is_absent()
    {
        var accounts = new FakeAccounts { BeforeCreate = () => throw new InvalidOperationException("policy refused creation") };
        Assert.Throws<InvalidOperationException>(() => Open(accounts).Prepare(false));
        Assert.Null(accounts.Sid);
        Assert.Null(State.Read()!.CreatedStudentSid);
        accounts.BeforeCreate = null;
        Assert.Equal(StudentAccountAction.Create, Open(accounts).Prepare(true));
        Assert.Equal(2, accounts.Creations);
    }

    [Fact]
    public void Replacement_after_creation_cannot_report_success_or_authorize_its_profile()
    {
        var accounts = new FakeAccounts();
        accounts.BeforeLookup = () =>
        {
            if (accounts.Creations != 0) accounts.Sid = OtherSid;
        };
        Assert.Throws<InvalidOperationException>(() => Open(accounts).Prepare(false));
        Assert.Equal(OwnedSid, State.Read()!.CreatedStudentSid);
        Assert.Throws<InvalidOperationException>(() => State.RequireManagedStudent(OtherSid));
    }

    private sealed class FakeAccounts : IStudentAccountSystem
    {
        public string? Sid { get; set; }
        public Action? BeforeLookup { get; set; }
        public Action? BeforeCreate { get; set; }
        public int Lookups { get; private set; }
        public int Creations { get; private set; }

        public string? FindStudentSid()
        {
            Lookups++;
            BeforeLookup?.Invoke();
            return Sid;
        }

        public string CreateDisabledStudent()
        {
            Creations++;
            BeforeCreate?.Invoke();
            if (Sid is not null) throw new InvalidOperationException("already exists");
            return Sid = OwnedSid;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
