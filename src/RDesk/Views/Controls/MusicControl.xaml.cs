using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Control;

namespace RDesk.Views.Controls;

/// <summary>DeskBox 音樂格子: reads the Windows media session (any player) — cover, title, progress, controls.</summary>
public sealed partial class MusicControl : UserControl
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private TimeSpan _position;
    private TimeSpan _duration;
    private DateTimeOffset _positionUpdated;
    private bool _playing;

    public MusicControl()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => UpdateProgress();
        Unloaded += (_, _) => _tick.Stop();
    }

    public bool IsReady => _manager is not null;
    public string? LastError { get; private set; }
    public string CurrentTitle => TitleText.Text;
    public bool HasSession => _session is not null;

    public async Task StartAsync()
    {
        if (_manager is not null)
        {
            return;
        }

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => DispatcherQueue.TryEnqueue(AttachSession);
            AttachSession();
            _tick.Start();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StartupLog.Write("Media session unavailable", ex);
        }
    }

    private void AttachSession()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        }

        _session = _manager?.GetCurrentSession();
        if (_session is null)
        {
            ShowIdle();
            return;
        }

        _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
        _session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
        _session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        _ = RefreshMediaAsync();
        RefreshPlayback();
        RefreshTimeline();
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => _ = RefreshMediaAsync());

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(RefreshPlayback);

    private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(RefreshTimeline);

    private void ShowIdle()
    {
        TitleText.Text = "沒有正在播放的媒體";
        ArtistText.Text = "在 Spotify、瀏覽器或媒體播放器播放後會顯示在這裡";
        SourceText.Text = string.Empty;
        TimeText.Text = string.Empty;
        Progress.Value = 0;
        CoverImage.Source = null;
        CoverGlyph.Visibility = Visibility.Visible;
        PlayGlyph.Glyph = "\uE768";
        _playing = false;
        SetControlsEnabled(false);
    }

    private void SetControlsEnabled(bool enabled)
    {
        PrevButton.IsEnabled = enabled;
        PlayButton.IsEnabled = enabled;
        NextButton.IsEnabled = enabled;
    }

    private async Task RefreshMediaAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (!ReferenceEquals(session, _session))
            {
                return;
            }

            TitleText.Text = string.IsNullOrWhiteSpace(props.Title) ? "未知曲目" : props.Title;
            ArtistText.Text = string.Join(" · ", new[] { props.Artist, props.AlbumTitle }.Where(s => !string.IsNullOrWhiteSpace(s)));
            SourceText.Text = FriendlySource(session.SourceAppUserModelId);
            if (props.Thumbnail is not null)
            {
                using var stream = await props.Thumbnail.OpenReadAsync();
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                CoverImage.Source = bitmap;
                CoverGlyph.Visibility = Visibility.Collapsed;
            }
            else
            {
                CoverImage.Source = null;
                CoverGlyph.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private static string FriendlySource(string aumid)
    {
        var lower = aumid.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("spotify") => "Spotify",
            _ when lower.Contains("chrome") => "Chrome",
            _ when lower.Contains("msedge") => "Edge",
            _ when lower.Contains("firefox") => "Firefox",
            _ when lower.Contains("zunemusic") || lower.Contains("media") => "媒體播放器",
            _ => aumid.Split('!')[0].Split('_')[0].Split('.').LastOrDefault() ?? aumid
        };
    }

    private void RefreshPlayback()
    {
        var info = _session?.GetPlaybackInfo();
        if (info is null)
        {
            return;
        }

        _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        PlayGlyph.Glyph = _playing ? "\uE769" : "\uE768";
        PrevButton.IsEnabled = info.Controls.IsPreviousEnabled;
        NextButton.IsEnabled = info.Controls.IsNextEnabled;
        PlayButton.IsEnabled = info.Controls.IsPlayPauseToggleEnabled || info.Controls.IsPlayEnabled || info.Controls.IsPauseEnabled;
    }

    private void RefreshTimeline()
    {
        var timeline = _session?.GetTimelineProperties();
        if (timeline is null)
        {
            return;
        }

        _position = timeline.Position;
        _duration = timeline.EndTime - timeline.StartTime;
        _positionUpdated = timeline.LastUpdatedTime;
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        if (_session is null || _duration <= TimeSpan.Zero)
        {
            Progress.Value = 0;
            TimeText.Text = string.Empty;
            return;
        }

        var position = _position;
        if (_playing && _positionUpdated != default)
        {
            position += DateTimeOffset.Now - _positionUpdated;
        }

        if (position > _duration)
        {
            position = _duration;
        }

        Progress.Value = position.TotalSeconds / _duration.TotalSeconds;
        TimeText.Text = $"{Format(position)} / {Format(_duration)}";
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private async void Prev_Click(object sender, RoutedEventArgs e) => await Try(s => s.TrySkipPreviousAsync().AsTask());

    private async void Play_Click(object sender, RoutedEventArgs e) => await Try(s => s.TryTogglePlayPauseAsync().AsTask());

    private async void Next_Click(object sender, RoutedEventArgs e) => await Try(s => s.TrySkipNextAsync().AsTask());

    private async Task Try(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await action(_session);
        }
        catch (Exception ex)
        {
            StartupLog.Write("Media control failed", ex);
        }
    }
}
