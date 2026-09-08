using System.Runtime.InteropServices;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace LabControl.ConsoleSetup;

/// <summary>
/// The Start-menu and desktop <c>.lnk</c> files, through <c>IShellLinkW</c> (D-59 item 1).
/// Writing the same shortcut twice produces the same file, so a repeated install is a no-op
/// as far as the teacher's Start menu is concerned.
/// </summary>
internal static class WindowsShortcut
{
    /// <summary>The shell's <c>ShellLink</c> coclass; the interfaces themselves come from CsWin32.</summary>
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    public static void Write(string path, string target, string workingDirectory, string description, string icon)
    {
        var type = Type.GetTypeFromCLSID(ShellLinkClsid)
            ?? throw new IOException("The Windows shell link component is unavailable.");
        var instance = Activator.CreateInstance(type)
            ?? throw new IOException("The Windows shell link component could not be created.");
        try
        {
            var link = (IShellLinkW)instance;
            unsafe
            {
                fixed (char* value = target)
                {
                    link.SetPath(value);
                }

                fixed (char* value = workingDirectory)
                {
                    link.SetWorkingDirectory(value);
                }

                fixed (char* value = description)
                {
                    link.SetDescription(value);
                }

                fixed (char* value = icon)
                {
                    link.SetIconLocation(value, 0);
                }
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = (IPersistFile)instance;
            unsafe
            {
                fixed (char* value = path)
                {
                    file.Save(value, true);
                }
            }
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            throw new IOException("The shortcut " + path + " could not be written.", error);
        }
        finally
        {
            if (Marshal.IsComObject(instance))
            {
                Marshal.ReleaseComObject(instance);
            }
        }
    }

    /// <summary>Removes a shortcut this installer wrote; a missing file is success.</summary>
    public static bool Remove(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
