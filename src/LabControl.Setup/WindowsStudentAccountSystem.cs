using System.ComponentModel;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.NetManagement;

namespace LabControl.Setup;

/// <summary>Local SAM only; null server never follows a domain account with the same name.
/// No password-policy fallback until the original-settings journal can restore it.</summary>
internal sealed class WindowsStudentAccountSystem : IStudentAccountSystem
{
    public string? FindStudentSid() => ReadStudent()?.Sid;

    public unsafe string CreateDisabledStudent()
    {
        // NetUserAdd does not return a SID. Bind the immediate read-back to this particular
        // successful call, not just the name. The marker is not a credential and is never
        // used to adopt an account on repair or after a failed NetUserAdd.
        var marker = Defaults.StudentCreationCommentPrefix + Guid.NewGuid().ToString("D");
        fixed (char* name = Defaults.StudentAccountName)
        fixed (char* password = Defaults.StudentDefaultPassword)
        fixed (char* comment = marker)
        {
            var info = new USER_INFO_1
            {
                usri1_name = new PWSTR(name),
                usri1_password = new PWSTR(password),
                usri1_comment = new PWSTR(comment),
                usri1_priv = USER_PRIV.USER_PRIV_USER,
                usri1_flags = USER_ACCOUNT_FLAGS.UF_SCRIPT | (USER_ACCOUNT_FLAGS)PInvoke.UF_NORMAL_ACCOUNT | USER_ACCOUNT_FLAGS.UF_ACCOUNTDISABLE
                    | USER_ACCOUNT_FLAGS.UF_DONT_EXPIRE_PASSWD | USER_ACCOUNT_FLAGS.UF_PASSWD_CANT_CHANGE,
            };
            uint parameter = 0;
            var status = PInvoke.NetUserAdd(default, 1, (byte*)&info, &parameter);
            if (status != 0) throw Failure("Creating the disabled student account", (uint)status);
        }

        var created = ReadStudent();
        if (created is null || created.Value.Comment != marker || !created.Value.Disabled)
            throw new InvalidOperationException("The newly created student account could not be verified. It has not been adopted; preserve it and review the interrupted installation.");
        return created.Value.Sid;
    }

    private static unsafe (string Sid, string Comment, bool Disabled)? ReadStudent()
    {
        byte* buffer = null;
        try
        {
            fixed (char* name = Defaults.StudentAccountName)
            {
                var status = PInvoke.NetUserGetInfo(default, new PCWSTR(name), 23, &buffer);
                if ((uint)status == PInvoke.NERR_UserNotFound) return null;
                if (status != 0) throw Failure("Looking up the local student account", (uint)status);
            }
            if (buffer is null) throw new InvalidDataException("Windows returned no student account information.");
            var info = (USER_INFO_23*)buffer;
            var sid = new SecurityIdentifier((nint)info->usri23_user_sid.Value).Value;
            return (sid, info->usri23_comment.ToString(), (info->usri23_flags & USER_ACCOUNT_FLAGS.UF_ACCOUNTDISABLE) != 0);
        }
        finally
        {
            if (buffer is not null) PInvoke.NetApiBufferFree(buffer);
        }
    }

    // Only operation and numeric status: never include API inputs or the password.
    private static Win32Exception Failure(string operation, uint status) =>
        new(unchecked((int)status), $"{operation} failed (Windows status {status}). Account and password policy were not changed by a fallback.");
}
