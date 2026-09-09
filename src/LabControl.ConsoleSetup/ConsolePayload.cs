using System.IO.Compression;
using LabControl.Shared.Packaging;

namespace LabControl.ConsoleSetup;

/// <summary>
/// The published <c>win-x64</c> console, embedded as one zip by <c>tools/package-windows.sh</c>
/// (D-59 item 1). The project compiles without it — so an ordinary <c>dotnet build</c> on any
/// machine still builds this executable — and an executable built that way refuses to install
/// and says so, instead of writing an empty program directory.
///
/// Reading the zip is all this class does. Where the bytes may land, whether an entry is
/// allowed to name that path at all, and the retry around an antivirus scan all live in
/// <see cref="ConsoleInstalledFiles"/>, which is tested on any operating system.
/// </summary>
internal sealed class ConsolePayload
{
    private const string ResourceName = "LabControl.ConsoleSetup.Payload.zip";

    private ConsolePayload(IReadOnlyList<string> entries) => Entries = entries;

    /// <summary>The relative paths inside the install directory, in the order they are written.</summary>
    public IReadOnlyList<string> Entries { get; }

    public static bool IsPresent => typeof(ConsolePayload).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    public static ConsolePayload Open()
    {
        using var archive = OpenArchive();
        return new ConsolePayload(ConsoleInstalledFiles.EntriesOf(archive));
    }

    /// <summary>
    /// Writes every entry into the install directory, skipping the ones already there byte for
    /// byte, and returns how many files it replaced.
    /// </summary>
    public static int ExtractTo(ConsoleInstalledFiles files)
    {
        using var archive = OpenArchive();
        return files.ExtractArchive(archive);
    }

    private static ZipArchive OpenArchive()
    {
        var stream = typeof(ConsolePayload).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "This installer was built without the console program files. Build it with tools/package-windows.sh, "
                + "which publishes the console and embeds it, and run that executable instead.");
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }
}
