using System.Data.OleDb;
using RDesk.Models;

namespace RDesk.Services;

public sealed class SearchResult
{
    /// <summary>App / File / Folder / Todo / Note.</summary>
    public string Kind { get; init; } = "File";
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public object? Tag { get; init; }
    public int Score { get; set; }

    public string KindLabel => Kind switch
    {
        "App" => "應用",
        "Folder" => "資料夾",
        "Todo" => "待辦",
        "Note" => "隨記",
        _ => "檔案"
    };

    public string Glyph => Kind switch
    {
        "App" => "\uE71D",
        "Folder" => "\uE8B7",
        "Todo" => "\uE9D5",
        "Note" => "\uE70B",
        _ => "\uE8A5"
    };
}

/// <summary>
/// DeskBox 桌面搜尋: one entry for apps (Start menu), desktop, file boxes, recent files, todos and notes,
/// plus the Windows Search index for everything else on disk.
/// </summary>
public sealed class SearchService
{
    private readonly ConfigService _config;
    private List<SearchResult> _index = new();
    private DateTime _indexedAt;
    private Task? _indexTask;
    private readonly object _gate = new();

    public SearchService(ConfigService config)
    {
        _config = config;
    }

    public int IndexedCount
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    public Task EnsureIndexAsync(bool force = false)
    {
        lock (_gate)
        {
            if (!force && _indexTask is not null && (DateTime.Now - _indexedAt) < TimeSpan.FromMinutes(5))
            {
                return _indexTask;
            }

            _indexedAt = DateTime.Now;
            var groups = _config.Current.FavoriteGroups.ToList();
            _indexTask = Task.Run(() =>
            {
                var built = BuildIndex(groups);
                lock (_gate)
                {
                    _index = built;
                }
            });
            return _indexTask;
        }
    }

