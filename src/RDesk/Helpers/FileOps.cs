using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace RDesk.Helpers;

/// <summary>Real file-system actions used by file boxes, organize and search.</summary>
public static class FileOps
{
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StartupLog.Write("Open failed", ex, path);
        }
    }

    public static void Reveal(string path)
    {
        try
        {
            var args = Directory.Exists(path) && !File.Exists(path)
                ? $"/select,\"{path.TrimEnd('\\')}\""
                : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StartupLog.Write("Reveal failed", ex, path);
        }
    }

    public static void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    /// <summary>Puts real files on the clipboard so Explorer / WeChat can paste them.</summary>
    public static async Task CopyFilesAsync(IEnumerable<string> paths, bool cut = false)
    {
        var items = new List<IStorageItem>();
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                }
                else if (File.Exists(path))
                {
                    items.Add(await StorageFile.GetFileFromPathAsync(path));
                }
            }
            catch (Exception ex)
            {
                StartupLog.Write("Clipboard item skipped", ex, path);
            }
        }

        if (items.Count == 0)
        {
            return;
        }

        var data = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
        data.SetStorageItems(items);
        Clipboard.SetContent(data);
    }

    public static async Task<(List<string> Paths, bool Move)> ReadClipboardFilesAsync()
    {
        var view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.StorageItems))
        {
            return (new List<string>(), false);
        }

        var items = await view.GetStorageItemsAsync();
        var move = view.RequestedOperation.HasFlag(DataPackageOperation.Move);
        return (items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList(), move);
    }

    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static IEnumerable<string> DesktopFolders => new[]
    {
        UserDesktop,
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }.Where(p => !string.IsNullOrEmpty(p));

    public static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var r = root.TrimEnd('\\') + "\\";
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the item sits directly on the user's or the public desktop.</summary>
    public static bool IsOnDesktop(string path)
    {
        var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
        return parent is not null && DesktopFolders.Any(d => string.Equals(parent.TrimEnd('\\'), d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Moves an item out of a box back to where it came from (its original folder, else the desktop).
    /// Returns the new path, or null when it could not be moved.
    /// </summary>
    public static string? PutBack(string path, string? originalPath)
    {
        var dir = originalPath is not null ? Path.GetDirectoryName(originalPath.TrimEnd('\\')) : null;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            dir = UserDesktop;
        }

        var moved = Transfer(new[] { path }, dir, move: true);
        return moved.Count == 1 ? moved[0] : null;
    }

    /// <summary>Copies or moves files / folders into <paramref name="destDir"/>, renaming on conflict.</summary>
    public static List<string> Transfer(IEnumerable<string> sources, string destDir, bool move)
    {
        Directory.CreateDirectory(destDir);
        var results = new List<string>();
        foreach (var src in sources)
        {
            try
            {
                var name = Path.GetFileName(src.TrimEnd('\\'));
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var sameFolder = string.Equals(Path.GetDirectoryName(src.TrimEnd('\\')), destDir.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
                if (sameFolder)
                {
                    continue;
                }

                var target = UniquePath(Path.Combine(destDir, name));
                if (Directory.Exists(src))
                {
                    if (move)
                    {
                        MoveDirectory(src, target);
                    }
                    else
                    {
                        CopyDirectory(src, target);
                    }
                }
                else if (File.Exists(src))
                {
                    if (move)
                    {
                        File.Move(src, target);
                    }
                    else
                    {
                        File.Copy(src, target);
                    }
                }
                else
                {
                    continue;
                }

                results.Add(target);
            }
            catch (Exception ex)
            {
                StartupLog.Write("Transfer failed", ex, src);
            }
        }

        return results;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    public static void MoveDirectory(string src, string target)
    {
        if (string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(src, target);
            return;
        }

        CopyDirectory(src, target);
        Directory.Delete(src, recursive: true);
    }

    public static void CopyDirectory(string src, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(src))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(src))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    /// <summary>Renames a real file or folder in place; returns the new full path.</summary>
    public static string Rename(string path, string newName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(invalid) >= 0)
        {
            throw new IOException("名稱包含不允許的字元");
        }

        var target = Path.Combine(Path.GetDirectoryName(path.TrimEnd('\\'))!, newName.Trim());
        if (target == path)
        {
            return path;
        }

        if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }

        return target;
    }

    /// <summary>Sends files / folders to the Recycle Bin (undoable from Explorer).</summary>
    public static bool DeleteToRecycleBin(IEnumerable<string> paths)
    {
        var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (list.Count == 0)
        {
            return true;
        }

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', list) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };
        var result = SHFileOperation(ref op);
        if (result != 0)
        {
            StartupLog.Write("Recycle failed", detail: $"code={result}");
        }

        return result == 0 && !op.fAnyOperationsAborted;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
