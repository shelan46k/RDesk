using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDesk.Services;

namespace RDesk.Views.Controls;

public sealed partial class SystemNetworkControl : UserControl
{
    private readonly HardwareMonitorService _hw = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>Shows either half alone at full width, or both side by side.</summary>
    public void SetParts(bool system, bool network)
    {
        SystemPart.Visibility = system ? Visibility.Visible : Visibility.Collapsed;
        NetworkPart.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(NetworkPart, system ? 2 : 0);
        Grid.SetColumnSpan(SystemPart, network ? 1 : 3);
        Grid.SetColumnSpan(NetworkPart, system ? 1 : 3);
        Visibility = system || network ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsSystemPartVisible => Visibility == Visibility.Visible && SystemPart.Visibility == Visibility.Visible;
    public bool IsNetworkPartVisible => Visibility == Visibility.Visible && NetworkPart.Visibility == Visibility.Visible;

    public SystemNetworkControl()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            _timer.Start();
            Refresh();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    public HardwareSnapshot? LastSample { get; private set; }

    private void Refresh()
    {
        var s = _hw.Sample();
        LastSample = s;
        var warn = s.CpuPercent >= 80 || s.RamPercent >= 80;
        RamBar.Value = s.RamPercent;
        RamBar.Foreground = ThemeService.Brush(s.RamPercent >= 80
            ? ThemeService.WarningTextBrushKey
            : ThemeService.AccentTextBrushKey);
        CpuRamText.Text = $"CPU {s.CpuPercent:0}% · 記憶體 {s.RamPercent:0}%";
        CpuRamText.Foreground = ThemeService.Brush(warn
            ? ThemeService.WarningTextBrushKey
            : ThemeService.PrimaryTextBrushKey);

        var days = (int)s.Uptime.TotalDays;
        var hours = s.Uptime.Hours;
        DiskUptimeText.Text = $"系統碟 {s.DiskPercent:0}% · 運行 {days}天{hours}小時";

        ConnText.Text = s.IsConnected ? "● 已連接" : "● 未連接";
        ConnText.Foreground = ThemeService.Brush(s.IsConnected
            ? ThemeService.SuccessTextBrushKey
            : ThemeService.DangerTextBrushKey);

        SpeedText.Text =
            $"↓ {HardwareMonitorService.FormatSpeed(s.DownloadBytesPerSec)} · ↑ {HardwareMonitorService.FormatSpeed(s.UploadBytesPerSec)}";
        NicText.Text = $"{s.NicLabel} · {s.IPv4}";
    }
}
