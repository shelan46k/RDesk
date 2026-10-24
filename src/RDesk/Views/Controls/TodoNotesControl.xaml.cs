using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RDesk.Helpers;
using RDesk.Models;
using RDesk.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace RDesk.Views.Controls;

/// <summary>
/// 待辦 / 隨記 / 剪貼簿 card (DeskBox todo + quick capture): dated, reminding, repeating, coloured todos with
/// attachments and filters; pinned notes with attachments; and a local clipboard history.
/// </summary>
public sealed partial class TodoNotesControl : UserControl
{
    public const int TodoMode = 0;
    public const int NoteMode = 1;
    public const int ClipMode = 2;

    private static readonly (string Key, string Label)[] Filters =
    [
        ("Active", "進行中"), ("Today", "今天"), ("Week", "本週"), ("Important", "重要"), ("Completed", "已完成"), ("All", "全部")
    ];

    private ConfigService? _config;
    private ClipboardHistoryService? _clips;
    private readonly SegmentBar _modes = new();
    private int _mode = TodoMode;
    private bool _pinnedOnly;
    private TodoItemModel? _bannerTodo;

    public event EventHandler? FoldClicked;

    public void SetFolded(bool folded) => CardFold.Apply(CardBody, FoldGlyph, FoldButton, folded, FoldSummary);

    private void FoldButton_Click(object sender, RoutedEventArgs e) => FoldClicked?.Invoke(this, EventArgs.Empty);

