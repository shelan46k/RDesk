using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using IOPath = System.IO.Path;
using RDesk.Helpers;
using RDesk.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace RDesk.Views.Controls;

public sealed partial class MusicControl : UserControl
{
    private const int BarCount = 5;
    private MusicPlayerService? _player;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _vizTick = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly ObservableCollection<MusicTrackRow> _tracks = new();
    private bool _uiBusy;
    private bool _barsReady;
    private bool _dragging;
    private ScrollViewer? _outerScroll;
    private ScrollMode _outerScrollMode = ScrollMode.Enabled;

    public event EventHandler? FoldClicked;

    public void SetFolded(bool folded)
    {
        CardFold.Apply(CardBody, FoldGlyph, FoldButton, folded, FoldSummary);
        var extra = folded ? Visibility.Collapsed : Visibility.Visible;
        FolderButton.Visibility = extra;
        PlaylistButton.Visibility = extra;
    }

    private void FoldButton_Click(object sender, RoutedEventArgs e) => FoldClicked?.Invoke(this, EventArgs.Empty);

    public MusicControl()
    {
        InitializeComponent();
        TrackList.ItemsSource = _tracks;
        TrackList.PointerEntered += (_, _) => SetOuterScroll(false);
        TrackList.PointerExited += (_, _) =>
        {
            if (!_dragging)
            {
                SetOuterScroll(true);
            }
        };
        _tick.Tick += (_, _) => UpdateProgress();
        _vizTick.Tick += (_, _) => PulseVisualizer();
        Loaded += (_, _) =>
        {
            EnsureBars();
            _tick.Start();
            _vizTick.Start();
        };
        Unloaded += (_, _) =>
        {
            _tick.Stop();
            _vizTick.Stop();
            AppHost.Current.Theme.ThemeChanged -= OnThemeChanged;
        };
    }

    public bool IsReady => _player is not null;
    public string? LastError => _player?.LastError;
    public string CurrentTitle => _player?.CurrentTitle ?? string.Empty;

    public void Bind(MusicPlayerService player)
    {
        if (_player is not null)
        {
            _player.StateChanged -= OnState;
            _player.QueueChanged -= OnQueue;
        }

        _player = player;
        player.AttachDispatcher(DispatcherQueue);
        player.StateChanged += OnState;
        player.QueueChanged += OnQueue;
        AppHost.Current.Theme.ThemeChanged -= OnThemeChanged;
        AppHost.Current.Theme.ThemeChanged += OnThemeChanged;
        _tick.Start();
        _vizTick.Start();
        EnsureBars();
        RefreshPlaylists();
        RefreshList();
        RefreshNowPlaying();
        _uiBusy = true;
        VolumeSlider.Value = player.Volume * 100;
        _uiBusy = false;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ApplyVisualizerColors);

