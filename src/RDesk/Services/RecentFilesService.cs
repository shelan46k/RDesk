using RDesk.Helpers;
using RDesk.Models;

namespace RDesk.Services;

public sealed class RecentFilesService
{
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx", ".xls", ".docx", ".doc", ".pptx", ".ppt", ".pdf", ".txt", ".md", ".csv",
        ".jpg", ".png", ".zip", ".rar", ".sln", ".cs", ".py", ".json"
    };

    private readonly ConfigService _config;

    public RecentFilesService(ConfigService config)
    {
        _config = config;
    }

    public async Task<IReadOnlyList<RecentFileItemModel>> LoadAsync(int max = 12)
    {
        var recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recentDir))
        {
            return Array.Empty<RecentFileItemModel>();
        }

        var pinned = _config.Current.PinnedRecentPaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lnks = Directory.EnumerateFiles(recentDir, "*.lnk")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(80)
            .ToList();

        var items = new List<RecentFileItemModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task AddPathAsync(string path, DateTime stamp, bool isPinned)
        {
            if (!seen.Add(path) || !File.Exists(path))
            {
                return;
            }

            var ext = Path.GetExtension(path);
            if (!AllowedExt.Contains(ext))
            {
                return;
            }

            items.Add(new RecentFileItemModel
            {
                DisplayName = Path.GetFileName(path),
                FullPath = path,
                ExtensionLabel = ext.TrimStart('.').ToUpperInvariant(),
                TimeLabel = stamp.ToLocalTime().ToString("MM-dd HH:mm"),
                IsPinned = isPinned,
                Icon = await ShellHelper.GetFileIconAsync(path)
            });
        }

        foreach (var p in pinned)
        {
            await AddPathAsync(p, File.GetLastWriteTimeUtc(p), true);
        }

        foreach (var lnk in lnks)
        {
            if (items.Count >= max + pinned.Count)
            {
                break;
            }

            var target = ShellHelper.ResolveShortcutTarget(lnk.FullName);
            if (string.IsNullOrWhiteSpace(target) || Directory.Exists(target))
            {
                continue;
            }

            await AddPathAsync(target, lnk.LastWriteTimeUtc, pinned.Contains(target, StringComparer.OrdinalIgnoreCase));
        }

        return items.Take(max + pinned.Count).ToList();
    }

    public void TogglePin(string path)
    {
        _config.Update(c =>
        {
            if (c.PinnedRecentPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                c.PinnedRecentPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                c.PinnedRecentPaths.Insert(0, path);
            }
        });
    }
}