    private static List<SearchResult> BuildIndex(List<FavoriteGroupModel> groups)
    {
        var results = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPath(string path, string kind, string subtitle)
        {
            if (!seen.Add(path))
            {
                return;
            }

            results.Add(new SearchResult
            {
                Kind = kind,
                Title = kind == "App" ? System.IO.Path.GetFileNameWithoutExtension(path) : System.IO.Path.GetFileName(path.TrimEnd('\\')),
                Subtitle = subtitle,
                Path = path
            });
        }

        // Apps: Start menu shortcuts (all users + current user).
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
                 })
        {
            foreach (var lnk in SafeEnumerate(root, "*.lnk", SearchOption.AllDirectories, 3000))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("解除安裝", StringComparison.Ordinal))
                {
                    continue;
                }

                AddPath(lnk, "App", "應用程式");
            }
        }

        // Desktop (user + public).
        foreach (var desktop in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                 })
        {
            foreach (var entry in SafeEnumerate(desktop, "*", SearchOption.TopDirectoryOnly, 2000))
            {
                var ext = System.IO.Path.GetExtension(entry).ToLowerInvariant();
                AddPath(entry, ext is ".lnk" or ".url" ? "App" : Directory.Exists(entry) ? "Folder" : "File", "桌面");
            }
        }

        // File boxes.
        foreach (var group in groups)
        {
            if (group.IsFolderBacked && Directory.Exists(group.MappedFolderPath ?? string.Empty))
            {
                foreach (var entry in SafeEnumerate(group.MappedFolderPath!, "*", SearchOption.AllDirectories, 4000))
                {
                    AddPath(entry, Directory.Exists(entry) ? "Folder" : "File", $"格子 · {group.Title}");
                }
            }
            else
            {
                foreach (var item in group.Items)
                {
                    AddPath(item.FullPath, item.IsDirectory ? "Folder" : "File", $"格子 · {group.Title}");
                }
            }
        }

        // Recent files (Windows Recent shortcuts → names only; resolved when opened).
        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        foreach (var lnk in SafeEnumerate(recent, "*.lnk", SearchOption.TopDirectoryOnly, 500))
        {
            var target = Helpers.ShellHelper.ResolveShortcutTarget(lnk);
            if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
            {
                AddPath(target, Directory.Exists(target) ? "Folder" : "File", "最近使用");
            }
        }

        return results;
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern, SearchOption option, int cap)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                MaxRecursionDepth = 4
            };
            return Directory.EnumerateFileSystemEntries(root, pattern, options).Take(cap).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Instant results from RDesk's own index, todos and notes.</summary>
    public List<SearchResult> QueryLocal(string query, string filter = "All", int max = 60)
    {
        var tokens = Tokens(query);
        List<SearchResult> index;
        lock (_gate)
        {
            index = _index;
        }

        var results = new List<SearchResult>();
        if (tokens.Length == 0)
        {
            return results;
        }

        foreach (var item in index)
        {
            var score = Score(item.Title, tokens);
            if (score > 0)
            {
                item.Score = score + (item.Kind == "App" ? 15 : 0);
                results.Add(item);
            }
        }

        foreach (var todo in _config.Current.TodoItems.ToList())
        {
            var score = Score(todo.Content, tokens);
            if (score > 0)
            {
                results.Add(new SearchResult
                {
                    Kind = "Todo",
                    Title = todo.Content,
                    Subtitle = todo.IsCompleted ? "已完成的待辦" : todo.DueAt is { } d ? $"待辦 · {ReminderService.FormatDue(d)}" : "待辦",
                    Tag = todo,
                    Score = score - (todo.IsCompleted ? 10 : 0)
                });
            }
        }

        foreach (var note in _config.Current.Notes.ToList())
        {
            var score = Score(note.Content, tokens);
            if (score > 0)
            {
                results.Add(new SearchResult
                {
                    Kind = "Note",
                    Title = note.Content.Split('\n')[0].Trim(),
                    Subtitle = $"隨記 · {note.UpdatedAt:MM/dd HH:mm}",
                    Tag = note,
                    Score = score
                });
            }
        }

        return Filter(results, filter)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title.Length)
            .Take(max)
            .ToList();
    }

    public static IEnumerable<SearchResult> Filter(IEnumerable<SearchResult> results, string filter) => filter switch
    {
        "App" => results.Where(r => r.Kind == "App"),
        "File" => results.Where(r => r.Kind is "File" or "Folder"),
        "Todo" => results.Where(r => r.Kind is "Todo" or "Note"),
        _ => results
    };

    /// <summary>Windows Search index (SYSTEMINDEX) — whole-disk file names; empty when the service is off.</summary>
    public async Task<List<SearchResult>> QueryWindowsIndexAsync(string query, CancellationToken token, int max = 40)
    {
        var tokens = Tokens(query);
        if (tokens.Length == 0 || query.Trim().Length < 2)
        {
            return new List<SearchResult>();
        }

        return await Task.Run(() =>
        {
            var list = new List<SearchResult>();
            try
            {
                var where = string.Join(" AND ", tokens.Select(t => $"System.FileName LIKE '%{t.Replace("'", "''").Replace("%", "")}%'"));
                var sql = $"SELECT TOP {max} System.ItemPathDisplay, System.FileName, System.ItemType FROM SYSTEMINDEX " +
                          $"WHERE {where} AND (SCOPE='file:') ORDER BY System.DateModified DESC";
                using var connection = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
                connection.Open();
                using var command = new OleDbCommand(sql, connection);
                using var reader = command.ExecuteReader();
                while (reader is not null && reader.Read())
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    var path = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    var type = reader.IsDBNull(2) ? string.Empty : reader.GetValue(2)?.ToString() ?? string.Empty;
                    list.Add(new SearchResult
                    {
                        Kind = string.Equals(type, "Directory", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path) ? "Folder" : "File",
                        Title = System.IO.Path.GetFileName(path),
                        Subtitle = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
                        Path = path,
                        Score = Score(System.IO.Path.GetFileName(path), tokens)
                    });
                }
            }
            catch (Exception ex)
            {
                StartupLog.Write("Windows Search query failed", detail: ex.Message);
            }

            return list;
        }, token);
    }

    public void RememberQuery(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return;
        }

        var history = _config.Current.SearchHistory;
        history.RemoveAll(h => string.Equals(h, query, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, query);
        if (history.Count > 12)
        {
            history.RemoveRange(12, history.Count - 12);
        }

        _config.SaveDebounced();
    }

    private static string[] Tokens(string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int Score(string text, string[] tokens)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var total = 0;
        foreach (var token in tokens)
        {
            var index = text.IndexOf(token, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
            {
                return 0;
            }

            total += index == 0 ? 100 : 50;
            if (text.Length == token.Length)
            {
                total += 50;
            }
        }

        return total;
    }
}