    public TodoNotesControl()
    {
        InitializeComponent();
        _modes.AddSegment("待辦", "\uE9D5");
        _modes.AddSegment("隨記", "\uE70B");
        _modes.AddSegment("剪貼簿", "\uE77F");
        _modes.SelectedIndex = 0;
        _modes.SelectionChanged += (_, index) => ShowMode(index);
        ModeHost.Content = _modes;

        NotePanel.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Link;
                e.DragUIOverride.Caption = "存成隨記附件";
            }
        };
        NotePanel.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0)
            {
                var text = string.IsNullOrWhiteSpace(NoteInput.Text)
                    ? string.Join(Environment.NewLine, paths.Select(p => Path.GetFileName(p.TrimEnd('\\'))))
                    : NoteInput.Text.Trim();
                AddNote(text, paths);
                NoteInput.Text = string.Empty;
            }
        };
    }

    public void Bind(ConfigService config, ClipboardHistoryService clips)
    {
        _config = config;
        _clips = clips;
        _clips.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_mode == ClipMode)
            {
                RenderClips();
            }
        });
        ApplyTabVisibility();
        ShowMode(_mode);
    }

    public int Mode => _mode;

    /// <summary>Hides the 隨記 / 剪貼簿 tabs the user switched off; the bar disappears when only 待辦 is left.</summary>
    public void ApplyTabVisibility()
    {
        if (_config is null)
        {
            return;
        }

        _modes.SetSegmentVisible(NoteMode, _config.Current.ShowNotesTab);
        _modes.SetSegmentVisible(ClipMode, _config.Current.ShowClipboardTab);
        _modes.Visibility = _modes.VisibleCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (!_modes.IsSegmentVisible(_mode))
        {
            ShowMode(TodoMode);
        }
    }

    public bool IsTabVisible(int mode) => _modes.IsSegmentVisible(mode) && _modes.Visibility == Visibility.Visible;

    public void ShowMode(int mode)
    {
        if (_config is not null && !_modes.IsSegmentVisible(mode))
        {
            mode = TodoMode;
        }

        _mode = mode;
        _modes.SelectedIndex = mode;
        TodoPanel.Visibility = mode == TodoMode ? Visibility.Visible : Visibility.Collapsed;
        NotePanel.Visibility = mode == NoteMode ? Visibility.Visible : Visibility.Collapsed;
        ClipPanel.Visibility = mode == ClipMode ? Visibility.Visible : Visibility.Collapsed;
        switch (mode)
        {
            case TodoMode:
                RenderTodos();
                break;
            case NoteMode:
                RenderNotes();
                break;
            default:
                RenderClips();
                break;
        }
    }

    public void RefreshTheme() => _modes.Refresh();

    // ================= 待辦 =================

    private List<TodoItemModel> Todos => _config?.Current.TodoItems ?? new List<TodoItemModel>();

    public IReadOnlyList<TodoItemModel> Items => Todos;

    public int RenderedRowCount => TodoHost.Children.Count;

    public string Filter => _config?.Current.TodoFilter ?? "Active";

    public void SetFilter(string filter)
    {
        _config?.Update(c => c.TodoFilter = filter);
        RenderTodos();
    }

    public IEnumerable<TodoItemModel> FilteredTodos()
    {
        var today = DateTime.Today;
        var endOfWeek = today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7));
        IEnumerable<TodoItemModel> list = Filter switch
        {
            "Today" => Todos.Where(t => !t.IsCompleted && t.DueAt is { } d && d.Date <= today),
            "Week" => Todos.Where(t => !t.IsCompleted && t.DueAt is { } d && d.Date < endOfWeek),
            "Important" => Todos.Where(t => !t.IsCompleted && t.IsImportant),
            "Completed" => Todos.Where(t => t.IsCompleted),
            "All" => Todos,
            _ => Todos.Where(t => !t.IsCompleted)
        };
        return list
            .OrderBy(t => t.IsCompleted)
            .ThenByDescending(t => !t.IsCompleted && t.IsImportant)
            .ThenBy(t => t.IsCompleted ? DateTime.MaxValue : t.DueAt ?? DateTime.MaxValue)
            .ThenByDescending(t => t.IsCompleted ? t.CompletedAt ?? t.CreatedAt : t.CreatedAt);
    }

    private void RenderTodos()
    {
        if (_mode == TodoMode)
        {
            ActionButton.Content = $"{Filters.FirstOrDefault(f => f.Key == Filter).Label ?? "進行中"} ▾";
        }

        TodoHost.Children.Clear();
        foreach (var item in FilteredTodos())
        {
            TodoHost.Children.Add(CreateTodoRow(item));
        }

        EmptyText.Visibility = TodoHost.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Filter switch
        {
            "Completed" => "還沒有完成的項目",
            "Today" => "今天沒有到期的待辦 ☕",
            "Important" => "沒有標為重要的待辦",
            _ => "今天沒有待辦，好好享受 ☕"
        };
        var done = Todos.Count(t => t.IsCompleted);
        var overdue = Todos.Count(t => !t.IsCompleted && t.DueAt is { } d && d < DateTime.Now);
        StatsText.Visibility = Todos.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatsText.Text = overdue > 0
            ? $"已完成 {done}/{Todos.Count} · {overdue} 項逾期"
            : $"已完成 {done}/{Todos.Count}";
        UpdateFoldSummary();
    }

    private FrameworkElement CreateTodoRow(TodoItemModel item)
    {
        var colorBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 6, 0, 6),
            Background = item.Color.Length > 0
                ? new SolidColorBrush(TodoEditor.ColorOf(item.Color))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };

        var check = new CheckBox { IsChecked = item.IsCompleted, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        check.Click += (_, _) => SetCompleted(item, check.IsChecked == true);

        var text = new TextBlock
        {
            Text = item.Content,
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13,
            Foreground = item.IsCompleted
                ? ThemeService.Brush(ThemeService.SecondaryTextBrushKey)
                : ThemeService.Brush(ThemeService.PrimaryTextBrushKey)
        };
        if (item.IsCompleted)
        {
            text.Style = (Style)Application.Current.Resources["CompletedTodoTextStyle"];
        }

        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Padding = new Thickness(0, 4, 0, 4) };
        textPanel.Children.Add(text);
        var meta = BuildTodoMeta(item);
        if (meta is not null)
        {
            textPanel.Children.Add(meta);
        }

        var star = new Button
        {
            Content = new FontIcon
            {
                Glyph = item.IsImportant ? "\uE735" : "\uE734",
                FontSize = 12,
                Foreground = item.IsImportant
                    ? ThemeService.Brush(ThemeService.WarningTextBrushKey)
                    : ThemeService.Brush(ThemeService.SecondaryTextBrushKey)
            },
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = item.IsImportant ? 1 : 0.55
        };
        ToolTipService.SetToolTip(star, item.IsImportant ? "取消重要" : "標為重要");
        star.Click += (_, _) =>
        {
            item.IsImportant = !item.IsImportant;
            PersistTodos();
        };

        var grid = new Grid
        {
            ColumnSpacing = 6,
            Padding = new Thickness(4, 0, 2, 0),
            CornerRadius = new CornerRadius(8),
            Opacity = item.IsCompleted ? 0.6 : 1.0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            AllowDrop = true
        };
        grid.PointerEntered += (_, _) => grid.Background = ThemeService.Brush(ThemeService.ItemHoverBrushKey);
        grid.PointerExited += (_, _) => grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        grid.DoubleTapped += async (_, e) =>
        {
            e.Handled = true;
            await EditTodoAsync(item);
        };
        grid.RightTapped += (_, e) =>
        {
            e.Handled = true;
            BuildTodoMenu(item).ShowAt(grid, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(grid) });
        };
        grid.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Link;
                e.DragUIOverride.Caption = "附加到這個待辦";
            }
        };
        grid.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var dropped = await e.DataView.GetStorageItemsAsync();
            Attach(item, dropped.Select(d => d.Path));
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(colorBar);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
        Grid.SetColumn(textPanel, 2);
        grid.Children.Add(textPanel);
        Grid.SetColumn(star, 3);
        grid.Children.Add(star);
        return grid;
    }

    private static FrameworkElement? BuildTodoMeta(TodoItemModel item)
    {
        var parts = new List<(string Text, string BrushKey)>();
        if (item.DueAt is { } due)
        {
            var overdue = !item.IsCompleted && due < DateTime.Now;
            parts.Add(($"{(overdue ? "已逾期 " : string.Empty)}{ReminderService.FormatDue(due)}",
                overdue ? ThemeService.DangerTextBrushKey : ThemeService.AccentTextBrushKey));
        }

        if (item.RemindEnabled && !item.IsCompleted)
        {
            parts.Add((item.SnoozeUntil is { } s ? $"⏰ 延後至 {s:HH:mm}" : "⏰", ThemeService.SecondaryTextBrushKey));
        }

        if (ReminderService.RepeatLabel(item.Repeat) is { Length: > 0 } repeat)
        {
            parts.Add(($"🔁 {repeat}", ThemeService.SecondaryTextBrushKey));
        }

        if (item.Attachments.Count > 0)
        {
            parts.Add(($"📎 {item.Attachments.Count}", ThemeService.SecondaryTextBrushKey));
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (text, key) in parts)
        {
            panel.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = ThemeService.Brush(key) });
        }

        if (item.Attachments.Count > 0)
        {
            panel.Tapped += (_, e) =>
            {
                e.Handled = true;
                if (item.Attachments.Count == 1)
                {
                    FileOps.Open(item.Attachments[0]);
                }
                else
                {
                    var menu = new MenuFlyout();
                    foreach (var path in item.Attachments)
                    {
                        var open = new MenuFlyoutItem { Text = Path.GetFileName(path.TrimEnd('\\')) };
                        open.Click += (_, _) => FileOps.Open(path);
                        menu.Items.Add(open);
                    }

                    menu.ShowAt(panel);
                }
            };
        }

        return panel;
    }

    private MenuFlyout BuildTodoMenu(TodoItemModel item)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("編輯…", "\uE70F", () => _ = EditTodoAsync(item)));
        menu.Items.Add(MenuItem(item.IsCompleted ? "標為未完成" : "標為完成", "\uE73E", () => SetCompleted(item, !item.IsCompleted)));
        menu.Items.Add(MenuItem(item.IsImportant ? "取消重要" : "標為重要", "\uE734", () =>
        {
            item.IsImportant = !item.IsImportant;
            PersistTodos();
        }));

        var colors = new MenuFlyoutSubItem { Text = "顏色", Icon = new FontIcon { Glyph = "\uE790" } };
        foreach (var (key, label, _) in TodoEditor.Colors)
        {
            var c = new RadioMenuFlyoutItem { Text = label, GroupName = "todoColor", IsChecked = item.Color == key };
            c.Click += (_, _) =>
            {
                item.Color = key;
                PersistTodos();
            };
            colors.Items.Add(c);
        }

        menu.Items.Add(colors);

        var snooze = new MenuFlyoutSubItem { Text = "稍後提醒", Icon = new FontIcon { Glyph = "\uEA8F" } };
        snooze.Items.Add(MenuItem("10 分鐘後", "\uE916", () => SnoozeTodo(item, TimeSpan.FromMinutes(10))));
        snooze.Items.Add(MenuItem("30 分鐘後", "\uE916", () => SnoozeTodo(item, TimeSpan.FromMinutes(30))));
        snooze.Items.Add(MenuItem("1 小時後", "\uE916", () => SnoozeTodo(item, TimeSpan.FromHours(1))));
        snooze.Items.Add(MenuItem("明天 09:00", "\uE787", () => SnoozeTodoUntil(item, DateTime.Today.AddDays(1).AddHours(9))));
        menu.Items.Add(snooze);

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("複製內容（含附件路徑）", "\uE8C8", () =>
        {
            var text = item.Content + (item.Attachments.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, item.Attachments) : string.Empty);
            if (_clips is not null)
            {
                _clips.CopyTextQuietly(text);
            }
            else
            {
                FileOps.CopyText(text);
            }
        }));
        if (item.Attachments.Count > 0)
        {
            var open = new MenuFlyoutSubItem { Text = "開啟附件", Icon = new FontIcon { Glyph = "\uE723" } };
            foreach (var path in item.Attachments)
            {
                open.Items.Add(MenuItem(Path.GetFileName(path.TrimEnd('\\')), "\uE8E5", () => FileOps.Open(path)));
            }

            menu.Items.Add(open);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("刪除", "\uE74D", () => RemoveTodo(item)));
        return menu;
    }

    private async Task EditTodoAsync(TodoItemModel item)
    {
        if (await TodoEditor.EditAsync(XamlRoot, item, "編輯待辦"))
        {
            PersistTodos();
        }
    }

    private void TodoInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _config is null)
        {
            return;
        }

        var text = TodoInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        AddTodo(text);
        TodoInput.Text = string.Empty;
        e.Handled = true;
    }

    private async void TodoDetail_Click(object sender, RoutedEventArgs e)
    {
        var item = new TodoItemModel { Content = TodoInput.Text?.Trim() ?? string.Empty };
        if (await TodoEditor.EditAsync(XamlRoot, item, "新增待辦"))
        {
            Todos.Add(item);
            TodoInput.Text = string.Empty;
            if (Filter == "Completed")
            {
                SetFilter("Active");
            }

            PersistTodos();
        }
    }

    public TodoItemModel AddTodo(string text, IEnumerable<string>? attachments = null)
    {
        var item = new TodoItemModel { Content = text, Attachments = attachments?.ToList() ?? new List<string>() };
        Todos.Add(item);
        if (_mode != TodoMode)
        {
            ShowMode(TodoMode);
        }

        if (Filter == "Completed")
        {
            SetFilter("Active");
        }

        PersistTodos();
        return item;
    }

    public void SetCompleted(TodoItemModel item, bool completed)
    {
        if (item.IsCompleted == completed)
        {
            return;
        }

        item.IsCompleted = completed;
        item.CompletedAt = completed ? DateTime.Now : null;
        if (completed && ReminderService.NextOccurrence(item) is { } next)
        {
            item.Repeat = "None";
            Todos.Add(next);
        }

        if (completed && ReferenceEquals(_bannerTodo, item))
        {
            HideBanner();
        }

        PersistTodos();
    }

    public void RemoveTodo(TodoItemModel item)
    {
        Todos.Remove(item);
        if (ReferenceEquals(_bannerTodo, item))
        {
            HideBanner();
        }

        PersistTodos();
    }

    public void ClearCompleted()
    {
        Todos.RemoveAll(t => t.IsCompleted);
        PersistTodos();
    }

    public void Attach(TodoItemModel item, IEnumerable<string> paths)
    {
        foreach (var p in paths.Where(p => !string.IsNullOrEmpty(p)))
        {
            if (!item.Attachments.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                item.Attachments.Add(p);
            }
        }

        PersistTodos();
    }

    public void SnoozeTodo(TodoItemModel item, TimeSpan delay)
    {
        ReminderService.Snooze(item, delay, DateTime.Now);
        if (ReferenceEquals(_bannerTodo, item))
        {
            HideBanner();
        }

        PersistTodos();
    }

    public void SnoozeTodoUntil(TodoItemModel item, DateTime when)
    {
        ReminderService.SnoozeUntil(item, when);
        if (ReferenceEquals(_bannerTodo, item))
        {
            HideBanner();
        }

        PersistTodos();
    }

    // Reminder banner
    public bool IsReminderBannerVisible => ReminderBanner.Visibility == Visibility.Visible;

    public void ShowReminder(TodoItemModel item)
    {
        _bannerTodo = item;
        ReminderText.Text = item.DueAt is { } d ? $"{item.Content}（{ReminderService.FormatDue(d)}）" : item.Content;
        ReminderBanner.Visibility = Visibility.Visible;
        if (_mode != TodoMode)
        {
            ShowMode(TodoMode);
        }
        else
        {
            RenderTodos();
        }
    }

    private void HideBanner()
    {
        _bannerTodo = null;
        ReminderBanner.Visibility = Visibility.Collapsed;
    }

    private void ReminderDone_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerTodo is { } item)
        {
            SetCompleted(item, true);
        }

        HideBanner();
    }

    private void ReminderSnooze_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerTodo is not { } item || sender is not Button { Tag: string tag })
        {
            return;
        }

        if (tag == "tomorrow")
        {
            SnoozeTodoUntil(item, DateTime.Today.AddDays(1).AddHours(9));
        }
        else if (int.TryParse(tag, out var minutes))
        {
            SnoozeTodo(item, TimeSpan.FromMinutes(minutes));
        }
    }

    private void ReminderDismiss_Click(object sender, RoutedEventArgs e) => HideBanner();

    private void PersistTodos()
    {
        _config?.SaveDebounced();
        if (_mode == TodoMode)
        {
            RenderTodos();
        }
    }

    // ================= 隨記 =================

    private List<NoteItemModel> Notes => _config?.Current.Notes ?? new List<NoteItemModel>();

    public IReadOnlyList<NoteItemModel> NoteItems => Notes;

    public int RenderedNoteCount => NoteHost.Children.Count;

    public bool PinnedOnly
    {
        get => _pinnedOnly;
        set
        {
            _pinnedOnly = value;
            RenderNotes();
        }
    }

    private void RenderNotes()
    {
        if (_mode == NoteMode)
        {
            ActionButton.Content = _pinnedOnly ? "已固定 ▾" : "全部 ▾";
        }

        NoteHost.Children.Clear();
        var notes = Notes
            .Where(n => !_pinnedOnly || n.IsPinned)
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.UpdatedAt);
        foreach (var note in notes)
        {
            NoteHost.Children.Add(CreateNoteRow(note));
        }

        NoteEmptyText.Visibility = NoteHost.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteEmptyText.Text = _pinnedOnly ? "還沒有固定的隨記" : "還沒有隨記，記下第一筆吧";
        NoteStatsText.Text = $"{Notes.Count} 筆 · {Notes.Count(n => n.IsPinned)} 筆固定";
        UpdateFoldSummary();
    }

    private FrameworkElement CreateNoteRow(NoteItemModel note)
    {
        var text = new TextBlock
        {
            Text = note.Content,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 4,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            IsTextSelectionEnabled = false,
            Foreground = ThemeService.Brush(ThemeService.PrimaryTextBrushKey)
        };
        var meta = new TextBlock
        {
            Text = $"{note.UpdatedAt:MM/dd HH:mm}" + (note.Attachments.Count > 0 ? $" · 📎 {note.Attachments.Count}" : string.Empty),
            FontSize = 11,
            Foreground = ThemeService.Brush(ThemeService.SecondaryTextBrushKey)
        };

        var pin = IconButton(note.IsPinned ? "\uE840" : "\uE718", note.IsPinned ? "取消固定" : "固定", () => TogglePin(note));
        if (note.IsPinned && pin.Content is FontIcon pinIcon)
        {
            pinIcon.Foreground = ThemeService.Brush(ThemeService.AccentTextBrushKey);
        }

        var copy = IconButton("\uE8C8", "複製文字", () => CopyNote(note));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Top };
        actions.Children.Add(pin);
        actions.Children.Add(copy);

        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(text);
        body.Children.Add(meta);

        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(body);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        var card = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 8, 4, 8),
            CornerRadius = new CornerRadius(10),
            Background = ThemeService.Brush(ThemeService.PillBackgroundBrushKey),
            BorderBrush = note.IsPinned ? ThemeService.Brush(ThemeService.AccentTextBrushKey) : ThemeService.Brush(ThemeService.CardBorderBrushKey),
            BorderThickness = new Thickness(1),
            AllowDrop = true
        };
        card.DoubleTapped += async (_, e) =>
        {
            e.Handled = true;
            await EditNoteAsync(note);
        };
        card.RightTapped += (_, e) =>
        {
            e.Handled = true;
            BuildNoteMenu(note).ShowAt(card, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(card) });
        };
        card.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Link;
                e.DragUIOverride.Caption = "附加到這則隨記";
                e.Handled = true;
            }
        };
        card.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            e.Handled = true;
            var dropped = await e.DataView.GetStorageItemsAsync();
            foreach (var p in dropped.Select(d => d.Path).Where(p => !string.IsNullOrEmpty(p)))
            {
                if (!note.Attachments.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    note.Attachments.Add(p);
                }
            }

            note.UpdatedAt = DateTime.Now;
            PersistNotes();
        };
        return card;
    }

    private MenuFlyout BuildNoteMenu(NoteItemModel note)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("編輯…", "\uE70F", () => _ = EditNoteAsync(note)));
        menu.Items.Add(MenuItem(note.IsPinned ? "取消固定" : "固定", "\uE718", () => TogglePin(note)));
        menu.Items.Add(MenuItem("複製文字", "\uE8C8", () => CopyNote(note)));
        menu.Items.Add(MenuItem("轉成待辦", "\uE9D5", () => AddTodo(note.Content.Split('\n')[0].Trim(), note.Attachments)));
        if (note.Attachments.Count > 0)
        {
            var open = new MenuFlyoutSubItem { Text = "開啟附件", Icon = new FontIcon { Glyph = "\uE723" } };
            foreach (var path in note.Attachments)
            {
                open.Items.Add(MenuItem(Path.GetFileName(path.TrimEnd('\\')), "\uE8E5", () => FileOps.Open(path)));
            }

            menu.Items.Add(open);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("刪除", "\uE74D", () => RemoveNote(note)));
        return menu;
    }

    private async Task EditNoteAsync(NoteItemModel note)
    {
        var box = new TextBox
        {
            Text = note.Content,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160,
            MaxHeight = 360,
            MinWidth = 300
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "編輯隨記",
            Content = box,
            PrimaryButtonText = "儲存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            note.Content = box.Text.Trim();
            note.UpdatedAt = DateTime.Now;
            PersistNotes();
        }
    }

    private void NoteInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter && ctrl)
        {
            SaveNoteInput();
            e.Handled = true;
        }
    }

    private void NoteSave_Click(object sender, RoutedEventArgs e) => SaveNoteInput();

    private void SaveNoteInput()
    {
        var text = NoteInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        AddNote(text);
        NoteInput.Text = string.Empty;
    }

    public NoteItemModel AddNote(string text, IEnumerable<string>? attachments = null)
    {
        var note = new NoteItemModel { Content = text, Attachments = attachments?.ToList() ?? new List<string>() };
        Notes.Add(note);
        if (_mode != NoteMode)
        {
            ShowMode(NoteMode);
        }

        PersistNotes();
        return note;
    }

    public void TogglePin(NoteItemModel note)
    {
        note.IsPinned = !note.IsPinned;
        PersistNotes();
    }

    public void RemoveNote(NoteItemModel note)
    {
        Notes.Remove(note);
        PersistNotes();
    }

    private void CopyNote(NoteItemModel note)
    {
        if (_clips is not null)
        {
            _clips.CopyTextQuietly(note.Content);
        }
        else
        {
            FileOps.CopyText(note.Content);
        }
    }

    private void PersistNotes()
    {
        _config?.SaveDebounced();
        if (_mode == NoteMode)
        {
            RenderNotes();
        }
    }

    // ================= 剪貼簿 =================

    public int RenderedClipCount => ClipHost.Children.Count;

    private void RenderClips()
    {
        if (_mode == ClipMode)
        {
            ActionButton.Content = "清空";
        }

        ClipHost.Children.Clear();
        if (_config is null || _clips is null)
        {
            return;
        }

        if (!_config.Current.ClipboardHistoryEnabled)
        {
            ClipStatusText.Text = "剪貼簿記錄已關閉，可在「設置 → 隨記與剪貼簿」開啟。";
            return;
        }

        foreach (var clip in _clips.Items.ToList())
        {
            ClipHost.Children.Add(CreateClipRow(clip));
        }

        ClipStatusText.Text = ClipHost.Children.Count == 0
            ? "複製文字或檔案後會出現在這裡（只保存在本機，最多 40 筆）。"
            : "點一下即可再次複製 · 只保存在本機";
        UpdateFoldSummary();
    }

    private void UpdateFoldSummary()
    {
        FoldSummary.Text = _mode switch
        {
            NoteMode => $"{Notes.Count} 筆隨記",
            ClipMode => $"{ClipHost.Children.Count} 筆剪貼",
            _ => $"{Todos.Count(t => !t.IsCompleted)} 項未完成 · 已完成 {Todos.Count(t => t.IsCompleted)}/{Todos.Count}"
        };
    }

    private FrameworkElement CreateClipRow(ClipItemModel clip)
    {
        var preview = clip.Kind == "Files"
            ? (clip.Paths.Count == 1
                ? $"📄 {Path.GetFileName(clip.Paths[0].TrimEnd('\\'))}"
                : $"📄 {Path.GetFileName(clip.Paths[0].TrimEnd('\\'))} 等 {clip.Paths.Count} 個檔案")
            : clip.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        var text = new TextBlock
        {
            Text = preview,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            Foreground = ThemeService.Brush(ThemeService.PrimaryTextBrushKey)
        };
        var meta = new TextBlock
        {
            Text = clip.CapturedAt.Date == DateTime.Today ? clip.CapturedAt.ToString("HH:mm") : clip.CapturedAt.ToString("MM/dd HH:mm"),
            FontSize = 11,
            Foreground = ThemeService.Brush(ThemeService.SecondaryTextBrushKey)
        };
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(text);
        body.Children.Add(meta);

        var save = IconButton("\uE70B", "存成隨記", () => AddNote(clip.Kind == "Files" ? string.Join(Environment.NewLine, clip.Paths.Select(p => Path.GetFileName(p.TrimEnd('\\')))) : clip.Text,
            clip.Kind == "Files" ? clip.Paths : null));
        var remove = IconButton("\uE711", "移除", () => _clips?.Remove(clip));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(save);
        actions.Children.Add(remove);

        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(body);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 6, 4, 6),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        ToolTipService.SetToolTip(row, clip.Text.Length > 400 ? clip.Text[..400] + "…" : clip.Text);
        row.PointerEntered += (_, _) => row.Background = ThemeService.Brush(ThemeService.ItemHoverBrushKey);
        row.PointerExited += (_, _) => row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        row.Tapped += async (_, _) =>
        {
            if (_clips is null)
            {
                return;
            }

            await _clips.RestoreAsync(clip);
            meta.Text = "✓ 已複製";
            meta.Foreground = ThemeService.Brush(ThemeService.SuccessTextBrushKey);
        };
        return row;
    }

    // ================= Header action =================

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case TodoMode:
            {
                var menu = new MenuFlyout();
                foreach (var (key, label) in Filters)
                {
                    var count = key switch
                    {
                        "Completed" => Todos.Count(t => t.IsCompleted),
                        "All" => Todos.Count,
                        _ => -1
                    };
                    var item = new RadioMenuFlyoutItem
                    {
                        Text = count >= 0 ? $"{label}（{count}）" : label,
                        GroupName = "todoFilter",
                        IsChecked = Filter == key
                    };
                    item.Click += (_, _) => SetFilter(key);
                    menu.Items.Add(item);
                }

                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MenuItem("清除已完成", "\uE74D", ClearCompleted));
                menu.ShowAt(ActionButton);
                break;
            }
            case NoteMode:
            {
                var menu = new MenuFlyout();
                var all = new RadioMenuFlyoutItem { Text = "全部記錄", GroupName = "noteFilter", IsChecked = !_pinnedOnly };
                all.Click += (_, _) => PinnedOnly = false;
                var pinned = new RadioMenuFlyoutItem { Text = "只看固定", GroupName = "noteFilter", IsChecked = _pinnedOnly };
                pinned.Click += (_, _) => PinnedOnly = true;
                menu.Items.Add(all);
                menu.Items.Add(pinned);
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MenuItem("複製全部文字", "\uE8C8", () =>
                {
                    var text = string.Join(Environment.NewLine + Environment.NewLine, Notes.Select(n => n.Content));
                    if (_clips is not null)
                    {
                        _clips.CopyTextQuietly(text);
                    }
                    else
                    {
                        FileOps.CopyText(text);
                    }
                }));
                menu.ShowAt(ActionButton);
                break;
            }
            default:
                _clips?.Clear();
                RenderClips();
                break;
        }
    }

    private static Button IconButton(string glyph, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 11 },
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }
}
