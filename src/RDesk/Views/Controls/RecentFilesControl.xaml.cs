using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDesk.Models;
using RDesk.Services;

namespace RDesk.Views.Controls;

public sealed partial class RecentFilesControl : UserControl
{
    private RecentFilesService? _service;
    private ConfigService? _config;
    private int _loadVersion;
    public event EventHandler<string>? AddToFavoritesRequested;
    public event EventHandler? FoldClicked;

    public void SetFolded(bool folded) => CardFold.Apply(CardBody, FoldGlyph, FoldButton, folded, FoldSummary);

    private void FoldButton_Click(object sender, RoutedEventArgs e) => FoldClicked?.Invoke(this, EventArgs.Empty);

    public int ItemCount => FilesHost.Children.Count;
    public string? LastError { get; private set; }

    public RecentFilesControl()
    {
        InitializeComponent();
    }

    public void Bind(ConfigService config)
    {
        _config = config;
        _service = new RecentFilesService(config);
        _ = ReloadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    public async Task ReloadAsync()
    {
        if (_service is null)
        {
            return;
        }

        RefreshButton.IsEnabled = false;
        var version = ++_loadVersion;
        try
        {
            var items = await _service.LoadAsync();
            if (version != _loadVersion)
            {
                return;
            }

            FilesHost.Children.Clear();
            foreach (var item in items)
            {
                FilesHost.Children.Add(CreateCard(item));
            }

            FoldSummary.Text = items.Count == 0
                ? "沒有最近開啟的文件"
                : $"{items[0].DisplayName} · 共 {items.Count} 個";
            HintText.Text = items.Count == 0
                ? "目前沒有最近開啟的文件。若一直是空的，請到 Windows 設定 → 個人化 → 開始，開啟「在開始、跳躍清單和檔案總管中顯示最近開啟的項目」。"
                : "雙擊打開；右鍵可收藏置頂 / 加到常用 / 定位文件";
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StartupLog.Write("Recent files load failed", ex);
            HintText.Text = $"讀取最近文件失敗：{ex.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private FrameworkElement CreateCard(RecentFileItemModel item)
    {
        var border = FileTile.Create(item.Icon, item.DisplayName, $"{item.ExtensionLabel} · {item.TimeLabel}",
            item.FullPath, item.IsPinned);
        border.Tag = item;
        border.DoubleTapped += (_, _) => Open(item.FullPath);
        border.ContextFlyout = BuildFlyout(item);
        return border;
    }

    private MenuFlyout BuildFlyout(RecentFileItemModel item)
    {
        var flyout = new MenuFlyout();
        var pin = new MenuFlyoutItem { Text = item.IsPinned ? "取消收藏置頂" : "收藏置頂" };
        pin.Click += async (_, _) =>
        {
            _service?.TogglePin(item.FullPath);
            await ReloadAsync();
        };
        var fav = new MenuFlyoutItem { Text = "添加至常用文件" };
        fav.Click += (_, _) => AddToFavoritesRequested?.Invoke(this, item.FullPath);
        var reveal = new MenuFlyoutItem { Text = "定位文件" };
        reveal.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"")
            {
                UseShellExecute = true
            });
        };
        flyout.Items.Add(pin);
        flyout.Items.Add(fav);
        flyout.Items.Add(reveal);
        return flyout;
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
