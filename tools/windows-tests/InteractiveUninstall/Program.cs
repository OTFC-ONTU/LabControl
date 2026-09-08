using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LabControl.Setup;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Setup;

return Fixture.Run(args);

internal static class Fixture
{
    private const int IdYes = 6;
    private sealed record Request(string InstallationId, uint SessionId, string ConsoleSid, bool SystemDesktop,
        bool RemoveStudent, bool ConfirmRemoveStudent, string Executable, string Sha256, bool PendingStudentRetry = false);
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length < 2 || !Guid.TryParseExact(args[1], "D", out _)
            || args[0] is not ("--disposable-vm" or "--system-desktop" or "--pending-student-retry" or "--desktop")) return 2;
        var remove = args.Length == 4 && args[2] == "--remove-student" && args[3] == "--confirm-remove-student";
        if (args.Length != 2 && !remove || args[0] is "--desktop" or "--pending-student-retry" && args.Length != 2) return 2;
        try { return args[0] == "--desktop" ? Desktop(args[1]) : Schedule(args[1], args[0] is "--system-desktop" or "--pending-student-retry", remove, args[0] == "--pending-student-retry"); }
        catch (Exception error)
        {
            System.Console.Error.WriteLine("Interactive uninstall fixture failed: " + error.GetType().Name);
            return 1;
        }
    }
    private static string RequireInstallation(string id)
    {
        using var scope = AccountSetupScope.Open();
        var state = new InstallationState(Defaults.AgentDataDirectory).Read();
        if (state?.InstallationId != id || state.RemovalReady) throw new IOException("Fixture installation identity differs.");
        SetupInstallationFiles.RequireOwnership(id);
        var executable = Path.Combine(Defaults.AgentInstallDirectory, Defaults.UninstallExecutableName);
        if (!File.Exists(executable)) throw new IOException("Standalone uninstaller is absent.");
        return executable;
    }
    private static int Schedule(string id, bool systemDesktop, bool removeStudent, bool pendingStudentRetry)
    {
        var uninstall = RequireInstallation(id);
        using var caller = WindowsIdentity.GetCurrent();
        if (!caller.IsSystem) throw new UnauthorizedAccessException("The scheduler phase requires the isolated VM SYSTEM guest agent.");
        var session = WTSGetActiveConsoleSessionId();
        if (session is 0 or uint.MaxValue) throw new IOException("No valid active console session.");
        string sid;
        if (pendingStudentRetry)
        {
            if (!systemDesktop || removeStudent) throw new ArgumentException();
            RequirePendingStudentRemoval(id);
            RequireNoLoggedOnUser(session);
            sid = "";
        }
        else
        {
            if (!WTSQueryUserToken(session, out var token)) throw new IOException("No active console token.");
            try
            {
                using var user = new WindowsIdentity(token);
                var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                if (user.User is null || (!systemDesktop && (!user.Name.StartsWith(Environment.MachineName + "\\", StringComparison.OrdinalIgnoreCase)
                    || user.Groups is null || !user.Groups.Contains(admin))))
                    throw new UnauthorizedAccessException("The active console user is not a local administrator.");
                sid = user.User.Value;
            }
            finally { CloseHandle(token); }
        }
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LabControl.InteractiveUninstall." + Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        foreach (var kind in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(kind, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).Create(security);
        RequirePrivate(Environment.ProcessPath!, false);
        var executable = Path.Combine(directory, "LabControl.InteractiveUninstall.exe");
        File.Copy(Environment.ProcessPath!, executable, false);
        var request = new Request(id, session, sid, systemDesktop, removeStudent, removeStudent, uninstall, FileHash.Sha256HexOfFile(uninstall), pendingStudentRetry);
        using (var output = new FileStream(Path.Combine(directory, "request.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { output.Write(JsonSerializer.SerializeToUtf8Bytes(request)); output.Flush(true); }
        if (systemDesktop) return RunSystemDesktop(executable, directory, request);
        var resultPath = Path.Combine(directory, "result.json");
        var name = "LabControl isolated uninstall " + Guid.NewGuid().ToString("N");
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder("\\");
        dynamic definition = scheduler.NewTask(0);
        definition.Principal.UserId = sid;
        definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN: never stores a password.
        definition.Principal.RunLevel = 1;
        definition.Settings.ExecutionTimeLimit = "PT5M";
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        dynamic action = definition.Actions.Create(0);
        action.Path = executable;
        action.Arguments = "--desktop " + id;
        action.WorkingDirectory = directory;
        dynamic task = folder.RegisterTaskDefinition(name, definition, 2, sid, null, 3, null); // TASK_CREATE only.
        string registered = task.Xml;
        try
        {
            _ = task.Run(null);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(resultPath) && timer.Elapsed < TimeSpan.FromMinutes(4)) Thread.Sleep(250);
            if (!File.Exists(resultPath)) throw new IOException("Interactive fixture did not return a result.");
            var json = File.ReadAllText(resultPath);
            System.Console.WriteLine(json);
            using var result = JsonDocument.Parse(json);
            return result.RootElement.GetProperty("ok").GetBoolean() ? 0 : 1;
        }
        finally
        {
            // Preserve an externally changed task instead of deleting it blindly.
            dynamic current = folder.GetTask(name);
            if ((string)current.Xml == registered) folder.DeleteTask(name, 0);
        }
    }
    private static int Desktop(string id)
    {
        var resultPath = Path.Combine(AppContext.BaseDirectory, "result.json");
        bool clicked = false;
        int? exitCode = null;
        string? error = null;
        Process? ownedChild = null;
        bool terminatedOwnedChild = false;
        try
        {
            var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            RequirePrivate(directory, true);
            var requestPath = Path.Combine(directory, "request.json"); RequirePrivate(requestPath, false);
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath)) ?? throw new IOException();
            using var caller = WindowsIdentity.GetCurrent();
            if (request.InstallationId != id || request.SessionId != Process.GetCurrentProcess().SessionId
                || request.SystemDesktop != caller.IsSystem || (!request.SystemDesktop && caller.User?.Value != request.ConsoleSid)
                || request.RemoveStudent != request.ConfirmRemoveStudent)
                throw new UnauthorizedAccessException("The fixture execution identity differs from its private request.");
            RequireActiveConsole(request);
            if (Process.GetCurrentProcess().SessionId == 0) throw new IOException("Interactive session is required.");
            var executable = RequireInstallation(id);
            if (request.Executable != executable || FileHash.Sha256HexOfFile(executable) != request.Sha256) throw new IOException("Owned uninstaller changed.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Defaults.AgentInstallDirectory,
                RedirectStandardOutput = request.SystemDesktop, RedirectStandardError = request.SystemDesktop };
            start.ArgumentList.Add("--uninstall");
            if (request.RemoveStudent) { start.ArgumentList.Add("--remove-student"); start.ArgumentList.Add("--confirm-remove-student"); }
            var process = ownedChild = Process.Start(start) ?? throw new IOException("Uninstaller did not start.");
            using var stdout = request.SystemDesktop ? new FileStream(Path.Combine(directory, "stdout.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read) : null;
            using var stderr = request.SystemDesktop ? new FileStream(Path.Combine(directory, "stderr.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read) : null;
            var output = stdout is null ? Task.CompletedTask : process.StandardOutput.BaseStream.CopyToAsync(stdout);
            var errors = stderr is null ? Task.CompletedTask : process.StandardError.BaseStream.CopyToAsync(stderr);
            var timer = Stopwatch.StartNew();
            while (!process.HasExited && timer.Elapsed < TimeSpan.FromMinutes(2))
            {
                if (File.Exists(Path.Combine(directory, "cancel.request"))) throw new OperationCanceledException();
                if (!clicked)
                {
                    RequireActiveConsole(request);
                    var windows = new List<IntPtr>();
                    EnumWindows((window, _) =>
                    {
                        GetWindowThreadProcessId(window, out var pid);
                        if (pid != process.Id || !IsWindowVisible(window)) return true;
                        var className = new StringBuilder(256); GetClassName(window, className, className.Capacity);
                        var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
                        if (className.ToString() == "#32770" && title.ToString() == SetupDialog.Text("Title")) windows.Add(window);
                        return true;
                    }, IntPtr.Zero);
                    if (windows.Count > 1) throw new IOException("Multiple matching dialogs exist.");
                    if (windows.Count == 1)
                    {
                        if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Uninstaller executable changed.");
                        var button = GetDlgItem(windows[0], IdYes);
                        if (button == IntPtr.Zero || !IsWindowVisible(button) || !IsWindowEnabled(button))
                            throw new IOException("Expected confirmation button absent.");
                        if (SendMessageTimeout(button, 0x00F5, IntPtr.Zero, IntPtr.Zero, 2, 5_000, out _) == IntPtr.Zero)
                            throw new IOException("The exact confirmation button did not respond.");
                        clicked = true; // BM_CLICK exact IDYES only.
                    }
                }
                Thread.Sleep(100);
            }
            if (!process.HasExited) throw new IOException("Uninstaller did not finish.");
            if (!Task.WhenAll(output, errors).Wait(TimeSpan.FromSeconds(10)))
                throw new IOException("Uninstaller output streams did not close after process exit.");
            exitCode = process.ExitCode;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name;
            if (ownedChild is not null)
            {
                try { if (!ownedChild.HasExited) { ownedChild.Kill(); ownedChild.WaitForExit(5000); terminatedOwnedChild = ownedChild.HasExited; } }
                catch (Exception) { error += ":owned-child-cleanup-failed"; }
            }
        }
        finally { ownedChild?.Dispose(); }
        var ok = clicked && exitCode == 0 && error is null;
        var temporary = resultPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { ok, clicked, exit_code = exitCode, error, terminated_owned_child = terminatedOwnedChild }));
        File.Move(temporary, resultPath, false);
        return ok ? 0 : 1;
    }
    private static void RequireActiveConsole(Request request)
    {
        if (request.SessionId == 0 || WTSGetActiveConsoleSessionId() != request.SessionId)
            throw new IOException("The expected interactive console is unavailable.");
        using var caller = WindowsIdentity.GetCurrent();
        if (request.PendingStudentRetry)
        {
            if (!caller.IsSystem || !request.SystemDesktop || request.RemoveStudent || request.ConfirmRemoveStudent || request.ConsoleSid.Length != 0)
                throw new UnauthorizedAccessException("Only the explicit SYSTEM pending-removal retry may have no user.");
            RequirePendingStudentRemoval(request.InstallationId);
            RequireNoLoggedOnUser(request.SessionId);
            return;
        }
        if (!caller.IsSystem)
        {
            if (request.SystemDesktop || caller.User?.Value != request.ConsoleSid
                || Process.GetCurrentProcess().SessionId != request.SessionId)
                throw new UnauthorizedAccessException("The expected administrator console changed.");
            return; // WTSQueryUserToken requires SYSTEM; preserve the existing administrator path.
        }
        if (!WTSQueryUserToken(request.SessionId, out var token)) throw new IOException("The active console token is unavailable.");
        try
        {
            using var user = new WindowsIdentity(token);
            if (user.User?.Value != request.ConsoleSid) throw new IOException("The active console user changed.");
        }
        finally { CloseHandle(token); }
    }
    private static void RequirePendingStudentRemoval(string installationId)
    {
        using var scope = AccountSetupScope.Open();
        var state = new InstallationState(Defaults.AgentDataDirectory).Read();
        if (state?.InstallationId != installationId || state.CreateStudentAccount != true
            || !state.StudentRemovalPending || state.CreatedStudentSid is null || state.StudentRemoved || state.RemovalReady)
            throw new IOException("A durable pending owned-student removal is required for the no-user retry.");
    }
    private static void RequireNoLoggedOnUser(uint session)
    {
        if (session is 0 or uint.MaxValue || WTSGetActiveConsoleSessionId() != session) throw new IOException();
        if (WTSQueryUserToken(session, out var token))
        {
            CloseHandle(token);
            throw new IOException("A user remains logged on; no-user retry refused.");
        }
        if (Marshal.GetLastWin32Error() != 1008) // ERROR_NO_TOKEN only; privilege/provider failures are not evidence of logoff.
            throw new IOException("Windows did not confirm absence of an interactive user token.");
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 5, out var buffer, out var bytes)) // WTSUserName
            throw new IOException("Windows could not verify the logged-off session.");
        try
        {
            if (buffer == IntPtr.Zero || bytes < sizeof(char) || bytes > 2048 || bytes % sizeof(char) != 0
                || (Marshal.PtrToStringUni(buffer, checked((int)bytes / sizeof(char))) ?? "").TrimEnd('\0').Length != 0)
                throw new IOException("The console session still reports a user name.");
        }
        finally { WTSFreeMemory(buffer); }
    }

    private static int RunSystemDesktop(string executable, string directory, Request request)
    {
        using var caller = WindowsIdentity.GetCurrent();
        if (!caller.IsSystem || !request.SystemDesktop) throw new UnauthorizedAccessException();
        RequireActiveConsole(request);
        RequirePrivate(executable, false);
        IntPtr ownToken = IntPtr.Zero, primary = IntPtr.Zero, environment = IntPtr.Zero;
        var child = new ProcessInformation();
        bool resultRead = false;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x000A, out ownToken)
                || !DuplicateTokenEx(ownToken, 0x000F01FF, IntPtr.Zero, 1, 1, out primary)) throw new System.ComponentModel.Win32Exception();
            var session = request.SessionId;
            if (!SetTokenInformation(primary, 12, ref session, sizeof(uint))) throw new System.ComponentModel.Win32Exception(); // TokenSessionId
            using (var duplicated = new WindowsIdentity(primary))
                if (!duplicated.IsSystem) throw new UnauthorizedAccessException();
            if (!GetTokenInformation(primary, 12, out var confirmedSession, sizeof(uint), out _) || confirmedSession != session)
                throw new IOException("The duplicated token session was not verified.");
            if (!CreateEnvironmentBlock(out environment, primary, false)) throw new System.ComponentModel.Win32Exception();
            RequireActiveConsole(request);
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            var command = new StringBuilder("\"" + executable + "\" --desktop " + request.InstallationId);
            if (!CreateProcessAsUser(primary, executable, command, IntPtr.Zero, IntPtr.Zero, false,
                0x00000400 | 0x08000000, environment, directory, ref startup, out child)) // Unicode environment; no separate console window.
                throw new System.ComponentModel.Win32Exception();
            var resultPath = Path.Combine(directory, "result.json");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(resultPath) && timer.Elapsed < TimeSpan.FromMinutes(4))
            {
                if (WaitForSingleObject(child.Process, 0) == 0) break;
                Thread.Sleep(250);
            }
            if (!File.Exists(resultPath)) throw new IOException("The SYSTEM desktop fixture did not finish; cancellation requested.");
            RequirePrivate(resultPath, false);
            if (new FileInfo(resultPath).Length > 16384) throw new IOException();
            var json = File.ReadAllText(resultPath);
            resultRead = true;
            Console.WriteLine("Fixture output: " + directory);
            Console.WriteLine(json);
            using var result = JsonDocument.Parse(json);
            return result.RootElement.GetProperty("ok").GetBoolean() ? 0 : 1;
        }
        finally
        {
            if (child.Process != IntPtr.Zero)
            {
                if (!resultRead && WaitForSingleObject(child.Process, 0) != 0)
                {
                    // The child retains its exact uninstaller Process handle. Let its
                    // normal failure cleanup kill that process before exiting itself.
                    File.WriteAllText(Path.Combine(directory, "cancel.request"), "cancel");
                    if (WaitForSingleObject(child.Process, 20000) != 0)
                        Console.Error.WriteLine("Owned desktop helper has not acknowledged cancellation; inspect this fixture before retrying.");
                }
                CloseHandle(child.Process);
            }
            if (child.Thread != IntPtr.Zero) CloseHandle(child.Thread);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (ownToken != IntPtr.Zero) CloseHandle(ownToken);
        }
    }
    private static void RequirePrivate(string path, bool directory)
    {
        HandoutDelivery.RejectReparseAncestors(path);
        if (((File.GetAttributes(path) & FileAttributes.Directory) != 0) != directory) throw new IOException();
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        static bool Trusted(IdentityReference sid) => sid is SecurityIdentifier value &&
            (value.IsWellKnown(WellKnownSidType.LocalSystemSid) || value.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier))!)) throw new UnauthorizedAccessException();
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType != AccessControlType.Allow || !Trusted(rule.IdentityReference)) throw new UnauthorizedAccessException();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, Width, Height, Columns, Rows, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize; public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetTokenInformation(IntPtr token, int informationClass, ref uint information, int size);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(IntPtr token, int informationClass, out uint information, int size, out int required);
    [DllImport("userenv.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr data);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int information, out IntPtr buffer, out uint bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
