using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDesk.Models;
using RDesk.Services;

namespace RDesk.Views.Controls;

public sealed partial class WeatherClockControl : UserControl
{
    private readonly WeatherService _weather = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private ConfigService? _config;

    public event EventHandler? FoldClicked;

    public void SetFolded(bool folded) => CardFold.Apply(CardBody, FoldGlyph, FoldButton, folded, FoldSummary);

    private void FoldButton_Click(object sender, RoutedEventArgs e) => FoldClicked?.Invoke(this, EventArgs.Empty);

    public WeatherClockControl()
    {
        InitializeComponent();
        _clockTimer.Tick += (_, _) => UpdateClock();
        _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _clockTimer.Stop();
            _weatherTimer.Stop();
        };
    }

    public void Bind(ConfigService config)
    {
        _config = config;
        _ = RefreshWeatherAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateClock();
        _clockTimer.Start();
        _weatherTimer.Start();
    }

    private void UpdateFoldSummary()
    {
        var time = DateTime.Now.ToString("HH:mm");
        var temp = LastSnapshot is null ? "--°C" : $"{LastSnapshot.Temperature:0}°C";
        var loc = LastSnapshot?.LocationName ?? _config?.Current.LocationName ?? string.Empty;
        FoldSummary.Text = string.IsNullOrWhiteSpace(loc) ? $"{time} · {temp}" : $"{time} · {temp} · {loc}";
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm:ss");
        UpdateFoldSummary();
        var week = now.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            _ => "星期日"
        };
        DateText.Text = $"{now:yyyy}年{now:MM}月{now:dd}日 {week}";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshWeatherAsync();

    public WeatherSnapshot? LastSnapshot { get; private set; }
    public string? LastError { get; private set; }

    private void RenderForecast(WeatherSnapshot snap)
    {
        ForecastGrid.Children.Clear();
        for (var i = 0; i < snap.Days.Count && i < 3; i++)
        {
            var d = snap.Days[i];
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 0 };
            stack.Children.Add(new TextBlock
            {
                Text = $"{d.Label} {d.Emoji}",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = ThemeService.Brush(ThemeService.SecondaryTextBrushKey)
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{d.Min:0}~{d.Max:0}°C",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = ThemeService.Brush(ThemeService.PrimaryTextBrushKey)
            });
            Grid.SetColumn(stack, i);
            ForecastGrid.Children.Add(stack);
        }
    }

    public async Task RefreshWeatherAsync()
    {
        if (_config is null)
        {
            return;
        }

        try
        {
            RefreshButton.IsEnabled = false;
            var cfg = _config.Current;
            var snap = await _weather.FetchAsync(cfg.Latitude, cfg.Longitude, cfg.LocationName);
            if (snap is null)
            {
                LastError = "empty response";
                LocationText.Text = $"{cfg.LocationName} · 天氣取得失敗";
                return;
            }

            LastError = null;
            LastSnapshot = snap;
            LocationText.Text = $"{snap.LocationName} · {snap.Description}";
            ToolTipService.SetToolTip(LocationText, LocationText.Text);
            WeatherEmoji.Text = snap.Emoji;
            DecorGlyph.Text = snap.Emoji;
            TempText.Text = $"{snap.Temperature:0.0}°C";
            FeelText.Text = $"體感 {snap.ApparentTemperature:0.0}°C";
            HumidityText.Text = $"濕度 {snap.Humidity}%";
            WindText.Text = $"風速 {snap.WindSpeed:0.0}km/h";
            RenderForecast(snap);
            UpdateFoldSummary();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LocationText.Text = $"{_config.Current.LocationName} · 離線";
            ToolTipService.SetToolTip(LocationText, $"天氣取得失敗：{ex.Message}");
            UpdateFoldSummary();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }
}
