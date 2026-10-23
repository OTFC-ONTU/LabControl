namespace LabControl.Shared.Files;

/// <summary>What a light reset did to one folder: entries removed, and entries left because a program held them or Windows refused.</summary>
public sealed record WipeSummary(int Removed, int Left);

/// <summary>
/// The light profile reset (M6 portion 5, D-78): empties a folder — the folder itself and what
/// Windows keeps at its top (<see cref="IsWindowsOwn"/>) stay, so it keeps its name, icon and links. A symbolic link or
/// junction is removed as a link and never entered: the agent runs this under the student's
/// token, and a link is the one way a student's folder could name someone else's files.
/// Bounded in entries and time; never throws for one entry it cannot remove.
/// </summary>
public static class ProfileWipe
{
    /// <summary>
    /// What Windows itself keeps at the top of a known folder and a light reset leaves alone:
    /// <c>desktop.ini</c>, and anything both hidden and system — Documents' compatibility junctions
    /// <c>My Music</c>, <c>My Pictures</c>, <c>My Videos</c> (VM drill 2026-10-23: the first build
    /// removed them). A student could mark a file so to keep it; the full reset is the cure for that.
    /// </summary>
    public static bool IsWindowsOwn(string name, FileAttributes attributes, bool directory) =>
        (!directory && string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
        || (attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System);

    public static WipeSummary Clear(string folder, int maxEntries, TimeSpan maxDuration, CancellationToken token)
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return new WipeSummary(0, 0);
        }

        var deadline = DateTime.UtcNow + maxDuration;
        var removed = 0;
        var left = 0;
        var visited = 0;

        // Iterative, never recursive: a student can nest folders thousands deep, and a stack
        // overflow in the SYSTEM service cannot be caught (review 2026-10-23). Each directory is
        // seen twice — once to empty it, once, after everything below it, to remove it.
        var pending = new Stack<(DirectoryInfo Directory, bool Emptied)>();
        pending.Push((root, false));
        while (pending.Count > 0)
        {
            var (directory, emptied) = pending.Pop();
            var top = directory.FullName == root.FullName;
            if (emptied)
            {
                if (top)
                {
                    continue;
                }

                try
                {
                    if (directory.EnumerateFileSystemInfos().Any())
                    {
                        continue; // what is still inside was counted as left
                    }

                    directory.Attributes = FileAttributes.Directory;
                    directory.Delete(recursive: false);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    left++;
                }

                continue;
            }

            List<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left++;
                continue;
            }

            pending.Push((directory, true));
            foreach (var entry in entries)
            {
                if (visited >= maxEntries || DateTime.UtcNow >= deadline || token.IsCancellationRequested)
                {
                    left++;
                    continue;
                }

                visited++;
                if (top && IsWindowsOwn(entry.Name, entry.Attributes, entry is DirectoryInfo))
                {
                    continue;
                }

                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // The link itself, never what it points at.
                        if (entry is DirectoryInfo)
                        {
                            Directory.Delete(entry.FullName, recursive: false);
                        }
                        else
                        {
                            File.Delete(entry.FullName);
                        }

                        removed++;
                        continue;
                    }

                    if (entry is DirectoryInfo child)
                    {
                        pending.Push((child, false));
                        continue;
                    }

                    if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    }

                    entry.Delete();
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    left++;
                }
            }
        }

        return new WipeSummary(removed, left);
    }
}
