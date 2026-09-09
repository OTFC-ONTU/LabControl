namespace LabControl.Shared.Packaging;

/// <summary>
/// Every path and registry name the per-user Windows installer touches (D-59 item 1),
/// derived from four roots that are passed in rather than read from the environment, so
/// the whole plan can be built and asserted on a Mac. Nothing here is machine-wide: the
/// installer never writes outside the signed-in user's own profile.
/// </summary>
public sealed record ConsoleInstallLayout(
    string LocalAppData,
    string StartMenuPrograms,
    string Desktop,
    string DataDirectory)
{
    /// <summary><c>%LOCALAPPDATA%\Programs\LabControl\Console</c>.</summary>
    public string InstallDirectory =>
        Path.Combine(LocalAppData, "Programs", "LabControl", "Console");

    public string ConsoleExecutable => Path.Combine(InstallDirectory, Defaults.ConsoleExecutableName);

    public string SetupExecutable => Path.Combine(InstallDirectory, Defaults.ConsoleSetupExecutableName);

    /// <summary>Both the Installed-apps icon and the two ProgId icons: the console exe carries the icon resource.</summary>
    public string IconReference => ConsoleExecutable + ",0";

    public string StartMenuShortcut =>
        Path.Combine(StartMenuPrograms, Defaults.ConsoleProductName + Defaults.ShortcutFileExtension);

    public string DesktopShortcut =>
        Path.Combine(Desktop, Defaults.ConsoleProductName + Defaults.ShortcutFileExtension);

    /// <summary>The lock a running console holds (D-55 item 12): the installer replaces files only when nobody holds it.</summary>
    public string LockFile => Path.Combine(DataDirectory, Defaults.ConsoleLockFileName);

    /// <summary>The append-only installer log, next to the console's own data but under <c>%LOCALAPPDATA%</c>.</summary>
    public string LogFile => Path.Combine(LocalAppData, "LabControl", Defaults.ConsoleSetupLogFileName);

    /// <summary>
    /// Every root this layout cannot use, as a sentence naming it. <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
    /// returns an empty string for a special folder Windows has not materialised — a fresh
    /// profile with no Desktop directory, a locked-down or redirected shell folder — and an
    /// empty root would silently turn every path below into a relative one, writing shortcuts
    /// into whatever the current working directory happened to be. The installer refuses
    /// instead, and says which root Windows would not give it.
    /// </summary>
    public IReadOnlyList<string> UnusableRoots()
    {
        var problems = new List<string>();
        Check("the local application data directory", LocalAppData);
        Check("the Start-menu Programs directory", StartMenuPrograms);
        Check("the desktop directory", Desktop);
        Check("the console's data directory", DataDirectory);
        return problems;

        void Check(string what, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                problems.Add(what + " (Windows reported no path)");
            }
            else if (!Path.IsPathRooted(value))
            {
                problems.Add(what + " (\"" + value + "\" is not a full path)");
            }
        }
    }

    /// <summary>The roots Windows reports for the signed-in user. Only meaningful on Windows.</summary>
    /// <exception cref="InvalidOperationException">Windows did not report a usable root.</exception>
    public static ConsoleInstallLayout Current()
    {
        var layout = new ConsoleInstallLayout(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Defaults.ConsoleDataDirectory);

        return layout.UnusableRoots() is { Count: > 0 } unusable
            ? throw new InvalidOperationException(
                "Windows did not say where to install: " + string.Join("; ", unusable)
                + ". Sign in as the teacher's own user and run this again.")
            : layout;
    }
}

/// <summary>
/// One file type the console owns (D-59 item 1): a ProgId under
/// <c>HKCU\Software\Classes</c> plus the extension key that offers it. The installer
/// adds itself to <c>OpenWithProgids</c> always and becomes the default only when the
/// extension has no <c>UserChoice</c> — a teacher's own choice is never overwritten
/// (D-54 item 4).
/// </summary>
public sealed record ConsoleFileType(string Extension, string ProgId, string Description)
{
    public string ExtensionKey => @"Software\Classes\" + Extension;

    public string ProgIdKey => @"Software\Classes\" + ProgId;

    public string CommandKey => ProgIdKey + @"\shell\open\command";

    public string DefaultIconKey => ProgIdKey + @"\DefaultIcon";

    public string OpenWithProgidsKey => ExtensionKey + @"\OpenWithProgids";

    /// <summary>Where Windows records the user's own pick; its presence forbids taking the default.</summary>
    public string UserChoiceKey =>
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + Extension + @"\UserChoice";

    /// <summary>Always quoted, always a single <c>"%1"</c>: a path with spaces must arrive as one argument.</summary>
    public static string CommandFor(string executable) => "\"" + executable + "\" \"%1\"";
}

/// <summary>The two file types (D-56, D-26) the console registers for on Windows and Linux, and declares on macOS.</summary>
public static class ConsoleFileTypes
{
    public static ConsoleFileType LabFile { get; } = new(
        Defaults.LabFileExtension,
        Defaults.ConsoleLabFileProgId,
        "LabControl lab file");

    public static ConsoleFileType Backup { get; } = new(
        Defaults.BackupFileExtension,
        Defaults.ConsoleBackupProgId,
        "LabControl lab backup");

    public static IReadOnlyList<ConsoleFileType> All { get; } = [LabFile, Backup];
}

/// <summary>
/// The values of <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\LabControl Console</c>.
/// Kept as data so the dry-run prints exactly what will be written and a test can assert it.
/// </summary>
public static class ConsoleUninstallEntry
{
    public static string Key => Defaults.ConsoleUninstallRegistryKey;

    public static IReadOnlyDictionary<string, string> StringValues(ConsoleInstallLayout layout, string version) =>
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["DisplayName"] = Defaults.ConsoleProductName,
            ["DisplayVersion"] = version,
            ["DisplayIcon"] = layout.IconReference,
            ["Publisher"] = Defaults.ConsolePublisher,
            ["InstallLocation"] = layout.InstallDirectory,
            ["UninstallString"] = "\"" + layout.SetupExecutable + "\" " + Defaults.ConsoleSetupUninstallSwitch,
        };

    /// <summary>Neither Change nor Repair exists: the installer only installs or removes.</summary>
    public static IReadOnlyDictionary<string, int> DwordValues { get; } =
        new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["NoModify"] = 1,
            ["NoRepair"] = 1,
        };
}
