namespace LabControl.Shared.Files;

/// <summary>What a light reset did to one folder: entries removed, and entries left because a program held them or Windows refused.</summary>
public sealed record WipeSummary(int Removed, int Left);

/// <summary>
/// The light profile reset (M6 portion 5, D-78): empties a folder — the folder itself and its
/// top-level <c>desktop.ini</c> stay, so Windows keeps its name and icon. A symbolic link or
/// junction is removed as a link and never entered: the agent runs this under the student's
/// token, and a link is the one way a student's folder could name someone else's files.
/// Bounded in entries and time; never throws for one entry it cannot remove.
/// </summary>
public static class ProfileWipe
{
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
                if (top && entry is FileInfo && string.Equals(entry.Name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
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
