using System.Collections.ObjectModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RDesk.Helpers;
using RDesk.Models;
using RDesk.Services;
using RDesk.Views.Controls;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using WinUIEx;

namespace RDesk.Views;

/// <summary>DeskBox-style search popup (own hotkey): find, then act — open, reveal, copy path, add to box / todo.</summary>
public sealed partial class SearchWindow : WindowEx
{
    private static readonly (string Key, string Label)[] FilterKeys =
        [("All", "全部"), ("App", "應用"), ("File", "檔案"), ("Todo", "待辦 / 隨記")];

    private readonly AppHost _host;
    private readonly ObservableCollection<SearchResult> _results = new();
    private readonly SegmentBar _filters = new() { SegmentFontSize = 11 };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private CancellationTokenSource? _cts;
    private AppWindow? _appWindow;
    private bool _closed;
    private string _filter = "All";

    public SearchWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        ResultsList.ItemsSource = _results;
        foreach (var (_, label) in FilterKeys)
        {
            _filters.AddSegment(label);
        }

        _filters.SelectedIndex = 0;
        _filters.SelectionChanged += (_, index) =>
        {
            _filter = FilterKeys[index].Key;
            _ = RunQueryAsync();
        };
        FilterHost.Content = _filters;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunQueryAsync();
        };
        Activated += OnActivated;
        Closed += (_, _) => _closed = true;
    }

    public bool IsClosed => _closed;
    public IReadOnlyList<SearchResult> Results => _results;
    public Task? LastQuery { get; private set; }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        if (_appWindow is null)
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            _appWindow.IsShownInSwitchers = false;
            ApplyTheme();
            _appWindow.Show();
        }

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Popup behaviour: clicking elsewhere hides it.
            this.Hide();
        }
    }

    public void ApplyTheme()
    {
        if (_closed)
        {
            return;
        }

        _host.Theme.ApplyTo(RootGrid);
        _host.Theme.ApplyWindowChrome(this);
        _filters.Refresh();
    }

    /// <summary>Shows centered on the monitor under the cursor, input focused.</summary>
    public void ShowSearch(string? query = null)
    {
        CenterOnCursorMonitor();
        this.Show();
        Activate();
        DesktopPinHelper.BringToTop(WindowNative.GetWindowHandle(this));
        if (query is not null)
        {
            QueryBox.Text = query;
        }

        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
        RenderIdle();
        _ = _host.Search.EnsureIndexAsync();
        if (!string.IsNullOrWhiteSpace(QueryBox.Text))
        {
            LastQuery = RunQueryAsync();
        }
    }

    private void CenterOnCursorMonitor()
    {
        if (_appWindow is null)
        {
            return;
        }

        GetCursorPos(out var pt);
        var area = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Primary).WorkArea;
        var size = _appWindow.Size;
        _appWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (int)(area.Height * 0.18)));
    }

    private void RenderIdle()
    {
        var empty = string.IsNullOrWhiteSpace(QueryBox.Text);
        IdlePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty)
        {
            return;
        }

        HistoryHost.Children.Clear();
        foreach (var query in _host.Config.Current.SearchHistory.Take(10))
        {
            var chip = new Button
            {
                Content = query,
                Style = (Style)Application.Current.Resources["PillButtonStyle"],
                Margin = new Thickness(0, 0, 6, 6)
            };
            chip.Click += (_, _) =>
            {
                QueryBox.Text = query;
                QueryBox.SelectionStart = query.Length;
            };
            HistoryHost.Children.Add(chip);
        }

        IdleHint.Text = _host.Config.Current.SearchHistory.Count == 0
            ? "輸入關鍵字即可搜尋：開始選單的應用、桌面、文件格子、最近檔案、待辦與隨記，以及 Windows 索引中的檔案。"
            : "點最近搜尋可再次查詢。";
        StatusText.Text = $"已索引 {_host.Search.IndexedCount} 個項目";
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderIdle();
        _debounce.Stop();
        _debounce.Start();
    }

    public async Task RunQueryAsync()
    {
        var query = QueryBox.Text?.Trim() ?? string.Empty;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        if (query.Length == 0)
        {
            _results.Clear();
            RenderIdle();
            return;
        }

        await _host.Search.EnsureIndexAsync();
        if (cts.IsCancellationRequested)
        {
            return;
        }

        var local = _host.Search.QueryLocal(query, _filter);
        Fill(local);
        StatusText.Text = $"{local.Count} 個結果 · 正在查詢 Windows 索引…";

        if (_filter is "All" or "File")
        {
            try
            {
                var indexed = await _host.Search.QueryWindowsIndexAsync(query, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                var known = new HashSet<string>(local.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
                var merged = local.Concat(indexed.Where(r => !known.Contains(r.Path))).Take(80).ToList();
                Fill(merged);
                StatusText.Text = $"{merged.Count} 個結果";
            }
            catch (OperationCanceledException)
            {
            }
        }
        else
        {
            StatusText.Text = $"{local.Count} 個結果";
        }
    }

    private void Fill(List<SearchResult> results)
    {
        _results.Clear();
        foreach (var r in results)
        {
            _results.Add(r);
        }

        if (_results.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                this.Hide();
                break;
            case VirtualKey.Down:
                MoveSelection(1);
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                break;
            case VirtualKey.Enter when ResultsList.SelectedItem is SearchResult r:
                if (ctrl)
                {
                    Reveal(r);
                }
                else
                {
                    Launch(r);
                }

                break;
            case VirtualKey.C when ctrl && shift && ResultsList.SelectedItem is SearchResult r:
                if (r.Path.Length > 0)
                {
                    FileOps.CopyText(r.Path);
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void MoveSelection(int delta)
    {
        if (_results.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(ResultsList.SelectedIndex + delta, 0, _results.Count - 1);
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(_results[index]);
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResult r)
        {
            Launch(r);
        }
    }

    private void ResultsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SearchResult r)
        {
            return;
        }

        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        var hasPath = r.Path.Length > 0;
        Add("開啟", "\uE8E5", () => Launch(r));
        Add("開啟所在位置", "\uE838", () => Reveal(r), hasPath);
        Add("複製路徑", "\uE71B", () => FileOps.CopyText(r.Path), hasPath);
        Add("複製檔案", "\uE8C8", () => _ = FileOps.CopyFilesAsync(new[] { r.Path }), hasPath && r.Kind is "File" or "Folder");
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("加入目前的文件格子", "\uE734", () => _host.Sidebar?.FavoritesView.AddFavoritePath(r.Path), hasPath);
        Add("附加到新待辦", "\uE9D5", () => _host.Sidebar?.TodoView.AddTodo($"處理 {r.Title}", new[] { r.Path }), hasPath);
        Add("存到隨記", "\uE70B", () => _host.Sidebar?.TodoView.AddNote(r.Title, hasPath ? new[] { r.Path } : null));
        menu.ShowAt((FrameworkElement)e.OriginalSource, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = e.GetPosition((UIElement)e.OriginalSource)
        });
        e.Handled = true;
    }

    private void Launch(SearchResult r)
    {
        _host.Search.RememberQuery(QueryBox.Text ?? string.Empty);
        switch (r.Kind)
        {
            case "Todo" when r.Tag is TodoItemModel:
                _host.ShowSidebar();
                _host.Sidebar?.TodoView.SetFilter("All");
                _host.Sidebar?.TodoView.ShowMode(TodoNotesControl.TodoMode);
                break;
            case "Note":
                _host.ShowSidebar();
                _host.Sidebar?.TodoView.ShowMode(TodoNotesControl.NoteMode);
                break;
            default:
                FileOps.Open(r.Path);
                break;
        }

        this.Hide();
    }

    private void Reveal(SearchResult r)
    {
        if (r.Path.Length == 0)
        {
            return;
        }

        _host.Search.RememberQuery(QueryBox.Text ?? string.Empty);
        FileOps.Reveal(r.Path);
        this.Hide();
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
