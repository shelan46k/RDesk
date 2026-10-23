using RDesk.Models;
using RDesk.Views;

namespace RDesk.Services;

/// <summary>
/// App coordinator — DeskBox-style hide/reveal (tray + hotkey, no floating ball), detached desktop boxes,
/// search popup, desktop organize, reminders, clipboard history and backups.
/// </summary>
public sealed class AppHost
{
    private static AppHost? _current;

    public static AppHost Current => _current ?? throw new InvalidOperationException("AppHost not initialized.");

    public ConfigService Config { get; }
    public ThemeService Theme { get; }
    public MonitorService Monitors { get; }
    public StartupService Startup { get; }
    public HotkeyService Hotkeys { get; }
    public DesktopOrganizerService Organizer { get; }
    public SearchService Search { get; }
    public BackupService Backup { get; }
    public ClipboardHistoryService Clipboard { get; }
    public ReminderService Reminders { get; }
    public UpdateService Updates { get; }
    public MusicPlayerService Music { get; }
    public TrayService? Tray { get; set; }

    public MainSidebarWindow? Sidebar { get; private set; }
    public SettingsWindow? Settings { get; private set; }
    public SearchWindow? SearchPopup { get; private set; }
    public OrganizeWindow? OrganizeView { get; private set; }
    public PlaylistWindow? PlaylistView { get; private set; }

    private readonly List<DetachedBoxWindow> _detached = new();
    public IReadOnlyList<DetachedBoxWindow> DetachedBoxes => _detached;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private Microsoft.UI.Xaml.DispatcherTimer? _boxesFrontTimer;

    public bool IsSidebarVisible { get; private set; } = true;
    private bool _exiting;

    public event EventHandler? VisibilityChanged;

    public AppHost()
    {
        Config = new ConfigService();
        Theme = new ThemeService(Config);
        Monitors = new MonitorService(Config);
        Startup = new StartupService();
        Hotkeys = new HotkeyService(Config).BindInstanceBridge();
        Organizer = new DesktopOrganizerService(Config);
        Search = new SearchService(Config);
        Backup = new BackupService(Config);
        Clipboard = new ClipboardHistoryService(Config);
        Reminders = new ReminderService(this);
        Updates = new UpdateService(Config);
        Music = new MusicPlayerService(Config);
        _current = this;
    }

    public void AttachWindows(MainSidebarWindow sidebar)
    {
        Sidebar = sidebar;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Hotkeys.RevealRequested += (_, _) => ToggleFromTrayOrHotkey();
        Hotkeys.SearchRequested += (_, _) => OpenSearch();
        Hotkeys.BoxesRequested += (_, _) => ShowAllDetachedBoxes();
        Theme.ThemeChanged += (_, _) => _dispatcher?.TryEnqueue(ApplyThemeEverywhere);
        Monitors.DisplaysChanged += (_, _) =>
        {
            if (IsSidebarVisible)
            {
                Sidebar?.Reposition();
            }
        };
        Monitors.StartWatching();

        // Keep the run-at-startup registry entry in sync with the saved preference.
        if (Startup.IsEnabled() != Config.Current.RunOnStartup)
        {
            Startup.SetEnabled(Config.Current.RunOnStartup);
        }

        Organizer.UiDispatcher = _dispatcher;
        Organizer.AutoOrganized += (_, batch) =>
        {
            OnStorageChanged();
            Tray?.ShowNotification("RDesk 自動整理",
                $"已把 {batch.Moves.Count} 個新檔案收進格子：{string.Join("、", batch.Moves.Take(3).Select(m => Path.GetFileName(m.To)))}");
        };
        Organizer.ApplyAutoSetting();

        Reminders.ReminderDue += (_, todo) => Sidebar?.TodoView.ShowReminder(todo);
        Reminders.Start();
        if (Sidebar is not null)
        {
            Clipboard.Start(Sidebar);
        }

        _ = Task.Run(() => Backup.AutoSnapshotIfDue());
        _ = Task.Run(() => Updates.CheckAndDownloadAsync());
    }

    public void RestoreDetachedBoxes()
    {
        foreach (var group in Config.Current.FavoriteGroups.Where(g => g.IsDetachedToDesktop).ToList())
        {
            OpenDetachedBox(group);
        }
    }

    public void OpenDetachedBox(FavoriteGroupModel group)
    {
        var existing = _detached.FirstOrDefault(d => d.GroupId == group.Id);
        if (existing is not null)
        {
            existing.BringToFront();
            return;
        }

        group.IsDetachedToDesktop = true;
        Config.SaveImmediate();
        var win = new DetachedBoxWindow(this, group);
        _detached.Add(win);
        win.Activate();
    }

    public void CloseDetachedBox(string groupId)
    {
        _detached.FirstOrDefault(d => d.GroupId == groupId)?.Recall();
    }

    public void NotifyDetachedClosed(DetachedBoxWindow window)
    {
        _detached.Remove(window);
        if (!_exiting)
        {
            Sidebar?.FavoritesView.ReloadGroups(Sidebar.FavoritesView.CurrentGroup is { } g
                ? Config.Current.FavoriteGroups.IndexOf(g)
                : 0);
        }
    }

