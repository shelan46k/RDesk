using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDesk.Services;

namespace RDesk.Views.Controls;

public sealed partial class SystemNetworkControl : UserControl
{
    private readonly HardwareMonitorService _hw = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public event EventHandler? FoldClicked;

    public void SetFolded(bool folded) => CardFold.Apply(CardBody, FoldGlyph, FoldButton, folded, FoldSummary);

    private void FoldButton_Click(object sender, RoutedEventArgs e) => FoldClicked?.Invoke(this, EventArgs.Empty);

    public void SetParts(bool system, bool network, bool disk)
    {
        SystemPart.Visibility = system ? Visibility.Visible : Visibility.Collapsed;
        NetworkPart.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
        DiskPart.Visibility = disk ? Visibility.Visible : Visibility.Collapsed;
        FoldSystem.Visibility = SystemPart.Visibility;
        FoldNetwork.Visibility = NetworkPart.Visibility;
        FoldDisk.Visibility = DiskPart.Visibility;
        Visibility = system || network || disk ? Visibility.Visible : Visibility.Collapsed;
        LayoutFoldChips();
    }

    public bool IsSystemPartVisible => Visibility == Visibility.Visible && SystemPart.Visibility == Visibility.Visible;
    public bool IsNetworkPartVisible => Visibility == Visibility.Visible && NetworkPart.Visibility == Visibility.Visible;
    public bool IsDiskPartVisible => Visibility == Visibility.Visible && DiskPart.Visibility == Visibility.Visible;

    public SystemNetworkControl()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            LayoutFoldChips();
            _timer.Start();
            Refresh();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    public HardwareSnapshot? LastSample { get; private set; }

    private void LayoutFoldChips()
    {
        var chips = new[] { FoldSystem, FoldNetwork, FoldDisk }
            .Where(c => c.Visibility == Visibility.Visible)
            .ToArray();
        FoldSummary.ColumnDefinitions.Clear();
        for (var i = 0; i < Math.Max(chips.Length, 1); i++)
        {
            FoldSummary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < chips.Length; i++)
        {
            Grid.SetColumn(chips[i], i);
        }
    }

    private void Refresh()
    {
        var s = _hw.Sample();
        LastSample = s;
        var cpuWarn = s.CpuPercent >= 80;
        var ramWarn = s.RamPercent >= 80;

        CpuText.Text = $"{s.CpuPercent:0}%";
        CpuText.Foreground = ThemeService.Brush(cpuWarn
            ? ThemeService.WarningTextBrushKey
            : ThemeService.PrimaryTextBrushKey);
        SetBar(CpuUsed, CpuFree, CpuFill, s.CpuPercent, cpuWarn);
        RamText.Text = $"{s.RamPercent:0}%";
        RamText.Foreground = ThemeService.Brush(ramWarn
            ? ThemeService.WarningTextBrushKey
            : ThemeService.PrimaryTextBrushKey);
        SetBar(RamUsed, RamFree, RamFill, s.RamPercent, ramWarn);

        var days = (int)s.Uptime.TotalDays;
        UptimeText.Text = days > 0
            ? $"運行 {days} 天 {s.Uptime.Hours} 小時"
            : $"運行 {s.Uptime.Hours} 小時 {s.Uptime.Minutes} 分";

        ConnText.Text = s.IsConnected ? "已連接" : "未連接";
        ConnText.Foreground = ThemeService.Brush(s.IsConnected
            ? ThemeService.SuccessTextBrushKey
            : ThemeService.DangerTextBrushKey);
        DownText.Text = HardwareMonitorService.FormatSpeed(s.DownloadBytesPerSec);
        UpText.Text = HardwareMonitorService.FormatSpeed(s.UploadBytesPerSec);
        NicText.Text = $"{s.NicLabel} · {s.IPv4}";

        DiskReadText.Text = HardwareMonitorService.FormatSpeed(s.DiskReadBytesPerSec);
        DiskWriteText.Text = HardwareMonitorService.FormatSpeed(s.DiskWriteBytesPerSec);
        RenderDrives(s.Drives);
        UpdateFoldSummary(s);
    }

    private static void SetBar(ColumnDefinition used, ColumnDefinition free, Border fill, double percent, bool warn)
    {
        used.Width = new GridLength(Math.Max(percent, 0.1), GridUnitType.Star);
        free.Width = new GridLength(Math.Max(100 - percent, 0.1), GridUnitType.Star);
        fill.Background = ThemeService.Brush(warn
            ? ThemeService.WarningTextBrushKey
            : ThemeService.AccentFillBrushKey);
    }

