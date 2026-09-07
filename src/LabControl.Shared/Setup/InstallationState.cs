using System.Globalization;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Setup;

public enum StudentAccountAction { Skip, Create, AlreadyManaged, OwnershipConflict }
public enum StudentRemovalAction { Keep, RemoveOwnedAccount, OwnershipConflict }

/// <summary>
/// The account portion of Setup's journal. Call only under Setup's exclusive installation
/// lock, in the SYSTEM/Administrators-only data directory. OS operations are deliberately
/// separate: persist intent before creation and persist its returned SID before configuring
/// sign-in or allowing profile operations. No account is adopted after an uncertain crash.
/// </summary>
public sealed class InstallationState(string dataDirectory)
{
    private bool _creationStartedThisRun;
    public string FilePath { get; } = Path.Combine(dataDirectory, Defaults.InstallationFileName);

    public InstallationDocument? Read()
    {
        InstallationDocument state;
        try { state = JsonStore.Load<InstallationDocument>(FilePath, InstallationDocument.Migrations); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        Validate(state);
        return state;
    }

    /// <summary>Only a positively identified fresh installation gets the default on.
    /// A legacy installation needs an explicit choice; existing users still cannot be adopted.</summary>
    public InstallationDocument Configure(bool existingInstallation, bool? createStudentAccount = null)
    {
        _creationStartedThisRun = false;
        var state = Read();
        if (state is null)
        {
            if (existingInstallation && createStudentAccount is null)
                throw new InvalidOperationException("Installation history is missing. Choose the account mode explicitly; existing accounts will be preserved.");
            state = new InstallationDocument
            {
                InstallationId = Guid.NewGuid().ToString("d"),
                CreateStudentAccount = createStudentAccount ?? true,
            };
        }
        else if (createStudentAccount is { } selected)
            state.CreateStudentAccount = selected;
        Save(state);
        return state;
    }

    /// <param name="currentSid">SID resolved by Windows for the configured account name,
    /// or null only when Windows positively reports no such account. Lookup errors must throw.</param>
    public StudentAccountAction PlanStudentAccount(string? currentSid)
    {
        var state = RequireState();
        if (state.CreateStudentAccount != true) return StudentAccountAction.Skip;
        if (state.CreatedStudentSid is { } owned)
            return owned == currentSid ? StudentAccountAction.AlreadyManaged : StudentAccountAction.OwnershipConflict;
        return currentSid is null ? StudentAccountAction.Create : StudentAccountAction.OwnershipConflict;
    }

    public void BeginStudentCreation(string? currentSid)
    {
        _creationStartedThisRun = false;
        if (PlanStudentAccount(currentSid) != StudentAccountAction.Create)
            throw new InvalidOperationException("Student account creation is not permitted by the installation history.");
        var state = RequireState();
        state.StudentCreationPending = true;
        Save(state);
        _creationStartedThisRun = true;
    }

    /// <summary>Accept only the SID returned by a successful create-new operation in this
    /// setup run. Never call with a SID discovered during repair or after an already-exists error.</summary>
    public void CompleteStudentCreation(string createdSid)
    {
        if (!IsLocalAccountSid(createdSid))
            throw new InvalidDataException("Account creation did not return a valid account SID.");
        var state = RequireState();
        if (!_creationStartedThisRun || !state.StudentCreationPending || state.CreateStudentAccount != true || state.CreatedStudentSid is not null)
            throw new InvalidOperationException("No student creation is pending.");
        state.CreatedStudentSid = createdSid;
        state.StudentCreationPending = false;
        Save(state);
        _creationStartedThisRun = false;
    }

    /// <summary>No profile path is trusted from disk. Windows must resolve and operate
    /// under this exact SID, with its own reparse-point and token checks.</summary>
    public string RequireManagedStudent(string? currentSid)
    {
        if (PlanStudentAccount(currentSid) != StudentAccountAction.AlreadyManaged)
            throw new InvalidOperationException("No verified managed student account is configured.");
        return currentSid!;
    }

    public StudentRemovalAction PlanStudentRemoval(string? currentSid, bool removeStudent, bool deletionConfirmed)
    {
        var state = Read();
        if (!removeStudent || !deletionConfirmed || state?.CreateStudentAccount != true)
            return StudentRemovalAction.Keep;
        return state.CreatedStudentSid is { } owned && owned == currentSid
            ? StudentRemovalAction.RemoveOwnedAccount : StudentRemovalAction.OwnershipConflict;
    }

    private InstallationDocument RequireState() => Read()
        ?? throw new InvalidOperationException("Installation history is missing; account operations are unavailable.");

    private void Save(InstallationDocument state)
    {
        Validate(state);
        JsonStore.Save(FilePath, state, InstallationDocument.Migrations, ownerOnly: true);
    }

    private static void Validate(InstallationDocument state)
    {
        if (!Guid.TryParseExact(state.InstallationId, "D", out var id) || id == Guid.Empty
            || state.CreateStudentAccount is null
            || (state.CreatedStudentSid is not null && (!IsLocalAccountSid(state.CreatedStudentSid) || state.StudentCreationPending)))
            throw new InvalidDataException("Installation history is invalid; account ownership cannot be established.");
    }

    private static bool IsLocalAccountSid(string? sid)
    {
        if (sid is null) return false;
        var parts = sid.Split('-');
        return parts.Length == 8 && parts[0] == "S" && parts[1] == "1" && parts[2] == "5" && parts[3] == "21"
            && parts.Skip(4).All(p => uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
}
