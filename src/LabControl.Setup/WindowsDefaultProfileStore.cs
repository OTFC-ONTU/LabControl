using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Setup;
using Windows.Win32;
using Windows.Win32.Security;

namespace LabControl.Setup;

/// <summary>Only the OS-resolved Default profile. Existing files are never replaced;
/// every touched directory must deny untrusted writes, so a student cannot race SYSTEM.</summary>
internal sealed class WindowsDefaultProfileStore : IProfileTemplateStore
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private string? _root;
    public string RootIdentity => (_root ??= DefaultProfile()).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();

    public byte[]? ReadFile(string relativePath)
    {
        var path = Resolve(relativePath, false);
        if (Attributes(path) is null) return null;
        RequireNode(path, directory: false);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > Defaults.ProfileTemplateMaxFileBytes) throw new IOException("An existing template destination exceeds the size limit.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }
    public bool DirectoryExists(string relativePath)
    {
        var path = Resolve(relativePath, true);
        if (Attributes(path) is null) return false;
        RequireNode(path, directory: true);
        return true;
    }
    public void CreateFile(string relativePath, byte[] content)
    {
        if (content.Length > Defaults.ProfileTemplateMaxFileBytes) throw new InvalidDataException("Template file exceeds the size limit.");
        var target = Resolve(relativePath, false);
        if (Attributes(target) is not null) throw new IOException("The template destination appeared before creation.");
        // A crashed copy must not leave a partial document in the shared Default profile.
        // Require one volume so the final move is atomic; staging remains in private data.
        var staging = Path.Combine(Defaults.AgentDataDirectory, Defaults.ProfileTemplateStagingDirectoryName);
        HandoutDelivery.RejectReparseAncestors(staging);
        Directory.CreateDirectory(staging);
        RequireNode(staging, true);
        var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(content);
                file.Flush(flushToDisk: true);
            }
            var security = new FileSecurity();
            security.SetOwner(AdminSid);
            security.SetAccessRuleProtection(false, false);
            security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(AdminSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            new FileInfo(temporary).SetAccessControl(security);
            _ = Resolve(relativePath, false);
            File.Move(temporary, target, overwrite: false);
            // Persist clears FileSecurity's modified flags. A fresh object is required:
            // reusing security here would be a no-op and retain staging inheritance.
            var finalSecurity = new FileSecurity();
            finalSecurity.SetSecurityDescriptorBinaryForm(security.GetSecurityDescriptorBinaryForm(),
                AccessControlSections.Access | AccessControlSections.Owner);
            new FileInfo(target).SetAccessControl(finalSecurity);
            RequireNode(target, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void RemoveFile(string relativePath, byte[] expected)
    {
        var current = ReadFile(relativePath);
        if (current is null || !current.AsSpan().SequenceEqual(expected)) throw new IOException("The template file changed before removal.");
        File.Delete(Resolve(relativePath, false));
    }
    public unsafe void CreateDirectory(string relativePath)
    {
        var path = Resolve(relativePath, true);
        if (Attributes(path) is not null) throw new IOException("The template directory appeared before creation.");
        var security = new DirectorySecurity();
        security.SetOwner(AdminSid);
        security.SetAccessRuleProtection(false, false);
        foreach (var sid in new[] { SystemSid, AdminSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var bytes = security.GetSecurityDescriptorBinaryForm();
        fixed (byte* descriptor = bytes)
        fixed (char* name = path)
        {
            var attributes = new SECURITY_ATTRIBUTES { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = descriptor };
            if (!PInvoke.CreateDirectory(name, &attributes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The Default-profile template directory could not be created.");
        }
        RequireNode(path, true);
    }
    public void RemoveEmptyDirectory(string relativePath)
    {
        var path = Resolve(relativePath, true);
        RequireNode(path, true);
        Directory.Delete(path, recursive: false);
    }
    private string Resolve(string relativePath, bool directory)
    {
        var normalized = ProfileTemplateArchive.NormalizePath(relativePath, directory);
        _root ??= DefaultProfile();
        if (!string.Equals(Path.GetPathRoot(_root), Path.GetPathRoot(Defaults.AgentDataDirectory), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The optional profile template requires Default profile and LabControl data on the same volume.");
        HandoutDelivery.RejectReparseAncestors(_root);
        RequireNode(_root, true);
        var path = _root;
        var parts = normalized.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            path = Path.Combine(path, parts[i]);
            if (Attributes(path) is not null) RequireNode(path, i < parts.Length - 1 || directory);
        }
        return path;
    }
    private static unsafe string DefaultProfile()
    {
        uint length = 0;
        PInvoke.GetDefaultUserProfileDirectory(default, &length);
        if (length is 0 or > 32768) throw new IOException("Windows could not locate the Default profile.");
        var buffer = new char[length];
        fixed (char* path = buffer)
        {
            if (!PInvoke.GetDefaultUserProfileDirectory(path, &length))
                throw new IOException("Windows could not locate the Default profile.");
            var result = new string(path);
            if (!Path.IsPathFullyQualified(result) || result.StartsWith("\\\\", StringComparison.Ordinal))
                throw new IOException("The Default profile must be local.");
            return Path.GetFullPath(result);
        }
    }
    private static void RequireNode(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("Default-profile template paths cannot contain links or unexpected file types.");
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        static bool Trusted(IdentityReference? sid) => SystemSid.Equals(sid) || AdminSid.Equals(sid);
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("Default-profile template paths must be owned by SYSTEM or Administrators.");
        const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow
                && !Trusted(rule.IdentityReference) && (rule.FileSystemRights & write) != 0)
                throw new UnauthorizedAccessException("Default-profile template paths must not be writable by other users.");
    }
    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