    private string _diskKey = string.Empty;
    private readonly Dictionary<string, DiskRow> _diskRows = new();

    private sealed class DiskRow
    {
        public required TextBlock Name { get; init; }
        public required TextBlock Percent { get; init; }
        public required ColumnDefinition Used { get; init; }
        public required ColumnDefinition FreeCol { get; init; }
        public required Border Fill { get; init; }
        public required TextBlock Free { get; init; }
    }

    private void RenderDrives(IReadOnlyList<DiskDriveInfo> drives)
    {
        var key = string.Join("|", drives.Select(d => d.Name));
        if (key != _diskKey)
        {
            _diskKey = key;
            DiskHost.Children.Clear();
            _diskRows.Clear();
            if (drives.Count == 0)
            {
                DiskHost.Children.Add(new TextBlock
                {
                    Text = "讀取不到硬碟",
                    FontSize = 11,
                    Foreground = ThemeService.Brush(ThemeService.SecondaryTextBrushKey)
                });
                return;
            }

            foreach (var d in drives)
            {
                DiskHost.Children.Add(CreateDriveRow(d));
            }
        }

        foreach (var d in drives)
        {
            UpdateDriveRow(d);
        }
    }

    private FrameworkElement CreateDriveRow(DiskDriveInfo d)
    {
        var used = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var freeCol = new ColumnDefinition { Width = new GridLength(99, GridUnitType.Star) };
        var fill = new Border { CornerRadius = new CornerRadius(3) };
        var name = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeService.Brush(ThemeService.SecondaryTextBrushKey)
        };
        var percent = new TextBlock
        {
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var free = new TextBlock
        {
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeService.Brush(ThemeService.SecondaryTextBrushKey)
        };

        var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = ThemeService.Brush(ThemeService.CardBorderBrushKey)
        });
        var fillGrid = new Grid();
        fillGrid.ColumnDefinitions.Add(used);
        fillGrid.ColumnDefinitions.Add(freeCol);
        fillGrid.Children.Add(fill);
        bar.Children.Add(fillGrid);

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.Children.Add(name);
        Grid.SetColumn(percent, 1);
        row.Children.Add(percent);
        Grid.SetColumn(bar, 2);
        row.Children.Add(bar);
        Grid.SetColumn(free, 3);
        row.Children.Add(free);

        _diskRows[d.Name] = new DiskRow
        {
            Name = name,
            Percent = percent,
            Used = used,
            FreeCol = freeCol,
            Fill = fill,
            Free = free
        };
        UpdateDriveRow(d);
        return row;
    }

    private void UpdateDriveRow(DiskDriveInfo d)
    {
        if (!_diskRows.TryGetValue(d.Name, out var row))
        {
            return;
        }

        var warn = d.UsedPercent >= 90;
        row.Name.Text = d.Name;
        row.Percent.Text = $"{d.UsedPercent:0}%";
        row.Percent.Foreground = ThemeService.Brush(warn
            ? ThemeService.WarningTextBrushKey
            : ThemeService.PrimaryTextBrushKey);
        SetBar(row.Used, row.FreeCol, row.Fill, d.UsedPercent, warn);
        row.Free.Text = HardwareMonitorService.FormatSize(d.FreeBytes);
    }

    private void UpdateFoldSummary(HardwareSnapshot s)
    {
        FoldCpu.Text = $"CPU {s.CpuPercent:0}%";
        FoldRam.Text = $"記憶體 {s.RamPercent:0}%";
        FoldConn.Text = s.IsConnected ? "網路 · 已連接" : "網路 · 未連接";
        FoldConn.Foreground = ThemeService.Brush(s.IsConnected
            ? ThemeService.SuccessTextBrushKey
            : ThemeService.DangerTextBrushKey);
        FoldDown.Text = $"↓ {HardwareMonitorService.FormatSpeed(s.DownloadBytesPerSec)}";
        FoldUp.Text = $"↑ {HardwareMonitorService.FormatSpeed(s.UploadBytesPerSec)}";
        FoldDiskRead.Text = $"讀 {HardwareMonitorService.FormatSpeed(s.DiskReadBytesPerSec)}";
        FoldDiskWrite.Text = $"寫 {HardwareMonitorService.FormatSpeed(s.DiskWriteBytesPerSec)}";
    }
}
