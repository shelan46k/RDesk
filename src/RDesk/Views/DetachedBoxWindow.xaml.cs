using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using RDesk.Helpers;
using RDesk.Models;
using RDesk.Services;
using RDesk.Views.Controls;
using Windows.Graphics;
using WinRT.Interop;
using WinUIEx;

namespace RDesk.Views;

/// <summary>
/// A file box placed on the desktop. Owned by the desktop layer (Win+D resistant), can be dragged by
/// its title, resized from its edges, and collapsed into a capsule that expands while files are dragged onto it.
/// </summary>
public sealed partial class DetachedBoxWindow : WindowEx
{
    private const int CollapsedHeight = 44;

    private readonly AppHost _host;
    private readonly FavoriteGroupModel _group;
    private AppWindow? _appWindow;
    private int _expandedHeight = 260;
    private bool _collapsed;
    private bool _peeking;
    private bool _recalled;
    private bool _closed;
    private DispatcherTimer? _peekTimer;

    public string GroupId => _group.Id;
    public int ItemCount => Box.ItemCount;
    public bool IsCollapsed => _collapsed;
    public bool IsMenuVisible => MenuButton.Visibility == Visibility.Visible;
    public FileBoxView BoxView => Box;

    public DetachedBoxWindow(AppHost host, FavoriteGroupModel group)
    {
        _host = host;
        _group = group;
        InitializeComponent();
        // Must stay false: ExtendsContentIntoTitleBar turns the whole window into a system caption.
        // With the HWND owned by Explorer, mouse-up never comes back (click-to-hold / click-to-release)
        // and the title-bar buttons hang inside the system move loop.
        ExtendsContentIntoTitleBar = false;
        Box.GroupChanged += (_, _) =>
        {
            _host.Config.SaveImmediate();
            _host.NotifyFavoritesChanged(_group.Id, this);
            UpdateTitle();
        };
        Box.DragHoverChanged += (_, hovering) => OnDragHover(hovering);
        RootGrid.AllowDrop = true;
        RootGrid.DragEnter += (_, _) => OnDragHover(true);
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            _closed = true;
            _peekTimer?.Stop();
            _gestureTimer?.Stop();
            EndGesture();
            if (_recalled)
            {
                _group.IsDetachedToDesktop = false;
                _host.Config.SaveImmediate();
            }

            _host.NotifyDetachedClosed(this);
        };
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_appWindow is not null || _closed)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        _appWindow.IsShownInSwitchers = false;
        ApplyTheme();

        var w = Math.Max(180, _group.DetachedWidth);
        var h = Math.Max(120, _group.DetachedHeight);
        _expandedHeight = h;
        var rect = ClampToVisibleArea(new RectInt32(_group.DetachedX, _group.DetachedY, w, h));
        _appWindow.MoveAndResize(rect);
        _appWindow.Changed += AppWindow_Changed;

        if (_group.IsCollapsedToCapsule)
        {
            ApplyCollapsed(true);
        }

        ApplyDesktopLayer();
        Reload();

        // SetBorderAndTitleBar hides a window that is already shown; re-show it once setup is done.
        _appWindow.Show();
        DesktopPinHelper.BringToTop(hwnd);
    }

    public void ApplyDesktopLayer()
    {
        DesktopPinHelper.SetPinToDesktop(WindowNative.GetWindowHandle(this), _host.Config.Current.PinToDesktop);
    }

    public void Reload()
    {
        Box.Bind(_group);
        FolderGlyph.Glyph = _group.IsStorageBox ? "\uE7B8" : _group.IsFolderMapping ? "\uE8DA" : "\uE734";
        UpdateTitle();
    }

    private void UpdateTitle() => TitleText.Text = $"{_group.Title}（{Box.ItemCount}）";

    private static RectInt32 ClampToVisibleArea(RectInt32 rect)
    {
        var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Min(rect.Width, area.Width);
        var h = Math.Min(rect.Height, area.Height);
        var x = Math.Clamp(rect.X, area.X, area.X + area.Width - w);
        var y = Math.Clamp(rect.Y, area.Y, area.Y + area.Height - h);
        return new RectInt32(x, y, w, h);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_appWindow is null || (!args.DidPositionChange && !args.DidSizeChange))
        {
            return;
        }

        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        _group.DetachedX = pos.X;
        _group.DetachedY = pos.Y;
        _group.DetachedWidth = size.Width;
        // SetWindowPos is async (window is owned by Explorer). A collapse/expand can
        // report the previous height first; never treat the capsule height as the restored size.
        if (!_collapsed && !_peeking && size.Height >= 100)
        {
            _group.DetachedHeight = size.Height;
            _expandedHeight = size.Height;
        }

        _host.Config.SaveDebounced();
    }

    private void TitleBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != TitleBar;
             node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return;
            }
        }

        if (e.GetCurrentPoint(TitleBar).Properties.IsLeftButtonPressed)
        {
            BeginGesture(Gesture.Move);
            e.Handled = true;
        }
    }

    private void TitleBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture == Gesture.Move)
        {
            ApplyGesture();
            e.Handled = true;
        }
    }

    private void TitleBar_PointerReleased(object sender, PointerRoutedEventArgs e) => EndGesture();

    private void TitleBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndGesture();

    private void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_collapsed && e.GetCurrentPoint(Grip).Properties.IsLeftButtonPressed)
        {
            BeginGesture(Gesture.Resize);
            e.Handled = true;
        }
    }

    private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture == Gesture.Resize)
        {
            ApplyGesture();
            e.Handled = true;
        }
    }

    private void Grip_PointerReleased(object sender, PointerRoutedEventArgs e) => EndGesture();

    private void Grip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndGesture();

    private enum Gesture { None, Move, Resize }

    private Gesture _gesture;
    private NativePoint _gestureCursor;
    private PointInt32 _gestureStartPos;
    private SizeInt32 _gestureStartSize;
    private DispatcherTimer? _gestureTimer;

    public bool IsDraggingWindow => _gesture != Gesture.None;

    private nint Hwnd => WindowNative.GetWindowHandle(this);

    private void BeginGesture(Gesture kind)
    {
        if (!DesktopPinHelper.TryGetBounds(Hwnd, out var bounds))
        {
            return;
        }

        GetCursorPos(out _gestureCursor);
        _gestureStartPos = new PointInt32(bounds.Left, bounds.Top);
        _gestureStartSize = new SizeInt32(bounds.Width, bounds.Height);
        _gesture = kind;
        _gestureTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        _gestureTimer.Tick -= GestureTimer_Tick;
        _gestureTimer.Tick += GestureTimer_Tick;
        _gestureTimer.Start();
    }

    private void GestureTimer_Tick(object? sender, object e)
    {
        // Explorer (the window owner) often swallows mouse-up. Poll the physical button
        // so press-move-release works: hold to drag, let go to stop — no second click.
        if (!IsLeftButtonDown())
        {
            EndGesture();
            return;
        }

        ApplyGesture();
    }

    private void ApplyGesture()
    {
        if (_gesture == Gesture.None)
        {
            return;
        }

        GetCursorPos(out var now);
        var dx = now.X - _gestureCursor.X;
        var dy = now.Y - _gestureCursor.Y;
        if (_gesture == Gesture.Move)
        {
            var proposed = new RectInt32(_gestureStartPos.X + dx, _gestureStartPos.Y + dy,
                _gestureStartSize.Width, _gestureStartSize.Height);
            var snapped = BoxSnap.Snap(proposed, NeighborBounds(), WorkArea());
            DesktopPinHelper.Move(Hwnd, snapped.X, snapped.Y);
        }
        else
        {
            DesktopPinHelper.SetBounds(Hwnd, _gestureStartPos.X, _gestureStartPos.Y,
                Math.Max(180, _gestureStartSize.Width + dx), Math.Max(120, _gestureStartSize.Height + dy));
        }
    }

    public RectInt32 CurrentBounds =>
        DesktopPinHelper.TryGetBounds(Hwnd, out var native)
            ? new RectInt32(native.Left, native.Top, native.Width, native.Height)
            : new RectInt32(_appWindow?.Position.X ?? 0, _appWindow?.Position.Y ?? 0,
                _appWindow?.Size.Width ?? 0, _appWindow?.Size.Height ?? 0);

    private IEnumerable<RectInt32> NeighborBounds() =>
        _host.DetachedBoxes.Where(b => !ReferenceEquals(b, this)).Select(b => b.CurrentBounds);

    private RectInt32 WorkArea()
    {
        var bounds = CurrentBounds;
        return DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
    }

    private void EndGesture()
    {
        if (_gesture == Gesture.None)
        {
            return;
        }

        _gesture = Gesture.None;
        _gestureTimer?.Stop();
        if (DesktopPinHelper.TryGetBounds(Hwnd, out var bounds))
        {
            var rect = ClampToVisibleArea(new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
            if (rect.X != bounds.Left || rect.Y != bounds.Top)
            {
                DesktopPinHelper.Move(Hwnd, rect.X, rect.Y);
            }
        }
    }

    public void BeginMoveForTest() => BeginGesture(Gesture.Move);

    public void EndMoveForTest() => EndGesture();

    public void SetBoundsForTest(int x, int y, int width, int height) =>
        DesktopPinHelper.SetBounds(Hwnd, x, y, width, height);

    public RectInt32 SnapMoveToForTest(int x, int y)
    {
        var bounds = CurrentBounds;
        var snapped = BoxSnap.Snap(new RectInt32(x, y, bounds.Width, bounds.Height), NeighborBounds(), WorkArea());
        DesktopPinHelper.Move(Hwnd, snapped.X, snapped.Y);
        return snapped;
    }

    private static bool IsLeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private void TitleBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != TitleBar;
             node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return;
            }
        }

        ApplyCollapsed(!_collapsed);
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => ToggleCollapse();

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = Box.BuildBackgroundMenu();
        menu.Items.Add(new MenuFlyoutSeparator());
        var rename = new MenuFlyoutItem { Text = "重新命名格子…", Icon = new FontIcon { Glyph = "\uE8AC" } };
        rename.Click += async (_, _) =>
        {
            var title = await Dialogs.PromptAsync(RootGrid.XamlRoot, "重新命名格子", _group.Title);
            if (!string.IsNullOrWhiteSpace(title))
            {
                _group.Title = title;
                _host.Config.SaveImmediate();
                _host.NotifyFavoritesChanged(_group.Id, this);
                _host.Sidebar?.FavoritesView.ReloadGroups(_host.Config.Current.FavoriteGroups.IndexOf(_group));
                UpdateTitle();
            }
        };
        menu.Items.Add(rename);
        menu.ShowAt(MenuButton);
    }

    public void ToggleCollapse() => ApplyCollapsed(!_collapsed);

    private void ApplyCollapsed(bool collapsed)
    {
        if (_appWindow is null)
        {
            return;
        }

        _collapsed = collapsed;
        _peeking = false;
        _group.IsCollapsedToCapsule = collapsed;
        _expandedHeight = Math.Max(120, Math.Max(_expandedHeight, _group.DetachedHeight));
        Resize(collapsed);
        _host.Config.SaveDebounced();
    }

    private void Resize(bool collapsed)
    {
        if (_appWindow is null)
        {
            return;
        }

        DesktopPinHelper.TryGetBounds(Hwnd, out var native);
        var x = native.Width > 0 ? native.Left : _appWindow.Position.X;
        var y = native.Height > 0 ? native.Top : _appWindow.Position.Y;
        var width = Math.Max(180, native.Width > 0 ? native.Width : _appWindow.Size.Width);
        if (collapsed)
        {
            Box.Visibility = Visibility.Collapsed;
            Grip.Visibility = Visibility.Collapsed;
            MenuButton.Visibility = Visibility.Collapsed;
            DesktopPinHelper.SetBounds(Hwnd, x, y, width, CollapsedHeight);
            CollapseGlyph.Glyph = "\uE8A1";
            ToolTipService.SetToolTip(CollapseButton, "展開");
        }
        else
        {
            var rect = ClampToVisibleArea(new RectInt32(x, y, width, Math.Max(120, _expandedHeight)));
            DesktopPinHelper.SetBounds(Hwnd, rect.X, rect.Y, rect.Width, rect.Height);
            Box.Visibility = Visibility.Visible;
            Grip.Visibility = Visibility.Visible;
            MenuButton.Visibility = Visibility.Visible;
            CollapseGlyph.Glyph = "\uE921";
            ToolTipService.SetToolTip(CollapseButton, "收折成膠囊（也可雙擊標題）");
        }
    }

    /// <summary>Dragging files onto a capsule expands it so the drop target is visible, then folds back.</summary>
    private void OnDragHover(bool hovering)
    {
        if (!_collapsed)
        {
            return;
        }

        if (hovering)
        {
            _peekTimer?.Stop();
            if (!_peeking)
            {
                _peeking = true;
                Resize(collapsed: false);
            }

            return;
        }

        _peekTimer ??= CreatePeekTimer();
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    private DispatcherTimer CreatePeekTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_collapsed && _peeking)
            {
                _peeking = false;
                Resize(collapsed: true);
            }
        };
        return timer;
    }

    public bool IsPeeking => _peeking;

    /// <summary>Test hook: simulates files hovering / leaving the capsule.</summary>
    public void SimulateDragHover(bool hovering) => OnDragHover(hovering);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Recall();

    /// <summary>Closes the box and puts the group back into the sidebar only.</summary>
    public void Recall()
    {
        _recalled = true;
        Close();
    }

    public void ApplyTheme()
    {
        if (_closed)
        {
            return;
        }

        _host.Theme.ApplyTo(RootGrid);
        _host.Theme.ApplyWindowChrome(this, ThemeService.ChromeKind.Box);
    }

    public new void BringToFront()
    {
        this.Show();
        var hwnd = WindowNative.GetWindowHandle(this);
        if (_host.Config.Current.PinToDesktop)
        {
            DesktopPinHelper.RaiseAboveApps(hwnd);
        }
        else
        {
            DesktopPinHelper.BringToTop(hwnd);
        }
    }
}
