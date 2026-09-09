using LabControl.Shared;
using LabControl.Shared.Packaging;
using Microsoft.Win32;

namespace LabControl.ConsoleSetup;

/// <summary>What happened to one registry key uninstall tried to remove.</summary>
internal enum RegistryRemoval
{
    /// <summary>The key was this installation's, and it is gone.</summary>
    Removed = 0,

    /// <summary>There was no such key.</summary>
    Missing = 1,

    /// <summary>
    /// The key belongs to another installation of the console — a second copy, or one
    /// installed somewhere else — so it is left exactly as it is. That is a reason to say so
    /// and carry on, never a reason to abandon an uninstall half-way and leave the machine in
    /// a state with no entry to retry from.
    /// </summary>
    PreservedForReview = 2,
}

/// <summary>
/// Everything the installer writes under <c>HKEY_CURRENT_USER</c> (D-59 item 1): the
/// Installed-apps entry and the two file types. Nothing is written machine-wide, and the
/// teacher's own default-handler choice is never overwritten (D-54 item 4).
/// </summary>
internal static class WindowsRegistrations
{
    public static void WriteUninstallEntry(ConsoleInstallLayout layout, string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ConsoleUninstallEntry.Key, true);
        foreach (var pair in ConsoleUninstallEntry.StringValues(layout, version))
        {
            key.SetValue(pair.Key, pair.Value, RegistryValueKind.String);
        }

        foreach (var pair in ConsoleUninstallEntry.DwordValues)
        {
            key.SetValue(pair.Key, pair.Value, RegistryValueKind.DWord);
        }
    }

    /// <summary>Removes the entry only while it still points at this installation.</summary>
    public static RegistryRemoval RemoveUninstallEntry(ConsoleInstallLayout layout)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(ConsoleUninstallEntry.Key))
        {
            if (key is null)
            {
                return RegistryRemoval.Missing;
            }

            if (key.GetValue("InstallLocation") is string location
                && !string.Equals(location.TrimEnd(Path.DirectorySeparatorChar), layout.InstallDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return RegistryRemoval.PreservedForReview;
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(ConsoleUninstallEntry.Key, false);
        return RegistryRemoval.Removed;
    }

    public static void WriteProgId(ConsoleFileType type, ConsoleInstallLayout layout)
    {
        using (var progId = Registry.CurrentUser.CreateSubKey(type.ProgIdKey, true))
        {
            progId.SetValue(null, type.Description, RegistryValueKind.String);
        }

        using (var icon = Registry.CurrentUser.CreateSubKey(type.DefaultIconKey, true))
        {
            icon.SetValue(null, layout.IconReference, RegistryValueKind.String);
        }

        using var command = Registry.CurrentUser.CreateSubKey(type.CommandKey, true);
        command.SetValue(null, ConsoleFileType.CommandFor(layout.ConsoleExecutable), RegistryValueKind.String);
    }

    /// <summary>Removes the ProgId tree, but only while its open command still names this installation.</summary>
    public static RegistryRemoval RemoveProgId(ConsoleFileType type, ConsoleInstallLayout layout)
    {
        using (var command = Registry.CurrentUser.OpenSubKey(type.CommandKey))
        {
            if (command is null)
            {
                using var progId = Registry.CurrentUser.OpenSubKey(type.ProgIdKey);
                if (progId is null)
                {
                    return RegistryRemoval.Missing;
                }
            }
            else if (command.GetValue(null) is string line
                && !line.Contains(layout.ConsoleExecutable, StringComparison.OrdinalIgnoreCase))
            {
                return RegistryRemoval.PreservedForReview;
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(type.ProgIdKey, false);
        return RegistryRemoval.Removed;
    }

    /// <summary>
    /// Offers this console for the extension and takes the default only when Windows holds
    /// no <c>UserChoice</c> for it. Returns whether it became the default.
    /// </summary>
    public static bool Associate(ConsoleFileType type)
    {
        using (var handlers = Registry.CurrentUser.CreateSubKey(type.OpenWithProgidsKey, true))
        {
            // The value's name is the ProgId; its content is deliberately empty.
            handlers.SetValue(type.ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        if (HasUserChoice(type))
        {
            return false;
        }

        using var extension = Registry.CurrentUser.CreateSubKey(type.ExtensionKey, true);
        if (extension.GetValue(null) is string existing && !string.IsNullOrEmpty(existing))
        {
            return string.Equals(existing, type.ProgId, StringComparison.OrdinalIgnoreCase);
        }

        extension.SetValue(null, type.ProgId, RegistryValueKind.String);
        return true;
    }

    /// <summary>
    /// Stops offering this console for the extension: the <c>OpenWithProgids</c> entry goes,
    /// and the default goes only when it is still this console's ProgId. A user choice is
    /// left exactly as the teacher made it.
    /// </summary>
    public static bool Disassociate(ConsoleFileType type)
    {
        var changed = false;
        using (var handlers = Registry.CurrentUser.OpenSubKey(type.OpenWithProgidsKey, true))
        {
            if (handlers?.GetValue(type.ProgId) is not null)
            {
                handlers.DeleteValue(type.ProgId, false);
                changed = true;
            }
        }

        using (var extension = Registry.CurrentUser.OpenSubKey(type.ExtensionKey, true))
        {
            if (extension?.GetValue(null) is string existing
                && string.Equals(existing, type.ProgId, StringComparison.OrdinalIgnoreCase))
            {
                extension.DeleteValue(string.Empty, false);
                changed = true;
            }
        }

        RemoveIfEmpty(type.OpenWithProgidsKey);
        RemoveIfEmpty(type.ExtensionKey);
        return changed;
    }

    private static bool HasUserChoice(ConsoleFileType type)
    {
        using var choice = Registry.CurrentUser.OpenSubKey(type.UserChoiceKey);
        return choice is not null;
    }

    private static void RemoveIfEmpty(string path)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(path))
        {
            if (key is null || key.ValueCount != 0 || key.SubKeyCount != 0)
            {
                return;
            }
        }

        Registry.CurrentUser.DeleteSubKey(path, false);
    }
}
