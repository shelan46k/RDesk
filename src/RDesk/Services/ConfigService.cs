using System.Text.Json;
using RDesk.Helpers;
using RDesk.Models;

namespace RDesk.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Keep Chinese readable in config.json instead of \uXXXX escapes.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ConfigFilePath => AppPaths.ConfigFilePath;

    private readonly string _configPath;
    private readonly object _gate = new();
    private CancellationTokenSource? _debounceCts;
    private AppConfig _config = new();

    public ConfigService()
    {
        AppPaths.EnsureDataDirectory();
        _configPath = ConfigFilePath;
        Load();
    }

    public AppConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    public event EventHandler? ConfigChanged;

    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_configPath))
            {
                _config = new AppConfig
                {
                    LocationName = "台中市 · 南區",
                    FavoriteGroups =
                    [
                        new FavoriteGroupModel { Title = "預設收藏" }
                    ]
                };
                Normalize(_config);
                SaveImmediate();
                return;
            }

            try
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                if (_config.FavoriteGroups.Count == 0)
                {
                    _config.FavoriteGroups.Add(new FavoriteGroupModel { Title = "預設收藏" });
                }
            }
            catch
            {
                _config = new AppConfig();
            }

            Normalize(_config);
        }
    }

    private static void Normalize(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.QuickNoteContent))
        {
            config.Notes.Insert(0, new NoteItemModel { Content = config.QuickNoteContent });
            config.QuickNoteContent = string.Empty;
        }

        if (config.OrganizeRules.Count == 0)
        {
            config.OrganizeRules.AddRange(FileCategories.DefaultRules());
        }

        config.FavoriteGroups.ForEach(g => g.Items ??= new());
        config.TodoItems.ForEach(t => t.Attachments ??= new());
        config.Notes.ForEach(n => n.Attachments ??= new());
        config.MusicPlaylists ??= new();
        config.MusicPlaylists.ForEach(p => p.Paths ??= new());
        config.MusicFolderOrder ??= new();
        config.MusicVolume = Math.Clamp(config.MusicVolume, 0.0, 1.0);
        if (string.IsNullOrWhiteSpace(config.MusicPlaylistId))
        {
            config.MusicPlaylistId = "__folder__";
        }

        if (config.MusicRepeatMode is not ("None" or "All" or "One"))
        {
            config.MusicRepeatMode = "All";
        }

        if (!config.TransparencyMigrated)
        {
            config.PanelTransparency = Math.Clamp(1.0 - config.PanelOpacity, 0.0, 1.0);
            config.TransparencyMigrated = true;
        }

        config.PanelTransparency = Math.Clamp(config.PanelTransparency, 0.0, 1.0);
        config.CardTransparency = Math.Clamp(config.CardTransparency, 0.0, 1.0);
        config.PanelOpacity = 1.0 - config.PanelTransparency;

        if (string.IsNullOrWhiteSpace(config.UpdateGithubUrl))
        {
            config.UpdateGithubUrl = AppConfig.DefaultUpdateGithubUrl;
        }

        if (IsUnsafeStorageRoot(config.StorageRoot) && !string.IsNullOrWhiteSpace(config.StorageRoot))
        {
            config.StorageRoot = string.Empty;
        }
    }

    /// <summary>Root folder that storage boxes (收納格子) create their folders in.</summary>
    public string StorageRoot
    {
        get
        {
            var raw = Current.StorageRoot;
            return IsUnsafeStorageRoot(raw)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RDesk 收納")
                : raw!;
        }
    }

    private static bool IsUnsafeStorageRoot(string? path) =>
        string.IsNullOrWhiteSpace(path) || FileOps.IsEphemeralPath(path);

    /// <summary>Replaces the whole config (backup restore) and persists it.</summary>
    public void ReplaceFromJson(string json)
    {
        var restored = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                       ?? throw new InvalidDataException("config.json 內容無效");
        lock (_gate)
        {
            _config = restored;
            if (_config.FavoriteGroups.Count == 0)
            {
                _config.FavoriteGroups.Add(new FavoriteGroupModel { Title = "預設收藏" });
            }

            Normalize(_config);
        }

        SaveImmediate();
    }

    public void CancelPendingSaves()
    {
        _debounceCts?.Cancel();
    }

    public void SaveDebounced(int delayMs = 400)
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                SaveImmediate();
            }
            catch (TaskCanceledException)
            {
            }
        }, token);
    }

    /// <summary>
    /// Re-attaches storage folders that still exist on disk when the box entry
    /// disappeared from config (self-test leak, restore, or a wiped settings file).
    /// </summary>
    public int RecoverOrphanedStorageBoxes()
    {
        string root;
        lock (_gate)
        {
            root = string.IsNullOrWhiteSpace(_config.StorageRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RDesk 收納")
                : _config.StorageRoot;
        }

        var remapped = 0;
        lock (_gate)
        {
            foreach (var group in _config.FavoriteGroups.Where(g => g.IsStorageBox))
            {
                if (!FileOps.IsEphemeralPath(group.MappedFolderPath))
                {
                    continue;
                }

                var safe = string.Concat((group.Title ?? string.Empty).Split(Path.GetInvalidFileNameChars())).Trim();
                if (safe.Length == 0)
                {
                    safe = "收納";
                }

                group.MappedFolderPath = FileOps.UniquePath(Path.Combine(root, safe));
                Directory.CreateDirectory(group.MappedFolderPath);
                remapped++;
            }
        }

        if (remapped > 0)
        {
            SaveImmediate();
            StartupLog.Write("Moved storage boxes off temp", detail: $"{remapped}");
        }

        if (!Directory.Exists(root))
        {
            return remapped;
        }

        var added = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            lock (_gate)
            {
                if (_config.FavoriteGroups.Any(g =>
                        !string.IsNullOrWhiteSpace(g.MappedFolderPath) &&
                        string.Equals(g.MappedFolderPath, dir, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _config.FavoriteGroups.Add(new FavoriteGroupModel
                {
                    Title = Path.GetFileName(dir.TrimEnd('\\')),
                    IsStorageBox = true,
                    MappedFolderPath = dir,
                    SortBy = "Date",
                    SortDescending = true
                });
                added++;
            }
        }

        if (added > 0)
        {
            SaveImmediate();
            StartupLog.Write("Recovered storage boxes", detail: $"{added} from {root}");
        }

        return added + remapped;
    }

    public void SaveImmediate()
    {
        lock (_gate)
        {
            try
            {
                var json = JsonSerializer.Serialize(_config, JsonOptions);
                var tmp = _configPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _configPath, overwrite: true);
            }
            catch (InvalidOperationException)
            {
                // A list was mutated on the UI thread mid-serialize; retry shortly.
                SaveDebounced();
                return;
            }
            catch (IOException ex)
            {
                StartupLog.Write("Config save failed", ex);
                return;
            }
        }

        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AppConfig> mutator)
    {
        lock (_gate)
        {
            mutator(_config);
        }

        SaveDebounced();
    }
}
