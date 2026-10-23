using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using RDesk.Helpers;
using RDesk.Models;
using RDesk.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace RDesk.Views.Controls;

/// <summary>
/// The contents of one file box (常用 / 收納 / 映射). Shared by the sidebar card and detached desktop boxes:
/// icon / list view, sorting, type stacks, drag in (link / copy / move), drag out to other apps,
/// real file operations, quick preview and live folder sync.
/// </summary>
public sealed partial class FileBoxView : UserControl
{
    private readonly ObservableCollection<FavoriteItemViewModel> _items = new();
    private List<FavoriteItemViewModel> _all = new();
    private FavoriteGroupModel? _group;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watchDebounce;
    private SegmentBar? _chips;
    private List<string> _chipCategories = new();
    private string _category = string.Empty;
    private bool _dragging;

    /// <summary>Raised after this view changed the group (persist + sync other surfaces).</summary>
    public event EventHandler? GroupChanged;

    /// <summary>Raised while external files hover / leave (detached capsules expand temporarily).</summary>
    public event EventHandler<bool>? DragHoverChanged;

    public FileBoxView()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _items;
        ItemsList.ItemsSource = _items;
        foreach (var view in new ListViewBase[] { ItemsGrid, ItemsList })
        {
            view.DoubleTapped += Items_DoubleTapped;
            view.RightTapped += Items_RightTapped;
            view.KeyDown += Items_KeyDown;
        }

