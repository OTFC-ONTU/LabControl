using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Setup;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.NetManagement;

namespace LabControl.Agent;

/// <summary>Owns the Setup lock while authorizing profile writes, and supplies only
/// the recorded student's token. Never borrows a home-PC user's profile or token.</summary>
internal sealed class ManagedStudentAccess : IDisposable
{
    private readonly FileStream _lease;
    public SafeAccessTokenHandle Token { get; }
    public string Sid { get; }
    public string MaterialsDirectory { get; }
    public uint? SessionId { get; }

    private ManagedStudentAccess(FileStream lease, SafeAccessTokenHandle token, string sid, string materials, uint? sessionId)
    { _lease = lease; Token = token; Sid = sid; MaterialsDirectory = materials; SessionId = sessionId; }

    public static unsafe ManagedStudentAccess Open()
    {
        var directory = Defaults.AgentDataDirectory;
        RequirePrivateDirectory(directory);
        var journal = Path.Combine(directory, Defaults.InstallationFileName);
        RequirePrivateFile(journal);
        var lockFile = Path.Combine(directory, Defaults.SetupLockFileName);
        if (File.Exists(lockFile)) RequirePrivateFile(lockFile);
        var lease = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        SafeAccessTokenHandle? token = null;
        try
        {
            var state = new InstallationState(directory);
            // Opt-out and legacy installs fail before SAM or token access.
            if (state.Read()?.CreateStudentAccount != true)
                throw new InvalidOperationException("This installation has no managed student account. Handout delivery is unavailable.");
            var sid = state.RequireManagedStudent(FindStudentSid());
            var session = InteractiveSession.Snapshot();
            uint? sessionId = null;
            HANDLE native = default;
            if (session.SessionId is { } id && PInvoke.WTSQueryUserToken(id, ref native))
            {
                token = new SafeAccessTokenHandle((nint)native.Value);
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                if (identity.User?.Value == sid) sessionId = id;
                else { token.Dispose(); token = null; }
            }
            if (token is null)
            {
                fixed (char* name = Defaults.StudentAccountName)
                fixed (char* domain = Environment.MachineName)
                fixed (char* password = Defaults.StudentDefaultPassword)
                {
                    if (!PInvoke.LogonUser(name, domain, password,
                        Windows.Win32.Security.LOGON32_LOGON.LOGON32_LOGON_INTERACTIVE,
                        Windows.Win32.Security.LOGON32_PROVIDER.LOGON32_PROVIDER_DEFAULT, &native))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "The managed student token is unavailable; sign in as the managed student and retry.");
                    token = new SafeAccessTokenHandle((nint)native.Value);
                }
            }
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                RequireStandardIdentity(identity, sid);
            }
            var desktop = Desktop(token);
            HandoutDelivery.RejectReparseAncestors(desktop);
            return new ManagedStudentAccess(lease, token, sid, Path.Combine(desktop, Defaults.MaterialsFolderName), sessionId);
        }
        catch { token?.Dispose(); lease.Dispose(); throw; }
    }

    internal static void RequireStandardIdentity(WindowsIdentity identity, string expectedSid)
    {
        // UAC-filtered administrators have a deny-only Administrators SID: IsInRole
        // alone returns false. Such a token is not a standard student's identity.
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (identity.User?.Value != expectedSid || identity.IsSystem || identity.Groups is null
            || identity.Groups.Contains(administrators)
            || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Handouts require the recorded standard student's token.");
    }

    private static unsafe string Desktop(SafeAccessTokenHandle token)
    {
        var folder = Defaults.StudentDesktopKnownFolderId;
        PWSTR path = default;
        var result = PInvoke.SHGetKnownFolderPath(&folder, 0, (HANDLE)token.DangerousGetHandle(), &path);
        try
        {
            if (result.Failed) throw new IOException("Windows could not resolve the managed student's Desktop. Sign in as the managed student and retry.");
            var value = path.ToString();
            if (!Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal))
                throw new IOException("Handout delivery requires a local managed Desktop.");
            uint size = 0;
            PInvoke.GetUserProfileDirectory((HANDLE)token.DangerousGetHandle(), default, &size);
            if (size == 0 || size > 32768) throw new IOException("Windows could not resolve the managed student's profile.");
            var buffer = new char[size];
            fixed (char* profile = buffer)
            {
                if (!PInvoke.GetUserProfileDirectory((HANDLE)token.DangerousGetHandle(), profile, &size))
                    throw new IOException("Windows could not resolve the managed student's profile.");
                var root = Path.GetFullPath(new string(profile)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(value).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The managed Desktop must remain inside the managed student's profile.");
            }
            return value;
        }
        finally { Marshal.FreeCoTaskMem((nint)path.Value); }
    }

    private static unsafe string? FindStudentSid()
    {
        byte* buffer = null;
        try
        {
            fixed (char* name = Defaults.StudentAccountName)
            {
                var status = PInvoke.NetUserGetInfo(default, name, 23, &buffer);
                if ((uint)status == PInvoke.NERR_UserNotFound) return null;
                if (status != 0) throw new Win32Exception((int)status, "The local student account could not be verified.");
            }
            if (buffer is null) throw new InvalidDataException("Windows returned no student account information.");
            return new SecurityIdentifier((nint)((USER_INFO_23*)buffer)->usri23_user_sid.Value).Value;
        }
        finally { if (buffer is not null) PInvoke.NetApiBufferFree(buffer); }
    }

    public static void RequirePrivateDirectory(string directory)
    {
        HandoutDelivery.RejectReparseAncestors(directory);
        RequirePrivate(new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
    }

    private static void RequirePrivateFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Installation ownership storage must be a regular file.");
        RequirePrivate(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
    }

    private static void RequirePrivate(FileSystemSecurity security)
    {
        static bool Trusted(IdentityReference? sid) => sid is SecurityIdentifier s &&
            (s.IsWellKnown(WellKnownSidType.LocalSystemSid) || s.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("Installation storage has an untrusted owner.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType != AccessControlType.Allow || !Trusted(rule.IdentityReference))
                throw new UnauthorizedAccessException("Installation storage must be private to SYSTEM and Administrators.");
    }

    public void Dispose() { Token.Dispose(); _lease.Dispose(); }
}
