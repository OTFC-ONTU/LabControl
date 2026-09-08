using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Setup;

/// <summary>Test-only: request normal UAC from the exact managed student's session.
/// Never enters credentials, invokes consent controls, or changes UAC policy.</summary>
internal static class StudentUacFixture
{
    private const string Prefix = "LabControl.StudentUacFixture.";
    public static int Prompt(string expectedSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem || identity.User?.Value != expectedSid || Process.GetCurrentProcess().SessionId == 0
            || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            || identity.Groups?.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) == true) return 73;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--uac-inert")
            { UseShellExecute = true, Verb = "runas" });
            return process is null ? 1 : 0;
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223) { return 0; } // Normal operator cancellation.
    }
    public static int Schedule(string installationId)
    {
        if (!Guid.TryParseExact(installationId, "D", out _)) return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) throw new UnauthorizedAccessException();
        CheckNode(Defaults.AgentDataDirectory, true);
        CheckNode(Path.Combine(Defaults.AgentDataDirectory, Defaults.InstallationFileName), false);
        var lockPath = Path.Combine(Defaults.AgentDataDirectory, Defaults.SetupLockFileName);
        CheckNode(lockPath, false);
        using var lease = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var state = new InstallationState(Defaults.AgentDataDirectory).Read();
        if (state?.InstallationId != installationId || state.CreateStudentAccount != true || state.CreatedStudentSid is null
            || state.RemovalReady) throw new IOException("The expected managed installation is absent.");
        var session = WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue || !WTSQueryUserToken(session, out var token)) throw new IOException("No active student console token.");
        try
        {
            using var student = new WindowsIdentity(token);
            if (student.User?.Value != state.CreatedStudentSid || new WindowsPrincipal(student).IsInRole(WindowsBuiltInRole.Administrator)
                || student.Groups?.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) == true)
                throw new UnauthorizedAccessException("The active console user is not the recorded standard student.");
        }
        finally { CloseHandle(token); }
        var fixture = Guid.NewGuid().ToString("D");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Prefix + fixture);
        HandoutDelivery.RejectReparseAncestors(root);
        if (Directory.Exists(root)) throw new IOException();
        var acl = new DirectorySecurity();
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        acl.SetAccessRuleProtection(true, false);
        foreach (var kind in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(kind, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(state.CreatedStudentSid), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).Create(acl);
        CheckNode(Environment.ProcessPath!, false);
        var executable = Path.Combine(root, "LabControl.Setup.NativeSmoke.exe");
        File.Copy(Environment.ProcessPath!, executable, false);
        var taskName = Prefix + fixture;
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder("\\");
        dynamic definition = scheduler.NewTask(0);
        definition.Principal.UserId = state.CreatedStudentSid;
        definition.Principal.LogonType = 3; // Existing interactive user token; no password is stored.
        definition.Principal.RunLevel = 0; // LIMITED user: the UAC request must originate as student.
        definition.Settings.ExecutionTimeLimit = "PT3M";
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        dynamic action = definition.Actions.Create(0);
        action.Path = executable; action.Arguments = "--uac-prompt " + state.CreatedStudentSid; action.WorkingDirectory = root;
        dynamic task = folder.RegisterTaskDefinition(taskName, definition, 2, state.CreatedStudentSid, null, 3, null);
        var xml = (string)task.Xml;
        using (var output = new FileStream(Path.Combine(root, "task.xml"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { output.Write(System.Text.Encoding.UTF8.GetBytes(xml)); output.Flush(true); }
        _ = task.Run(null);
        Console.WriteLine(JsonSerializer.Serialize(new { scheduled = true, fixture_id = fixture,
            standard_student_token_verified = true, credentials_entered = false, authentication_proven = false }));
        return 0;
    }
    public static int Cleanup(string fixtureId)
    {
        if (!Guid.TryParseExact(fixtureId, "D", out var id)) return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) throw new UnauthorizedAccessException();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Prefix + id.ToString("D"));
        CheckNode(root, true);
        var path = Path.Combine(root, "task.xml"); CheckNode(path, false);
        if (new FileInfo(path).Length > 65536) throw new IOException();
        var xml = File.ReadAllText(path);
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        scheduler.Connect(); dynamic folder = scheduler.GetFolder("\\");
        dynamic task = folder.GetTask(Prefix + id.ToString("D"));
        if ((string)task.Xml != xml || (int)task.State == 4) throw new IOException("Cancel UAC normally and wait for the exact fixture task to finish.");
        folder.DeleteTask(Prefix + id.ToString("D"), 0);
        Console.WriteLine("{\"cleaned\":true,\"evidence_retained\":true}");
        return 0;
    }
    private static void CheckNode(string path, bool directory)
    {
        HandoutDelivery.RejectReparseAncestors(path);
        if (((File.GetAttributes(path) & FileAttributes.Directory) != 0) != directory) throw new IOException();
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        static bool Trusted(IdentityReference sid) => sid is SecurityIdentifier s &&
            (s.IsWellKnown(WellKnownSidType.LocalSystemSid) || s.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier))!)) throw new UnauthorizedAccessException();
        var writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !Trusted(rule.IdentityReference) && (rule.FileSystemRights & writes) != 0)
                throw new UnauthorizedAccessException();
    }
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
