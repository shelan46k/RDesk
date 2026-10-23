using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RDesk.Services;
using RDesk.Views.Controls;
using Windows.System;
using WinUIEx;
using IOPath = System.IO.Path;

namespace RDesk.Views;

public sealed partial class PlaylistWindow : WindowEx
{
    private readonly AppHost _host;
    private readonly ObservableCollection<MusicTrackRow> _tracks = new();
    private bool _closed;
    private bool _loaded;
    private string _filter = string.Empty;

    public PlaylistWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        this.CenterOnScreen();
        TrackList.ItemsSource = _tracks;
        Closed += OnClosed;
        ApplyTheme();
        BindPlayer();
        ReloadTracks(restoreSelection: false, scrollPlaying: true);
        Activated += (_, _) =>
        {
            if (_loaded || _closed)
            {
                return;
            }

            _loaded = true;
            ApplyTheme();
        };
    }

    public void ApplyTheme()
    {
        if (!_closed)
        {
            _host.Theme.ApplyTo(RootGrid, overlay: false);
        }
    }

    private void BindPlayer()
    {
        _host.Music.StateChanged += OnState;
        _host.Music.QueueChanged += OnQueue;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _host.Music.StateChanged -= OnState;
        _host.Music.QueueChanged -= OnQueue;
    }

    private void OnState(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed)
        {
            return;
        }

        UpdatePlayingFlags();
        UpdateChrome();
    });

    private void OnQueue(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closed)
        {
            ReloadTracks(restoreSelection: true, scrollPlaying: false);
        }
    });

    private void ReloadTracks(bool restoreSelection, bool scrollPlaying)
    {
        var keep = restoreSelection
            ? TrackList.SelectedItems.OfType<MusicTrackRow>().Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var player = _host.Music;
        _tracks.Clear();
        for (var i = 0; i < player.Queue.Count; i++)
        {
            _tracks.Add(MusicTrackRow.From(player.Queue[i], i + 1, i == player.Index));
        }

        ApplyFilter();
        if (keep.Count > 0)
        {
            foreach (var row in VisibleRows().Where(r => keep.Contains(r.Path)))
            {
                TrackList.SelectedItems.Add(row);
            }
        }

        if (scrollPlaying)
        {
            var playing = _tracks.FirstOrDefault(t => t.IsPlaying);
            if (playing is not null)
            {
                TrackList.ScrollIntoView(playing);
            }
        }

        UpdateChrome();
    }

    private void UpdatePlayingFlags()
    {
        var current = _host.Music.CurrentPath;
        foreach (var row in _tracks)
        {
            row.IsPlaying = string.Equals(row.Path, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void UpdateChrome()
    {
        var cfg = _host.Config.Current;
        var custom = cfg.MusicPlaylistId != MusicPlayerService.FolderPlaylistId;
        var name = custom
            ? cfg.MusicPlaylists.FirstOrDefault(p => p.Id == cfg.MusicPlaylistId)?.Name ?? "播放清單"
            : string.IsNullOrWhiteSpace(cfg.MusicFolder) ? "資料夾" : IOPath.GetFileName(cfg.MusicFolder);
        SubtitleText.Text = $"{name} · 多選後可一次拖曳調整順序";
        RemoveButton.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        var selected = TrackList.SelectedItems.Count;
        CountText.Text = selected > 0
            ? $"已選 {selected} / {_tracks.Count}"
            : $"{_host.Music.Index + 1} / {_tracks.Count}";
    }

    private bool IsFiltering => !string.IsNullOrWhiteSpace(_filter);

    private IEnumerable<MusicTrackRow> VisibleRows() =>
        TrackList.Items.OfType<MusicTrackRow>();

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        ApplyFilter();
        UpdateChrome();
    }

    private void ApplyFilter()
    {
        if (!IsFiltering)
        {
            TrackList.ItemsSource = _tracks;
            TrackList.CanDragItems = true;
            TrackList.CanReorderItems = true;
            HintText.Text = "Ctrl 點選多首 · Shift 連選 · 拖曳調整順序 · 連按兩下播放";
            return;
        }

        var query = _filter;
        TrackList.ItemsSource = _tracks
            .Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        t.Folder.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        TrackList.CanDragItems = false;
        TrackList.CanReorderItems = false;
        HintText.Text = "搜尋時只能瀏覽與多選移動，清空搜尋後才能拖曳";
    }

    private List<MusicTrackRow> SelectedInListOrder()
    {
        var selected = TrackList.SelectedItems.OfType<MusicTrackRow>()
            .Select(r => r.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _tracks.Where(t => selected.Contains(t.Path)).ToList();
    }

    private void MoveBlock(int destInFullList)
    {
        var moving = SelectedInListOrder();
        if (moving.Count == 0)
        {
            return;
        }

        var paths = moving.Select(m => m.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = _tracks.Where(t => !paths.Contains(t.Path)).ToList();
        var dest = destInFullList - _tracks.Take(Math.Clamp(destInFullList, 0, _tracks.Count)).Count(t => paths.Contains(t.Path));
        dest = Math.Clamp(dest, 0, remaining.Count);
        remaining.InsertRange(dest, moving);
        _host.Music.ApplyOrder(remaining.Select(t => t.Path).ToList());
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var moving = SelectedInListOrder();
        if (moving.Count == 0)
        {
            return;
        }

        var first = _tracks.ToList().FindIndex(t => string.Equals(t.Path, moving[0].Path, StringComparison.OrdinalIgnoreCase));
        MoveBlock(Math.Max(0, first - 1));
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var moving = SelectedInListOrder();
        if (moving.Count == 0)
        {
            return;
        }

        var lastPath = moving[^1].Path;
        var last = _tracks.ToList().FindIndex(t => string.Equals(t.Path, lastPath, StringComparison.OrdinalIgnoreCase));
        MoveBlock(last + 2);
    }

    private void MoveTop_Click(object sender, RoutedEventArgs e) => MoveBlock(0);

    private void MoveBottom_Click(object sender, RoutedEventArgs e) => MoveBlock(_tracks.Count);

    private void PlaySelected_Click(object sender, RoutedEventArgs e)
    {
        var moving = SelectedInListOrder();
        if (moving.Count == 0)
        {
            return;
        }

        var index = _host.Music.Queue.ToList().FindIndex(p =>
            string.Equals(p, moving[0].Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _host.Music.PlayIndex(index);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e) => await RemoveSelectedAsync();

    private async Task RemoveSelectedAsync()
    {
        if (_host.Config.Current.MusicPlaylistId == MusicPlayerService.FolderPlaylistId || RootGrid.XamlRoot is null)
        {
            return;
        }

        var moving = SelectedInListOrder();
        if (moving.Count == 0)
        {
            return;
        }

        if (!await Helpers.Dialogs.ConfirmAsync(RootGrid.XamlRoot, "從清單移除",
                moving.Count == 1 ? $"要從清單拿掉「{moving[0].Title}」嗎？" : $"要從清單拿掉選取的 {moving.Count} 首嗎？",
                "移除"))
        {
            return;
        }

        _host.Music.RemovePathsFromCurrent(moving.Select(m => m.Path));
    }

    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_closed)
        {
            UpdateChrome();
        }
    }

    private void TrackList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var order = IsFiltering ? null : _tracks.Select(t => t.Path).ToList();
        if (order is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || order.Count != _host.Music.Queue.Count ||
                order.SequenceEqual(_host.Music.Queue, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _host.Music.ApplyOrder(order);
        });
    }

    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement fe || fe.DataContext is not MusicTrackRow row)
        {
            return;
        }

        var index = _host.Music.Queue.ToList().FindIndex(p =>
            string.Equals(p, row.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _host.Music.PlayIndex(index);
        }
    }

    private void TrackList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement fe || fe.DataContext is not MusicTrackRow row)
        {
            return;
        }

        if (!TrackList.SelectedItems.Contains(row))
        {
            TrackList.SelectedItems.Clear();
            TrackList.SelectedItems.Add(row);
        }

        var menu = new MenuFlyout();
        Add(menu, "播放", () => PlaySelected_Click(this, new RoutedEventArgs()));
        Add(menu, "上移", () => MoveUp_Click(this, new RoutedEventArgs()));
        Add(menu, "下移", () => MoveDown_Click(this, new RoutedEventArgs()));
        Add(menu, "移到最前", () => MoveTop_Click(this, new RoutedEventArgs()));
        Add(menu, "移到最後", () => MoveBottom_Click(this, new RoutedEventArgs()));
        if (_host.Config.Current.MusicPlaylistId != MusicPlayerService.FolderPlaylistId)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(menu, "從清單移除", () => _ = RemoveSelectedAsync());
        }

        menu.ShowAt(fe, e.GetPosition(fe));
    }

    private static void Add(MenuFlyout menu, string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private async void TrackList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete)
        {
            await RemoveSelectedAsync();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            PlaySelected_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
