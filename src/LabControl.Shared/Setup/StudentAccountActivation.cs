namespace LabControl.Shared.Setup;

public interface IStudentAccountActivationSystem
{
    string? FindStudentSid();
    void EnsureStandardUsersMembership(string expectedSid);
    void Activate(string expectedSid);
    void VerifyStandardUsersMembership(string expectedSid);
}

/// <summary>Activation is deliberately an explicit final pipeline call, after all
/// required install steps succeed. Every operation remains gated by recorded ownership.</summary>
public sealed class StudentAccountActivation(InstallationState state, IStudentAccountActivationSystem system)
{
    public bool PrepareMembership()
    {
        var sid = OwnedSid();
        if (sid is null) return false;
        system.EnsureStandardUsersMembership(sid);
        state.RequireManagedStudent(system.FindStudentSid());
        return true;
    }
    public bool ActivateAfterSuccessfulSetup()
    {
        var sid = OwnedSid();
        if (sid is null) return false;
        system.EnsureStandardUsersMembership(sid);
        state.RequireManagedStudent(system.FindStudentSid());
        system.Activate(sid);
        system.VerifyStandardUsersMembership(sid);
        state.RequireManagedStudent(system.FindStudentSid());
        return true;
    }
    private string? OwnedSid()
    {
        var configuration = state.Read() ?? throw new InvalidOperationException("Installation history is required for account activation.");
        if (configuration.CreateStudentAccount != true) return null;
        var sid = system.FindStudentSid();
        state.RequireManagedStudent(sid);
        return sid!;
    }
}
