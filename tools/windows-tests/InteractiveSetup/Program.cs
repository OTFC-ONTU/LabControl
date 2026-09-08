using System.Text;
using System.Resources;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Setup;
using Microsoft.Win32;

return Fixture.Run(args);

internal static class Fixture
{
    private const string Prefix = "LabControl.InteractiveSetup.";
    private sealed record Request(string Payload, string Executable, string Sha256, int Number, string AdminSid, int? InspectPid = null);
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        try
        {
            if (args is ["--desktop"]) return Desktop();
            if (args is ["--disposable-vm", "--payload", var inspectionPayload, "--inspect-pid", var processId]
                && int.TryParse(processId, out var inspectionPid) && inspectionPid > 0)
                return Schedule(inspectionPayload, 1, inspectionPid);
            if (args is not ["--disposable-vm", "--payload", var payload, "--number", var number]
                || !int.TryParse(number, out var pc) || pc is < 1 or > Defaults.MaxStudentPcs) return 2;
            return Schedule(payload, pc);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Interactive Setup fixture failed: " + error.GetType().Name);
            return 1;
        }
    }

    private static void RequireFresh()
    {
        if (Directory.Exists(Defaults.AgentInstallDirectory)
            || new InstallationState(Defaults.AgentDataDirectory).Read() is not null)
            throw new IOException("This fixture requires a fully removed previous installation.");
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Defaults.ServiceName);
        if (service is not null) throw new IOException("A LabControl service already exists.");
    }

    private static string ValidatePayload(string payload)
    {
        if (!Path.IsPathFullyQualified(payload)) throw new IOException("An absolute private payload is required.");
        RequirePrivate(payload, true);
        var executable = Path.Combine(payload, Defaults.SetupExecutableName);
        if (!File.Exists(executable)) executable = Path.Combine(Path.GetDirectoryName(payload)!, Defaults.SetupExecutableName);
        RequirePrivate(executable, false);
        // No network/ISO source or student-writable file may become the elevated action.
        var pending = new Queue<string>(); pending.Enqueue(payload);
        var count = 0;
        while (pending.TryDequeue(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 512) throw new IOException("Fixture payload is unexpectedly large.");
                var isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                RequirePrivate(path, isDirectory);
                if (isDirectory) pending.Enqueue(path);
            }
        }
        using var authority = SetupPayload.Open(payload).Authority;
        return executable;
    }

    private static int Schedule(string payload, int number, int? inspectPid = null)
    {
        using var caller = WindowsIdentity.GetCurrent();
        if (!caller.IsSystem) throw new UnauthorizedAccessException("The launcher requires the isolated VM SYSTEM guest agent.");
        if (inspectPid is null) RequireFresh();
        payload = Path.GetFullPath(payload);
        var setup = ValidatePayload(payload);
        var session = WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue || !WTSQueryUserToken(session, out var token)) throw new IOException("No active console token.");
        string sid;
        try
        {
            using var user = new WindowsIdentity(token);
            var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (user.User is null || !user.Name.StartsWith(Environment.MachineName + "\\", StringComparison.OrdinalIgnoreCase)
                || user.Groups is null || !user.Groups.Contains(admin))
                throw new UnauthorizedAccessException("The active console user is not a local administrator.");
            sid = user.User.Value;
        }
        finally { CloseHandle(token); }
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Prefix + Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        foreach (var kind in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(kind, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).Create(security);
        var executable = Path.Combine(directory, "LabControl.InteractiveSetup.exe");
        File.Copy(Environment.ProcessPath!, executable, false);
        File.WriteAllText(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new Request(payload, setup, FileHash.Sha256HexOfFile(setup), number, sid, inspectPid)));
        var resultPath = Path.Combine(directory, "result.json");
        var name = Prefix + Guid.NewGuid().ToString("N");
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder("\\");
        dynamic definition = scheduler.NewTask(0);
        definition.Principal.UserId = sid;
        definition.Principal.LogonType = 3;
        definition.Principal.RunLevel = 1;
        definition.Settings.ExecutionTimeLimit = "PT10M";
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        dynamic action = definition.Actions.Create(0);
        action.Path = executable;
        action.Arguments = "--desktop";
        action.WorkingDirectory = directory;
        dynamic task = folder.RegisterTaskDefinition(name, definition, 2, sid, null, 3, null);
        string registered = task.Xml;
        try
        {
            _ = task.Run(null);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(resultPath) && timer.Elapsed < TimeSpan.FromMinutes(9)) Thread.Sleep(250);
            Console.WriteLine("Fixture output: " + directory);
            if (!File.Exists(resultPath)) throw new IOException("Interactive fixture did not return a result.");
            var json = File.ReadAllText(resultPath);
            Console.WriteLine(json);
            using var result = JsonDocument.Parse(json);
            return result.RootElement.GetProperty("ok").GetBoolean() ? 0 : 1;
        }
        finally
        {
            dynamic current = folder.GetTask(name);
            if ((string)current.Xml == registered) folder.DeleteTask(name, 0);
        }
    }

    private static int Desktop()
    {
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.GetFileName(directory).StartsWith(Prefix, StringComparison.Ordinal)) throw new IOException("Unexpected fixture directory.");
        RequirePrivate(directory, true);
        var resultPath = Path.Combine(directory, "result.json");
        int? exitCode = null;
        string? error = null;
        Process? ownedProcess = null;
        bool terminatedOwnedChild = false;
        try
        {
            using var caller = WindowsIdentity.GetCurrent();
            if (Process.GetCurrentProcess().SessionId == 0 || !new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("An elevated interactive administrator is required.");
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(Path.Combine(directory, "request.json")))
                ?? throw new IOException("Missing fixture request.");
            if (caller.User?.Value != request.AdminSid) throw new UnauthorizedAccessException("The active administrator changed.");
            if (request.InspectPid is null) RequireFresh();
            if (ValidatePayload(request.Payload) != request.Executable || FileHash.Sha256HexOfFile(request.Executable) != request.Sha256)
                throw new IOException("The Setup executable changed before launch.");
            if (request.InspectPid is { } inspectPid)
            {
                using var inspected = Process.GetProcessById(inspectPid);
                if (inspected.SessionId != Process.GetCurrentProcess().SessionId
                    || !string.Equals(inspected.MainModule?.FileName, request.Executable, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The inspected process is not the expected interactive Setup.");
                InspectExistingDialog(inspected, directory);
                File.WriteAllText(resultPath, "{\"ok\":true,\"inspection_only\":true}");
                return 0;
            }
            var start = new ProcessStartInfo(request.Executable)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(request.Executable)!,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("--payload"); start.ArgumentList.Add(request.Payload);
            start.ArgumentList.Add("--no-reboot");
            var process = ownedProcess = Process.Start(start) ?? throw new IOException("Setup did not start.");
            using var stdout = new FileStream(Path.Combine(directory, "stdout.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var stderr = new FileStream(Path.Combine(directory, "stderr.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var output = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            var errors = process.StandardError.BaseStream.CopyToAsync(stderr);
            CompleteInitialDialog(process, request, directory);
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(8).TotalMilliseconds)) throw new IOException("Setup has not finished; inspect before retrying.");
            Task.WhenAll(output, errors).GetAwaiter().GetResult();
            exitCode = process.ExitCode;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name;
            // Own only the process handle returned by our launch. Never kill an inspected dialog.
            if (ownedProcess is not null)
            {
                try
                {
                    if (!ownedProcess.HasExited) { ownedProcess.Kill(); ownedProcess.WaitForExit(5000); terminatedOwnedChild = ownedProcess.HasExited; }
                }
                catch (Exception) { error += ":owned-child-cleanup-failed"; }
            }
        }
        finally { ownedProcess?.Dispose(); }
        var ok = exitCode == 0 && error is null;
        File.WriteAllText(resultPath + ".tmp", JsonSerializer.Serialize(new { ok, exit_code = exitCode, error, terminated_owned_child = terminatedOwnedChild }));
        File.Move(resultPath + ".tmp", resultPath, false);
        return ok ? 0 : 1;
    }

    private static void InspectExistingDialog(Process process, string directory)
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var pid); if (pid == process.Id && IsWindowVisible(window)) windows.Add(window); return true; }, IntPtr.Zero);
        var rows = new List<object>();
        foreach (var window in windows)
        {
            var children = new List<IntPtr> { window };
            EnumChildWindows(window, (child, _) => { children.Add(child); return true; }, IntPtr.Zero);
            foreach (var child in children)
            {
                GetWindowThreadProcessId(child, out var pid);
                if (pid != process.Id) continue;
                var nativeText = new StringBuilder(512); GetWindowText(child, nativeText, nativeText.Capacity);
                string? messageError = null; string? text = null; long? check = null;
                try { text = WindowText(child); } catch (Exception error) { messageError = error.GetType().Name; }
                var label = nativeText.ToString();
                // Labels belong to this exact owned Setup process; never inspect credentials.
                var resources = new ResourceManager("LabControl.InteractiveSetup.SetupStrings", typeof(Fixture).Assembly);
                var known = new[] { "Title", "Number", "CreateStudent", "Install", "Cancel", "Changes" }
                    .FirstOrDefault(key => resources.GetString(key) == label || resources.GetString(key) == text);
                if (known == "CreateStudent")
                    try { check = Message(child, 0x00F0, IntPtr.Zero, IntPtr.Zero).ToInt64(); } catch (Exception error) { messageError = error.GetType().Name; }
                rows.Add(new { class_name = Class(child), visible = IsWindowVisible(child), enabled = IsWindowEnabled(child),
                    known_label = known, numeric_text = int.TryParse(text, out var numeric) ? (int?)numeric : null,
                    button_check_state = check, message_error = messageError });
            }
        }
        File.WriteAllText(Path.Combine(directory, "dialog-inspection.json"), JsonSerializer.Serialize(rows));
    }

    private static void CompleteInitialDialog(Process process, Request request, string directory)
    {
        var resources = new ResourceManager("LabControl.InteractiveSetup.SetupStrings", typeof(Fixture).Assembly);
        string Text(string key) => resources.GetString(key) ?? throw new IOException("Missing fixture UI label.");
        InspectExistingDialog(process, directory);
        var timer = Stopwatch.StartNew();
        while (!process.HasExited && timer.Elapsed < TimeSpan.FromSeconds(45))
        {
            var windows = new List<IntPtr>();
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                if (pid == process.Id && IsWindowVisible(window)) windows.Add(window);
                return true;
            }, IntPtr.Zero);
            windows = windows.Where(window => WindowText(window) == Text("Title")).ToList();
            if (windows.Count > 1) throw new IOException("Multiple matching Setup windows exist.");
            if (windows.Count == 1)
            {
                if (!string.Equals(process.MainModule?.FileName, request.Executable, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The Setup process image changed.");
                var dialog = windows[0];
                var children = new List<IntPtr>();
                EnumChildWindows(dialog, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out var pid);
                    if (pid == process.Id && IsWindowVisible(child)) children.Add(child);
                    return true;
                }, IntPtr.Zero);
                var accounts = children.Where(child => WindowText(child) == Text("CreateStudent") && Class(child).Contains("BUTTON", StringComparison.OrdinalIgnoreCase)).ToArray();
                var installs = children.Where(child => WindowText(child) == Text("Install") && Class(child).Contains("BUTTON", StringComparison.OrdinalIgnoreCase)).ToArray();
                var edits = children.Where(child => Class(child).Contains("EDIT", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (accounts.Length == 0 || installs.Length == 0 || edits.Length == 0) { Thread.Sleep(100); continue; }
                if (accounts.Length != 1 || installs.Length != 1 || edits.Length != 1)
                    throw new IOException("Unexpected Setup dialog controls.");
                var account = accounts[0]; var install = installs[0]; var edit = edits[0];
                if (!IsWindowEnabled(account) || !IsWindowEnabled(install) || !IsWindowEnabled(edit)) throw new IOException("Disabled Setup input.");
                var checkedByDefault = IsChecked(account); // MSAA sees WinForms owner-drawn state.
                if (!checkedByDefault) throw new IOException("Fresh Setup did not default to managed student creation.");
                if (!int.TryParse(WindowText(edit), out var initialNumber) || initialNumber is < 1 or > Defaults.MaxStudentPcs)
                    throw new IOException("Unexpected initial PC number.");
                var desired = request.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _ = Message(dialog, 0x0028, edit, new IntPtr(1));
                var buffer = Marshal.StringToHGlobalUni(desired);
                try { _ = Message(edit, 0x000C, IntPtr.Zero, buffer); } // WM_SETTEXT on the sole numeric edit.
                finally { Marshal.FreeHGlobal(buffer); }
                if (WindowText(edit) != desired) throw new IOException("PC number edit did not retain the requested value.");
                // Transfer focus so NumericUpDown commits/validates the edited value.
                _ = Message(dialog, 0x0028, install, new IntPtr(1)); // WM_NEXTDLGCTL, exact target handle.
                if (WindowText(edit) != desired || !IsChecked(account))
                    throw new IOException("Setup input changed before confirmation.");
                var evidence = new { schema = 1, dialog_observed = true, default_student_checked = checkedByDefault,
                    initial_number = initialNumber, entered_number = request.Number, exact_process_verified = true,
                    account_control_count = accounts.Length, number_edit_count = edits.Length, install_button_count = installs.Length };
                using (var output = new FileStream(Path.Combine(directory, "initial-ui.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                { output.Write(JsonSerializer.SerializeToUtf8Bytes(evidence)); output.Flush(true); }
                _ = Message(install, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK only this process's exact Install button.
                return;
            }
            Thread.Sleep(100);
        }
        throw new IOException("The real Setup number dialog was not observed.");
    }
    [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IAccessibleState
    {
        [DispId(-5007)]
        object this[[MarshalAs(UnmanagedType.Struct)] object child]
        { [return: MarshalAs(UnmanagedType.Struct)] get; }
    }
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IAccessibleState accessible);
    private static bool IsChecked(IntPtr window)
    {
        var id = typeof(IAccessibleState).GUID;
        Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(window, 0xFFFFFFFC, ref id, out var accessible)); // OBJID_CLIENT
        try
        {
            if (accessible[0] is not int state) throw new IOException("The checkbox accessibility state is unavailable.");
            return (state & 0x10) != 0 && (state & (0x1 | 0x20 | 0x8000)) == 0; // Checked; not unavailable/mixed/invisible.
        }
        finally { Marshal.ReleaseComObject(accessible); }
    }

    private static string WindowText(IntPtr window)
    {
        var buffer = Marshal.AllocHGlobal(512 * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            _ = Message(window, 0x000D, new IntPtr(512), buffer); // WM_GETTEXT also supports cross-process edit controls.
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string Class(IntPtr window)
    {
        var text = new StringBuilder(256);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }
    private static IntPtr Message(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (SendMessageTimeout(window, message, wParam, lParam, 2, 5000, out var result) == IntPtr.Zero)
            throw new IOException("The exact Setup UI control did not respond.");
        return result;
    }
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    private static void RequirePrivate(string path, bool directory)
    {
        HandoutDelivery.RejectReparseAncestors(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("Fixture source is not a regular private node.");
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        static bool Trusted(IdentityReference? sid) => sid is SecurityIdentifier s &&
            (s.IsWellKnown(WellKnownSidType.LocalSystemSid) || s.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("Untrusted fixture owner.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType != AccessControlType.Allow || !Trusted(rule.IdentityReference))
                throw new UnauthorizedAccessException("Fixture data must remain private to SYSTEM and Administrators.");
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
