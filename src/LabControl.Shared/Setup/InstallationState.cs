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
        if (state is { RemovalReady: true } or { StudentRemovalPending: true } or { StudentRemoved: true })
            throw new InvalidOperationException("Removal has started. Finish removal before installing again.");
        if (state is null)
        {
            if (existingInstallation && createStudentAccount is null)
                throw new InvalidOperationException("Installation history is missing. Choose the account mode explicitly; existing accounts will be preserved.");
            state = new InstallationDocument
            {
                InstallationId = Guid.NewGuid().ToString("d"),
                SettingsInitializationPending = !existingInstallation,
                CreateStudentAccount = createStudentAccount ?? true,
            };
        }
        else if (createStudentAccount is { } selected)
            state.CreateStudentAccount = selected;
        Save(state);
        return state;
    }

    /// <summary>Only the fresh-install intent can authorize retrying journal creation.
    /// Call after the protected journal has been created and validated, before settings.</summary>
    public void MarkRemovalReady()
    {
        var state = RequireState();
        if (state.StudentRemovalPending)
            throw new InvalidOperationException("Finish the previously authorized student removal before deleting ownership history.");
        state.RemovalReady = true;
        Save(state);
    }

    public void BeginInstallDirectoryCreation(bool directoryExists)
    {
        var state = RequireState();
        if (state.InstallDirectoryOwned) return;
        if (directoryExists) throw new InvalidOperationException("The installation directory existed before ownership was recorded; preserve it for review.");
        state.InstallDirectoryOwned = true;
        Save(state);
    }

    public void BeginStudentRemoval(string currentSid)
    {
        var state = RequireState();
        RequireManagedStudent(currentSid);
        state.StudentRemovalPending = true;
        Save(state);
    }

    public void CompleteStudentRemoval()
    {
        var state = RequireState();
        if (!state.StudentRemovalPending) throw new InvalidOperationException("Student removal was not recorded.");
        state.StudentRemoved = true;
        state.StudentRemovalPending = false;
        Save(state);
    }

    public string RecordHiddenAdministrator(string sid)
    {
        var state = RequireState();
        if (state.CreateStudentAccount != true || state.CreatedStudentSid is null || !IsLocalAccountSid(sid) || sid == state.CreatedStudentSid)
            throw new InvalidOperationException("Only the setup administrator can be selected for visibility settings.");
        if (state.HiddenAdministratorSid is { } saved && saved != sid)
            throw new InvalidOperationException("The recorded administrator visibility target cannot change during repair.");
        state.HiddenAdministratorSid = sid;
        Save(state);
        return sid;
    }

    public void SelectNumber(int number)
    {
        var state = RequireState();
        if (number < 1 || number > Defaults.MaxStudentPcs || state.Number is { } saved && saved != number)
            throw new InvalidOperationException("Repair must retain the installed PC number.");
        state.Number = number;
        Save(state);
    }

    public void CompleteSettingsInitialization()
    {
        var state = RequireState();
        if (!state.SettingsInitializationPending) return;
        state.SettingsInitializationPending = false;
        Save(state);
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
            || state.Number is < 1 or > Defaults.MaxStudentPcs
            || (state.StudentRemovalPending && state.StudentRemoved)
            || ((state.StudentRemovalPending || state.StudentRemoved) && state.CreatedStudentSid is null)
            || (state.HiddenAdministratorSid is { } admin && (state.CreatedStudentSid is null || !IsLocalAccountSid(admin) || admin == state.CreatedStudentSid))
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
