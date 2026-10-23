using System.IO.Compression;

namespace RDesk.Services;

/// <summary>
/// DeskBox-style backup: ZIP export / restore of RDesk data (settings, boxes, todos, notes, organize history),
/// a pre-restore snapshot, and a daily automatic snapshot (keeps the newest 7). File contents of mapped /
/// storage folders are not included — they stay ordinary Windows folders.
/// </summary>
public sealed class BackupService
{
    private const string ConfigEntry = "config.json";
    private const string HistoryEntry = "organize-history.json";
    private const int KeepSnapshots = 7;

    private readonly ConfigService _config;

    public BackupService(ConfigService config)
    {
        _config = config;
    }

    public static string SnapshotFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDesk", "Backups");

    public void Export(string zipPath)
    {
        _config.SaveImmediate();
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        var tmp = zipPath + ".tmp";
        if (File.Exists(tmp))
        {
            File.Delete(tmp);
        }

        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(ConfigService.ConfigFilePath, ConfigEntry);
            if (File.Exists(DesktopOrganizerService.HistoryPath))
            {
                zip.CreateEntryFromFile(DesktopOrganizerService.HistoryPath, HistoryEntry);
            }

            var info = zip.CreateEntry("rdesk-backup.txt");
            using var writer = new StreamWriter(info.Open());
            writer.WriteLine($"RDesk backup {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine("包含：設定、文件格子清單、待辦、隨記、剪貼簿記錄、整理記錄。");
            writer.WriteLine("不包含：映射 / 收納資料夾內的實際檔案。");
        }

        File.Move(tmp, zipPath, overwrite: true);
    }

    /// <summary>Restores a backup ZIP; a snapshot of the current data is taken first.</summary>
    /// <returns>Path of the pre-restore snapshot.</returns>
    public string Import(string zipPath)
    {
        string json;
        string? history = null;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.GetEntry(ConfigEntry) ?? throw new InvalidDataException("這不是 RDesk 備份檔（缺少 config.json）");
            using (var reader = new StreamReader(entry.Open()))
            {
                json = reader.ReadToEnd();
            }

            var historyEntry = zip.GetEntry(HistoryEntry);
            if (historyEntry is not null)
            {
                using var reader = new StreamReader(historyEntry.Open());
                history = reader.ReadToEnd();
            }
        }

        var snapshot = CreateSnapshot("pre-restore");
        _config.ReplaceFromJson(json);
        if (history is not null)
        {
            File.WriteAllText(DesktopOrganizerService.HistoryPath, history);
        }

        return snapshot;
    }

    public string CreateSnapshot(string kind = "manual")
    {
        Directory.CreateDirectory(SnapshotFolder);
        var path = Path.Combine(SnapshotFolder, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        Export(path);
        Prune();
        return path;
    }

    /// <summary>Once per day on startup.</summary>
    public bool AutoSnapshotIfDue()
    {
        var last = _config.Current.LastAutoSnapshot;
        if (last is { } l && l.Date == DateTime.Today)
        {
            return false;
        }

        try
        {
            CreateSnapshot("auto");
            _config.Update(c => c.LastAutoSnapshot = DateTime.Now);
            return true;
        }
        catch (Exception ex)
        {
            StartupLog.Write("Auto snapshot failed", ex);
            return false;
        }
    }

    public IReadOnlyList<FileInfo> Snapshots() =>
        Directory.Exists(SnapshotFolder)
            ? new DirectoryInfo(SnapshotFolder).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTime).ToList()
            : Array.Empty<FileInfo>();

    private void Prune()
    {
        foreach (var group in Snapshots().GroupBy(f => f.Name.Split('-')[0]))
        {
            foreach (var old in group.Skip(KeepSnapshots))
            {
                try
                {
                    old.Delete();
                }
                catch
                {
                }
            }
        }
    }
}
