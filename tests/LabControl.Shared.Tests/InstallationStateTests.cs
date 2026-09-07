using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class InstallationStateTests : IDisposable
{
    private const string OwnedSid = "S-1-5-21-100-200-300-1001";
    private const string ReplacementSid = "S-1-5-21-100-200-300-1002";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-installation-" + Guid.NewGuid().ToString("n"));
    private InstallationState Open() => new(_directory);

    [Fact]
    public void Fresh_default_is_on_but_repair_preserves_explicit_opt_out()
    {
        var state = Open();
        var id = state.Configure(false).InstallationId;
        Assert.Equal(StudentAccountAction.Create, state.PlanStudentAccount(null));
        state.Configure(true, false);
        var repaired = Open().Configure(true);
        Assert.Equal(id, repaired.InstallationId);
        Assert.False(repaired.CreateStudentAccount);
        Assert.Equal(StudentAccountAction.Skip, state.PlanStudentAccount(ReplacementSid));
        Assert.Throws<InvalidOperationException>(() => state.BeginStudentCreation(null));
        Assert.Throws<InvalidOperationException>(() => state.RequireManagedStudent(ReplacementSid));
    }

    [Fact]
    public void Missing_legacy_history_never_applies_fresh_defaults_or_authorizes_removal()
    {
        var state = Open();
        Assert.Null(state.Read());
        Assert.Throws<InvalidOperationException>(() => state.Configure(true));
        Assert.Equal(StudentRemovalAction.Keep, state.PlanStudentRemoval(OwnedSid, true, true));
        Assert.Throws<InvalidOperationException>(() => state.RequireManagedStudent(OwnedSid));
        state.Configure(true, false);
        Assert.Equal(StudentAccountAction.Skip, state.PlanStudentAccount(OwnedSid));
    }

    [Fact]
    public void Existing_student_is_never_adopted_even_after_explicit_opt_in()
    {
        var state = Open();
        state.Configure(false, false);
        state.Configure(true, true);
        Assert.Equal(StudentAccountAction.OwnershipConflict, state.PlanStudentAccount(OwnedSid));
        Assert.Throws<InvalidOperationException>(() => state.BeginStudentCreation(OwnedSid));
        Assert.Throws<InvalidOperationException>(() => state.CompleteStudentCreation(OwnedSid));
        Assert.Equal(StudentRemovalAction.OwnershipConflict, state.PlanStudentRemoval(OwnedSid, true, true));
        Assert.Null(state.Read()!.CreatedStudentSid);
    }

    [Fact]
    public void Successful_creation_persists_sid_and_replacement_accounts_are_protected()
    {
        var state = Open();
        state.Configure(false);
        state.BeginStudentCreation(null);
        Assert.True(Open().Read()!.StudentCreationPending);
        state.CompleteStudentCreation(OwnedSid);
        var repaired = Open();
        Assert.False(repaired.Read()!.StudentCreationPending);
        Assert.Equal(OwnedSid, repaired.RequireManagedStudent(OwnedSid));
        Assert.Equal(StudentAccountAction.OwnershipConflict, repaired.PlanStudentAccount(ReplacementSid));
        Assert.Equal(StudentAccountAction.OwnershipConflict, repaired.PlanStudentAccount(null));
        Assert.Throws<InvalidOperationException>(() => repaired.RequireManagedStudent(ReplacementSid));
        Assert.Equal(StudentRemovalAction.Keep, repaired.PlanStudentRemoval(OwnedSid, false, true));
        Assert.Equal(StudentRemovalAction.Keep, repaired.PlanStudentRemoval(OwnedSid, true, false));
        Assert.Equal(StudentRemovalAction.RemoveOwnedAccount, repaired.PlanStudentRemoval(OwnedSid, true, true));
        Assert.Equal(StudentRemovalAction.OwnershipConflict, repaired.PlanStudentRemoval(ReplacementSid, true, true));
        repaired.Configure(true, false);
        Assert.Equal(OwnedSid, repaired.Read()!.CreatedStudentSid);
        Assert.Equal(StudentRemovalAction.Keep, repaired.PlanStudentRemoval(OwnedSid, true, true));
        Assert.Throws<InvalidOperationException>(() => repaired.RequireManagedStudent(OwnedSid));
        repaired.Configure(true, true);
        Assert.Equal(OwnedSid, repaired.RequireManagedStudent(OwnedSid));
    }

    [Fact]
    public void Interrupted_creation_can_retry_only_if_the_account_is_still_absent()
    {
        var state = Open();
        state.Configure(false);
        state.BeginStudentCreation(null);
        var afterCrash = Open();
        Assert.Throws<InvalidOperationException>(() => afterCrash.CompleteStudentCreation(OwnedSid));
        Assert.Equal(StudentAccountAction.OwnershipConflict, afterCrash.PlanStudentAccount(OwnedSid));
        Assert.Throws<InvalidOperationException>(() => afterCrash.BeginStudentCreation(OwnedSid));
        Assert.Equal(StudentRemovalAction.OwnershipConflict, afterCrash.PlanStudentRemoval(OwnedSid, true, true));
        afterCrash.BeginStudentCreation(null);
        afterCrash.CompleteStudentCreation(OwnedSid);
        Assert.Equal(OwnedSid, Open().RequireManagedStudent(OwnedSid));
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-21-1-2-3--1")]
    [InlineData("S-1-5-21-1-2-3-4294967296")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_creation_result_does_not_change_ownership(string? sid)
    {
        var state = Open();
        state.Configure(false);
        state.BeginStudentCreation(null);
        Assert.Throws<InvalidDataException>(() => state.CompleteStudentCreation(sid!));
        Assert.Null(Open().Read()!.CreatedStudentSid);
        Assert.True(Open().Read()!.StudentCreationPending);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"schema_version\":1,\"installation_id\":\"00000000-0000-0000-0000-000000000001\"}")]
    public void Corrupt_history_is_not_treated_as_a_fresh_install(string json)
    {
        var state = Open();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(state.FilePath, json);
        Assert.ThrowsAny<Exception>(() => state.Configure(false));
        Assert.ThrowsAny<Exception>(() => state.PlanStudentRemoval(OwnedSid, true, true));
        Assert.Equal(json, File.ReadAllText(state.FilePath));
    }

    [Fact]
    public void Future_schema_is_refused_and_failed_intent_save_does_not_authorize_creation_completion()
    {
        var state = Open();
        state.Configure(false);
        var json = File.ReadAllText(state.FilePath).Replace("\"schema_version\": 1", "\"schema_version\": 999");
        File.WriteAllText(state.FilePath, json);
        Assert.Throws<SchemaVersionException>(() => state.Configure(true));
        File.Delete(state.FilePath);
        state.Configure(false);
        Directory.CreateDirectory(state.FilePath + ".tmp");
        Assert.Throws<UnauthorizedAccessException>(() => state.BeginStudentCreation(null));
        Assert.False(Open().Read()!.StudentCreationPending);
        Assert.Throws<InvalidOperationException>(() => state.CompleteStudentCreation(OwnedSid));
    }

    [Fact]
    public void Failed_sid_save_leaves_an_ambiguous_account_unmanaged_after_restart()
    {
        var state = Open();
        state.Configure(false);
        state.BeginStudentCreation(null);
        Directory.CreateDirectory(state.FilePath + ".tmp");
        Assert.Throws<UnauthorizedAccessException>(() => state.CompleteStudentCreation(OwnedSid));
        var restarted = Open();
        Assert.True(restarted.Read()!.StudentCreationPending);
        Assert.Null(restarted.Read()!.CreatedStudentSid);
        Assert.Throws<InvalidOperationException>(() => restarted.RequireManagedStudent(OwnedSid));
        Assert.Equal(StudentRemovalAction.OwnershipConflict, restarted.PlanStudentRemoval(OwnedSid, true, true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
