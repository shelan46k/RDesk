using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDesk.Helpers;
using RDesk.Models;
using RDesk.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RDesk.Views.Controls;

/// <summary>
/// Sidebar card for file boxes (DeskBox 文件格子): favorites (links), storage boxes (real folder that
/// receives dropped files) and mapped folders (live view of an existing directory).
/// </summary>
public sealed partial class FavoritesControl : UserControl
{
    private ConfigService? _config;
    private FavoriteGroupModel? _current;
    private bool _reloadingGroups;

    public event EventHandler<FavoriteGroupModel>? DetachRequested;

    public FavoritesControl()
    {
        InitializeComponent();
        Box.GroupChanged += (_, _) => Persist();
    }

    public void Bind(ConfigService config)
    {
        _config = config;
        ReloadGroups(0);
    }

    public FavoriteGroupModel? CurrentGroup => _current;
    public FileBoxView BoxView => Box;
    public int ItemCount => Box.ItemCount;

    public void AddFavoritePath(string path)
    {
        if (_current is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_current.IsFolderMapping)
        {
            HintText.Text = "映射格子顯示資料夾本身的內容；要收藏捷徑請切到一般格子。";
            return;
        }

        Box.AddPaths(new[] { path }, move: false);
    }

    public bool RenameItem(string path, string newName) => Box.RenameDisplay(path, newName);

    public void RemoveFavoritePath(string path)
    {
        var vm = Box.VisibleItems.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (vm is not null)
        {
            Box.RemoveFromGroup(new[] { vm });
        }
    }

    /// <summary>Called by AppHost when another surface (e.g. a detached box) changed this group.</summary>
    public void RefreshIfShowing(string groupId)
    {
        if (_current?.Id == groupId)
        {
            Box.Reload();
            UpdateHint();
        }
    }

    public void SelectGroup(string groupId)
    {
        if (_config is null)
        {
            return;
        }

        var index = _config.Current.FavoriteGroups.FindIndex(g => g.Id == groupId);
        if (index >= 0)
        {
            ReloadGroups(index);
        }
    }

