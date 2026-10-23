using System.Collections.Concurrent;
using System.Text.Json;
using RDesk.Models;

namespace RDesk.Services;

public sealed class OrganizePlanItem
{
    public string SourcePath { get; init; } = string.Empty;
    public string Category { get; set; } = FileCategories.Other;
    public string TargetFolder { get; set; } = string.Empty;
    public bool Include { get; set; } = true;
    /// <summary>Non-empty when the file is kept on the desktop.</summary>
    public string SkipReason { get; init; } = string.Empty;
    public long Size { get; init; }
    public string Name => Path.GetFileName(SourcePath);
}

public sealed class OrganizeMove
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public sealed class OrganizeBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public bool IsAuto { get; set; }
    public bool Undone { get; set; }
    public List<OrganizeMove> Moves { get; set; } = new();
}

/// <summary>
/// DeskBox 桌面整理: scan → reviewable move plan → execute into category storage boxes → undo.
/// Plus optional auto-organize (off by default) that only handles files appearing after it was enabled
/// and only once they stop changing.
/// </summary>
public sealed class DesktopOrganizerService : IDisposable
{
    public const long MaxFileSize = 4L * 1024 * 1024 * 1024;

    private static readonly string[] TempExtensions =
        [".tmp", ".crdownload", ".part", ".partial", ".download", ".!ut", ".opdownload", ".temp"];

    private static readonly string[] ShortcutExtensions = [".lnk", ".url", ".appref-ms"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ConfigService _config;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _autoTimer;
    private string? _watchedFolder;

    public static string HistoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDesk", "organize-history.json");

    public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Raised (on a worker thread) after files were moved by auto-organize.</summary>
    public event EventHandler<OrganizeBatch>? AutoOrganized;

    public DesktopOrganizerService(ConfigService config)
    {
        _config = config;
    }

    // ---------- One-click organize ----------

    public List<OrganizePlanItem> Scan(string? folder = null)
    {
        folder ??= DesktopFolder;
        var plan = new List<OrganizePlanItem>();
        if (!Directory.Exists(folder))
        {
            return plan;
        }

        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var reason = SkipReason(path, out var size);
            var category = FileCategories.Classify(path, _config.Current.OrganizeRules);
            plan.Add(new OrganizePlanItem
            {
                SourcePath = path,
                Category = category,
                TargetFolder = Path.Combine(_config.StorageRoot, category),
                Include = reason.Length == 0,
                SkipReason = reason,
                Size = size
            });
        }

        return plan
            .OrderBy(p => p.SkipReason.Length > 0)
            .ThenBy(p => p.Category)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Why a file must stay on the desktop (empty = can be organized).</summary>
    public static string SkipReason(string path, out long size)
    {
        size = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return "檔案已不存在";
            }

            size = info.Length;
            var attrs = info.Attributes;
            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                return "隱藏或系統檔案";
            }

            // Cloud placeholders (OneDrive "files on demand"): RECALL_ON_DATA_ACCESS / OFFLINE.
            if (((int)attrs & 0x00400000) != 0 || (attrs & FileAttributes.Offline) != 0)
            {
                return "雲端預留位置檔案";
            }

            var name = info.Name;
            var ext = info.Extension.ToLowerInvariant();
            if (name.StartsWith("~$", StringComparison.Ordinal) || TempExtensions.Contains(ext))
            {
                return "暫存或下載中的檔案";
            }

            if (ShortcutExtensions.Contains(ext))
            {
                return "捷徑保留在桌面";
            }

