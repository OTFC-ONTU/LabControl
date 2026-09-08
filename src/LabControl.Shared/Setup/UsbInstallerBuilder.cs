using System.Globalization;
using System.Resources;
using LabControl.Shared.Files;

namespace LabControl.Shared.Setup;

/// <summary>Copies only the three published executables into the offline installer layout.
/// Trust provisioning remains the console's responsibility; no directory-wide copy can
/// accidentally put a lab backup or private key onto a stick.</summary>
public sealed class UsbInstallerBuilder
{
    private static readonly ResourceManager Resources = new("LabControl.Shared.Setup.UsbInstallerStrings", typeof(UsbInstallerBuilder).Assembly);
    private readonly AgentBuild _build;
    private readonly string _setup;

    public UsbInstallerBuilder(string publishDirectory, string version)
    {
        if (!AgentBuild.TryLoad(publishDirectory, version, out _build, out var error))
            throw new InvalidDataException(error);
        _setup = Path.Combine(publishDirectory, Defaults.SetupExecutableName);
        if (!File.Exists(_setup))
            _setup = Path.Combine(publishDirectory, "LabControl.Setup", Defaults.SetupExecutableName);
        if (!File.Exists(_setup) || new FileInfo(_setup).Length == 0)
            throw new InvalidDataException("The published Setup executable is missing or empty.");
    }

    public string Version => _build.Version;

    public void ValidateDestination(string payloadDirectory)
    {
        RejectLinks(payloadDirectory);
        var app = Path.Combine(payloadDirectory, Defaults.UsbBinariesDirectoryName, Defaults.AgentAppDirectoryName);
        RejectLinks(Path.Combine(app, Version));
        if (Directory.Exists(app) && Directory.EnumerateDirectories(app)
                .Any(path => !string.Equals(Path.GetFileName(path), Version, StringComparison.Ordinal)))
            throw new InvalidOperationException("This USB already contains another agent build. Choose an empty destination and write its enrollment payload first.");
    }

    /// <summary>Add binaries beside an existing setup.json and ca.crt. Existing trust
    /// material and unrelated files are preserved. Refuse multiple version directories
    /// rather than deleting files from the teacher's stick.</summary>
    public string Build(string payloadDirectory)
    {
        payloadDirectory = Path.GetFullPath(payloadDirectory);
        using var authority = SetupPayload.Open(payloadDirectory).Authority;
        if (!File.Exists(Path.Combine(payloadDirectory, Defaults.SetupFileName)))
            payloadDirectory = Path.Combine(payloadDirectory, Defaults.PayloadDirectoryName);
        var app = Path.Combine(payloadDirectory, Defaults.UsbBinariesDirectoryName, Defaults.AgentAppDirectoryName);
        ValidateDestination(payloadDirectory);

        var target = Path.Combine(app, Version);
        RejectLinks(payloadDirectory);
        RejectLinks(target);
        Directory.CreateDirectory(target);
        foreach (var file in _build.Files)
            CopyVerified(file.Path, Path.Combine(target, file.Name), file.Sha256);
        CopyVerified(_setup, Path.Combine(payloadDirectory, Defaults.SetupExecutableName), FileHash.Sha256HexOfFile(_setup));
        var instructions = Path.Combine(payloadDirectory, Defaults.UsbInstructionsFileName);
        RejectLinks(instructions);
        var text = Resources.GetString("Instructions", CultureInfo.CurrentUICulture)
            ?? throw new InvalidOperationException("Missing USB installer instructions.");
        File.WriteAllText(instructions, string.Format(CultureInfo.CurrentCulture, text, Defaults.SetupExecutableName));
        return payloadDirectory;
    }

    private static void CopyVerified(string source, string destination, string expectedHash)
    {
        RejectLinks(destination);
        var temporary = destination + ".tmp";
        RejectLinks(temporary);
        try
        {
            File.Copy(source, temporary, overwrite: true);
            if (!string.Equals(FileHash.Sha256HexOfFile(temporary), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A published executable changed during the USB copy; build the USB again.");
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The USB destination cannot contain symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
