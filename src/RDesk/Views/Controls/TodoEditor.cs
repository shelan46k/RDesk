using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RDesk.Helpers;
using RDesk.Models;
using RDesk.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RDesk.Views.Controls;

/// <summary>Todo details dialog: text, important, colour, due date / time, reminder, repeat, attachments.</summary>
public static class TodoEditor
{
    public static readonly (string Key, string Label, Windows.UI.Color Color)[] Colors =
    [
        ("", "無", Windows.UI.Color.FromArgb(0, 0, 0, 0)),
        ("Red", "紅", Windows.UI.Color.FromArgb(0xFF, 0xE5, 0x3E, 0x3E)),
        ("Orange", "橙", Windows.UI.Color.FromArgb(0xFF, 0xED, 0x89, 0x36)),
        ("Yellow", "黃", Windows.UI.Color.FromArgb(0xFF, 0xEC, 0xC9, 0x4B)),
        ("Green", "綠", Windows.UI.Color.FromArgb(0xFF, 0x48, 0xBB, 0x78)),
        ("Blue", "藍", Windows.UI.Color.FromArgb(0xFF, 0x42, 0x99, 0xE1)),
        ("Purple", "紫", Windows.UI.Color.FromArgb(0xFF, 0x9F, 0x7A, 0xEA))
    ];

    private static readonly (int Minutes, string Label)[] Leads =
    [
        (0, "準時提醒"), (5, "提前 5 分鐘"), (15, "提前 15 分鐘"), (30, "提前 30 分鐘"), (60, "提前 1 小時"), (1440, "提前 1 天")
    ];

    private static readonly (string Key, string Label)[] Repeats =
    [
        ("None", "不重複"), ("Daily", "每天"), ("Weekdays", "工作日"), ("Weekly", "每週"), ("Monthly", "每月")
    ];

    public static Windows.UI.Color ColorOf(string key) =>
        Colors.FirstOrDefault(c => c.Key == key).Color;

    /// <returns>True when the user saved.</returns>
    public static async Task<bool> EditAsync(XamlRoot root, TodoItemModel item, string title)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 300 };

        var text = new TextBox
        {
            Text = item.Content,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            PlaceholderText = "要做什麼？"
        };
        panel.Children.Add(text);

        var important = new CheckBox { Content = "標為重要 ★", IsChecked = item.IsImportant };
        panel.Children.Add(important);

        var selectedColor = item.Color;
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var swatches = new List<Border>();
        foreach (var (key, label, color) in Colors)
        {
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = key.Length == 0 ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : new SolidColorBrush(color),
                BorderBrush = ThemeService.Brush(ThemeService.SecondaryTextBrushKey),
                BorderThickness = new Thickness(key == selectedColor ? 2.5 : (key.Length == 0 ? 1 : 0)),
                Child = key.Length == 0 ? new FontIcon { Glyph = "\uE711", FontSize = 10 } : null
            };
            ToolTipService.SetToolTip(swatch, label);
            swatch.Tapped += (_, _) =>
            {
                selectedColor = key;
                for (var i = 0; i < swatches.Count; i++)
                {
                    swatches[i].BorderThickness = new Thickness(Colors[i].Key == key ? 2.5 : (Colors[i].Key.Length == 0 ? 1 : 0));
                }
            };
            swatches.Add(swatch);
            colorRow.Children.Add(swatch);
        }

        panel.Children.Add(Labeled("顏色", colorRow));

        var date = new CalendarDatePicker
        {
            PlaceholderText = "選擇日期",
            Date = item.DueAt is { } d ? new DateTimeOffset(d.Date) : null,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var time = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            Time = item.DueAt?.TimeOfDay is { } t && t != TimeSpan.Zero ? t : new TimeSpan(9, 0, 0)
        };
        var clearDate = new HyperlinkButton { Content = "清除日期", Padding = new Thickness(0) };
        clearDate.Click += (_, _) => date.Date = null;
        var dateRow = new StackPanel { Spacing = 6 };
        dateRow.Children.Add(date);
        dateRow.Children.Add(time);
        dateRow.Children.Add(clearDate);
        panel.Children.Add(Labeled("截止", dateRow));

        var remind = new CheckBox { Content = "到期提醒（Windows 通知）", IsChecked = item.RemindEnabled };
        var lead = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in Leads)
        {
            lead.Items.Add(label);
        }

        lead.SelectedIndex = Math.Max(0, Array.FindIndex(Leads, l => l.Minutes == item.RemindLeadMinutes));
        var remindRow = new StackPanel { Spacing = 6 };
        remindRow.Children.Add(remind);
        remindRow.Children.Add(lead);
        panel.Children.Add(remindRow);

        var repeat = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in Repeats)
        {
            repeat.Items.Add(label);
        }

        repeat.SelectedIndex = Math.Max(0, Array.FindIndex(Repeats, r => r.Key == item.Repeat));
        panel.Children.Add(Labeled("重複", repeat));

        var attachments = item.Attachments.ToList();
        var attachHost = new StackPanel { Spacing = 4 };
        void RenderAttachments()
        {
            attachHost.Children.Clear();
            foreach (var path in attachments.ToList())
            {
                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var link = new HyperlinkButton
                {
                    Content = new TextBlock { Text = Path.GetFileName(path.TrimEnd('\\')), TextTrimming = TextTrimming.CharacterEllipsis },
                    Padding = new Thickness(0)
                };
                ToolTipService.SetToolTip(link, path);
                link.Click += (_, _) => FileOps.Open(path);
                var remove = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, Padding = new Thickness(6) };
                remove.Click += (_, _) =>
                {
                    attachments.Remove(path);
                    RenderAttachments();
                };
                row.Children.Add(link);
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                attachHost.Children.Add(row);
            }
        }

        RenderAttachments();
        var addAttachment = new Button { Content = "加入附件…" };
        addAttachment.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(AppHost.Current.Sidebar));
            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();
            foreach (var f in files)
            {
                if (!attachments.Contains(f.Path, StringComparer.OrdinalIgnoreCase))
                {
                    attachments.Add(f.Path);
                }
            }

            RenderAttachments();
        };
        var attachPanel = new StackPanel { Spacing = 4 };
        attachPanel.Children.Add(attachHost);
        attachPanel.Children.Add(addAttachment);
        panel.Children.Add(Labeled("附件（關聯原檔案路徑）", attachPanel));

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new ScrollViewer { Content = panel, MaxHeight = 560, Padding = new Thickness(0, 0, 12, 0) },
            PrimaryButtonText = "儲存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(text.Text))
        {
            return false;
        }

        var newDue = date.Date is { } picked ? picked.Date + time.Time : (DateTime?)null;
        var newLead = Leads[Math.Max(0, lead.SelectedIndex)].Minutes;
        var newRemind = remind.IsChecked == true && newDue is not null;
        if (newDue != item.DueAt || newRemind != item.RemindEnabled || newLead != item.RemindLeadMinutes)
        {
            item.Reminded = false;
            item.SnoozeUntil = null;
        }

        item.Content = text.Text.Trim();
        item.IsImportant = important.IsChecked == true;
        item.Color = selectedColor;
        item.DueAt = newDue;
        item.RemindEnabled = newRemind;
        item.RemindLeadMinutes = newLead;
        item.Repeat = Repeats[Math.Max(0, repeat.SelectedIndex)].Key;
        item.Attachments = attachments;
        return true;
    }

    private static FrameworkElement Labeled(string label, FrameworkElement content)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 });
        panel.Children.Add(content);
        return panel;
    }
}