            if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                return "系統檔案";
            }

            if (size > MaxFileSize)
            {
                return "超過 4 GB 的大檔案";
            }

            if (DateTime.Now - info.LastWriteTime < TimeSpan.FromSeconds(5))
            {
                return "剛被修改，可能仍在寫入";
            }

            if (IsLocked(path))
            {
                return "檔案正被其他程式使用";
            }
        }
        catch (UnauthorizedAccessException)
        {
            return "權限不足";
        }
        catch (Exception ex)
        {
            return $"無法讀取：{ex.Message}";
        }

        return string.Empty;
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public OrganizeBatch Execute(IEnumerable<OrganizePlanItem> plan, bool isAuto = false)
    {
        var batch = new OrganizeBatch { IsAuto = isAuto };
        foreach (var item in plan.Where(p => p.Include && p.SkipReason.Length == 0))
        {
            try
            {
                if (!File.Exists(item.SourcePath))
                {
                    continue;
                }

                Directory.CreateDirectory(item.TargetFolder);
                var target = Helpers.FileOps.UniquePath(Path.Combine(item.TargetFolder, item.Name));
                File.Move(item.SourcePath, target);
                batch.Moves.Add(new OrganizeMove { From = item.SourcePath, To = target });
            }
            catch (Exception ex)
            {
                StartupLog.Write("Organize move failed", ex, item.SourcePath);
            }
        }

        if (batch.Moves.Count > 0)
        {
            EnsureStorageBoxes(batch);
            AppendHistory(batch);
        }

        return batch;
    }

    /// <summary>Makes sure every category folder that received files shows up as a storage box.</summary>
    private void EnsureStorageBoxes(OrganizeBatch batch)
    {
        foreach (var folder in batch.Moves.Select(m => Path.GetDirectoryName(m.To)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_config.Current.FavoriteGroups.Any(g => g.IsFolderBacked &&
                    string.Equals(g.MappedFolderPath, folder, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _config.Current.FavoriteGroups.Add(new FavoriteGroupModel
            {
                Title = Path.GetFileName(folder),
                IsStorageBox = true,
                MappedFolderPath = folder,
                SortBy = "Date",
                SortDescending = true
            });
        }

        _config.SaveImmediate();
    }

    public List<OrganizeBatch> LoadHistory()
    {
        try
        {
            return File.Exists(HistoryPath)
                ? JsonSerializer.Deserialize<List<OrganizeBatch>>(File.ReadAllText(HistoryPath), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            StartupLog.Write("Organize history unreadable", ex);
            return new();
        }
    }

    private readonly object _historyGate = new();

    private void AppendHistory(OrganizeBatch batch)
    {
        lock (_historyGate)
        {
            var history = LoadHistory();
            history.Insert(0, batch);
            if (history.Count > 20)
            {
                history.RemoveRange(20, history.Count - 20);
            }

            SaveHistory(history);
        }
    }

    private static void SaveHistory(List<OrganizeBatch> history)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history, JsonOptions));
    }

    public OrganizeBatch? LastUndoableBatch() => LoadHistory().FirstOrDefault(b => !b.Undone && b.Moves.Count > 0);

    /// <returns>Number of files moved back.</returns>
    public int Undo(OrganizeBatch batch)
    {
        var restored = 0;
        foreach (var move in batch.Moves.AsEnumerable().Reverse())
        {
            try
            {
                if (!File.Exists(move.To))
                {
                    continue;
                }

                var back = Helpers.FileOps.UniquePath(move.From);
                Directory.CreateDirectory(Path.GetDirectoryName(back)!);
                File.Move(move.To, back);
                restored++;
            }
            catch (Exception ex)
            {
                StartupLog.Write("Organize undo failed", ex, move.To);
            }
        }

        lock (_historyGate)
        {
            var history = LoadHistory();
            var stored = history.FirstOrDefault(b => b.Id == batch.Id);
            if (stored is not null)
            {
                stored.Undone = true;
                SaveHistory(history);
            }
        }

        batch.Undone = true;
        return restored;
    }

    // ---------- Auto organize ----------

    public bool IsAutoRunning => _watcher is not null;

    public void ApplyAutoSetting(string? folder = null)
    {
        if (_config.Current.AutoOrganizeEnabled)
        {
            StartAuto(folder);
        }
        else
        {
            StopAuto();
        }
    }

    public void StartAuto(string? folder = null)
    {
        folder ??= DesktopFolder;
        if (_watcher is not null && string.Equals(_watchedFolder, folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StopAuto();
        if (!Directory.Exists(folder))
        {
            return;
        }

        _watchedFolder = folder;
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
        };
        // Only files that appear from now on; existing desktop files are left alone.
        _watcher.Created += (_, e) => _pending[e.FullPath] = DateTime.Now;
        _watcher.Renamed += (sender, e) =>
        {
            _pending.TryRemove(e.OldFullPath, out _);
            _pending[e.FullPath] = DateTime.Now;
        };
        _watcher.Changed += (_, e) =>
        {
            if (_pending.ContainsKey(e.FullPath))
            {
                _pending[e.FullPath] = DateTime.Now;
            }
        };
        _watcher.EnableRaisingEvents = true;
        _autoTimer = new Timer(_ => ProcessPending(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        StartupLog.Write("Auto organize started", detail: folder);
    }

    public void StopAuto()
    {
        _autoTimer?.Dispose();
        _autoTimer = null;
        _watcher?.Dispose();
        _watcher = null;
        _watchedFolder = null;
        _pending.Clear();
    }

    /// <summary>Stability window: a new file must be unchanged for this long before it is moved.</summary>
    public TimeSpan StableDelay { get; set; } = TimeSpan.FromSeconds(4);

    private int _processing;

    public void ProcessPending()
    {
        if (Interlocked.Exchange(ref _processing, 1) == 1)
        {
            return;
        }

        try
        {
            var now = DateTime.Now;
            var ready = new List<OrganizePlanItem>();
            foreach (var (path, lastSeen) in _pending.ToArray())
            {
                if (now - lastSeen < StableDelay)
                {
                    continue;
                }

                _pending.TryRemove(path, out _);
                if (!File.Exists(path))
                {
                    continue;
                }

                var category = FileCategories.Classify(path, _config.Current.OrganizeRules);
                if (category == FileCategories.Other)
                {
                    // Auto mode only acts on files a rule explicitly matches.
                    continue;
                }

                var reason = SkipReason(path, out var size);
                if (reason == "剛被修改，可能仍在寫入" || reason == "檔案正被其他程式使用")
                {
                    _pending[path] = now;
                    continue;
                }

                if (reason.Length > 0)
                {
                    continue;
                }

                ready.Add(new OrganizePlanItem
                {
                    SourcePath = path,
                    Category = category,
                    TargetFolder = Path.Combine(_config.StorageRoot, category),
                    Size = size
                });
            }

            if (ready.Count == 0)
            {
                return;
            }

            void Run()
            {
                var batch = Execute(ready, isAuto: true);
                if (batch.Moves.Count > 0)
                {
                    StartupLog.Write("Auto organized", detail: $"{batch.Moves.Count} file(s)");
                    AutoOrganized?.Invoke(this, batch);
                }
            }

            // Config lists are owned by the UI thread.
            if (UiDispatcher is null || !UiDispatcher.TryEnqueue(Run))
            {
                Run();
            }
        }
        catch (Exception ex)
        {
            StartupLog.Write("Auto organize failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    public int PendingCount => _pending.Count;

    public Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcher { get; set; }

    public void Dispose() => StopAuto();
}
