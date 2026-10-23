using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RDesk.Models;
using Windows.Graphics;

namespace RDesk.Services;

public sealed class MonitorService
{
    private readonly ConfigService _config;
    private DisplayAreaWatcher? _watcher;

    public MonitorService(ConfigService config)
    {
        _config = config;
    }

    public event EventHandler? DisplaysChanged;

    public void StartWatching()
    {
        if (_watcher != null)
        {
            return;
        }

        _watcher = DisplayArea.CreateWatcher();
        _watcher.Added += (_, _) => DisplaysChanged?.Invoke(this, EventArgs.Empty);
        _watcher.Removed += (_, _) => DisplaysChanged?.Invoke(this, EventArgs.Empty);
        _watcher.Updated += (_, _) => DisplaysChanged?.Invoke(this, EventArgs.Empty);
        _watcher.EnumerationCompleted += (_, _) => DisplaysChanged?.Invoke(this, EventArgs.Empty);
        _watcher.Start();
    }

    public IReadOnlyList<MonitorInfoModel> GetMonitors()
    {
        var areas = DisplayArea.FindAll();
        var list = new List<MonitorInfoModel>(areas.Count);
        for (var i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            var work = area.WorkArea;
            var outer = area.OuterBounds;
            // Prefer work area; if empty, fall back to outer bounds.
            var wx = work.Width > 0 ? work.X : outer.X;
            var wy = work.Height > 0 ? work.Y : outer.Y;
            var ww = work.Width > 0 ? work.Width : outer.Width;
            var wh = work.Height > 0 ? work.Height : outer.Height;

            list.Add(new MonitorInfoModel
            {
                DisplayId = area.DisplayId.Value.ToString(),
                Index = i,
                IsPrimary = area.IsPrimary,
                DisplayName = area.IsPrimary
                    ? $"螢幕 {i + 1} (主螢幕) - {outer.Width}x{outer.Height}"
                    : $"螢幕 {i + 1} - {outer.Width}x{outer.Height}",
                WorkAreaX = wx,
                WorkAreaY = wy,
                WorkAreaWidth = ww,
                WorkAreaHeight = wh,
                OuterX = outer.X,
                OuterY = outer.Y,
                OuterWidth = outer.Width,
                OuterHeight = outer.Height
            });
        }

        return list;
    }

    public MonitorInfoModel ResolveTargetMonitor()
    {
        var monitors = GetMonitors().Where(m => m.WorkAreaWidth > 100 && m.WorkAreaHeight > 100).ToList();
        if (monitors.Count == 0)
        {
            // Last-resort primary screen via Win32.
            return new MonitorInfoModel
            {
                DisplayName = "主螢幕",
                IsPrimary = true,
                WorkAreaX = 0,
                WorkAreaY = 0,
                WorkAreaWidth = GetSystemMetrics(0),  // SM_CXSCREEN
                WorkAreaHeight = GetSystemMetrics(1) // SM_CYSCREEN
            };
        }

        var cfg = _config.Current;
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        if (!string.IsNullOrWhiteSpace(cfg.TargetMonitorDeviceId))
        {
            var byId = monitors.FirstOrDefault(m => m.DisplayId == cfg.TargetMonitorDeviceId);
            if (byId != null)
            {
                return byId;
            }
        }

        // Only honor an explicit non-negative index from Settings; default -1 = primary.
        if (cfg.TargetMonitorIndex >= 0 && cfg.TargetMonitorIndex < monitors.Count)
        {
            return monitors[cfg.TargetMonitorIndex];
        }

        return primary;
    }

    public void DockSidebar(Window window, AppWindow appWindow)
    {
        var monitor = ResolveTargetMonitor();
        var dpi = Math.Max(96u, GetDpi(window));
        var scale = dpi / 96.0;

        var logicalWidth = Math.Clamp(_config.Current.SidebarWidth, 340, 460);
        var physicalWidth = (int)Math.Round(logicalWidth * scale);
        var margin = (int)Math.Round(12 * scale);
        var physicalHeight = Math.Max(200, monitor.WorkAreaHeight - (margin * 2));

        // Never exceed work area.
        physicalWidth = Math.Min(physicalWidth, Math.Max(200, monitor.WorkAreaWidth - margin * 2));
        physicalHeight = Math.Min(physicalHeight, Math.Max(200, monitor.WorkAreaHeight - margin * 2));

        var dockRight = !string.Equals(_config.Current.DockSide, "Left", StringComparison.OrdinalIgnoreCase);
        var x = dockRight
            ? monitor.WorkAreaX + monitor.WorkAreaWidth - physicalWidth - margin
            : monitor.WorkAreaX + margin;
        var y = monitor.WorkAreaY + margin;

        // Clamp into work area so the window cannot open off-screen.
        var maxX = monitor.WorkAreaX + monitor.WorkAreaWidth - physicalWidth;
        var maxY = monitor.WorkAreaY + monitor.WorkAreaHeight - physicalHeight;
        x = Math.Clamp(x, monitor.WorkAreaX, Math.Max(monitor.WorkAreaX, maxX));
        y = Math.Clamp(y, monitor.WorkAreaY, Math.Max(monitor.WorkAreaY, maxY));

        StartupLog.Write(
            "DockSidebar",
            detail: $"monitor=({monitor.WorkAreaX},{monitor.WorkAreaY},{monitor.WorkAreaWidth}x{monitor.WorkAreaHeight}) dpi={dpi} -> ({x},{y},{physicalWidth}x{physicalHeight})");

        appWindow.MoveAndResize(new RectInt32(x, y, physicalWidth, physicalHeight));
    }

    public static uint GetDpi(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : dpi;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
