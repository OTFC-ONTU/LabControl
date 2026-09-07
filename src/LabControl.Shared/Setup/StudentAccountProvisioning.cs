namespace LabControl.Shared.Setup;

/// <summary>The Windows adapter must distinguish an absent local user from lookup failure.
/// Creation is create-new only and returns a disabled account's verified SID, never a
/// SID recovered after an already-exists error. It must not change machine policy.</summary>
public interface IStudentAccountSystem
{
    string? FindStudentSid();
    string CreateDisabledStudent();
}

/// <summary>Prepare account ownership under Setup's private-directory/exclusive-lock scope.
/// Activation, group configuration and sign-in belong to later journaled setup steps.</summary>
public sealed class StudentAccountProvisioning(InstallationState state, IStudentAccountSystem system)
{
    public StudentAccountAction Prepare(bool existingInstallation, bool? createStudentAccount = null)
    {
        var configuration = state.Configure(existingInstallation, createStudentAccount);
        // Opt-out must not even query the account adapter (D-40).
        if (configuration.CreateStudentAccount != true) return StudentAccountAction.Skip;

        var currentSid = system.FindStudentSid();
        var action = state.PlanStudentAccount(currentSid);
        if (action == StudentAccountAction.OwnershipConflict)
            throw new InvalidOperationException("The student account does not match this installation's ownership record. Run Setup with account creation off to preserve existing accounts.");
        if (action == StudentAccountAction.AlreadyManaged) return action;

        state.BeginStudentCreation(currentSid);
        var createdSid = system.CreateDisabledStudent();
        // A failed save deliberately leaves a disabled, unowned account. Never roll back
        // by deleting a name: another account might have replaced it meanwhile.
        state.CompleteStudentCreation(createdSid);
        state.RequireManagedStudent(system.FindStudentSid());
        return StudentAccountAction.Create;
    }
}