    /// <summary>Keeps the sidebar card and any detached box of the same group in sync.</summary>
    public void NotifyFavoritesChanged(string groupId, object source)
    {
        foreach (var box in _detached.Where(d => d.GroupId == groupId && !ReferenceEquals(d, source)))
        {
            box.Reload();
        }

        if (!ReferenceEquals(Sidebar?.FavoritesView, source))
        {
            Sidebar?.FavoritesView.RefreshIfShowing(groupId);
        }
    }

    /// <summary>Organize / undo created folders or groups: refresh every file box surface.</summary>
    public void OnStorageChanged()
    {
        var current = Sidebar?.FavoritesView.CurrentGroup;
        Sidebar?.FavoritesView.ReloadGroups(current is null ? 0 : Math.Max(0, Config.Current.FavoriteGroups.IndexOf(current)));
        foreach (var box in _detached)
        {
            box.Reload();
        }

        _ = Search.EnsureIndexAsync(force: true);
    }

    public void ShowAllDetachedBoxes()
    {
        foreach (var group in Config.Current.FavoriteGroups.Where(g => g.IsDetachedToDesktop &&
                     _detached.All(d => d.GroupId != g.Id)).ToList())
        {
            OpenDetachedBox(group);
        }

        foreach (var box in _detached.ToList())
        {
            box.BringToFront();
        }

        if (Config.Current.PinToDesktop && _detached.Count > 0)
        {
            _boxesFrontTimer ??= new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _boxesFrontTimer.Tick -= RestoreBoxesDesktopLayer;
            _boxesFrontTimer.Tick += RestoreBoxesDesktopLayer;
            _boxesFrontTimer.Stop();
            _boxesFrontTimer.Start();
        }
    }

    private void RestoreBoxesDesktopLayer(object? sender, object e)
    {
        _boxesFrontTimer?.Stop();
        ApplyDesktopLayerEverywhere();
    }

    public void ApplyDesktopLayerEverywhere()
    {
        Sidebar?.Reposition();
        foreach (var box in _detached)
        {
            box.ApplyDesktopLayer();
        }
    }

    public void ShowSidebar()
    {
        if (Sidebar is null)
        {
            return;
        }

        Sidebar.ShowPanel();
        IsSidebarVisible = true;
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void HideSidebar()
    {
        if (Sidebar is null)
        {
            return;
        }

        Sidebar.HidePanel();
        IsSidebarVisible = false;
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleFromTrayOrHotkey()
    {
        if (IsSidebarVisible)
        {
            HideSidebar();
        }
        else
        {
            ShowSidebar();
        }
    }

    public void OpenSettings()
    {
        if (Settings is null)
        {
            Settings = new SettingsWindow();
            Settings.Closed += (_, _) => Settings = null;
        }

        Settings.Activate();
    }

    public void OpenSearch(string? query = null)
    {
        if (SearchPopup is null || SearchPopup.IsClosed)
        {
            SearchPopup = new SearchWindow(this);
            SearchPopup.Closed += (s, _) =>
            {
                if (ReferenceEquals(SearchPopup, s))
                {
                    SearchPopup = null;
                }
            };
            SearchPopup.Activate();
        }

        SearchPopup.ShowSearch(query);
    }

    public void OpenPlaylist()
    {
        if (PlaylistView is null)
        {
            PlaylistView = new PlaylistWindow(this);
            PlaylistView.Closed += (_, _) => PlaylistView = null;
        }

        PlaylistView.Activate();
    }

    public OrganizeWindow OpenOrganizer(string? folder = null)
    {
        if (OrganizeView is null)
        {
            OrganizeView = new OrganizeWindow(this, folder);
            OrganizeView.Closed += (_, _) => OrganizeView = null;
        }

        OrganizeView.Activate();
        return OrganizeView;
    }

    /// <summary>After a backup restore: rebuild every surface from the new config.</summary>
    public void ReloadAfterRestore()
    {
        foreach (var box in _detached.ToList())
        {
            box.Close();
        }

        Sidebar?.RebindModules();
        RestoreDetachedBoxes();
        Organizer.ApplyAutoSetting();
        Hotkeys.RefreshRegistration();
        Theme.NotifyAppearanceChanged();
    }

    public void ApplyThemeEverywhere()
    {
        Sidebar?.ApplyTheme();
        Settings?.ApplyTheme();
        SearchPopup?.ApplyTheme();
        OrganizeView?.ApplyTheme();
        PlaylistView?.ApplyTheme();
        foreach (var box in _detached)
        {
            box.ApplyTheme();
        }
    }

    public void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Config.SaveImmediate();
        Music.Dispose();
        Hotkeys.Dispose();
        Clipboard.Stop();
        Organizer.Dispose();
        Tray?.Dispose();
        foreach (var box in _detached.ToList())
        {
            box.Close();
        }

        Settings?.Close();
        SearchPopup?.Close();
        OrganizeView?.Close();
        PlaylistView?.Close();
        Sidebar?.Close();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }
}
