using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using RDesk.Models;

namespace RDesk.Services;

/// <summary>
/// Plays one local file at a time from a folder or a custom playlist. The queue is only file paths.
/// </summary>
public sealed class MusicPlayerService : IDisposable
{
    public const string FolderPlaylistId = "__folder__";
    public const int MaxTracks = 2000;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".ogg", ".opus", ".aif", ".aiff"
    };

    private readonly ConfigService _config;
    private readonly MediaPlayer _player;
    private DispatcherQueue? _dispatcher;
    private bool _opening;

    public MusicPlayerService(ConfigService config)
    {
        _config = config;
        _player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AudioDeviceType = MediaPlayerAudioDeviceType.Multimedia,
            AutoPlay = false,
            Volume = Math.Clamp(config.Current.MusicVolume, 0, 1)
        };
        // Keep play/pause local. SMTC would also resume Chrome / YouTube.
        _player.CommandManager.IsEnabled = false;
        try
        {
            _player.SystemMediaTransportControls.IsEnabled = false;
        }
        catch
        {
        }
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        ReloadQueue(play: false);
    }

    public IReadOnlyList<string> Queue { get; private set; } = Array.Empty<string>();
    public int Index { get; private set; }
    public bool IsPlaying { get; private set; }
    public string? LastError { get; private set; }
    public string CurrentPath => Index >= 0 && Index < Queue.Count ? Queue[Index] : string.Empty;
    public string CurrentTitle => string.IsNullOrEmpty(CurrentPath)
        ? string.Empty
        : Path.GetFileNameWithoutExtension(CurrentPath);

    public TimeSpan Position => _player.PlaybackSession?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration
    {
        get
        {
            var d = _player.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }
    }

    public double Volume
    {
        get => _player.Volume;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            _player.Volume = v;
            _config.Update(c => c.MusicVolume = v);
        }
    }

    public event EventHandler? StateChanged;
    public event EventHandler? QueueChanged;

    public void AttachDispatcher(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public void ReloadQueue(bool play)
    {
        var cfg = _config.Current;
        var id = cfg.MusicPlaylistId;
        List<string> paths;
        if (id == FolderPlaylistId || string.IsNullOrWhiteSpace(id))
        {
            paths = ApplySavedOrder(ScanFolder(cfg.MusicFolder), cfg.MusicFolderOrder);
        }
        else
        {
            var list = cfg.MusicPlaylists.FirstOrDefault(p => p.Id == id);
            paths = list?.Paths.Where(File.Exists).Where(IsAudio).ToList() ?? new List<string>();
        }

        Queue = paths;
        Index = paths.Count == 0 ? 0 : Math.Clamp(cfg.MusicIndex, 0, paths.Count - 1);
        QueueChanged?.Invoke(this, EventArgs.Empty);
        if (play && paths.Count > 0)
        {
            _ = OpenAsync(Index, start: true);
        }
        else
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    public static List<string> ScanFolder(string folder)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return result;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (!IsAudio(file))
                {
                    continue;
                }

                result.Add(file);
                if (result.Count >= MaxTracks)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            StartupLog.Write("Music folder scan failed", ex);
        }

        result.Sort(StringComparer.CurrentCultureIgnoreCase);
        return result;
    }

    private static List<string> ApplySavedOrder(List<string> scanned, List<string>? saved)
    {
        if (scanned.Count == 0 || saved is null || saved.Count == 0)
        {
            return scanned;
        }

        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in scanned)
        {
            byKey.TryAdd(path, path);
        }

        var result = new List<string>(scanned.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in saved)
        {
            if (byKey.TryGetValue(path, out var actual) && used.Add(actual))
            {
                result.Add(actual);
            }
        }

        foreach (var path in scanned)
        {
            if (used.Add(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    public void Toggle()
    {
        if (Queue.Count == 0)
        {
            return;
        }

        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_player.Source is null)
        {
            _ = OpenAsync(Index, start: true);
            return;
        }

        _player.Play();
        IsPlaying = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PlayIndex(int index)
    {
        if (index < 0 || index >= Queue.Count)
        {
            return;
        }

        _ = OpenAsync(index, start: true);
    }

    public string RepeatMode => _config.Current.MusicRepeatMode is "None" or "All" or "One"
        ? _config.Current.MusicRepeatMode
        : "All";

    public void CycleRepeat()
    {
        var next = RepeatMode switch
        {
            "All" => "One",
            "One" => "None",
            _ => "All"
        };
        _config.Update(c => c.MusicRepeatMode = next);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShuffleQueue()
    {
        if (Queue.Count < 2)
        {
            return;
        }

        var list = Queue.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        ApplyOrder(list);
    }

    public void MoveTrack(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Queue.Count || to >= Queue.Count)
        {
            return;
        }

        var list = Queue.ToList();
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        ApplyOrder(list);
    }

    public void ApplyOrder(IReadOnlyList<string> ordered, bool notifyQueue = true)
    {
        var current = CurrentPath;
        var list = ordered.Where(File.Exists).Where(IsAudio).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return;
        }

        Queue = list;
        var found = list.FindIndex(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
        Index = found >= 0 ? found : Math.Clamp(Index, 0, list.Count - 1);
        PersistQueueOrder();
        if (notifyQueue)
        {
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PersistQueueOrder()
    {
        var id = _config.Current.MusicPlaylistId;
        var list = Queue.ToList();
        _config.Update(c =>
        {
            c.MusicIndex = Index;
            if (id == FolderPlaylistId)
            {
                c.MusicFolderOrder = list;
                return;
            }

            var playlist = c.MusicPlaylists.FirstOrDefault(p => p.Id == id);
            if (playlist is not null)
            {
                playlist.Paths = list;
            }
        });
    }

    public void Next() => Advance(fromEnded: false);

    private void Advance(bool fromEnded)
    {
        if (Queue.Count == 0)
        {
            return;
        }

        if (fromEnded && RepeatMode == "One")
        {
            _ = OpenAsync(Index, start: true);
            return;
        }

        var next = Index + 1;
        if (next >= Queue.Count)
        {
            if (fromEnded && RepeatMode != "All")
            {
                _player.Pause();
                IsPlaying = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            next = 0;
        }

        _ = OpenAsync(next, start: true);
    }

    public void Previous()
    {
        if (Queue.Count == 0)
        {
            return;
        }

        if (Position.TotalSeconds > 3)
        {
            _player.PlaybackSession.Position = TimeSpan.Zero;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _ = OpenAsync((Index - 1 + Queue.Count) % Queue.Count, start: true);
    }

    public void Seek(double ratio)
    {
        var duration = Duration;
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        _player.PlaybackSession.Position = TimeSpan.FromTicks((long)(duration.Ticks * Math.Clamp(ratio, 0, 1)));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetFolder(string folder)
    {
        _config.Update(c =>
        {
            c.MusicFolder = folder;
            c.MusicPlaylistId = FolderPlaylistId;
            c.MusicIndex = 0;
            c.MusicFolderOrder = new List<string>();
        });
        ReloadQueue(play: false);
    }

    public void SelectPlaylist(string id)
    {
        _config.Update(c =>
        {
            c.MusicPlaylistId = id;
            c.MusicIndex = 0;
        });
        ReloadQueue(play: false);
    }

    public MusicPlaylistModel AddPlaylist(string name)
    {
        var list = new MusicPlaylistModel { Name = string.IsNullOrWhiteSpace(name) ? "播放清單" : name.Trim() };
        _config.Update(c =>
        {
            c.MusicPlaylists.Add(list);
            c.MusicPlaylistId = list.Id;
            c.MusicIndex = 0;
        });
        ReloadQueue(play: false);
        return list;
    }

    public void DeleteCurrentPlaylist()
    {
        var id = _config.Current.MusicPlaylistId;
        if (id == FolderPlaylistId)
        {
            return;
        }

        _config.Update(c =>
        {
            c.MusicPlaylists.RemoveAll(p => p.Id == id);
            c.MusicPlaylistId = FolderPlaylistId;
            c.MusicIndex = 0;
        });
        ReloadQueue(play: false);
    }

    public void AddPathsToCurrent(IEnumerable<string> paths)
    {
        var id = _config.Current.MusicPlaylistId;
        if (id == FolderPlaylistId)
        {
            return;
        }

        var add = paths.Where(File.Exists).Where(IsAudio).ToList();
        if (add.Count == 0)
        {
            return;
        }

        _config.Update(c =>
        {
            var list = c.MusicPlaylists.FirstOrDefault(p => p.Id == id);
            if (list is null)
            {
                return;
            }

            foreach (var path in add.Where(p => !list.Paths.Contains(p, StringComparer.OrdinalIgnoreCase)))
            {
                list.Paths.Add(path);
            }
        });
        ReloadQueue(play: false);
    }

    public void RemovePathFromCurrent(string path) => RemovePathsFromCurrent(new[] { path });

    public void RemovePathsFromCurrent(IEnumerable<string> paths)
    {
        var id = _config.Current.MusicPlaylistId;
        if (id == FolderPlaylistId)
        {
            return;
        }

        var remove = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (remove.Count == 0)
        {
            return;
        }

        _config.Update(c =>
        {
            var list = c.MusicPlaylists.FirstOrDefault(p => p.Id == id);
            if (list is null)
            {
                return;
            }

            list.Paths.RemoveAll(p => remove.Contains(p));
            if (c.MusicIndex >= list.Paths.Count)
            {
                c.MusicIndex = Math.Max(0, list.Paths.Count - 1);
            }
        });
        ReloadQueue(play: false);
    }

    public void Dispose()
    {
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        _player.Pause();
        _player.Source = null;
        _player.Dispose();
    }

    private async Task OpenAsync(int index, bool start)
    {
        if (_opening || index < 0 || index >= Queue.Count)
        {
            return;
        }

        _opening = true;
        LastError = null;
        try
        {
            var path = Queue[index];
            var file = await StorageFile.GetFileFromPathAsync(path);
            _player.Pause();
            _player.Source = MediaSource.CreateFromStorageFile(file);
            _player.Volume = Math.Clamp(_config.Current.MusicVolume, 0, 1);
            Index = index;
            _config.Update(c => c.MusicIndex = index);
            if (start)
            {
                _player.Play();
                IsPlaying = true;
            }
            else
            {
                IsPlaying = false;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsPlaying = false;
            StartupLog.Write("Music open failed", ex, Queue[index]);
        }
        finally
        {
            _opening = false;
            RaiseOnUi(() => StateChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    private void OnMediaEnded(MediaPlayer sender, object args) =>
        RaiseOnUi(() => Advance(fromEnded: true));

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        LastError = args.ErrorMessage;
        IsPlaying = false;
        RaiseOnUi(() => StateChanged?.Invoke(this, EventArgs.Empty));
    }

    private void RaiseOnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }
}
