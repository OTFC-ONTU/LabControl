using LabControl.Shared;
using LabControl.Shared.Setup;
using Windows.Win32;

namespace LabControl.Setup;

internal static class WindowsStudentRemoval
{
    public static void Remove(InstallationState state)
    {
        var document = state.Read() ?? throw new InvalidOperationException("Account ownership history is missing.");
        if (document.StudentRemoved) return;
        var accounts = new WindowsStudentAccountSystem();
        var sid = accounts.FindStudentSid();
        if (document.StudentRemovalPending && sid is null)
        {
            state.CompleteStudentRemoval();
            return;
        }
        var owned = state.RequireManagedStudent(sid);
        state.BeginStudentRemoval(owned);
        // DeleteProfile refuses a loaded profile. Never fall back to deleting a path
        // obtained from an account name, and never delete other users' profile folders.
        if (!PInvoke.DeleteProfile(owned, null, null))
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (error is not (2 or 3)) throw new IOException("The owned student profile could not be removed. Log that student out, then retry removal.");
        }
        state.RequireManagedStudent(accounts.FindStudentSid());
        var result = PInvoke.NetUserDel(null, Defaults.StudentAccountName);
        if (result != 0) throw new IOException("The owned student account could not be removed.");
        if (accounts.FindStudentSid() is not null) throw new IOException("Account removal could not be verified.");
        state.CompleteStudentRemoval();
    }
}
