using System.Diagnostics;
using Xunit;

namespace LabControl.Tests;

/// <summary>
/// A real, tiny volume that the test fills up, so a write fails the way it does on a full disk —
/// <c>ENOSPC</c> / <c>ERROR_DISK_FULL</c> from the operating system, in the middle of a write, not
/// an exception a seam pretends (M7 robustness pass). On macOS it is a RAM disk made with
/// <c>hdiutil</c> and detached afterwards; elsewhere <c>LABCONTROL_FULL_DISK_VOLUME</c> may name
/// a dedicated small volume (a VHD on Windows, a loop device on Linux) of at most 64 MB — never
/// the system disk, which the test would fill. Without either the test is skipped.
/// Linked into both test projects.
/// </summary>
internal sealed class FullVolume : IDisposable
{
    private const long LargestAccepted = 64L * 1024 * 1024;

    private readonly string? _device;
    private readonly string _filler;

    private FullVolume(string root, string? device)
    {
        Root = root;
        _device = device;
        _filler = Path.Combine(root, "filler.bin");
        Directory.CreateDirectory(root);
    }

    /// <summary>A directory on the volume for this test's files.</summary>
    public string Root { get; }

    public static FullVolume Create(int megabytes = 4)
    {
        var given = Environment.GetEnvironmentVariable("LABCONTROL_FULL_DISK_VOLUME");
        if (!string.IsNullOrWhiteSpace(given))
        {
            var drive = new DriveInfo(given);
            if (drive.TotalSize > LargestAccepted)
            {
                Assert.Skip($"LABCONTROL_FULL_DISK_VOLUME is {drive.TotalSize / (1024 * 1024)} MB; only a dedicated volume of at most 64 MB is filled.");
            }

            return new FullVolume(Path.Combine(given, "labcontrol-full-" + Guid.NewGuid().ToString("n")), null);
        }

        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("A full volume needs macOS (hdiutil) or LABCONTROL_FULL_DISK_VOLUME.");
        }

        var (attached, device) = Run("hdiutil", "attach", "-nomount", $"ram://{megabytes * 2048}");
        device = device.Trim().Split((char[])['\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (!attached || !device.StartsWith("/dev/", StringComparison.Ordinal))
        {
            Assert.Skip("hdiutil could not make a RAM disk here: " + device);
        }

        var name = "LCFULL" + Guid.NewGuid().ToString("n")[..8].ToUpperInvariant();
        var (formatted, output) = Run("diskutil", "erasevolume", "HFS+", name, device);
        var mount = Path.Combine("/Volumes", name);
        if (!formatted || !Directory.Exists(mount))
        {
            Run("hdiutil", "detach", device, "-force");
            Assert.Skip("diskutil could not format the RAM disk: " + output);
        }

        return new FullVolume(Path.Combine(mount, "t"), device);
    }

    /// <summary>
    /// Fills the volume until the operating system says there is no space, then gives back
    /// <paramref name="leaveBytes"/> — enough for a file to be created and a little written, not
    /// enough for what the test writes next.
    /// </summary>
    public void Fill(long leaveBytes = 0)
    {
        var chunk = new byte[64 * 1024];
        using var stream = new FileStream(_filler, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
        try
        {
            while (true)
            {
                stream.Write(chunk);
            }
        }
        catch (IOException)
        {
        }

        // Byte by byte to the very end: a 64 KB write refused can still leave room for less.
        try
        {
            while (true)
            {
                stream.Write(chunk, 0, 512);
            }
        }
        catch (IOException)
        {
        }

        if (leaveBytes > 0)
        {
            stream.SetLength(Math.Max(0, stream.Length - leaveBytes));
        }
    }

    /// <summary>Space is back.</summary>
    public void Free() => File.Delete(_filler);

    public void Dispose()
    {
        try
        {
            if (_device is not null)
            {
                Run("hdiutil", "detach", _device, "-force");
            }
            else
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static (bool Ok, string Output) Run(string file, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60_000))
            {
                process.Kill();
                return (false, output);
            }

            return (process.ExitCode == 0, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }
}
