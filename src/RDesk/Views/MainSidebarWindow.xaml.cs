using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDesk.Helpers;
using RDesk.Services;
using RDesk.Views.Controls;
using WinRT.Interop;
using WinUIEx;
using StartupLog = RDesk.StartupLog;

namespace RDesk.Views;

public sealed partial class MainSidebarWindow : WindowEx
{
    private readonly AppHost _host;
    private AppWindow? _appWindow;
    private bool _modulesBound;

    public MainSidebarWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        FavoritesCard.DetachRequested += (_, group) => _host.OpenDetachedBox(group);
        RecentCard.AddToFavoritesRequested += (_, path) => FavoritesCard.AddFavoritePath(path);

        Activated += OnActivated;
        Closed += (_, _) => _host.Exit();
    }

    public FavoritesControl FavoritesView => FavoritesCard;
    public WeatherClockControl WeatherView => WeatherCard;
    public RecentFilesControl RecentView => RecentCard;
    public TodoNotesControl TodoView => TodoCard;
    public MusicControl MusicView => MusicCard;
    public SystemNetworkControl SystemView => SystemCard;
    public bool IsTipVisible => TipText.Parent is UIElement { Visibility: Visibility.Visible };
    public bool ModulesReady => _modulesBound;

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_appWindow is not null)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // Like DeskBox: no taskbar button, not in Alt+Tab; tray icon + hotkey are the entry points.
        _appWindow.IsShownInSwitchers = false;
        ApplyTheme();
        Reposition();
        BindModules();
        this.Show();
        DesktopPinHelper.BringToTop(hwnd);
        _host.Hotkeys.Start(this);
        UpdateTip();
        _host.RestoreDetachedBoxes();
        StartupLog.Write("MainSidebar ready",
            detail: $"pos={_appWindow.Position.X},{_appWindow.Position.Y} size={_appWindow.Size.Width}x{_appWindow.Size.Height}");
    }

    private void BindModules()
    {
        if (_modulesBound)
        {
            return;
        }

        _modulesBound = true;
        WeatherCard.Bind(_host.Config);
        RecentCard.Bind(_host.Config);
        FavoritesCard.Bind(_host.Config);
        TodoCard.Bind(_host.Config, _host.Clipboard);
        MusicCard.Bind(_host.Music);
        ApplyCardVisibility();
    }

    /// <summary>Re-reads config into every card (after a backup restore).</summary>
    public void RebindModules()
    {
        WeatherCard.Bind(_host.Config);
        RecentCard.Bind(_host.Config);
        FavoritesCard.Bind(_host.Config);
        TodoCard.ShowMode(TodoCard.Mode);
        MusicCard.Bind(_host.Music);
        ApplyCardVisibility();
        ApplyTheme();
        Reposition();
        UpdateTip();
    }

    public void ApplyCardVisibility()
    {
        var c = _host.Config.Current;
        Set(WeatherCard, c.ShowWeatherCard);
        Set(RecentCard, c.ShowRecentCard);
        Set(FavoritesCard, c.ShowFavoritesCard);
        Set(TodoCard, c.ShowTodoCard);
        Set(MusicCard, c.ShowMusicCard);
        SystemCard.SetParts(c.ShowSystemCard, c.ShowNetworkCard);
        TodoCard.ApplyTabVisibility();
        if (TipText.Parent is UIElement tip)
        {
            Set(tip, c.ShowSidebarTip);
        }

        static void Set(UIElement card, bool visible) =>
            card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateTip()
    {
        var cfg = _host.Config.Current;
        var hotkey = cfg.EnableRevealHotkey && _host.Hotkeys.IsRegistered
            ? $"或按 {cfg.RevealHotkey} "
            : string.Empty;
        var search = cfg.EnableSearchHotkey && _host.Hotkeys.IsSearchRegistered
            ? $"按 {cfg.SearchHotkey} 可隨時搜尋。"
            : string.Empty;
        var boxes = cfg.EnableBoxesHotkey && _host.Hotkeys.IsBoxesRegistered
            ? $"按 {cfg.BoxesHotkey} 可把桌面上的文件格子提到最前面。"
            : string.Empty;
        TipText.Text = $"按「隱藏」後桌面不留任何東西；點右下角系統匣的 RDesk 圖示{hotkey}即可再次展開。{search}{boxes}";
        ToolTipService.SetToolTip(SearchButton, cfg.EnableSearchHotkey ? $"搜尋（{cfg.SearchHotkey}）" : "搜尋");
    }

    public void ApplyTheme()
    {
        _host.Theme.ApplyTo(RootGrid);
        _host.Theme.ApplyWindowChrome(this);
        TodoCard.RefreshTheme();
        // Sun when dark (click → light), moon when light (click → dark).
        ThemeGlyph.Glyph = _host.Theme.IsDark ? "\uE706" : "\uE708";
    }

    public void Reposition()
    {
        if (_appWindow is null)
        {
            return;
        }

        _host.Monitors.DockSidebar(this, _appWindow);
        var hwnd = WindowNative.GetWindowHandle(this);
        var attached = DesktopPinHelper.SetPinToDesktop(hwnd, _host.Config.Current.PinToDesktop);
        if (_host.Config.Current.PinToDesktop && !attached)
        {
            StartupLog.Write("Desktop layer attach failed (no SHELLDLL_DefView)");
        }
    }

    public void ShowPanel()
    {
        Reposition();
        this.Show();
        DesktopPinHelper.BringToTop(WindowNative.GetWindowHandle(this));
        _ = RecentCard.ReloadAsync();
    }

    public void HidePanel() => this.Hide();

    public Task RefreshWeatherAsync() => WeatherCard.RefreshWeatherAsync();

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _host.OpenSearch();

    private void ThemeButton_Click(object sender, RoutedEventArgs e) => _host.Theme.Toggle();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _host.OpenSettings();

    private void HideButton_Click(object sender, RoutedEventArgs e) => _host.HideSidebar();
}
