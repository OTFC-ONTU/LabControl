using System.ComponentModel;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.NetManagement;

namespace LabControl.Setup;

internal sealed class WindowsStudentAccountActivationSystem : IStudentAccountActivationSystem
{
    public string? FindStudentSid() => new WindowsStudentAccountSystem().FindStudentSid();
    private void RequireSid(string expected)
    {
        if (FindStudentSid() != expected) throw new InvalidOperationException("The student account ownership changed.");
    }
    public unsafe void EnsureStandardUsersMembership(string expectedSid)
    {
        RequireSid(expectedSid);
        var users = ((NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)
            .Translate(typeof(NTAccount))).Value.Split('\\')[^1];
        var groups = Groups();
        if (groups.Any(group => !string.Equals(group, users, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The managed student belongs to an unexpected local group; preserve its permissions for review.");
        if (!groups.Contains(users, StringComparer.OrdinalIgnoreCase))
        {
            var sid = new SecurityIdentifier(expectedSid);
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            fixed (byte* sidPointer = bytes)
            fixed (char* group = users)
            {
                RequireSid(expectedSid);
                var member = new LOCALGROUP_MEMBERS_INFO_0 { lgrmi0_sid = new Windows.Win32.Security.PSID(sidPointer) };
                var status = PInvoke.NetLocalGroupAddMembers(default, new PCWSTR(group), 0, (byte*)&member, 1);
                if (status != 0) throw Failure((uint)status);
            }
        }
        RequireSid(expectedSid);
        var confirmed = Groups();
        if (confirmed.Count != 1 || !string.Equals(confirmed[0], users, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The managed student's standard Users membership could not be verified.");
    }
    public void VerifyStandardUsersMembership(string expectedSid)
    {
        RequireSid(expectedSid);
        var users = ((NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)
            .Translate(typeof(NTAccount))).Value.Split('\\')[^1];
        var groups = Groups();
        if (groups.Count != 1 || !string.Equals(groups[0], users, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The managed student's standard Users membership changed during activation.");
        RequireSid(expectedSid);
    }

    public unsafe void Activate(string expectedSid)
    {
        RequireSid(expectedSid);
        byte* buffer = null;
        try
        {
            fixed (char* name = Defaults.StudentAccountName)
            {
                var status = PInvoke.NetUserGetInfo(default, new PCWSTR(name), 1, &buffer);
                if (status != 0) throw Failure((uint)status);
                if (buffer is null) throw new IOException("Windows returned no account flags.");
                var info = (USER_INFO_1*)buffer;
                if (info->usri1_priv != USER_PRIV.USER_PRIV_USER) throw new IOException("The managed account is not a standard user.");
                if ((info->usri1_flags & USER_ACCOUNT_FLAGS.UF_ACCOUNTDISABLE) == 0) return;
                var flags = new USER_INFO_1008 { usri1008_flags = info->usri1_flags & ~USER_ACCOUNT_FLAGS.UF_ACCOUNTDISABLE };
                RequireSid(expectedSid);
                uint parameter = 0;
                status = PInvoke.NetUserSetInfo(default, new PCWSTR(name), 1008, (byte*)&flags, &parameter);
                if (status != 0) throw Failure((uint)status);
            }
        }
        finally { if (buffer is not null) PInvoke.NetApiBufferFree(buffer); }
        RequireSid(expectedSid);
        VerifyEnabled();
    }
    private static unsafe void VerifyEnabled()
    {
        byte* buffer = null;
        try
        {
            fixed (char* name = Defaults.StudentAccountName)
            {
                var status = PInvoke.NetUserGetInfo(default, new PCWSTR(name), 1, &buffer);
                if (status != 0) throw Failure((uint)status);
            }
            if (buffer is null || ((USER_INFO_1*)buffer)->usri1_priv != USER_PRIV.USER_PRIV_USER
                || (((USER_INFO_1*)buffer)->usri1_flags & USER_ACCOUNT_FLAGS.UF_ACCOUNTDISABLE) != 0)
                throw new IOException("The managed account was not activated.");
        }
        finally { if (buffer is not null) PInvoke.NetApiBufferFree(buffer); }
    }
    private static unsafe List<string> Groups()
    {
        byte* buffer = null;
        try
        {
            uint read = 0, total = 0;
            fixed (char* name = Defaults.StudentAccountName)
            {
                var status = PInvoke.NetUserGetLocalGroups(default, new PCWSTR(name), 0, 1, &buffer, uint.MaxValue, &read, &total);
                if (status != 0) throw Failure((uint)status);
            }
            if (read != total || (read > 0 && buffer is null)) throw new IOException("The account's group list is incomplete.");
            var result = new List<string>();
            for (var index = 0; index < read; index++) result.Add(((LOCALGROUP_USERS_INFO_0*)buffer)[index].lgrui0_name.ToString());
            return result;
        }
        finally { if (buffer is not null) PInvoke.NetApiBufferFree(buffer); }
    }
    private static Win32Exception Failure(uint status) => new(unchecked((int)status), $"Student account configuration failed (Windows status {status}).");
}
