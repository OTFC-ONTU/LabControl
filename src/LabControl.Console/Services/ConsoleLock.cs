using LabControl.Shared;

namespace LabControl.Console.Services;

/// <summary>
/// One console process per data directory (M5 §2.1): <c>console.lock</c> is opened with
/// <see cref="FileShare.None"/> and held until the process ends. A second launch on the same
/// directory — a double-click, a second <c>--data</c> pointing at the same place — is told so
/// and quits, instead of two processes sharing <c>profiles.json</c> and the lab directories.
/// The operating system releases the lock if the process dies, so a stale file is never a
/// problem: the file's existence means nothing, only holding it open does.
/// </summary>
public sealed class ConsoleLock : IDisposable
{
    private readonly FileStream _stream;

    private ConsoleLock(FileStream stream) => _stream = stream;

    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, Defaults.ConsoleLockFileName);

    /// <summary>Takes the lock, or explains why not: another process holds it, or the directory is unwritable.</summary>
    public static ConsoleLock? TryAcquire(string dataDirectory, out string error)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var stream = new FileStream(PathFor(dataDirectory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
            error = string.Empty;
            return new ConsoleLock(stream);
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