    private void OnState(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshNowPlaying);
    private void OnQueue(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        RefreshPlaylists();
        RefreshList();
        RefreshNowPlaying();
    });

    private void RefreshPlaylists()
    {
        if (_player is null)
        {
            return;
        }

        _uiBusy = true;
        var cfg = AppHost.Current.Config.Current;
        PlaylistCombo.Items.Clear();
        PlaylistCombo.Items.Add(new ComboBoxItem
        {
            Content = string.IsNullOrWhiteSpace(cfg.MusicFolder) ? "資料夾（尚未選擇）" : $"資料夾 · {IOPath.GetFileName(cfg.MusicFolder)}",
            Tag = MusicPlayerService.FolderPlaylistId
        });
        foreach (var list in cfg.MusicPlaylists)
        {
            PlaylistCombo.Items.Add(new ComboBoxItem { Content = list.Name, Tag = list.Id });
        }

        var id = cfg.MusicPlaylistId;
        for (var i = 0; i < PlaylistCombo.Items.Count; i++)
        {
            if (PlaylistCombo.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, id, StringComparison.Ordinal))
            {
                PlaylistCombo.SelectedIndex = i;
                break;
            }
        }

        var custom = id != MusicPlayerService.FolderPlaylistId;
        DeletePlaylistButton.IsEnabled = custom;
        AddFilesButton.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        _uiBusy = false;
    }

    private void RefreshList()
    {
        if (_player is null)
        {
            return;
        }

        _tracks.Clear();
        for (var i = 0; i < _player.Queue.Count; i++)
        {
            _tracks.Add(MusicTrackRow.From(_player.Queue[i], i + 1, i == _player.Index));
        }

        if (_player.Queue.Count > 0)
        {
            TrackList.SelectedIndex = _player.Index;
            TrackList.ScrollIntoView(TrackList.SelectedItem);
        }

        ListHint.Text = _player.Queue.Count == 0
            ? "這個清單是空的"
            : $"{_player.Index + 1} / {_player.Queue.Count} · 拖曳或右鍵可調整順序";
    }

    private void RefreshNowPlaying()
    {
        if (_player is null)
        {
            return;
        }

        var has = _player.Queue.Count > 0;
        PrevButton.IsEnabled = has;
        PlayButton.IsEnabled = has;
        MiniPlayButton.IsEnabled = has;
        NextButton.IsEnabled = has;
        ShuffleButton.IsEnabled = has;
        var playGlyph = _player.IsPlaying ? "\uE769" : "\uE768";
        PlayGlyph.Glyph = playGlyph;
        MiniPlayGlyph.Glyph = playGlyph;
        UpdateRepeatGlyph();

        if (!has)
        {
            TitleText.Text = "還沒有音樂";
            ArtistText.Text = "先選一個資料夾，或建立自己的播放清單";
            MiniTitle.Text = "還沒有音樂";
            UpdateProgress();
            return;
        }

        TitleText.Text = _player.CurrentTitle;
        ArtistText.Text = IOPath.GetDirectoryName(_player.CurrentPath) is { } dir
            ? IOPath.GetFileName(dir)
            : string.Empty;
        MiniTitle.Text = TitleText.Text;
        if (TrackList.Items.Count == _player.Queue.Count)
        {
            TrackList.SelectedIndex = _player.Index;
        }

        ListHint.Text = $"{_player.Index + 1} / {_player.Queue.Count} · 拖曳或右鍵可調整順序";
        UpdateProgress();
        _ = LoadTagsAsync(_player.CurrentPath);
    }

    private void UpdateRepeatGlyph()
    {
        var mode = _player?.RepeatMode ?? "All";
        RepeatGlyph.Glyph = mode == "One" ? "\uE8ED" : "\uE8EE";
        RepeatGlyph.Opacity = mode == "None" ? 0.35 : 1;
        ToolTipService.SetToolTip(RepeatButton, mode switch
        {
            "One" => "單曲循環",
            "None" => "不循環",
            _ => "全部循環"
        });
    }

    private void UpdateProgress()
    {
        if (_player is null || _player.Duration <= TimeSpan.Zero)
        {
            Progress.Value = 0;
            TimeText.Text = string.Empty;
            return;
        }

        var pos = _player.Position;
        var dur = _player.Duration;
        Progress.Value = pos.TotalSeconds / dur.TotalSeconds;
        TimeText.Text = $"{Format(pos)} / {Format(dur)}";
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void EnsureBars()
    {
        if (_barsReady || VizCanvas is null)
        {
            return;
        }

        VizCanvas.Children.Clear();
        const double gap = 4;
        const double width = 6;
        var total = BarCount * width + (BarCount - 1) * gap;
        var startX = (56 - total) / 2;
        for (var i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = width,
                Height = 10,
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(bar, startX + i * (width + gap));
            Canvas.SetTop(bar, 40);
            _bars[i] = bar;
            VizCanvas.Children.Add(bar);
        }

        _barsReady = true;
        ApplyVisualizerColors();
        PulseVisualizer();
    }

    private void ApplyVisualizerColors()
    {
        if (!_barsReady)
        {
            return;
        }

        var theme = AppHost.Current.Theme;
        Color baseColor;
        if (theme.OverlayUsesLightText)
        {
            baseColor = Color.FromArgb(255, 210, 236, 255);
        }
        else if (Application.Current.Resources["AccentFillBrush"] is SolidColorBrush accent)
        {
            baseColor = accent.Color;
        }
        else
        {
            baseColor = theme.IsDark
                ? Color.FromArgb(255, 165, 210, 255)
                : Color.FromArgb(255, 43, 108, 176);
        }

        for (var i = 0; i < _bars.Length; i++)
        {
            var shift = (byte)Math.Clamp(18 * i, 0, 50);
            var color = Color.FromArgb(
                255,
                (byte)Math.Clamp(baseColor.R + shift, 0, 255),
                (byte)Math.Clamp(baseColor.G - shift / 2, 0, 255),
                (byte)Math.Clamp(baseColor.B - shift, 0, 255));
            _bars[i].Fill = new SolidColorBrush(color);
        }
    }

    private void PulseVisualizer()
    {
        if (!_barsReady)
        {
            return;
        }

        var playing = _player?.IsPlaying == true;
        var t = Environment.TickCount64 / 1000.0;
        for (var i = 0; i < _bars.Length; i++)
        {
            var wave = (Math.Sin(t * (playing ? 9.5 : 2.2) + i * 0.95) + 1) * 0.5;
            var min = playing ? 10.0 : 6.0;
            var span = playing ? 34.0 : 10.0;
            var height = min + wave * span;
            _bars[i].Height = height;
            Canvas.SetTop(_bars[i], 46 - height);
        }
    }

    private async Task LoadTagsAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.GetMusicPropertiesAsync();
            if (!string.Equals(path, _player?.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(props.Title))
            {
                TitleText.Text = props.Title;
            }

            if (!string.IsNullOrWhiteSpace(props.Artist))
            {
                ArtistText.Text = props.Artist;
            }

            MiniTitle.Text = TitleText.Text;
        }
        catch
        {
        }
    }

    private void ExpandList_Click(object sender, RoutedEventArgs e) => AppHost.Current.OpenPlaylist();

    private void Play_Click(object sender, RoutedEventArgs e) => _player?.Toggle();
    private void Prev_Click(object sender, RoutedEventArgs e) => _player?.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _player?.Next();

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _player?.CycleRepeat();
        UpdateRepeatGlyph();
    }

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || XamlRoot is null)
        {
            return;
        }

        if (_player.Queue.Count < 2)
        {
            await Dialogs.InfoAsync(XamlRoot, "隨機播放", "清單裡的歌曲不夠，沒有辦法重排。");
            return;
        }

        _player.ShuffleQueue();
        await Dialogs.InfoAsync(XamlRoot, "隨機播放", "已把目前播放清單隨機重新排序。");
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_uiBusy || _player is null)
        {
            return;
        }

        _player.Volume = VolumeSlider.Value / 100.0;
    }

    private void Progress_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(Progress);
        var width = Progress.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        _player.Seek(point.Position.X / width);
    }

    private void TrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_player is null || _dragging)
        {
            return;
        }

        var index = TrackIndex(e.ClickedItem);
        if (index >= 0)
        {
            _player.PlayIndex(index);
        }
    }

    private void TrackList_DragItemsStarting(object sender, DragItemsStartingEventArgs e) => _dragging = true;

    private void TrackList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try
        {
            if (_player is null)
            {
                return;
            }

            var order = _tracks.Select(t => t.Path).ToList();
            if (order.Count == _player.Queue.Count &&
                !order.SequenceEqual(_player.Queue, StringComparer.OrdinalIgnoreCase))
            {
                _player.ApplyOrder(order, notifyQueue: false);
            }
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _dragging = false;
                SetOuterScroll(true);
            });
        }
    }

    private void SetOuterScroll(bool enable)
    {
        _outerScroll ??= FindParentScroll(this);
        if (_outerScroll is null)
        {
            return;
        }

        if (enable)
        {
            _outerScroll.VerticalScrollMode = _outerScrollMode;
            return;
        }

        _outerScrollMode = _outerScroll.VerticalScrollMode;
        _outerScroll.VerticalScrollMode = ScrollMode.Disabled;
    }

    private static ScrollViewer? FindParentScroll(DependencyObject start)
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is ScrollViewer viewer)
            {
                return viewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void TrackList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_player is null || e.OriginalSource is not FrameworkElement fe)
        {
            return;
        }

        var row = fe.DataContext as MusicTrackRow ?? (fe.Parent as FrameworkElement)?.DataContext as MusicTrackRow;
        var index = TrackIndex(row);
        if (index < 0 || index >= _player.Queue.Count)
        {
            return;
        }

        var custom = AppHost.Current.Config.Current.MusicPlaylistId != MusicPlayerService.FolderPlaylistId;
        var menu = new MenuFlyout();
        if (index > 0)
        {
            var top = new MenuFlyoutItem { Text = "移到最前" };
            var up = new MenuFlyoutItem { Text = "上移" };
            var from = index;
            top.Click += (_, _) => _player.MoveTrack(from, 0);
            up.Click += (_, _) => _player.MoveTrack(from, from - 1);
            menu.Items.Add(top);
            menu.Items.Add(up);
        }

        if (index < _player.Queue.Count - 1)
        {
            var down = new MenuFlyoutItem { Text = "下移" };
            var bottom = new MenuFlyoutItem { Text = "移到最後" };
            var from = index;
            var last = _player.Queue.Count - 1;
            down.Click += (_, _) => _player.MoveTrack(from, from + 1);
            bottom.Click += (_, _) => _player.MoveTrack(from, last);
            menu.Items.Add(down);
            menu.Items.Add(bottom);
        }

        if (custom)
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            var remove = new MenuFlyoutItem { Text = "從清單移除" };
            var path = _player.Queue[index];
            remove.Click += (_, _) => _player.RemovePathFromCurrent(path);
            menu.Items.Add(remove);
        }

        if (menu.Items.Count > 0)
        {
            menu.ShowAt(fe, e.GetPosition(fe));
        }
    }

    private int TrackIndex(object? item)
    {
        if (item is MusicTrackRow row)
        {
            for (var i = 0; i < _tracks.Count; i++)
            {
                if (ReferenceEquals(_tracks[i], row) ||
                    string.Equals(_tracks[i].Path, row.Path, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private void PlaylistCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiBusy || _player is null || PlaylistCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var id = item.Tag as string ?? MusicPlayerService.FolderPlaylistId;
        if (!string.Equals(id, AppHost.Current.Config.Current.MusicPlaylistId, StringComparison.Ordinal))
        {
            _player.SelectPlaylist(id);
        }
    }

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(AppHost.Current.Sidebar));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _player.SetFolder(folder.Path);
        }
    }

    private async void AddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || XamlRoot is null)
        {
            return;
        }

        var name = await Dialogs.PromptAsync(XamlRoot, "新增播放清單", "我的清單", "清單名稱");
        if (!string.IsNullOrWhiteSpace(name))
        {
            _player.AddPlaylist(name);
        }
    }

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || XamlRoot is null)
        {
            return;
        }

        if (AppHost.Current.Config.Current.MusicPlaylistId == MusicPlayerService.FolderPlaylistId)
        {
            return;
        }

        if (await Dialogs.ConfirmAsync(XamlRoot, "刪除播放清單", "只會刪掉這份清單，檔案還在原本的資料夾。", "刪除"))
        {
            _player.DeleteCurrentPlaylist();
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(AppHost.Current.Sidebar));
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        foreach (var ext in new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".ogg", ".opus" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            _player.AddPathsToCurrent(files.Select(f => f.Path));
        }
    }
}

public sealed class MusicTrackRow : INotifyPropertyChanged
{
    private int _number;
    private bool _isPlaying;

    public string Path { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Folder { get; init; } = string.Empty;

    public int Number
    {
        get => _number;
        set
        {
            if (_number == value)
            {
                return;
            }

            _number = value;
            Changed(nameof(Number));
            Changed(nameof(NumberText));
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value)
            {
                return;
            }

            _isPlaying = value;
            Changed(nameof(IsPlaying));
            Changed(nameof(PlayingGlyph));
            Changed(nameof(PlayingLabel));
        }
    }

    public string NumberText => _number.ToString();
    public string PlayingGlyph => _isPlaying ? "\uE768" : "\uE76F";
    public string PlayingLabel => _isPlaying ? "播放中" : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static MusicTrackRow From(string path, int number, bool playing) => new()
    {
        Path = path,
        Title = IOPath.GetFileNameWithoutExtension(path),
        Folder = IOPath.GetFileName(IOPath.GetDirectoryName(path) ?? string.Empty) ?? string.Empty,
        Number = number,
        IsPlaying = playing
    };

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
