using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RDesk.Helpers;
using RDesk.Models;
using RDesk.Services;
using WinUIEx;

namespace RDesk.Views;

public sealed partial class SettingsWindow : WindowEx
{
    private readonly AppHost _host;
    private readonly WeatherService _weather = new();
    // True until LoadFromConfig finishes: control initialization raises change events
    // (e.g. Slider clamping 0 to Minimum) that must not be written back to config.
    private bool _loading = true;
    private bool _loaded;
    private bool _closed;
    private List<GeoResult> _geoResults = new();

    public SettingsWindow()
    {
        _host = AppHost.Current;
        InitializeComponent();
        this.CenterOnScreen();
        Closed += (_, _) =>
        {
            _closed = true;
            _host.Updates.StatusChanged -= Updates_StatusChanged;
        };
        ApplyTheme();
        LoadFromConfig();
        _host.Updates.StatusChanged += Updates_StatusChanged;
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

    public int SettingsPageCount => SettingsNav.MenuItems.OfType<NavigationViewItem>().Count();

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (string.IsNullOrEmpty(tag))
        {
            return;
        }

        PageGeneral.Visibility = PageVisible(tag, "general");
        PageAppearance.Visibility = PageVisible(tag, "appearance");
        PageBlocks.Visibility = PageVisible(tag, "blocks");
        PageDesktop.Visibility = PageVisible(tag, "desktop");
        PageFeatures.Visibility = PageVisible(tag, "features");
        PageBackup.Visibility = PageVisible(tag, "backup");
    }

    private static Visibility PageVisible(string tag, string id) =>
        string.Equals(tag, id, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;

    private void LoadFromConfig()
    {
        _loading = true;
        var cfg = _host.Config.Current;

        RefreshMonitorList();
        DockCombo.SelectedIndex = string.Equals(cfg.DockSide, "Left", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        WidthSlider.Value = cfg.SidebarWidth;
        WidthValueText.Text = $"{cfg.SidebarWidth} px（340 ~ 460）";
        ThemeCombo.SelectedIndex = cfg.ThemeMode switch
        {
            var m when string.Equals(m, "Dark", StringComparison.OrdinalIgnoreCase) => 1,
            var m when string.Equals(m, "System", StringComparison.OrdinalIgnoreCase) => 2,
            _ => 0
        };
        PanelTransparencySlider.Value = Math.Round(cfg.PanelTransparency * 100);
        CardTransparencySlider.Value = Math.Round(cfg.CardTransparency * 100);
        PanelTransparencyText.Text = ThemeService.TransparencyCaption(cfg.PanelTransparency);
        CardTransparencyText.Text = ThemeService.TransparencyCaption(cfg.CardTransparency);
        HotkeyToggle.IsOn = cfg.EnableRevealHotkey;
        HotkeyBox.Text = cfg.RevealHotkey;
        UpdateHotkeyStatus();
        CurrentLocationText.Text = $"目前：{cfg.LocationName}（{cfg.Latitude:0.####}, {cfg.Longitude:0.####}）";
        PinToggle.IsOn = cfg.PinToDesktop;
        StartupToggle.IsOn = _host.Startup.IsEnabled();
        ConfigPathText.Text = $"設定檔：{ConfigService.ConfigFilePath}";

        AccentCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeService.AccentNames, cfg.AccentColor));
        BackdropCombo.SelectedIndex = cfg.Backdrop switch { "Mica" => 1, "Solid" => 2, _ => 0 };
        BuildBlockRows();
        DropModeCombo.SelectedIndex = cfg.DropMovesFiles ? 1 : 0;
        StorageRootText.Text = _host.Config.StorageRoot;
        AutoOrganizeToggle.IsOn = cfg.AutoOrganizeEnabled;
        RulesBox.Text = FormatRules(cfg.OrganizeRules);
        ClipboardToggle.IsOn = cfg.ClipboardHistoryEnabled;
        SearchHotkeyToggle.IsOn = cfg.EnableSearchHotkey;
        SearchHotkeyBox.Text = cfg.SearchHotkey;
        UpdateSearchHotkeyStatus();
        BoxesHotkeyToggle.IsOn = cfg.EnableBoxesHotkey;
        BoxesHotkeyBox.Text = cfg.BoxesHotkey;
        UpdateBoxesHotkeyStatus();
        UpdateOrganizeStatus();
        UpdateSnapshotStatus();
        AutoUpdateToggle.IsOn = cfg.AutoUpdateEnabled;
        UpdateGithubBox.Text = cfg.UpdateGithubUrl;
        AppVersionFooter.Text = $"RDesk · {UpdateService.AppVersion}";
        RefreshUpdateStatus();
        _loading = false;
    }

