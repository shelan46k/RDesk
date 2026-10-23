using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using RDesk.Helpers;
using RDesk.Models;
using RDesk.Views.Controls;
using WinRT.Interop;

namespace RDesk.Services;

/// <summary>
/// End-to-end smoke test of every module, run with <c>RDesk.exe --selftest</c>.
/// Writes %AppData%\RDesk\selftest.log, restores the user's config / startup entry, then exits
/// with the number of failures as the process exit code.
/// </summary>
public static class SelfTest
{
    public static bool IsRequested =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));

    public static string LogPath => Path.Combine(Path.GetDirectoryName(ConfigService.ConfigFilePath)!, "selftest.log");

    private static readonly List<(string Name, bool Ok, string Detail)> Results = new();

    public static async Task RunAsync(AppHost host)
    {
        var configBackup = File.Exists(ConfigService.ConfigFilePath)
            ? await File.ReadAllBytesAsync(ConfigService.ConfigFilePath)
            : null;
        var startupBackup = host.Startup.IsEnabled();
        var tempDir = Path.Combine(Path.GetTempPath(), "RDeskSelfTest");
        Directory.CreateDirectory(tempDir);

        try
        {
            await WaitUntil(() => host.Sidebar?.ModulesReady == true, 10000);
            await Task.Delay(1500);
            await RunAllAsync(host, tempDir);
        }
        catch (Exception ex)
        {
            Record("SelfTest harness", false, ex.ToString());
        }
        finally
        {
            try
            {
                host.Startup.SetEnabled(startupBackup);
            }
            catch
            {
            }

            await Task.Delay(1200); // let debounced config saves flush before restoring
            if (configBackup is not null)
            {
                await File.WriteAllBytesAsync(ConfigService.ConfigFilePath, configBackup);
            }

            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
            }

            WriteLog();
            var failures = Results.Count(r => !r.Ok);
            host.Tray?.Dispose();
            // Environment.Exit can deadlock on the WinUI dispatcher thread while windows are still open.
            TerminateProcess(GetCurrentProcess(), (uint)failures);
        }
    }

    private static async Task RunAllAsync(AppHost host, string tempDir)
    {
        var sidebar = host.Sidebar!;
        var sidebarHwnd = WindowNative.GetWindowHandle(sidebar);

        await Check("Updates: version string and GitHub URL stays local", () =>
        {
            var version = UpdateService.AppVersion;
            var url = host.Config.Current.UpdateGithubUrl;
            return Task.FromResult(Expect(
                version.Count(c => c == '.') == 2 && !string.IsNullOrWhiteSpace(version),
                $"version={version} url={url}"));
        });

        await Check("Config: save and reload", async () =>
        {
            var marker = $"selftest-{Guid.NewGuid():N}";
            host.Config.Update(c => c.QuickNoteContent = marker);
            host.Config.SaveImmediate();
            var text = await File.ReadAllTextAsync(ConfigService.ConfigFilePath);
            return Expect(text.Contains(marker), "marker not written to config.json");
        });

        await Check("Theme: dark / light / system switch brushes", async () =>
        {
            var original = host.Config.Current.ThemeMode;
            var panelT = host.Config.Current.PanelTransparency;
            var cardT = host.Config.Current.CardTransparency;
            host.Config.Update(c =>
            {
                c.PanelTransparency = 0;
                c.CardTransparency = 0;
            });
            host.Theme.SetMode("Dark");
            await Task.Delay(300);
            var dark = ThemeService.Brush(ThemeService.PrimaryTextBrushKey).Color;
            host.Theme.SetMode("Light");
            await Task.Delay(300);
            var light = ThemeService.Brush(ThemeService.PrimaryTextBrushKey).Color;
            host.Theme.SetMode("System");
            await Task.Delay(300);
            host.Theme.SetMode(original);
            host.Config.Update(c =>
            {
                c.PanelTransparency = panelT;
                c.CardTransparency = cardT;
            });
            host.Theme.NotifyAppearanceChanged();
            await Task.Delay(300);
            return Expect(dark.R > 0xE0 && light.R < 0x40, $"dark={dark} light={light}");
        });

        await Check("Monitor: sidebar docked inside target work area", () =>
        {
            var monitors = host.Monitors.GetMonitors();
            var target = host.Monitors.ResolveTargetMonitor();
            var pos = sidebar.AppWindow.Position;
            var size = sidebar.AppWindow.Size;
            var inside = pos.X >= target.WorkAreaX && pos.Y >= target.WorkAreaY &&
                         pos.X + size.Width <= target.WorkAreaX + target.WorkAreaWidth &&
                         pos.Y + size.Height <= target.WorkAreaY + target.WorkAreaHeight;
            return Task.FromResult(Expect(monitors.Count > 0 && inside,
                $"monitors={monitors.Count} pos={pos.X},{pos.Y} size={size.Width}x{size.Height} " +
                $"work=({target.WorkAreaX},{target.WorkAreaY},{target.WorkAreaWidth}x{target.WorkAreaHeight})"));
        });

        await Check("Hotkey: parser and registration", () => Task.FromResult(Expect(
            HotkeyService.IsValidHotkey("F7") && HotkeyService.IsValidHotkey("Ctrl+Alt+R") &&
            HotkeyService.IsValidHotkey("Ctrl+Alt+B") && HotkeyService.IsValidHotkey("F8") &&
            !HotkeyService.IsValidHotkey("Foo+Bar") &&
            (!host.Config.Current.EnableRevealHotkey || host.Hotkeys.IsRegistered) &&
            (!host.Config.Current.EnableBoxesHotkey || host.Hotkeys.IsBoxesRegistered),
            $"registered={host.Hotkeys.IsRegistered} boxes={host.Hotkeys.IsBoxesRegistered}")));

        await Check("Hide / show (tray + hotkey toggle)", async () =>
        {
            host.HideSidebar();
            await Task.Delay(400);
            var hidden = !IsWindowVisible(sidebarHwnd);
            host.ToggleFromTrayOrHotkey();
            await Task.Delay(400);
            var shown = IsWindowVisible(sidebarHwnd);
            return Expect(hidden && shown && host.IsSidebarVisible, $"hidden={hidden} shown={shown}");
        });

        await Check("Second launch signal reveals sidebar", async () =>
        {
            host.HideSidebar();
            await Task.Delay(300);
            using (var signal = EventWaitHandle.OpenExisting("RDesk_Activate_Event"))
            {
                signal.Set();
            }

            await Task.Delay(800);
            return Expect(IsWindowVisible(sidebarHwnd) && host.IsSidebarVisible, "sidebar still hidden");
        });

        await Check("Desktop pin: owner is SHELLDLL_DefView (Win+D resistant)", () =>
        {
            if (!host.Config.Current.PinToDesktop)
            {
                return Task.FromResult<string?>(null);
            }

            var owner = GetWindowLongPtr(sidebarHwnd, -8);
            var cls = new StringBuilder(64);
            GetClassName(owner, cls, cls.Capacity);
            return Task.FromResult(Expect(cls.ToString() == "SHELLDLL_DefView", $"owner class='{cls}'"));
        });

        await Check("Tray: every menu item has a Command", () =>
        {
            var items = host.Tray?.MenuItems.ToList() ?? new();
            return Task.FromResult(Expect(items.Count >= 5 && items.All(i => i.Command is not null),
                $"items={items.Count} withCommand={items.Count(i => i.Command is not null)}"));
        });

        var weather = new WeatherService();
        await Check("Weather: Open-Meteo forecast", async () =>
        {
            var cfg = host.Config.Current;
            var snap = await weather.FetchAsync(cfg.Latitude, cfg.Longitude, cfg.LocationName);
            return Expect(snap is not null && snap.Days.Count == 3 && snap.Temperature is > -60 and < 70,
                snap is null ? "null" : $"{snap.Temperature}°C days={snap.Days.Count}");
        });

        await Check("Weather: city search", async () =>
        {
            var results = await weather.SearchCityAsync("台中");
            return Expect(results.Count > 0, "no results");
        });

        await Check("Weather card refresh", async () =>
        {
            await sidebar.WeatherView.RefreshWeatherAsync();
            return Expect(sidebar.WeatherView.LastError is null && sidebar.WeatherView.LastSnapshot is not null,
                sidebar.WeatherView.LastError ?? "no snapshot");
        });

        await Check("System monitor sample", async () =>
        {
            var hw = new HardwareMonitorService();
            hw.Sample();
            await Task.Delay(600);
            var s = hw.Sample();
            return Expect(s.CpuPercent is >= 0 and <= 100 && s.RamPercent is > 0 and <= 100 && s.Uptime > TimeSpan.Zero,
                $"cpu={s.CpuPercent:0.0} ram={s.RamPercent:0.0} disk={s.DiskPercent:0.0} nic={s.NicLabel}");
        });

        await Check("System card renders samples", () => Task.FromResult(
            Expect(sidebar.SystemView.LastSample is not null, "no sample yet")));

        await Check("Recent files: load", async () =>
        {
            var list = await new RecentFilesService(host.Config).LoadAsync();
            await sidebar.RecentView.ReloadAsync();
            return Expect(sidebar.RecentView.LastError is null,
                $"{sidebar.RecentView.LastError} (items={list.Count})");
        });

        await Check("Shell: file icon extraction", async () =>
        {
            var icon = await ShellHelper.GetFileIconAsync(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            return Expect(icon is not null, "null icon");
        });

        var testFile = Path.Combine(tempDir, "RDesk 測試文件.txt");
        await File.WriteAllTextAsync(testFile, "RDesk self test");

        await Check("Shell: .lnk target resolution", () =>
        {
            var lnk = Path.Combine(tempDir, "test.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = testFile;
            shortcut.Save();
            var resolved = ShellHelper.ResolveShortcutTarget(lnk);
            return Task.FromResult(Expect(string.Equals(resolved, testFile, StringComparison.OrdinalIgnoreCase),
                $"resolved='{resolved}'"));
        });

        await Check("Startup: run-at-login registry toggle", () =>
        {
            host.Startup.SetEnabled(true);
            var on = host.Startup.IsEnabled();
            host.Startup.SetEnabled(false);
            var off = !host.Startup.IsEnabled();
            return Task.FromResult(Expect(on && off, $"on={on} off={off}"));
        });

        await Check("Todo: add / complete (sorted last) / clear", async () =>
        {
            var todo = sidebar.TodoView;
            var filter = todo.Filter;
            todo.SetFilter("All");
            var before = todo.Items.Count;
            var a = todo.AddTodo("自我測試 A");
            todo.AddTodo("自我測試 B");
            todo.SetCompleted(a, true);
            await Task.Delay(100);
            var rows = todo.RenderedRowCount;
            var completedOk = a.IsCompleted && a.CompletedAt is not null;
            var b = todo.Items.First(t => t.Content == "自我測試 B");
            todo.RemoveTodo(b);
            todo.ClearCompleted();
            todo.SetFilter(filter);
            var persisted = host.Config.Current.TodoItems.Count;
            return Expect(rows == before + 2 && completedOk && todo.Items.Count == before &&
                          !todo.Items.Any(t => t.Content.StartsWith("自我測試")) && persisted == todo.Items.Count,
                $"before={before} rows={rows} after={todo.Items.Count} persisted={persisted}");
        });

        await Check("Favorites: add / rename / persist", async () =>
        {
            var fav = sidebar.FavoritesView;
            var before = fav.ItemCount;
            fav.AddFavoritePath(testFile);
            await Task.Delay(200);
            var renamed = fav.RenameItem(testFile, "已改名");
            var model = fav.CurrentGroup?.Items.FirstOrDefault(i => i.FullPath == testFile);
            var saved = (await File.ReadAllTextAsync(ConfigService.ConfigFilePath)).Contains("已改名");
            return Expect(fav.ItemCount == before + 1 && renamed && model?.Name == "已改名" && saved,
                $"before={before} after={fav.ItemCount} renamed={renamed} saved={saved}");
        });

        await Check("Detached box: open / sync / collapse / recall", async () =>
        {
            var group = sidebar.FavoritesView.CurrentGroup!;
            host.OpenDetachedBox(group);
            await WaitUntil(() => host.DetachedBoxes.Any(b => b.GroupId == group.Id && b.ItemCount > 0), 4000);
            var box = host.DetachedBoxes.FirstOrDefault(b => b.GroupId == group.Id);
            if (box is null)
            {
                return "box did not open";
            }

            var boxHwnd = WindowNative.GetWindowHandle(box);
            await WaitUntil(() => IsWindowVisible(boxHwnd), 2000);
            var visible = IsWindowVisible(boxHwnd);
            var appVisible = box.AppWindow.IsVisible;
            var itemsMatch = box.ItemCount == group.Items.Count;
            box.BeginMoveForTest();
            var dragStarted = box.IsDraggingWindow;
            box.EndMoveForTest();
            var dragStopped = !box.IsDraggingWindow;
            box.ToggleCollapse();
            await Task.Delay(300);
            var collapsedHeight = box.AppWindow.Size.Height;
            var collapsed = box.IsCollapsed && collapsedHeight < 80 && !box.IsMenuVisible;
            box.ToggleCollapse();
            await Task.Delay(300);
            var expanded = !box.IsCollapsed && box.AppWindow.Size.Height >= 120;
            host.ShowAllDetachedBoxes();
            await Task.Delay(200);
            var raised = IsWindowVisible(boxHwnd);
            host.CloseDetachedBox(group.Id);
            await Task.Delay(500);
            var recalled = host.DetachedBoxes.All(b => b.GroupId != group.Id) && !group.IsDetachedToDesktop;
            return Expect(visible && itemsMatch && dragStarted && dragStopped && collapsed && expanded && raised && recalled,
                $"visible={visible} appWindowVisible={appVisible} items={itemsMatch} drag={dragStarted}/{dragStopped} collapsed={collapsed}({collapsedHeight}) expanded={expanded} raised={raised} recalled={recalled}");
        });

        await Check("Favorites: remove", async () =>
        {
            var fav = sidebar.FavoritesView;
            var before = fav.ItemCount;
            fav.RemoveFavoritePath(testFile);
            await Task.Delay(100);
            return Expect(fav.ItemCount == before - 1, $"before={before} after={fav.ItemCount}");
        });

        await RunDeskBoxFeatureTestsAsync(host, tempDir, testFile);

        await Check("Settings window: opens without changing config", async () =>
        {
            var width = host.Config.Current.SidebarWidth;
            var panelT = host.Config.Current.PanelTransparency;
            var cardT = host.Config.Current.CardTransparency;
            host.OpenSettings();
            await WaitUntil(() => host.Settings is not null, 3000);
            await Task.Delay(1200);
            var opened = host.Settings is not null;
            var settingsWidth = host.Settings?.AppWindow.Size.Width ?? 0;
            var pages = host.Settings?.SettingsPageCount ?? 0;
            host.Settings?.Close();
            await Task.Delay(300);
            return Expect(opened && settingsWidth >= 840 && pages >= 6 && host.Config.Current.SidebarWidth == width &&
                          Math.Abs(host.Config.Current.PanelTransparency - panelT) < 0.001 &&
                          Math.Abs(host.Config.Current.CardTransparency - cardT) < 0.001,
                $"opened={opened} settingsWidth={settingsWidth} pages={pages} width {width}->{host.Config.Current.SidebarWidth} panelT {panelT}->{host.Config.Current.PanelTransparency} cardT {cardT}->{host.Config.Current.CardTransparency}");
        });
    }

    private static async Task RunDeskBoxFeatureTestsAsync(AppHost host, string tempDir, string testFile)
    {
        var sidebar = host.Sidebar!;
        var sidebarHwnd = WindowNative.GetWindowHandle(sidebar);
        var storageRoot = Path.Combine(tempDir, "Storage");
        host.Config.Current.StorageRoot = storageRoot;

        await Check("Box snap: edges and side-by-side alignment", () =>
        {
            var a = new Windows.Graphics.RectInt32(100, 100, 200, 180);
            var beside = BoxSnap.Snap(new Windows.Graphics.RectInt32(100 + 200 + BoxSnap.Gap + 10, 108, 200, 180), new[] { a });
            var below = BoxSnap.Snap(new Windows.Graphics.RectInt32(104, 100 + 180 + BoxSnap.Gap + 9, 200, 180), new[] { a });
            var far = BoxSnap.Snap(new Windows.Graphics.RectInt32(500, 500, 200, 180), new[] { a });
            return Task.FromResult(Expect(
                beside.X == 100 + 200 + BoxSnap.Gap && beside.Y == 100 &&
                below.X == 100 && below.Y == 100 + 180 + BoxSnap.Gap &&
                far.X == 500 && far.Y == 500,
                $"beside={beside.X},{beside.Y} below={below.X},{below.Y} far={far.X},{far.Y}"));
        });

        await Check("Appearance: DWM rounded corners, accent, backdrop", async () =>
        {
            var corner = 0;
            DwmGetWindowAttribute(sidebarHwnd, 33, out corner, sizeof(int));
            var cfg = host.Config.Current;
            var (accent, backdrop, panelT, cardT) = (cfg.AccentColor, cfg.Backdrop, cfg.PanelTransparency, cfg.CardTransparency);
            host.Config.Update(c =>
            {
                c.AccentColor = "Purple";
                c.PanelTransparency = 0;
                c.CardTransparency = 0;
            });
            host.Theme.NotifyAppearanceChanged();
            await Task.Delay(300);
            var expected = ThemeService.AccentPalette("Purple");
            var accentColor = ThemeService.Brush(ThemeService.AccentTextBrushKey).Color;
            var accentOk = accentColor == (host.Theme.IsDark ? expected.DarkText : expected.LightText);
            host.Config.Update(c =>
            {
                c.Backdrop = "Solid";
                c.PanelTransparency = 0;
            });
            host.Theme.NotifyAppearanceChanged();
            await Task.Delay(300);
            var solidOk = sidebar.SystemBackdrop is null &&
                          ThemeService.Brush(ThemeService.PanelBackgroundBrushKey).Color.A == 0xFF;
            host.Config.Update(c =>
            {
                c.Backdrop = "Acrylic";
                c.PanelTransparency = 1;
                c.CardTransparency = 1;
            });
            host.Theme.NotifyAppearanceChanged();
            await Task.Delay(300);
            var primary = ThemeService.Brush(ThemeService.PrimaryTextBrushKey).Color;
            var dialogPrimary = ThemeService.Brush(ThemeService.DialogPrimaryTextBrushKey).Color;
            var pill = ThemeService.Brush(ThemeService.PillBackgroundBrushKey).Color;
            var contrastOk = !WallpaperLuminance.IsDark ||
                             (primary.R > 0xC0 && pill.A >= 0x80 &&
                              (host.Theme.IsDark || dialogPrimary.R < 0x40));
            var clearOk = ThemeService.Brush(ThemeService.PanelBackgroundBrushKey).Color.A == 0 &&
                          ThemeService.Brush(ThemeService.CardBackgroundBrushKey).Color.A == 0 &&
                          sidebar.SystemBackdrop is ConfigurableSystemBackdrop &&
                          contrastOk;
            host.Config.Update(c =>
            {
                c.AccentColor = accent;
                c.Backdrop = backdrop;
                c.PanelTransparency = panelT;
                c.CardTransparency = cardT;
            });
            host.Theme.NotifyAppearanceChanged();
            await Task.Delay(300);
            var restored = backdrop == "Solid"
                ? sidebar.SystemBackdrop is null
                : sidebar.SystemBackdrop is ConfigurableSystemBackdrop;
            return Expect(corner == 2 && accentOk && solidOk && clearOk && restored,
                $"corner={corner} accent={accentColor} accentOk={accentOk} solidOk={solidOk} clearOk={clearOk} restored={restored} primary={primary} dialog={dialogPrimary} pillA={pill.A} wallDark={WallpaperLuminance.IsDark}");
        });

        await Check("Segmented control: todo / notes / clipboard tabs", async () =>
        {
            var todo = sidebar.TodoView;
            todo.ShowMode(TodoNotesControl.NoteMode);
            await Task.Delay(100);
            var notes = todo.Mode == TodoNotesControl.NoteMode;
            todo.ShowMode(TodoNotesControl.ClipMode);
            await Task.Delay(100);
            var clips = todo.Mode == TodoNotesControl.ClipMode;
            todo.ShowMode(TodoNotesControl.TodoMode);
            return Expect(notes && clips && todo.Mode == TodoNotesControl.TodoMode, $"notes={notes} clips={clips}");
        });

        await Check("File ops: copy / move / rename / recycle bin", () =>
        {
            var src = Path.Combine(tempDir, "ops");
            var dst = Path.Combine(tempDir, "ops-dst");
            Directory.CreateDirectory(src);
            var a = Path.Combine(src, "a.txt");
            File.WriteAllText(a, "a");
            var copied = FileOps.Transfer(new[] { a }, dst, move: false);
            var copiedAgain = FileOps.Transfer(new[] { a }, dst, move: false);
            var renamed = FileOps.Rename(copied[0], "改名.txt");
            var moved = FileOps.Transfer(new[] { a }, dst, move: true);
            var recycled = FileOps.DeleteToRecycleBin(new[] { copiedAgain[0] });
            return Task.FromResult(Expect(
                File.Exists(renamed) && Path.GetFileName(copiedAgain[0]) == "a (2).txt" && !File.Exists(a) &&
                moved.Count == 1 && File.Exists(moved[0]) && recycled && !File.Exists(copiedAgain[0]),
                $"renamed={renamed} again={copiedAgain.FirstOrDefault()} moved={moved.Count} recycled={recycled}"));
        });

        FavoriteGroupModel? storage = null;
        await Check("Storage box: drop copies real files, live folder sync, view / sort / stacks", async () =>
        {
            storage = FavoritesControl.CreateStorageBox(host.Config, "自測收納");
            var fav = sidebar.FavoritesView;
            fav.SelectGroup(storage.Id);
            await Task.Delay(200);
            var box = fav.BoxView;
            var added = box.AddPaths(new[] { testFile }, move: false);
            var copiedInto = File.Exists(Path.Combine(storage.MappedFolderPath!, Path.GetFileName(testFile)));
            var sourceKept = File.Exists(testFile);
            File.WriteAllText(Path.Combine(storage.MappedFolderPath!, "照片 RDeskSelfTestUnique.png"), "x");
            await WaitUntil(() => box.ItemCount == 2, 3000);
            var liveSync = box.ItemCount == 2;
            box.SetViewMode("List");
            box.SetSort("Size", descending: true);
            box.SetStackByType(true);
            await Task.Delay(150);
            var hasStacks = box.Categories.Contains("文件") && box.Categories.Contains("圖片");
            box.SelectCategory("圖片");
            var filtered = box.VisibleItems.Count == 1 && box.VisibleItems[0].Category == "圖片";
            box.SelectCategory(string.Empty);
            box.SetViewMode("Icon");
            return Expect(fav.CurrentGroup == storage && added == 1 && copiedInto && sourceKept && liveSync && hasStacks && filtered,
                $"selected={fav.CurrentGroup == storage} added={added} copied={copiedInto} kept={sourceKept} live={liveSync}({box.ItemCount}) stacks={string.Join('/', box.Categories)} filtered={filtered}");
        });

        await Check("Desktop box: capsule expands while files hover, folds back", async () =>
        {
            if (storage is null)
            {
                return "no storage box";
            }

            host.OpenDetachedBox(storage);
            await WaitUntil(() => host.DetachedBoxes.Any(b => b.GroupId == storage.Id && b.ItemCount > 0), 4000);
            var box = host.DetachedBoxes.FirstOrDefault(b => b.GroupId == storage.Id);
            if (box is null)
            {
                return "box did not open";
            }

            await WaitUntil(() => IsWindowVisible(WindowNative.GetWindowHandle(box)), 2000);
            var boxCorner = 0;
            DwmGetWindowAttribute(WindowNative.GetWindowHandle(box), 33, out boxCorner, sizeof(int));
            box.ToggleCollapse();
            await Task.Delay(300);
            var collapsedH = box.AppWindow.Size.Height;
            box.SimulateDragHover(true);
            await Task.Delay(300);
            var peekH = box.AppWindow.Size.Height;
            var peeking = box.IsPeeking;
            box.SimulateDragHover(false);
            await Task.Delay(1400);
            var foldedH = box.AppWindow.Size.Height;
            box.ToggleCollapse();
            host.CloseDetachedBox(storage.Id);
            await Task.Delay(400);
            return Expect(collapsedH < 80 && peeking && peekH >= 120 && foldedH < 80 && boxCorner == 2,
                $"collapsed={collapsedH} peek={peekH}({peeking}) folded={foldedH} corner={boxCorner}");
        });

        await Check("Desktop boxes: snap next to a neighbor", async () =>
        {
            var left = FavoritesControl.CreateStorageBox(host.Config, "對齊左");
            var right = FavoritesControl.CreateStorageBox(host.Config, "對齊右");
            try
            {
                host.OpenDetachedBox(left);
                host.OpenDetachedBox(right);
                await WaitUntil(() =>
                    host.DetachedBoxes.Any(b => b.GroupId == left.Id) &&
                    host.DetachedBoxes.Any(b => b.GroupId == right.Id), 4000);
                var a = host.DetachedBoxes.First(b => b.GroupId == left.Id);
                var b = host.DetachedBoxes.First(b => b.GroupId == right.Id);
                a.SetBoundsForTest(220, 160, 260, 200);
                b.SetBoundsForTest(600, 400, 260, 200);
                await Task.Delay(250);
                var expectedX = 220 + 260 + BoxSnap.Gap;
                b.SnapMoveToForTest(expectedX + 11, 168);
                await Task.Delay(200);
                var got = b.CurrentBounds;
                return Expect(Math.Abs(got.X - expectedX) <= 2 && Math.Abs(got.Y - 160) <= 2,
                    $"got={got.X},{got.Y} expected={expectedX},160 neighbor={a.CurrentBounds.X},{a.CurrentBounds.Y} {a.CurrentBounds.Width}x{a.CurrentBounds.Height}");
            }
            finally
            {
                host.CloseDetachedBox(left.Id);
                host.CloseDetachedBox(right.Id);
                host.Config.Current.FavoriteGroups.RemoveAll(g => g.Id == left.Id || g.Id == right.Id);
                sidebar.FavoritesView.ReloadGroups(0);
                await Task.Delay(200);
            }
        });

        await Check("Desktop tidy: drag desktop shortcut / folder into a box, icons, put back, drag-out prune", async () =>
        {
            var desktop = FileOps.UserDesktop;
            var lnk = Path.Combine(desktop, "RDesk 自測捷徑.lnk");
            var folder = Path.Combine(desktop, "RDesk 自測資料夾");
            var group = new FavoriteGroupModel { Title = "自測整理格子" };
            var fav = sidebar.FavoritesView;
            host.Config.Current.FavoriteGroups.Add(group);
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(lnk);
                shortcut.TargetPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
                shortcut.Save();
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "內容.txt"), "x");

                fav.ReloadGroups(host.Config.Current.FavoriteGroups.Count - 1);
                fav.SelectGroup(group.Id);
                await Task.Delay(200);
                var box = fav.BoxView;
                var paths = new List<string> { lnk, folder };
                var decideOk = FileOps.IsOnDesktop(lnk) && box.ShouldMove(paths, shift: false) &&
                               !box.ShouldMove(paths, shift: true) && !box.ShouldMove(new[] { testFile }, shift: false);

                var added = box.AddPaths(paths, move: true);
                var desktopClean = !File.Exists(lnk) && !Directory.Exists(folder);
                var storedOk = group.Items.Count == 2 &&
                               group.Items.All(i => FileOps.IsUnder(i.FullPath, group.StoreFolder) && i.OriginalPath is not null &&
                                                    (File.Exists(i.FullPath) || Directory.Exists(i.FullPath)));
                await WaitUntil(() => box.VisibleItems.Count == 2 && box.VisibleItems.All(v => v.Icon is not null), 5000);
                var iconsOk = box.VisibleItems.Count == 2 && box.VisibleItems.All(v => v.Icon is not null);
                var nameOk = box.VisibleItems.Any(v => v.DisplayName == "RDesk 自測捷徑");

                var putBack = box.PutBack(box.VisibleItems.ToList());
                var restoredOk = putBack == 2 && File.Exists(lnk) && Directory.Exists(Path.Combine(folder)) &&
                                 File.Exists(Path.Combine(folder, "內容.txt")) && group.Items.Count == 0;

                box.AddPaths(new[] { lnk }, move: true);
                var storedLnk = group.Items.Single().FullPath;
                File.Move(storedLnk, lnk);
                await WaitUntil(() => group.Items.Count == 0, 4000);
                var pruned = group.Items.Count == 0 && box.ItemCount == 0;
                var sameVolume = string.Equals(Path.GetPathRoot(group.StoreFolder), Path.GetPathRoot(desktop), StringComparison.OrdinalIgnoreCase) &&
                                 new DirectoryInfo(FileBoxView.StoreRoot).Attributes.HasFlag(FileAttributes.Hidden);
                pruned &= sameVolume;

                return Expect(decideOk && added == 2 && desktopClean && storedOk && iconsOk && nameOk && restoredOk && pruned,
                    $"decide={decideOk} added={added} clean={desktopClean} stored={storedOk} icons={iconsOk} name={nameOk} putBack={restoredOk}({putBack}) pruned={pruned}");
            }
            finally
            {
                foreach (var item in group.Items.ToList())
                {
                    FileOps.PutBack(item.FullPath, item.OriginalPath);
                }

                try
                {
                    File.Delete(lnk);
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }

                    if (group.StoreFolder is not null && Directory.Exists(group.StoreFolder))
                    {
                        Directory.Delete(group.StoreFolder, true);
                    }

                    if (Directory.Exists(FileBoxView.StoreRoot) && !Directory.EnumerateFileSystemEntries(FileBoxView.StoreRoot).Any())
                    {
                        Directory.Delete(FileBoxView.StoreRoot);
                    }
                }
                catch
                {
                }

                host.Config.Current.FavoriteGroups.Remove(group);
                fav.ReloadGroups(0);
            }
        });

        await Check("Settings: every sidebar block can be switched off and on", async () =>
        {
            host.OpenSettings();
            await WaitUntil(() => host.Settings is not null, 3000);
            var settings = host.Settings;
            if (settings is null)
            {
                return "settings did not open";
            }

            var todo = sidebar.TodoView;
            var probes = new Dictionary<string, Func<bool>>
            {
                ["weather"] = () => sidebar.WeatherView.Visibility == Visibility.Visible,
                ["recent"] = () => sidebar.RecentView.Visibility == Visibility.Visible,
                ["boxes"] = () => sidebar.FavoritesView.Visibility == Visibility.Visible,
                ["todo"] = () => todo.Visibility == Visibility.Visible,
                ["notes"] = () => todo.IsTabVisible(TodoNotesControl.NoteMode),
                ["clips"] = () => todo.IsTabVisible(TodoNotesControl.ClipMode),
                ["music"] = () => sidebar.MusicView.Visibility == Visibility.Visible,
                ["system"] = () => sidebar.SystemView.IsSystemPartVisible,
                ["network"] = () => sidebar.SystemView.IsNetworkPartVisible,
                ["tip"] = () => sidebar.IsTipVisible
            };
            var failures = new List<string>();
            foreach (var block in Views.SettingsWindow.Blocks)
            {
                var original = block.Get(host.Config.Current);
                settings.SetBlockVisible(block.Key, false);
                await Task.Delay(60);
                var hidden = !probes[block.Key]();
                settings.SetBlockVisible(block.Key, true);
                await Task.Delay(60);
                var shown = probes[block.Key]();
                settings.SetBlockVisible(block.Key, original);
                if (!hidden || !shown)
                {
                    failures.Add($"{block.Key}(hidden={hidden} shown={shown})");
                }
            }

            todo.ShowMode(TodoNotesControl.NoteMode);
            settings.SetBlockVisible("notes", false);
            var fellBack = todo.Mode == TodoNotesControl.TodoMode;
            settings.SetBlockVisible("notes", true);
            settings.Close();
            await Task.Delay(300);
            return Expect(failures.Count == 0 && fellBack && probes.Count == Views.SettingsWindow.Blocks.Length,
                $"failed=[{string.Join(", ", failures)}] fellBack={fellBack}");
        });

        var historyBackup = File.Exists(DesktopOrganizerService.HistoryPath)
            ? await File.ReadAllBytesAsync(DesktopOrganizerService.HistoryPath)
            : null;
        try
        {
            await Check("Desktop organize: scan plan / execute / undo", async () =>
            {
                var desk = Path.Combine(tempDir, "FakeDesktop");
                Directory.CreateDirectory(Path.Combine(desk, "資料夾不動"));
                var files = new[] { "相片.jpg", "報告.pdf", "封存.zip", "捷徑.lnk", "下載中.crdownload", "未知.qqq" };
                foreach (var f in files)
                {
                    var p = Path.Combine(desk, f);
                    File.WriteAllText(p, f);
                    File.SetLastWriteTime(p, DateTime.Now.AddMinutes(-5));
                }

                var plan = host.Organizer.Scan(desk);
                var movable = plan.Where(p => p.SkipReason.Length == 0).Select(p => p.Name).ToHashSet();
                var planOk = movable.SetEquals(new[] { "相片.jpg", "報告.pdf", "封存.zip", "未知.qqq" }) &&
                             plan.Single(p => p.Name == "捷徑.lnk").SkipReason.Length > 0 &&
                             plan.Single(p => p.Name == "下載中.crdownload").SkipReason.Length > 0 &&
                             plan.Single(p => p.Name == "相片.jpg").Category == "圖片";
                plan.Single(p => p.Name == "未知.qqq").Include = false;
                var batch = host.Organizer.Execute(plan);
                host.OnStorageChanged();
                var movedOk = batch.Moves.Count == 3 && File.Exists(Path.Combine(storageRoot, "圖片", "相片.jpg")) &&
                              !File.Exists(Path.Combine(desk, "相片.jpg")) && File.Exists(Path.Combine(desk, "未知.qqq"));
                var boxesOk = host.Config.Current.FavoriteGroups.Any(g => g.IsStorageBox &&
                    string.Equals(g.MappedFolderPath, Path.Combine(storageRoot, "文件"), StringComparison.OrdinalIgnoreCase));
                var undoTarget = host.Organizer.LastUndoableBatch();
                var restored = undoTarget is null ? 0 : host.Organizer.Undo(undoTarget);
                var undoOk = restored == 3 && File.Exists(Path.Combine(desk, "相片.jpg")) && host.Organizer.LastUndoableBatch()?.Id != batch.Id;

                var window = host.OpenOrganizer(desk);
                await WaitUntil(() => window.Plan.Count > 0, 3000);
                var windowOk = window.Plan.Count == plan.Count;
                window.Close();
                await Task.Delay(200);
                return Expect(planOk && movedOk && boxesOk && undoOk && windowOk,
                    $"plan={planOk}({string.Join(',', movable)}) moved={movedOk}({batch.Moves.Count}) boxes={boxesOk} undo={undoOk}({restored}) window={windowOk}");
            });

            await Check("Auto organize: only new, stable, rule-matched files", async () =>
            {
                var desk = Path.Combine(tempDir, "AutoDesktop");
                Directory.CreateDirectory(desk);
                var existing = Path.Combine(desk, "原本就在.png");
                File.WriteAllText(existing, "old");
                host.Organizer.StableDelay = TimeSpan.FromSeconds(1);
                host.Organizer.StartAuto(desk);
                await Task.Delay(300);
                var fresh = Path.Combine(desk, "新截圖.png");
                File.WriteAllText(fresh, "new");
                File.WriteAllText(Path.Combine(desk, "無規則.qqq"), "x");
                await Task.Delay(2500);
                File.SetLastWriteTime(fresh, DateTime.Now.AddMinutes(-1));
                var target = Path.Combine(storageRoot, "圖片", "新截圖.png");
                await WaitUntil(() => File.Exists(target), 12000);
                var moved = File.Exists(target) && !File.Exists(fresh);
                var untouched = File.Exists(existing) && File.Exists(Path.Combine(desk, "無規則.qqq"));
                host.Organizer.StopAuto();
                host.Organizer.StableDelay = TimeSpan.FromSeconds(4);
                host.Organizer.ApplyAutoSetting();
                return Expect(moved && untouched, $"moved={moved} untouched={untouched} pending={host.Organizer.PendingCount}");
            });
        }
        finally
        {
            if (historyBackup is not null)
            {
                await File.WriteAllBytesAsync(DesktopOrganizerService.HistoryPath, historyBackup);
            }
            else if (File.Exists(DesktopOrganizerService.HistoryPath))
            {
                File.Delete(DesktopOrganizerService.HistoryPath);
            }
        }

        await Check("Todo: due date, reminder + snooze, repeat, attachments, filters", async () =>
        {
            var todo = sidebar.TodoView;
            var now = DateTime.Now;
            var notificationsBefore = host.Tray?.NotificationCount ?? 0;
            var item = todo.AddTodo("自我測試 提醒");
            item.DueAt = now.AddMinutes(-1);
            item.RemindEnabled = true;
            item.Repeat = "Weekly";
            item.IsImportant = true;
            todo.Attach(item, new[] { testFile });
            var fired = host.Reminders.CheckNow(now);
            await Task.Delay(100);
            var firedOk = fired.Contains(item) && item.Reminded && todo.IsReminderBannerVisible &&
                          (host.Tray is null || host.Tray.NotificationCount == notificationsBefore + 1);
            todo.SnoozeTodo(item, TimeSpan.FromMinutes(10));
            var snoozeQuiet = host.Reminders.CheckNow(now.AddMinutes(1)).Count == 0;
            var snoozeFires = host.Reminders.CheckNow(DateTime.Now.AddMinutes(11)).Contains(item);
            todo.SetFilter("Today");
            var inToday = todo.FilteredTodos().Contains(item);
            todo.SetFilter("Important");
            var inImportant = todo.FilteredTodos().Contains(item);
            todo.SetFilter("Active");
            var due = item.DueAt!.Value;
            todo.SetCompleted(item, true);
            var next = todo.Items.FirstOrDefault(t => t.Content == "自我測試 提醒" && !t.IsCompleted);
            var repeatOk = next is not null && next.DueAt is { } nd && nd > due && next.Repeat == "Weekly" &&
                           next.Attachments.Count == 1 && item.Repeat == "None";
            foreach (var t in todo.Items.Where(t => t.Content == "自我測試 提醒").ToList())
            {
                todo.RemoveTodo(t);
            }

            return Expect(firedOk && snoozeQuiet && snoozeFires && inToday && inImportant && repeatOk,
                $"fired={firedOk} snoozeQuiet={snoozeQuiet} snoozeFires={snoozeFires} today={inToday} important={inImportant} repeat={repeatOk}");
        });

        await Check("Notes: add / attach / pin / filter / remove", async () =>
        {
            var todo = sidebar.TodoView;
            var note = todo.AddNote("自我測試隨記 RDeskNoteUnique", new[] { testFile });
            todo.TogglePin(note);
            await Task.Delay(100);
            todo.PinnedOnly = true;
            var pinnedVisible = todo.RenderedNoteCount >= 1 && note.IsPinned;
            todo.PinnedOnly = false;
            var shown = todo.Mode == TodoNotesControl.NoteMode;
            var saved = host.Config.Current.Notes.Contains(note) && note.Attachments.Count == 1;
            todo.RemoveNote(note);
            todo.ShowMode(TodoNotesControl.TodoMode);
            return Expect(pinnedVisible && shown && saved && !host.Config.Current.Notes.Contains(note),
                $"pinned={pinnedVisible} shown={shown} saved={saved}");
        });

        await Check("Clipboard history: capture / restore without duplicate", async () =>
        {
            host.Config.Current.ClipboardHistoryEnabled = true;
            var marker = $"RDesk 剪貼簿測試 {Guid.NewGuid():N}";
            FileOps.CopyText(marker);
            await WaitUntil(() => host.Clipboard.Items.Any(c => c.Text == marker), 3000);
            var viaEvent = host.Clipboard.Items.Any(c => c.Text == marker);
            if (!viaEvent)
            {
                await host.Clipboard.CaptureAsync();
            }

            var captured = host.Clipboard.Items.FirstOrDefault(c => c.Text == marker);
            if (captured is null)
            {
                return $"not captured (event={viaEvent})";
            }

            var count = host.Clipboard.Items.Count;
            await host.Clipboard.RestoreAsync(captured);
            await Task.Delay(900);
            var noDuplicate = host.Clipboard.Items.Count == count;
            sidebar.TodoView.ShowMode(TodoNotesControl.ClipMode);
            await Task.Delay(100);
            var rendered = sidebar.TodoView.RenderedClipCount > 0;
            sidebar.TodoView.ShowMode(TodoNotesControl.TodoMode);
            host.Clipboard.Remove(captured);
            return Expect(viaEvent && noDuplicate && rendered, $"event={viaEvent} noDuplicate={noDuplicate} rendered={rendered}");
        });

        await Check("Search: index, local results, todos, Windows index, popup", async () =>
        {
            var todoItem = sidebar.TodoView.AddTodo("自我測試 搜尋待辦 RDeskSearchTodo");
            await host.Search.EnsureIndexAsync(force: true);
            var indexed = host.Search.IndexedCount;
            var fileHit = host.Search.QueryLocal("RDeskSelfTestUnique").Any(r => r.Kind == "File");
            var todoHit = host.Search.QueryLocal("RDeskSearchTodo").Any(r => r.Kind == "Todo");
            var appHit = host.Search.QueryLocal("記事本").Concat(host.Search.QueryLocal("Notepad")).Any(r => r.Kind == "App") ||
                         host.Search.QueryLocal("Edge").Any(r => r.Kind == "App") ||
                         host.Search.QueryLocal("Windows").Any(r => r.Kind == "App");
            var windowsIndex = await host.Search.QueryWindowsIndexAsync("notepad", CancellationToken.None);
            host.OpenSearch("RDeskSelfTestUnique");
            await WaitUntil(() => host.SearchPopup?.Results.Count > 0, 4000);
            var popupOk = host.SearchPopup?.Results.Any(r => r.Path.Contains("RDeskSelfTestUnique")) == true;
            var popupHwnd = host.SearchPopup is null ? nint.Zero : WindowNative.GetWindowHandle(host.SearchPopup);
            var popupVisible = IsWindowVisible(popupHwnd);
            if (host.SearchPopup is not null)
            {
                WinUIEx.WindowExtensions.Hide(host.SearchPopup);
            }
            sidebar.TodoView.RemoveTodo(todoItem);
            return Expect(indexed > 0 && fileHit && todoHit && appHit && popupOk && popupVisible,
                $"indexed={indexed} file={fileHit} todo={todoHit} app={appHit} winIndex={windowsIndex.Count} popup={popupOk} visible={popupVisible}");
        });

        await Check("Music: Windows media session manager", () => Task.FromResult(Expect(
            sidebar.MusicView.IsReady && sidebar.MusicView.LastError is null,
            $"ready={sidebar.MusicView.IsReady} error={sidebar.MusicView.LastError} title={sidebar.MusicView.CurrentTitle}")));

        await Check("Backup: export / restore round-trip with pre-restore snapshot", async () =>
        {
            var zip = Path.Combine(tempDir, "backup.zip");
            var keep = sidebar.TodoView.AddNote("備份前的隨記 RDeskBackupKeep");
            host.Backup.Export(zip);
            var drop = sidebar.TodoView.AddNote("備份後的隨記 RDeskBackupDrop");
            host.Config.SaveImmediate();
            var snapshot = host.Backup.Import(zip);
            host.ReloadAfterRestore();
            await Task.Delay(300);
            var notes = host.Config.Current.Notes;
            var restored = notes.Any(n => n.Content.Contains("RDeskBackupKeep")) && !notes.Any(n => n.Content.Contains("RDeskBackupDrop"));
            var snapshotOk = File.Exists(snapshot);
            var groupsOk = host.Config.Current.FavoriteGroups.Count > 0 && sidebar.FavoritesView.CurrentGroup is not null;
            try
            {
                File.Delete(snapshot);
            }
            catch
            {
            }

            foreach (var n in host.Config.Current.Notes.Where(n => n.Content.Contains("RDeskBackup")).ToList())
            {
                host.Config.Current.Notes.Remove(n);
            }

            _ = keep;
            _ = drop;
            return Expect(restored && snapshotOk && groupsOk, $"restored={restored} snapshot={snapshotOk} groups={groupsOk}");
        });

        var current = host.Config.Current;
        current.StorageRoot = string.Empty;
        current.FavoriteGroups.RemoveAll(g => g.IsFolderBacked &&
            (g.MappedFolderPath ?? string.Empty).StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase));
        sidebar.FavoritesView.ReloadGroups(0);
    }

    private static async Task Check(string name, Func<Task<string?>> test)
    {
        try
        {
            var failure = await test();
            Record(name, failure is null, failure ?? string.Empty);
        }
        catch (Exception ex)
        {
            Record(name, false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string? Expect(bool condition, string detailWhenFalse) => condition ? null : detailWhenFalse;

    private static void Record(string name, bool ok, string detail)
    {
        Results.Add((name, ok, detail));
        StartupLog.Write($"[SelfTest] {(ok ? "PASS" : "FAIL")} {name}", detail: ok ? null : detail);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs)
        {
            await Task.Delay(100);
        }
    }

    private static void WriteLog()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"RDesk self test — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var (name, ok, detail) in Results)
        {
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok ? string.Empty : "  →  " + detail)}");
        }

        sb.AppendLine($"Total {Results.Count}, passed {Results.Count(r => r.Ok)}, failed {Results.Count(r => !r.Ok)}");
        File.WriteAllText(LogPath, sb.ToString(), Encoding.UTF8);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint hProcess, uint exitCode);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);
}
