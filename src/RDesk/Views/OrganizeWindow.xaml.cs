using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDesk.Helpers;
using RDesk.Services;
using WinUIEx;

namespace RDesk.Views;

/// <summary>Reviewable desktop organize plan (DeskBox 一鍵整理): scan, untick, execute, undo.</summary>
public sealed partial class OrganizeWindow : WindowEx
{
    private readonly AppHost _host;
    private readonly string? _folder;
    private List<OrganizePlanItem> _plan = new();
    private bool _closed;
    private bool _loaded;

    public OrganizeWindow(AppHost host, string? folder = null)
    {
        _host = host;
        _folder = folder;
        InitializeComponent();
        this.CenterOnScreen();
        Closed += (_, _) => _closed = true;
        ApplyTheme();
        Rescan();
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

    public IReadOnlyList<OrganizePlanItem> Plan => _plan;

    public void ApplyTheme()
    {
        if (!_closed)
        {
            _host.Theme.ApplyTo(RootGrid, overlay: false);
        }
    }

    public void Rescan()
    {
        _plan = _host.Organizer.Scan(_folder);
        Render();
        UndoButton.IsEnabled = _host.Organizer.LastUndoableBatch() is not null;
    }

    private void Render()
    {
        PlanHost.Children.Clear();
        var movable = _plan.Where(p => p.SkipReason.Length == 0).ToList();
        var skipped = _plan.Where(p => p.SkipReason.Length > 0).ToList();
        UpdateSummary();

        foreach (var group in movable.GroupBy(p => p.Category))
        {
            var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(2, 12, 0, 4) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.Children.Add(new FontIcon
            {
                Glyph = FileCategories.Glyph(group.Key),
                FontSize = 15,
                Foreground = ThemeService.Brush(ThemeService.AccentTextBrushKey)
            });
            var title = new TextBlock
            {
                Text = $"{group.Key}（{group.Count()}）→ {group.First().TargetFolder}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeService.Brush(ThemeService.DialogPrimaryTextBrushKey)
            };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            PlanHost.Children.Add(header);

            foreach (var item in group)
            {
                var check = new CheckBox
                {
                    IsChecked = item.Include,
                    Content = new TextBlock
                    {
                        Text = $"{item.Name}  ·  {FileCategories.FormatSize(item.Size)}",
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    Margin = new Thickness(26, 0, 0, 0)
                };
                ToolTipService.SetToolTip(check, item.SourcePath);
                check.Click += (_, _) =>
                {
                    item.Include = check.IsChecked == true;
                    UpdateSummary();
                };
                PlanHost.Children.Add(check);
            }
        }

        if (skipped.Count > 0)
        {
            PlanHost.Children.Add(new TextBlock
            {
                Text = $"保留在桌面（{skipped.Count}）",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(2, 16, 0, 4),
                Foreground = ThemeService.Brush(ThemeService.DialogPrimaryTextBrushKey)
            });
            foreach (var item in skipped)
            {
                PlanHost.Children.Add(new TextBlock
                {
                    Text = $"{item.Name} — {item.SkipReason}",
                    Margin = new Thickness(26, 2, 0, 2),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = ThemeService.Brush(ThemeService.DialogSecondaryTextBrushKey)
                });
            }
        }

        if (_plan.Count == 0)
        {
            PlanHost.Children.Add(new TextBlock
            {
                Text = "桌面上沒有檔案需要整理 🎉（資料夾不會被整理）",
                Margin = new Thickness(2, 16, 0, 0),
                Foreground = ThemeService.Brush(ThemeService.DialogSecondaryTextBrushKey)
            });
        }
    }

    private void UpdateSummary()
    {
        var selected = _plan.Count(p => p.Include && p.SkipReason.Length == 0);
        var skipped = _plan.Count(p => p.SkipReason.Length > 0);
        SummaryText.Text = $"掃描到 {_plan.Count} 個檔案：將移動 {selected} 個到「{_host.Config.StorageRoot}」下的分類收納格子，" +
                           $"{skipped} 個會保留在桌面。資料夾、捷徑、下載中或正在使用的檔案不會被移動。";
        ExecuteButton.IsEnabled = selected > 0;
    }

    public OrganizeBatch ExecutePlan()
    {
        var batch = _host.Organizer.Execute(_plan);
        _host.OnStorageChanged();
        return batch;
    }

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        var batch = ExecutePlan();
        Rescan();
        SummaryText.Text = batch.Moves.Count > 0
            ? $"✓ 已移動 {batch.Moves.Count} 個檔案。對應的收納格子已出現在「文件格子」中；不滿意可以按「復原上次整理」。"
            : "沒有檔案被移動。";
        if (batch.Moves.Count == 0)
        {
            await Dialogs.InfoAsync(RootGrid.XamlRoot, "整理桌面", "沒有檔案被移動，請確認勾選的項目。");
        }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var batch = _host.Organizer.LastUndoableBatch();
        if (batch is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(RootGrid.XamlRoot, "復原整理",
                $"會把 {batch.Time:MM/dd HH:mm} 那次整理的 {batch.Moves.Count} 個檔案移回原位置。", "復原"))
        {
            return;
        }

        var restored = _host.Organizer.Undo(batch);
        _host.OnStorageChanged();
        Rescan();
        SummaryText.Text = $"✓ 已移回 {restored} 個檔案。";
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => Rescan();
}