    // ---------- 外觀 ----------

    private void AccentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AccentCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _host.Config.Update(c => c.AccentColor = item.Tag?.ToString() ?? "Blue");
        _host.Theme.NotifyAppearanceChanged();
    }

    private void BackdropCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BackdropCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _host.Config.Update(c => c.Backdrop = item.Tag?.ToString() ?? "Acrylic");
        _host.Theme.NotifyAppearanceChanged();
    }

    /// <summary>Every sidebar block with its own on / off switch (DeskBox 小組件開關).</summary>
    public static readonly (string Key, string Title, string Description, string Glyph, string Color,
        Func<AppConfig, bool> Get, Action<AppConfig, bool> Set, string? Parent)[] Blocks =
    {
        ("weather", "時鐘與天氣", "時間、日期、即時天氣與三日預報", "\uE9CA", "#F59E0B", c => c.ShowWeatherCard, (c, v) => c.ShowWeatherCard = v, null),
        ("recent", "最近打開的文件", "Windows 最近使用的檔案，可固定常用項目", "\uE81C", "#3B82F6", c => c.ShowRecentCard, (c, v) => c.ShowRecentCard = v, null),
        ("boxes", "文件格子", "收納 / 映射 / 收藏格子；桌面上的項目可直接拖進來整理", "\uE8B7", "#E0A100", c => c.ShowFavoritesCard, (c, v) => c.ShowFavoritesCard = v, null),
        ("todo", "待辦事項", "待辦、提醒、重複與篩選", "\uE9D5", "#10B981", c => c.ShowTodoCard, (c, v) => c.ShowTodoCard = v, null),
        ("notes", "隨記分頁", "待辦卡片中的「隨記」分頁", "\uE70B", "#8B5CF6", c => c.ShowNotesTab, (c, v) => c.ShowNotesTab = v, "todo"),
        ("clips", "剪貼簿分頁", "待辦卡片中的「剪貼簿」記錄分頁", "\uE77F", "#64748B", c => c.ShowClipboardTab, (c, v) => c.ShowClipboardTab = v, "todo"),
        ("music", "音樂播放", "播放指定資料夾或自訂清單，可暫停、切歌與調音量", "\uE8D6", "#EC4899", c => c.ShowMusicCard, (c, v) => c.ShowMusicCard = v, null),
        ("system", "系統狀態", "CPU、記憶體與運行時間", "\uE9D9", "#7C5CFF", c => c.ShowSystemCard, (c, v) => c.ShowSystemCard = v, null),
        ("network", "網路狀態", "連線狀態與即時上傳 / 下載速度", "\uE774", "#0EA5E9", c => c.ShowNetworkCard, (c, v) => c.ShowNetworkCard = v, null),
        ("disk", "硬碟狀態", "各硬碟使用量與剩餘空間", "\uE7F4", "#F97316", c => c.ShowDiskCard, (c, v) => c.ShowDiskCard = v, null),
        ("tip", "底部提示文字", "側邊欄最下方的操作提示", "\uE946", "#94A3B8", c => c.ShowSidebarTip, (c, v) => c.ShowSidebarTip = v, null),
    };

    private readonly Dictionary<string, ToggleSwitch> _blockToggles = new();

    private void BuildBlockRows()
    {
        BlocksHost.Children.Clear();
        _blockToggles.Clear();
        var cfg = _host.Config.Current;
        foreach (var block in Blocks)
        {
            var toggle = new ToggleSwitch
            {
                IsOn = block.Get(cfg),
                OnContent = "顯示",
                OffContent = "隱藏",
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            var key = block.Key;
            toggle.Toggled += (_, _) => SetBlockVisible(key, toggle.IsOn);
            _blockToggles[key] = toggle;

            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new FontIcon
            {
                Glyph = block.Glyph,
                FontSize = 18,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(ParseColor(block.Color))
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = block.Title, Style = (Style)Application.Current.Resources["SettingTitleStyle"] });
            text.Children.Add(new TextBlock { Text = block.Description, Style = (Style)Application.Current.Resources["SettingDescriptionStyle"] });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(toggle, 2);
            grid.Children.Add(toggle);

            BlocksHost.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["SettingRowStyle"],
                Margin = block.Parent is null ? new Thickness(0) : new Thickness(28, 0, 0, 0),
                Child = grid
            });
        }

        UpdateChildBlocks();
    }

    /// <summary>Turns one sidebar block on / off and applies it immediately.</summary>
    public void SetBlockVisible(string key, bool visible)
    {
        if (_loading)
        {
            return;
        }

        var block = Blocks.First(b => b.Key == key);
        _host.Config.Update(c => block.Set(c, visible));
        if (_blockToggles.TryGetValue(key, out var toggle) && toggle.IsOn != visible)
        {
            toggle.IsOn = visible;
        }

        UpdateChildBlocks();
        _host.Sidebar?.ApplyCardVisibility();
    }

    private void UpdateChildBlocks()
    {
        var cfg = _host.Config.Current;
        foreach (var block in Blocks.Where(b => b.Parent is not null))
        {
            if (_blockToggles.TryGetValue(block.Key, out var toggle))
            {
                toggle.IsEnabled = Blocks.First(b => b.Key == block.Parent).Get(cfg);
            }
        }
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Windows.UI.Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    // ---------- 文件格子 ----------

    private void DropModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DropModeCombo.SelectedIndex < 0)
        {
            return;
        }

        _host.Config.Update(c => c.DropMovesFiles = DropModeCombo.SelectedIndex == 1);
    }

    private void OpenStorageRoot_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_host.Config.StorageRoot);
        Helpers.FileOps.Open(_host.Config.StorageRoot);
    }

    private async void ChangeStorageRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        _host.Config.Update(c => c.StorageRoot = folder.Path);
        StorageRootText.Text = $"{_host.Config.StorageRoot}（新收納格子會建在這裡；既有格子維持原位置）";
    }

    // ---------- 桌面整理 ----------

    private void UpdateOrganizeStatus()
    {
        var last = _host.Organizer.LastUndoableBatch();
        UndoOrganizeButton.IsEnabled = last is not null;
        OrganizeStatusText.Text = last is null
            ? "先預覽移動計畫，確認後才移動；整理後可以復原"
            : $"上次整理：{last.Time:MM/dd HH:mm}{(last.IsAuto ? "（自動）" : string.Empty)}，移動了 {last.Moves.Count} 個檔案";
    }

    private void OpenOrganizer_Click(object sender, RoutedEventArgs e) => _host.OpenOrganizer();

    private async void UndoOrganize_Click(object sender, RoutedEventArgs e)
    {
        var last = _host.Organizer.LastUndoableBatch();
        if (last is null)
        {
            return;
        }

        if (!await Helpers.Dialogs.ConfirmAsync(RootGrid.XamlRoot, "復原整理",
                $"會把 {last.Time:MM/dd HH:mm} 那次整理的 {last.Moves.Count} 個檔案移回原位置。", "復原"))
        {
            return;
        }

        var restored = _host.Organizer.Undo(last);
        _host.OnStorageChanged();
        OrganizeStatusText.Text = $"✓ 已移回 {restored} 個檔案";
        UndoOrganizeButton.IsEnabled = _host.Organizer.LastUndoableBatch() is not null;
    }

    private void AutoOrganizeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _host.Config.Update(c => c.AutoOrganizeEnabled = AutoOrganizeToggle.IsOn);
        _host.Organizer.ApplyAutoSetting();
    }

    private static string FormatRules(IEnumerable<OrganizeRuleModel> rules) =>
        string.Join(Environment.NewLine, rules.Select(r => $"{r.Name}: {string.Join(' ', r.Extensions)}"));

    public static List<OrganizeRuleModel> ParseRules(string text)
    {
        var rules = new List<OrganizeRuleModel>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOfAny([':', '：']);
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var exts = line[(colon + 1)..]
                .Split([' ', ',', '，', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
                .Distinct()
                .ToList();
            if (name.Length > 0 && exts.Count > 0 && rules.All(r => r.Name != name))
            {
                rules.Add(new OrganizeRuleModel { Name = name, Extensions = exts });
            }
        }

        return rules;
    }

    private void SaveRules_Click(object sender, RoutedEventArgs e)
    {
        var rules = ParseRules(RulesBox.Text ?? string.Empty);
        if (rules.Count == 0)
        {
            RulesStatusText.Text = "⚠ 沒有可用的規則，格式：圖片: .jpg .png";
            return;
        }

        _host.Config.Update(c => c.OrganizeRules = rules);
        RulesBox.Text = FormatRules(rules);
        RulesStatusText.Text = $"✓ 已儲存 {rules.Count} 條規則";
        _host.OnStorageChanged();
    }

    private void ResetRules_Click(object sender, RoutedEventArgs e)
    {
        RulesBox.Text = FormatRules(FileCategories.DefaultRules());
        RulesStatusText.Text = "已填入預設規則，按「儲存規則」生效";
    }

    // ---------- 隨記與剪貼簿 ----------

    private void ClipboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _host.Config.Update(c => c.ClipboardHistoryEnabled = ClipboardToggle.IsOn);
        if (_host.Sidebar?.TodoView is { Mode: Controls.TodoNotesControl.ClipMode } todo)
        {
            todo.ShowMode(Controls.TodoNotesControl.ClipMode);
        }
    }

    private void ClearClipboard_Click(object sender, RoutedEventArgs e) => _host.Clipboard.Clear();

    // ---------- 搜尋 ----------

    private void UpdateSearchHotkeyStatus()
    {
        var cfg = _host.Config.Current;
        SearchHotkeyStatusText.Text = !cfg.EnableSearchHotkey
            ? "已關閉，可從側邊欄的放大鏡或系統匣選單開啟搜尋"
            : _host.Hotkeys.IsSearchRegistered
                ? $"✓ {cfg.SearchHotkey} 已生效，與 {cfg.RevealHotkey} 分開設定"
                : $"⚠ {cfg.SearchHotkey} 無法註冊（格式錯誤或已被占用），請換一組";
        _host.Sidebar?.UpdateTip();
    }

    private void SearchHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _host.Config.Update(c => c.EnableSearchHotkey = SearchHotkeyToggle.IsOn);
        _host.Hotkeys.RefreshRegistration();
        UpdateSearchHotkeyStatus();
    }

    private void SearchHotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var text = string.IsNullOrWhiteSpace(SearchHotkeyBox.Text) ? "Ctrl+Alt+F" : SearchHotkeyBox.Text.Trim();
        if (!HotkeyService.IsValidHotkey(text))
        {
            SearchHotkeyStatusText.Text = $"⚠「{text}」格式無法辨識，請輸入如 Ctrl+Alt+F、Alt+Space。";
            return;
        }

        SearchHotkeyBox.Text = text;
        _host.Config.Update(c => c.SearchHotkey = text);
        _host.Hotkeys.RefreshRegistration();
        UpdateSearchHotkeyStatus();
    }

    // ---------- 桌面格子快捷鍵 ----------

    private void UpdateBoxesHotkeyStatus()
    {
        var cfg = _host.Config.Current;
        BoxesHotkeyStatusText.Text = !cfg.EnableBoxesHotkey
            ? "已關閉，可從系統匣右鍵「顯示所有桌面格子」把格子提到最前面"
            : _host.Hotkeys.IsBoxesRegistered
                ? $"✓ {cfg.BoxesHotkey} 已生效：桌面上的格子會浮到其他視窗之上"
                : $"⚠ {cfg.BoxesHotkey} 無法註冊（格式錯誤、與其他快捷鍵重複，或已被占用）";
        _host.Sidebar?.UpdateTip();
    }

    private void BoxesHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _host.Config.Update(c => c.EnableBoxesHotkey = BoxesHotkeyToggle.IsOn);
        _host.Hotkeys.RefreshRegistration();
        UpdateBoxesHotkeyStatus();
    }

    private void BoxesHotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var text = string.IsNullOrWhiteSpace(BoxesHotkeyBox.Text) ? "Ctrl+Alt+B" : BoxesHotkeyBox.Text.Trim();
        if (!HotkeyService.IsValidHotkey(text))
        {
            BoxesHotkeyStatusText.Text = $"⚠「{text}」格式無法辨識，請輸入如 Ctrl+Alt+B、F8。";
            return;
        }

        BoxesHotkeyBox.Text = text;
        _host.Config.Update(c => c.BoxesHotkey = text);
        _host.Hotkeys.RefreshRegistration();
        UpdateBoxesHotkeyStatus();
    }

    // ---------- 備份 ----------

    private void UpdateSnapshotStatus()
    {
        var snaps = _host.Backup.Snapshots();
        SnapshotStatusText.Text = snaps.Count == 0
            ? "每天第一次啟動時自動備份一次，保留最近 7 份"
            : $"每天自動備份，保留最近 7 份 · 最新：{snaps[0].LastWriteTime:MM/dd HH:mm}（共 {snaps.Count} 份）";
    }

    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = $"RDesk-備份-{DateTime.Now:yyyyMMdd}",
            DefaultFileExtension = ".zip"
        };
        picker.FileTypeChoices.Add("RDesk 備份", new List<string> { ".zip" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            _host.Backup.Export(file.Path);
            BackupStatusText.Text = $"✓ 已匯出到 {file.Path}";
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = $"⚠ 匯出失敗：{ex.Message}";
        }
    }

    private async void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        if (!await Helpers.Dialogs.ConfirmAsync(RootGrid.XamlRoot, "從備份還原",
                "目前的設定、格子清單、待辦與隨記會被備份內容取代（還原前會先自動建立快照）。", "還原"))
        {
            return;
        }

        try
        {
            var snapshot = _host.Backup.Import(file.Path);
            _host.ReloadAfterRestore();
            LoadFromConfig();
            BackupStatusText.Text = $"✓ 已還原。原本的資料已存成快照：{Path.GetFileName(snapshot)}";
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = $"⚠ 還原失敗，現有資料未變更：{ex.Message}";
        }
    }

    private void Updates_StatusChanged(object? sender, EventArgs e)
    {
        if (!_closed)
        {
            DispatcherQueue.TryEnqueue(RefreshUpdateStatus);
        }
    }

    private void RefreshUpdateStatus()
    {
        UpdateStatusText.Text = _host.Updates.Status;
        RestartUpdateButton.Visibility = _host.Updates.CanRestartNow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _host.Config.Update(c => c.AutoUpdateEnabled = AutoUpdateToggle.IsOn);
    }

    private void UpdateGithubBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var url = (UpdateGithubBox.Text ?? string.Empty).Trim().TrimEnd('/');
        _host.Config.Update(c => c.UpdateGithubUrl = url);
        _host.Updates.InvalidateSource();
        RefreshUpdateStatus();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateRing.Visibility = Visibility.Visible;
        CheckUpdateRing.IsActive = true;
        UpdateStatusText.Text = "正在檢查 GitHub 上的新版本…";
        try
        {
            UpdateGithubBox_LostFocus(sender, e);
            await _host.Updates.CheckAndDownloadAsync(userRequested: true);
            RefreshUpdateStatus();
            if (RootGrid.XamlRoot is not null)
            {
                await Dialogs.InfoAsync(RootGrid.XamlRoot, "檢查更新", _host.Updates.Status);
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "檢查更新失敗：" + ex.Message;
            if (RootGrid.XamlRoot is not null)
            {
                await Dialogs.InfoAsync(RootGrid.XamlRoot, "檢查更新", UpdateStatusText.Text);
            }
        }
        finally
        {
            CheckUpdateRing.IsActive = false;
            CheckUpdateRing.Visibility = Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = true;
            RefreshUpdateStatus();
        }
    }

    private void RestartUpdate_Click(object sender, RoutedEventArgs e)
    {
        _host.Updates.RestartAndApply();
    }

    private void CreateSnapshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _host.Backup.CreateSnapshot();
            BackupStatusText.Text = $"✓ 已建立快照 {Path.GetFileName(path)}";
            UpdateSnapshotStatus();
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = $"⚠ 建立快照失敗：{ex.Message}";
        }
    }

    private void OpenSnapshots_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(BackupService.SnapshotFolder);
        Helpers.FileOps.Open(BackupService.SnapshotFolder);
    }

    private void UpdateHotkeyStatus()
    {
        var cfg = _host.Config.Current;
        HotkeyStatusText.Text = !cfg.EnableRevealHotkey
            ? "快捷鍵已關閉，只能透過系統匣圖示展開。"
            : _host.Hotkeys.IsRegistered
                ? $"✓ {cfg.RevealHotkey} 已生效"
                : $"⚠ {cfg.RevealHotkey} 無法註冊（格式錯誤或已被其他程式占用），請換一組。";
        _host.Sidebar?.UpdateTip();
    }

    private void CityBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SearchCity_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void SearchCity_Click(object sender, RoutedEventArgs e)
    {
        var query = CityBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return;
        }

        try
        {
            CurrentLocationText.Text = "搜尋中...";
            _geoResults = (await _weather.SearchCityAsync(query)).ToList();
            _loading = true;
            CityResults.Items.Clear();
            foreach (var g in _geoResults)
            {
                CityResults.Items.Add(g.DisplayName);
            }

            _loading = false;
            CurrentLocationText.Text = _geoResults.Count == 0
                ? "找不到符合的城市，請換個關鍵字。"
                : "請在上方清單中點選地區。";
        }
        catch (Exception ex)
        {
            _loading = false;
            CurrentLocationText.Text = $"搜尋失敗：{ex.Message}";
        }
    }

    private async void CityResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CityResults.SelectedIndex < 0 || CityResults.SelectedIndex >= _geoResults.Count)
        {
            return;
        }

        var g = _geoResults[CityResults.SelectedIndex];
        _host.Config.Update(c =>
        {
            c.LocationName = g.DisplayName;
            c.Latitude = g.Latitude;
            c.Longitude = g.Longitude;
        });
        CurrentLocationText.Text = $"目前地區：{g.DisplayName}（{g.Latitude:0.####}, {g.Longitude:0.####}）";
        if (_host.Sidebar is not null)
        {
            await _host.Sidebar.RefreshWeatherAsync();
        }
    }

    private void RefreshMonitorList()
    {
        var wasLoading = _loading;
        _loading = true;
        var monitors = _host.Monitors.GetMonitors();
        var cfg = _host.Config.Current;
        MonitorCombo.Items.Clear();
        MonitorCombo.Items.Add("主顯示器（自動）");
        var selected = 0;
        for (var i = 0; i < monitors.Count; i++)
        {
            MonitorCombo.Items.Add(monitors[i].DisplayName);
            if (cfg.TargetMonitorIndex >= 0 &&
                (monitors[i].DisplayId == cfg.TargetMonitorDeviceId || monitors[i].Index == cfg.TargetMonitorIndex))
            {
                selected = i + 1;
            }
        }

        MonitorCombo.SelectedIndex = selected;
        _loading = wasLoading;
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => RefreshMonitorList();

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MonitorCombo.SelectedIndex < 0)
        {
            return;
        }

        if (MonitorCombo.SelectedIndex == 0)
        {
            _host.Config.Update(c =>
            {
                c.TargetMonitorIndex = -1;
                c.TargetMonitorDeviceId = string.Empty;
            });
        }
        else
        {
            var monitors = _host.Monitors.GetMonitors();
            var index = MonitorCombo.SelectedIndex - 1;
            if (index >= monitors.Count)
            {
                return;
            }

            var m = monitors[index];
            _host.Config.Update(c =>
            {
                c.TargetMonitorIndex = m.Index;
                c.TargetMonitorDeviceId = m.DisplayId;
            });
        }

        _host.Sidebar?.Reposition();
    }

    private void DockCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DockCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var side = item.Tag?.ToString() ?? "Right";
        _host.Config.Update(c => c.DockSide = side);
        _host.Sidebar?.Reposition();
    }

    private void WidthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var width = (int)Math.Round(e.NewValue);
        _host.Config.Update(c => c.SidebarWidth = width);
        WidthValueText.Text = $"{width} px（340 ~ 460）";
        _host.Sidebar?.Reposition();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _host.Theme.SetMode(item.Tag?.ToString() ?? "Light");
    }

    private void PanelTransparencySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        _host.Config.Update(c =>
        {
            c.PanelTransparency = value;
            c.PanelOpacity = 1.0 - value;
        });
        PanelTransparencyText.Text = ThemeService.TransparencyCaption(value);
        _host.Theme.NotifyAppearanceChanged();
    }

    private void CardTransparencySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        _host.Config.Update(c => c.CardTransparency = value);
        CardTransparencyText.Text = ThemeService.TransparencyCaption(value);
        _host.Theme.NotifyAppearanceChanged();
    }

    private void HotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _host.Config.Update(c => c.EnableRevealHotkey = HotkeyToggle.IsOn);
        _host.Hotkeys.RefreshRegistration();
        UpdateHotkeyStatus();
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var text = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? "F7" : HotkeyBox.Text.Trim();
        if (!HotkeyService.IsValidHotkey(text))
        {
            HotkeyStatusText.Text = $"⚠「{text}」格式無法辨識，請輸入如 F7、Ctrl+Alt+R。";
            return;
        }

        HotkeyBox.Text = text;
        _host.Config.Update(c => c.RevealHotkey = text);
        _host.Hotkeys.RefreshRegistration();
        UpdateHotkeyStatus();
    }

    private void PinToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _host.Config.Update(c => c.PinToDesktop = PinToggle.IsOn);
        _host.ApplyDesktopLayerEverywhere();
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _host.Startup.SetEnabled(StartupToggle.IsOn);
        _host.Config.Update(c => c.RunOnStartup = StartupToggle.IsOn);
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"\"{Path.GetDirectoryName(ConfigService.ConfigFilePath)}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StartupLog.Write("Open config folder failed", ex);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _host.Config.SaveImmediate();
        Close();
    }
}