    public void ReloadGroups(int selectIndex)
    {
        if (_config is null)
        {
            return;
        }

        if (_config.Current.FavoriteGroups.Count == 0)
        {
            _config.Update(c => c.FavoriteGroups.Add(new FavoriteGroupModel { Title = "預設收藏" }));
        }

        _reloadingGroups = true;
        GroupCombo.Items.Clear();
        foreach (var g in _config.Current.FavoriteGroups)
        {
            var prefix = g.IsStorageBox ? "📥 " : g.IsFolderMapping ? "📁 " : "⭐ ";
            GroupCombo.Items.Add(prefix + g.Title + (g.IsDetachedToDesktop ? "（桌面）" : string.Empty));
        }

        _reloadingGroups = false;
        GroupCombo.SelectedIndex = -1;
        GroupCombo.SelectedIndex = Math.Clamp(selectIndex, 0, GroupCombo.Items.Count - 1);
    }

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reloadingGroups || _config is null || GroupCombo.SelectedIndex < 0 ||
            GroupCombo.SelectedIndex >= _config.Current.FavoriteGroups.Count)
        {
            return;
        }

        _current = _config.Current.FavoriteGroups[GroupCombo.SelectedIndex];
        Box.Bind(_current);
        UpdateHint();
    }

    private void UpdateHint()
    {
        if (_current is null)
        {
            return;
        }

        HintText.Text = _current.IsStorageBox
            ? $"收納格子 · 桌面項目拖進來會收走，其他位置預設{(_config!.Current.DropMovesFiles ? "移動" : "複製")}；拖回桌面或右鍵「放回桌面」即可取出"
            : _current.IsFolderMapping
                ? $"映射 · {_current.MappedFolderPath}（即時同步）"
                : Box.ItemCount == 0
                    ? "把桌面上的捷徑、資料夾、檔案直接拖進來整理（按住 Shift 只加入連結）"
                    : "雙擊開啟 · 拖回桌面或右鍵「放回桌面」取出 · 空白處右鍵可切換檢視";
    }

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null)
        {
            Box.BuildBackgroundMenu().ShowAt(OptionsButton);
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null)
        {
            return;
        }

        var config = _config;
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("加入檔案…", "\uE8E5", async () =>
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, SidebarHwnd());
            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0)
            {
                Box.AddPaths(files.Select(f => f.Path).ToList(), move: false);
            }
        }, enabled: _current is { IsFolderMapping: false }));
        menu.Items.Add(MenuItem("加入資料夾…", "\uE8B7", async () =>
        {
            var folder = await PickFolderAsync();
            if (folder is not null)
            {
                Box.AddPaths(new[] { folder }, move: false);
            }
        }, enabled: _current is { IsFolderMapping: false }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("新增收納格子…", "\uE7B8", async () =>
        {
            var title = await Dialogs.PromptAsync(XamlRoot, "新增收納格子", "待處理",
                "會在收納資料夾內建立同名資料夾");
            if (!string.IsNullOrWhiteSpace(title))
            {
                var group = CreateStorageBox(config, title);
                ReloadGroups(config.Current.FavoriteGroups.IndexOf(group));
            }
        }));
        menu.Items.Add(MenuItem("新增映射資料夾格子…", "\uE8DA", async () =>
        {
            var folder = await PickFolderAsync();
            if (folder is null)
            {
                return;
            }

            config.Current.FavoriteGroups.Add(new FavoriteGroupModel
            {
                Title = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder,
                IsFolderMapping = true,
                MappedFolderPath = folder
            });
            Persist();
            ReloadGroups(config.Current.FavoriteGroups.Count - 1);
        }));
        menu.Items.Add(MenuItem("新增收藏格子…", "\uE734", async () =>
        {
            var title = await Dialogs.PromptAsync(XamlRoot, "新增收藏格子", $"收藏 {config.Current.FavoriteGroups.Count + 1}");
            if (!string.IsNullOrWhiteSpace(title))
            {
                config.Current.FavoriteGroups.Add(new FavoriteGroupModel { Title = title });
                Persist();
                ReloadGroups(config.Current.FavoriteGroups.Count - 1);
            }
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("一鍵整理桌面…", "\uE8CB", () =>
        {
            AppHost.Current.OpenOrganizer();
            return Task.CompletedTask;
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("重新命名目前格子…", "\uE8AC", async () =>
        {
            if (_current is null)
            {
                return;
            }

            var title = await Dialogs.PromptAsync(XamlRoot, "重新命名格子", _current.Title);
            if (!string.IsNullOrWhiteSpace(title))
            {
                var index = GroupCombo.SelectedIndex;
                _current.Title = title;
                Persist();
                ReloadGroups(index);
            }
        }));
        menu.Items.Add(MenuItem("刪除目前格子…", "\uE74D", async () =>
        {
            if (_current is null || config.Current.FavoriteGroups.Count <= 1)
            {
                return;
            }

            var stored = _current.IsFolderBacked
                ? new List<FavoriteItemModel>()
                : _current.Items.Where(i => FileOps.IsUnder(i.FullPath, _current.StoreFolder)).ToList();
            var message = _current.IsFolderBacked
                ? $"只會刪除「{_current.Title}」這個入口，資料夾 {_current.MappedFolderPath} 與裡面的檔案都會保留。"
                : stored.Count > 0
                    ? $"從桌面收進「{_current.Title}」的 {stored.Count} 個項目會放回原位置（桌面），其他捷徑只移除入口。"
                    : $"只會刪除「{_current.Title}」這個入口，收藏的檔案本身不受影響。";
            if (!await Dialogs.ConfirmAsync(XamlRoot, "刪除格子", message, "刪除格子"))
            {
                return;
            }

            foreach (var item in stored)
            {
                FileOps.PutBack(item.FullPath, item.OriginalPath);
            }

            AppHost.Current.CloseDetachedBox(_current.Id);
            config.Current.FavoriteGroups.Remove(_current);
            Persist();
            ReloadGroups(0);
        }, enabled: config.Current.FavoriteGroups.Count > 1));
        menu.ShowAt(AddButton);
    }

    public static FavoriteGroupModel CreateStorageBox(ConfigService config, string title)
    {
        var safe = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
        if (safe.Length == 0)
        {
            safe = "收納";
        }

        var folder = Path.Combine(config.StorageRoot, safe);
        var existing = config.Current.FavoriteGroups.FirstOrDefault(g =>
            g.IsStorageBox && string.Equals(g.MappedFolderPath, folder, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        Directory.CreateDirectory(folder);
        var group = new FavoriteGroupModel
        {
            Title = title,
            IsStorageBox = true,
            MappedFolderPath = folder,
            SortBy = "Date",
            SortDescending = true
        };
        config.Current.FavoriteGroups.Add(group);
        config.SaveImmediate();
        return group;
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, SidebarHwnd());
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static nint SidebarHwnd() => WindowNative.GetWindowHandle(AppHost.Current.Sidebar);

    private void DetachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null)
        {
            DetachRequested?.Invoke(this, _current);
            ReloadGroups(GroupCombo.SelectedIndex);
        }
    }

    private void Persist()
    {
        _config?.SaveImmediate();
        UpdateHint();
        if (_current is not null)
        {
            AppHost.Current.NotifyFavoritesChanged(_current.Id, this);
        }
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Func<Task> action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                StartupLog.Write($"Menu '{text}' failed", ex);
            }
        };
        return item;
    }
}