        RootGrid.RightTapped += Background_RightTapped;
        AddHandler(DragOverEvent, new DragEventHandler(OnExternalDragOver), true);
        AddHandler(DragLeaveEvent, new DragEventHandler(OnExternalDragLeave), true);
        AddHandler(DropEvent, new DragEventHandler(OnExternalDrop), true);
        Unloaded += (_, _) => StopWatching();
    }

    public FavoriteGroupModel? Group => _group;
    public int ItemCount => _all.Count;
    public IReadOnlyList<FavoriteItemViewModel> VisibleItems => _items;
    public string CurrentCategory => _category;
    public IReadOnlyList<string> Categories => _chipCategories;

    public double ItemsMaxHeight
    {
        get => ItemsGrid.MaxHeight;
        set
        {
            ItemsGrid.MaxHeight = value;
            ItemsList.MaxHeight = value;
        }
    }

    public void Bind(FavoriteGroupModel group)
    {
        var changed = !ReferenceEquals(_group, group);
        _group = group;
        if (changed)
        {
            _category = string.Empty;
            StartWatching();
        }

        Reload();
    }

    public void Reload()
    {
        if (_group is null)
        {
            return;
        }

        if (PruneMissingStored())
        {
            GroupChanged?.Invoke(this, EventArgs.Empty);
        }

        var rules = AppHost.Current.Config.Current.OrganizeRules;
        _all = FavoriteItemViewModel.ResolveItems(_group)
            .Select(m => new FavoriteItemViewModel(m) { Category = FileCategories.Classify(m.FullPath, rules, m.IsDirectory) })
            .ToList();
        _all = Sort(_all).ToList();
        RebuildChips();
        ApplyFilter();
    }

    private IEnumerable<FavoriteItemViewModel> Sort(IEnumerable<FavoriteItemViewModel> items)
    {
        var group = _group!;
        var sortBy = group.SortBy;
        if (sortBy == "Custom" && group.IsFolderBacked)
        {
            sortBy = "Name";
        }

        IOrderedEnumerable<FavoriteItemViewModel>? ordered = sortBy switch
        {
            "Name" => items.OrderByDescending(i => i.Model.IsDirectory)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            "Type" => items.OrderByDescending(i => i.Model.IsDirectory)
                .ThenBy(i => i.TypeLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            "Size" => items.OrderByDescending(i => i.Model.IsDirectory).ThenBy(i => i.Size),
            "Date" => items.OrderBy(i => i.Modified),
            _ => null
        };
        if (ordered is null)
        {
            return items;
        }

        return group.SortDescending ? ordered.Reverse() : ordered;
    }

    private void RebuildChips()
    {
        var group = _group!;
        ChipHost.Children.Clear();
        _chips = null;
        _chipCategories = new List<string>();
        if (!group.StackByType || _all.Count == 0)
        {
            ChipScroller.Visibility = Visibility.Collapsed;
            _category = string.Empty;
            return;
        }

        var counts = _all.GroupBy(i => i.Category).ToDictionary(g => g.Key, g => g.Count());
        var order = new[] { FileCategories.Folder }
            .Concat(AppHost.Current.Config.Current.OrganizeRules.Select(r => r.Name))
            .Append(FileCategories.Other)
            .Distinct()
            .Where(counts.ContainsKey)
            .ToList();
        if (!order.Contains(_category))
        {
            _category = string.Empty;
        }

        _chipCategories = order;
        _chips = new SegmentBar { SegmentFontSize = 11 };
        _chips.AddSegment($"全部 {_all.Count}");
        foreach (var cat in order)
        {
            _chips.AddSegment($"{cat} {counts[cat]}", FileCategories.Glyph(cat));
        }

        _chips.SelectedIndex = _category.Length == 0 ? 0 : order.IndexOf(_category) + 1;
        _chips.SelectionChanged += (_, index) => SelectCategory(index == 0 ? string.Empty : order[index - 1]);
        ChipHost.Children.Add(_chips);
        ChipScroller.Visibility = Visibility.Visible;
    }

    public void SelectCategory(string category)
    {
        _category = category;
        if (_chips is not null)
        {
            _chips.SelectedIndex = category.Length == 0 ? 0 : _chipCategories.IndexOf(category) + 1;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var group = _group!;
        _items.Clear();
        foreach (var vm in _all.Where(i => _category.Length == 0 || i.Category == _category))
        {
            _items.Add(vm);
            _ = vm.LoadIconAsync();
        }

        var list = group.ViewMode == "List";
        ItemsGrid.Visibility = !list && _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemsList.Visibility = list && _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var canReorder = !group.IsFolderBacked && group.SortBy == "Custom" && _category.Length == 0;
        ItemsGrid.CanReorderItems = canReorder;
        ItemsList.CanReorderItems = canReorder;
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = group.IsFolderMapping && !Directory.Exists(group.MappedFolderPath ?? string.Empty)
            ? "映射的資料夾不存在，請在選單中重新指定"
            : group.IsStorageBox
                ? "把檔案拖進來收納（會實際複製或移動到收納資料夾）"
                : "把桌面上的捷徑或資料夾拖進來";
    }

    // ---------- View options ----------

    public void SetViewMode(string mode)
    {
        if (_group is null)
        {
            return;
        }

        _group.ViewMode = mode;
        ApplyFilter();
        RaiseChanged(reloadSelf: false);
    }

    public void SetSort(string sortBy, bool? descending = null)
    {
        if (_group is null)
        {
            return;
        }

        _group.SortBy = sortBy;
        if (descending.HasValue)
        {
            _group.SortDescending = descending.Value;
        }

        Reload();
        RaiseChanged(reloadSelf: false);
    }

    public void SetStackByType(bool enabled)
    {
        if (_group is null)
        {
            return;
        }

        _group.StackByType = enabled;
        Reload();
        RaiseChanged(reloadSelf: false);
    }

    // ---------- Live folder sync ----------

    private void StartWatching()
    {
        StopWatching();
        var folder = _group is null ? null : _group.IsFolderBacked ? _group.MappedFolderPath : _group.StoreFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            var dispatcher = DispatcherQueue;
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler onChange = (_, _) => dispatcher.TryEnqueue(ScheduleWatchReload);
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Changed += onChange;
            _watcher.Renamed += (_, _) => dispatcher.TryEnqueue(ScheduleWatchReload);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            StartupLog.Write("Folder watch failed", ex, folder);
        }
    }

    private void ScheduleWatchReload()
    {
        _watchDebounce ??= CreateDebounce();
        _watchDebounce.Stop();
        _watchDebounce.Start();
    }

    private DispatcherTimer CreateDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Reload();
        };
        return timer;
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // ---------- Drag in ----------

    private List<string>? _dragPaths;

    /// <summary>Desktop items and items already living in another RDesk box are tidied (moved), everything else follows the setting.</summary>
    private static bool IsTidySource(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        var config = AppHost.Current.Config;
        return paths.All(p => FileOps.IsOnDesktop(p) || FileOps.IsUnder(p, config.StorageRoot) || FileOps.IsUnder(p, StoreRoot) ||
                              config.Current.FavoriteGroups.Any(g =>
                                  (g.IsStorageBox && FileOps.IsUnder(p, g.MappedFolderPath)) || FileOps.IsUnder(p, g.StoreFolder)));
    }

    /// <summary>Decides move vs copy / link for a drop. Shift always flips the default.</summary>
    public bool ShouldMove(IReadOnlyList<string> paths, bool shift)
    {
        if (_group is null)
        {
            return false;
        }

        var tidy = IsTidySource(paths);
        var baseMove = _group.IsFolderBacked ? tidy || AppHost.Current.Config.Current.DropMovesFiles : tidy;
        return shift ? !baseMove : baseMove;
    }

    private static bool IsShift(DragEventArgs e) =>
        e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift);

    private static async Task<List<string>> DropPathsAsync(DataPackageView view) =>
        (await view.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();

    private async void OnExternalDragOver(object sender, DragEventArgs e)
    {
        if (_dragging || _group is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = _group.IsFolderBacked ? DataPackageOperation.Copy : DataPackageOperation.Link;
        var deferral = e.GetDeferral();
        try
        {
            _dragPaths ??= await DropPathsAsync(e.DataView);
            var move = ShouldMove(_dragPaths, IsShift(e));
            string caption;
            if (move)
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                caption = $"收進「{_group.Title}」";
            }
            else if (_group.IsFolderBacked)
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                caption = $"複製到「{_group.Title}」";
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.Link;
                caption = $"加入捷徑到「{_group.Title}」";
            }

            e.DragUIOverride.Caption = caption;
            DropText.Text = caption + (IsTidySource(_dragPaths)
                ? (move ? "（桌面上的項目會移進格子，按住 Shift 只加入連結）" : string.Empty)
                : _group.IsFolderBacked ? "（按住 Shift 切換複製 / 移動）" : string.Empty);
            DropOverlay.Visibility = Visibility.Visible;
            DragHoverChanged?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            StartupLog.Write("Box drag-over failed", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnExternalDragLeave(object sender, DragEventArgs e)
    {
        _dragPaths = null;
        DropOverlay.Visibility = Visibility.Collapsed;
        DragHoverChanged?.Invoke(this, false);
    }

    private async void OnExternalDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        _dragPaths = null;
        if (_dragging || _group is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            DragHoverChanged?.Invoke(this, false);
            return;
        }

        var shift = IsShift(e);
        var deferral = e.GetDeferral();
        try
        {
            var paths = await DropPathsAsync(e.DataView);
            var move = ShouldMove(paths, shift);
            AddPaths(paths, move);
            e.AcceptedOperation = move ? DataPackageOperation.Move : _group.IsFolderBacked ? DataPackageOperation.Copy : DataPackageOperation.Link;
        }
        catch (Exception ex)
        {
            StartupLog.Write("Box drop failed", ex);
        }
        finally
        {
            deferral.Complete();
            DragHoverChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Favorites: adds links, or (move) tidies the items into the box's own store folder.
    /// Folder-backed boxes: copies / moves the real files in.
    /// </summary>
    public int AddPaths(IReadOnlyList<string> paths, bool move)
    {
        if (_group is null || paths.Count == 0)
        {
            return 0;
        }

        var config = AppHost.Current.Config;
        var origins = paths.ToDictionary(p => p, p => OriginOf(config.Current, p), StringComparer.OrdinalIgnoreCase);
        int added;
        if (_group.IsFolderBacked)
        {
            if (string.IsNullOrWhiteSpace(_group.MappedFolderPath))
            {
                return 0;
            }

            added = FileOps.Transfer(paths, _group.MappedFolderPath, move).Count;
        }
        else
        {
            added = 0;
            foreach (var source in paths)
            {
                var path = source;
                var original = (string?)null;
                if (move && !FileOps.IsUnder(source, _group.StoreFolder))
                {
                    var moved = FileOps.Transfer(new[] { source }, EnsureStoreFolder(config, _group), move: true);
                    if (moved.Count == 1)
                    {
                        path = moved[0];
                        original = origins[source];
                    }
                }

                if (_group.Items.Any(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _group.Items.Add(new FavoriteItemModel
                {
                    Name = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path,
                    FullPath = path,
                    IsDirectory = Directory.Exists(path),
                    OriginalPath = original
                });
                added++;
            }
        }

        if (added > 0)
        {
            if (_watcher is null)
            {
                StartWatching();
            }

            Reload();
            RaiseChanged(reloadSelf: false);
            if (move)
            {
                AppHost.Current.OnStorageChanged();
            }
        }

        return added;
    }

    /// <summary>Original location of an item; keeps the first origin when it hops from box to box.</summary>
    private static string OriginOf(AppConfig config, string path) =>
        config.FavoriteGroups.SelectMany(g => g.Items)
            .FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase) && i.OriginalPath is not null)
            ?.OriginalPath ?? path;

    /// <summary>
    /// Hidden folder on the desktop itself: same volume as the desktop, so tidying in and dragging back out
    /// are instant moves (Explorer defaults to copy across volumes).
    /// </summary>
    public static string StoreRoot => Path.Combine(FileOps.UserDesktop, ".RDesk 格子收納");

    public static string EnsureStoreFolder(ConfigService config, FavoriteGroupModel group)
    {
        if (string.IsNullOrWhiteSpace(group.StoreFolder))
        {
            var safe = string.Concat(group.Title.Split(Path.GetInvalidFileNameChars())).Trim();
            group.StoreFolder = FileOps.UniquePath(Path.Combine(StoreRoot, safe.Length > 0 ? safe : "格子"));
        }

        if (!Directory.Exists(StoreRoot))
        {
            var root = Directory.CreateDirectory(StoreRoot);
            root.Attributes |= System.IO.FileAttributes.Hidden;
        }

        Directory.CreateDirectory(group.StoreFolder);
        return group.StoreFolder;
    }

    /// <summary>Items that were tidied into this box (and so are owned by it) rather than plain shortcuts.</summary>
    public bool IsStored(FavoriteItemViewModel vm) =>
        _group is not null && (_group.IsStorageBox || FileOps.IsUnder(vm.FullPath, _group.StoreFolder));

    /// <summary>Moves items out of the box back to their original folder (default: the desktop).</summary>
    public int PutBack(IEnumerable<FavoriteItemViewModel> selection)
    {
        if (_group is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var vm in selection.ToList())
        {
            if (!IsStored(vm) || FileOps.PutBack(vm.FullPath, vm.Model.OriginalPath) is null)
            {
                continue;
            }

            count++;
            if (!_group.IsFolderBacked)
            {
                _group.Items.Remove(vm.Model);
            }
        }

        if (count > 0)
        {
            Reload();
            RaiseChanged(reloadSelf: false);
        }

        return count;
    }

    /// <summary>Drops entries whose tidied file was dragged out (e.g. back onto the desktop).</summary>
    private bool PruneMissingStored()
    {
        if (_group is null || _group.IsFolderBacked || string.IsNullOrEmpty(_group.StoreFolder))
        {
            return false;
        }

        var removed = _group.Items.RemoveAll(i => FileOps.IsUnder(i.FullPath, _group.StoreFolder) &&
                                                  !File.Exists(i.FullPath) && !Directory.Exists(i.FullPath));
        return removed > 0;
    }

    // ---------- Drag out / reorder ----------

    private void Items_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragging = true;
        var dragged = e.Items.OfType<FavoriteItemViewModel>().Where(i => i.Exists).ToList();
        var paths = dragged.Select(i => i.FullPath).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        // Items owned by the box can move back out (drag onto the desktop = put back); plain shortcuts
        // only offer copy / link so the user's original file never gets moved away by accident.
        var owned = _group is not null && (_group.IsFolderBacked || dragged.All(IsStored));
        e.Data.RequestedOperation = owned
            ? DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link
            : DataPackageOperation.Copy | DataPackageOperation.Link;
        e.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} 個項目";
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                request.SetData(await ToStorageItemsAsync(paths));
            }
            catch (Exception ex)
            {
                StartupLog.Write("Drag-out data failed", ex);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private static async Task<List<IStorageItem>> ToStorageItemsAsync(IEnumerable<string> paths)
    {
        var list = new List<IStorageItem>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                list.Add(await StorageFolder.GetFolderFromPathAsync(p));
            }
            else if (File.Exists(p))
            {
                list.Add(await StorageFile.GetFileFromPathAsync(p));
            }
        }

        return list;
    }

    private void Items_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _dragging = false;
        if (_group is null || _group.IsFolderBacked)
        {
            if (args.DropResult == DataPackageOperation.Move)
            {
                Reload();
            }

            return;
        }

        if (args.DropResult == DataPackageOperation.Move &&
            args.Items.OfType<FavoriteItemViewModel>().Any(vm => !File.Exists(vm.FullPath) && !Directory.Exists(vm.FullPath)))
        {
            Reload();
            return;
        }

        if (_group.SortBy == "Custom" && _category.Length == 0)
        {
            _group.Items = _items.Select(vm => vm.Model).ToList();
            RaiseChanged(reloadSelf: false);
        }
    }

    // ---------- Item interaction ----------

    private ListViewBase ActiveList => _group?.ViewMode == "List" ? ItemsList : ItemsGrid;

    private List<FavoriteItemViewModel> Selection(FavoriteItemViewModel? clicked)
    {
        var selected = ActiveList.SelectedItems.OfType<FavoriteItemViewModel>().ToList();
        if (clicked is not null && !selected.Contains(clicked))
        {
            ActiveList.SelectedItems.Clear();
            ActiveList.SelectedItem = clicked;
            return new List<FavoriteItemViewModel> { clicked };
        }

        return selected;
    }

    private static FavoriteItemViewModel? ItemFrom(object? source) =>
        (source as FrameworkElement)?.DataContext as FavoriteItemViewModel;

    private void Items_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ItemFrom(e.OriginalSource) is { } vm)
        {
            FileOps.Open(vm.FullPath);
            e.Handled = true;
        }
    }

    private void Items_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ItemFrom(e.OriginalSource) is not { } vm || _group is null)
        {
            return;
        }

        var selection = Selection(vm);
        var menu = BuildItemMenu(selection);
        menu.ShowAt((FrameworkElement)e.OriginalSource, new FlyoutShowOptions { Position = e.GetPosition((UIElement)e.OriginalSource) });
        e.Handled = true;
    }

    private MenuFlyout BuildItemMenu(List<FavoriteItemViewModel> selection)
    {
        var group = _group!;
        var first = selection[0];
        var paths = selection.Select(s => s.FullPath).ToList();
        var menu = new MenuFlyout();
        menu.Items.Add(Item("開啟", "\uE8E5", () => selection.ForEach(s => FileOps.Open(s.FullPath))));
        if (selection.Count == 1)
        {
            menu.Items.Add(Item("快速預覽", "\uE890", () => ShowPreview(first)));
        }

        menu.Items.Add(Item("開啟所在位置", "\uE838", () => FileOps.Reveal(first.FullPath)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("複製", "\uE8C8", () => _ = FileOps.CopyFilesAsync(paths)));
        if (group.IsFolderBacked)
        {
            menu.Items.Add(Item("剪下", "\uE8C6", () => _ = FileOps.CopyFilesAsync(paths, cut: true)));
        }

        menu.Items.Add(Item("複製路徑", "\uE71B", () => FileOps.CopyText(string.Join(Environment.NewLine, paths))));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("附加到新待辦", "\uE9D5", () =>
            AppHost.Current.Sidebar?.TodoView.AddTodo(selection.Count == 1 ? $"處理 {first.Name}" : $"處理 {selection.Count} 個檔案", paths)));
        menu.Items.Add(Item("存到隨記", "\uE70B", () =>
            AppHost.Current.Sidebar?.TodoView.AddNote(string.Join(Environment.NewLine, selection.Select(s => s.Name)), paths)));
        menu.Items.Add(new MenuFlyoutSeparator());

        if (selection.All(IsStored))
        {
            var target = selection.Count == 1 && selection[0].Model.OriginalPath is { } o && !FileOps.IsOnDesktop(o)
                ? "放回原位置"
                : "放回桌面";
            menu.Items.Add(Item(target, "\uE7A7", () => PutBack(selection)));
        }

        if (group.IsFolderBacked)
        {
            if (selection.Count == 1)
            {
                menu.Items.Add(Item("重新命名", "\uE8AC", () => _ = RenameRealAsync(first)));
            }

            menu.Items.Add(Item("刪除（移到資源回收筒）", "\uE74D", () => _ = DeleteRealAsync(selection)));
        }
        else
        {
            if (selection.Count == 1)
            {
                menu.Items.Add(Item("重新命名顯示名稱", "\uE8AC", () => _ = RenameDisplayAsync(first)));
            }

            if (!selection.All(IsStored))
            {
                menu.Items.Add(Item("從格子移除（不刪檔案）", "\uE711", () => RemoveFromGroup(selection)));
            }
        }

        return menu;
    }

    private void Background_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled || _group is null || ItemFrom(e.OriginalSource) is not null)
        {
            return;
        }

        BuildBackgroundMenu().ShowAt(RootGrid, new FlyoutShowOptions { Position = e.GetPosition(RootGrid) });
        e.Handled = true;
    }

    public MenuFlyout BuildBackgroundMenu()
    {
        var group = _group!;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("貼上", "\uE77F", () => _ = PasteAsync()));
        if (group.IsFolderBacked)
        {
            menu.Items.Add(Item("新增資料夾", "\uE8F4", () => _ = NewFolderAsync()));
            menu.Items.Add(Item("在檔案總管開啟", "\uE838", () => FileOps.Open(group.MappedFolderPath ?? string.Empty)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var view = new MenuFlyoutSubItem { Text = "檢視", Icon = new FontIcon { Glyph = "\uE8FD" } };
        view.Items.Add(Radio("圖示", "view", group.ViewMode != "List", () => SetViewMode("Icon")));
        view.Items.Add(Radio("清單（含大小、日期）", "view", group.ViewMode == "List", () => SetViewMode("List")));
        menu.Items.Add(view);

        var sort = new MenuFlyoutSubItem { Text = "排序方式", Icon = new FontIcon { Glyph = "\uE8CB" } };
        if (!group.IsFolderBacked)
        {
            sort.Items.Add(Radio("自訂（可拖曳排序）", "sort", group.SortBy == "Custom", () => SetSort("Custom")));
        }

        sort.Items.Add(Radio("名稱", "sort", group.SortBy == "Name" || (group.IsFolderBacked && group.SortBy == "Custom"), () => SetSort("Name")));
        sort.Items.Add(Radio("類型", "sort", group.SortBy == "Type", () => SetSort("Type")));
        sort.Items.Add(Radio("大小", "sort", group.SortBy == "Size", () => SetSort("Size")));
        sort.Items.Add(Radio("修改日期", "sort", group.SortBy == "Date", () => SetSort("Date")));
        sort.Items.Add(new MenuFlyoutSeparator());
        var desc = new ToggleMenuFlyoutItem { Text = "遞減", IsChecked = group.SortDescending };
        desc.Click += (_, _) => SetSort(group.SortBy, desc.IsChecked);
        sort.Items.Add(desc);
        menu.Items.Add(sort);

        var stack = new ToggleMenuFlyoutItem
        {
            Text = "依類型自動叠放",
            IsChecked = group.StackByType,
            Icon = new FontIcon { Glyph = "\uF156" }
        };
        stack.Click += (_, _) => SetStackByType(stack.IsChecked);
        menu.Items.Add(stack);
        menu.Items.Add(Item("重新整理", "\uE72C", Reload));
        return menu;
    }

    private void Items_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_group is null)
        {
            return;
        }

        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var selection = ActiveList.SelectedItems.OfType<FavoriteItemViewModel>().ToList();
        switch (e.Key)
        {
            case VirtualKey.Enter when selection.Count > 0:
                selection.ForEach(s => FileOps.Open(s.FullPath));
                break;
            case VirtualKey.Space when selection.Count == 1:
                ShowPreview(selection[0]);
                break;
            case VirtualKey.F2 when selection.Count == 1:
                _ = _group.IsFolderBacked ? RenameRealAsync(selection[0]) : RenameDisplayAsync(selection[0]);
                break;
            case VirtualKey.Delete when selection.Count > 0:
                if (_group.IsFolderBacked)
                {
                    _ = DeleteRealAsync(selection);
                }
                else
                {
                    RemoveFromGroup(selection);
                }

                break;
            case VirtualKey.C when ctrl && selection.Count > 0:
                _ = FileOps.CopyFilesAsync(selection.Select(s => s.FullPath));
                break;
            case VirtualKey.X when ctrl && selection.Count > 0 && _group.IsFolderBacked:
                _ = FileOps.CopyFilesAsync(selection.Select(s => s.FullPath), cut: true);
                break;
            case VirtualKey.V when ctrl:
                _ = PasteAsync();
                break;
            case VirtualKey.A when ctrl:
                ActiveList.SelectAll();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ---------- Operations ----------

    public async Task PasteAsync()
    {
        try
        {
            var (paths, move) = await FileOps.ReadClipboardFilesAsync();
            if (paths.Count > 0)
            {
                AddPaths(paths, move && _group!.IsFolderBacked);
            }
        }
        catch (Exception ex)
        {
            StartupLog.Write("Paste failed", ex);
        }
    }

    private async Task NewFolderAsync()
    {
        var name = await Dialogs.PromptAsync(XamlRoot, "新增資料夾", "新資料夾");
        if (string.IsNullOrWhiteSpace(name) || _group?.MappedFolderPath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(FileOps.UniquePath(Path.Combine(_group.MappedFolderPath, name)));
            Reload();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(XamlRoot, "無法建立資料夾", ex.Message);
        }
    }

    private async Task RenameRealAsync(FavoriteItemViewModel vm)
    {
        var name = await Dialogs.PromptAsync(XamlRoot, "重新命名（會改到實際檔案）", Path.GetFileName(vm.FullPath));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            FileOps.Rename(vm.FullPath, name);
            Reload();
            RaiseChanged(reloadSelf: false);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(XamlRoot, "重新命名失敗", ex.Message);
        }
    }

    private async Task RenameDisplayAsync(FavoriteItemViewModel vm)
    {
        var name = await Dialogs.PromptAsync(XamlRoot, "重新命名顯示名稱", vm.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            vm.Name = name;
            RaiseChanged(reloadSelf: false);
        }
    }

    private async Task DeleteRealAsync(List<FavoriteItemViewModel> selection)
    {
        var message = selection.Count == 1
            ? $"「{selection[0].Name}」會移到資源回收筒，可從回收筒還原。"
            : $"{selection.Count} 個項目會移到資源回收筒，可從回收筒還原。";
        if (!await Dialogs.ConfirmAsync(XamlRoot, "刪除檔案", message, "刪除"))
        {
            return;
        }

        FileOps.DeleteToRecycleBin(selection.Select(s => s.FullPath));
        Reload();
        RaiseChanged(reloadSelf: false);
    }

    public void RemoveFromGroup(IEnumerable<FavoriteItemViewModel> selection)
    {
        if (_group is null || _group.IsFolderBacked)
        {
            return;
        }

        var list = selection.ToList();
        PutBack(list.Where(IsStored));
        foreach (var vm in list.Where(vm => !IsStored(vm)))
        {
            _group.Items.Remove(vm.Model);
        }

        Reload();
        RaiseChanged(reloadSelf: false);
    }

    public bool RenameDisplay(string path, string name)
    {
        var vm = _all.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (vm is null || _group is null || _group.IsFolderBacked)
        {
            return false;
        }

        vm.Name = name;
        RaiseChanged(reloadSelf: false);
        return true;
    }

    private void ShowPreview(FavoriteItemViewModel vm)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Auto };
        var panel = new StackPanel { Spacing = 8, MaxWidth = 420 };
        panel.Children.Add(new TextBlock { Text = vm.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = vm.Details, FontSize = 12, Opacity = 0.7 });
        var ext = Path.GetExtension(vm.FullPath).ToLowerInvariant();
        try
        {
            if (vm.Exists && ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico")
            {
                panel.Children.Add(new Image
                {
                    Source = new BitmapImage(new Uri(vm.FullPath)),
                    MaxHeight = 320,
                    MaxWidth = 400,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
                });
            }
            else if (vm.Exists && !vm.Model.IsDirectory && vm.Size < 256 * 1024 &&
                     ext is ".txt" or ".md" or ".log" or ".json" or ".xml" or ".csv" or ".cs" or ".js" or ".ts" or ".py" or ".ini" or ".yaml" or ".yml")
            {
                var text = File.ReadAllText(vm.FullPath);
                panel.Children.Add(new ScrollViewer
                {
                    MaxHeight = 300,
                    Content = new TextBlock
                    {
                        Text = text.Length > 4000 ? text[..4000] + "…" : text,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    }
                });
            }
            else if (vm.Exists && vm.Model.IsDirectory)
            {
                var entries = Directory.EnumerateFileSystemEntries(vm.FullPath).Take(12).Select(Path.GetFileName);
                panel.Children.Add(new TextBlock { Text = string.Join(Environment.NewLine, entries), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            }
            else
            {
                panel.Children.Add(new Image { Source = vm.Icon, Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Left });
            }
        }
        catch (Exception ex)
        {
            panel.Children.Add(new TextBlock { Text = $"無法預覽：{ex.Message}", FontSize = 12 });
        }

        var open = new Button { Content = "開啟", HorizontalAlignment = HorizontalAlignment.Right };
        open.Click += (_, _) =>
        {
            flyout.Hide();
            FileOps.Open(vm.FullPath);
        };
        panel.Children.Add(open);
        flyout.Content = panel;
        flyout.ShowAt(this);
    }

    private void RaiseChanged(bool reloadSelf)
    {
        if (reloadSelf)
        {
            Reload();
        }

        GroupChanged?.Invoke(this, EventArgs.Empty);
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private static RadioMenuFlyoutItem Radio(string text, string group, bool isChecked, Action action)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = group, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }
}
