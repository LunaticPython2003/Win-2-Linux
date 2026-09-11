using System.IO;

namespace Win2Linux.Core.Common;

public static class FileUtilities
{
    /// <summary>
    /// Safely copies a file to destination, clearing any ReadOnly attributes on existing or copied files.
    /// This prevents System.UnauthorizedAccessException when overwriting files originating from CD/ISO media.
    /// </summary>
    public static void SafeCopy(string sourcePath, string destinationPath)
    {
        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        if (File.Exists(destinationPath))
        {
            try
            {
                var existingAttrs = File.GetAttributes(destinationPath);
                if ((existingAttrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(destinationPath, existingAttrs & ~FileAttributes.ReadOnly);
                }
                File.Delete(destinationPath);
            }
            catch { }
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);

        // Files copied from optical ISO images retain the ReadOnly attribute — remove it.
        try
        {
            var newAttrs = File.GetAttributes(destinationPath);
            if ((newAttrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(destinationPath, newAttrs & ~FileAttributes.ReadOnly);
            }
        }
        catch { }
    }

    /// <summary>
    /// Recursively removes the ReadOnly attribute from all files in a directory.
    /// </summary>
    public static void RemoveReadOnlyRecursive(string directoryPath)
    {
        if (!Directory.Exists(directoryPath)) return;
        try
        {
            foreach (var f in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(f);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(f, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
