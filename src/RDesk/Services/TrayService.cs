using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace RDesk.Services;

/// <summary>
/// System tray — DeskBox pattern: always present while app runs.
/// Left-click toggles sidebar; right-click opens menu. No desktop floating ball.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly AppHost _host;
    private TaskbarIcon? _icon;

    public IEnumerable<MenuFlyoutItem> MenuItems =>
        (_icon?.ContextFlyout as MenuFlyout)?.Items.OfType<MenuFlyoutItem>() ?? Enumerable.Empty<MenuFlyoutItem>();

    public TrayService(AppHost host)
    {
        _host = host;
    }

    public void Start()
    {
        if (_icon is not null)
        {
            return;
        }

        var menu = new MenuFlyout();
        menu.Items.Add(CreateItem("展開 / 隱藏主面板", () => _host.ToggleFromTrayOrHotkey()));
        menu.Items.Add(CreateItem("搜尋…", () => _host.OpenSearch()));
        var boxesLabel = _host.Config.Current.EnableBoxesHotkey
            ? $"顯示所有桌面格子（{_host.Config.Current.BoxesHotkey}）"
            : "顯示所有桌面格子";
        menu.Items.Add(CreateItem(boxesLabel, () => _host.ShowAllDetachedBoxes()));
        menu.Items.Add(CreateItem("一鍵整理桌面…", () => _host.OpenOrganizer()));
        menu.Items.Add(CreateItem("切換深淺色主題", () =>
        {
            _host.Theme.Toggle();
            _host.ApplyThemeEverywhere();
        }));
        menu.Items.Add(CreateItem("偏好設定...", () => _host.OpenSettings()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateItem("退出 RDesk", () => _host.Exit()));

        _icon = new TaskbarIcon
        {
            ToolTipText = "RDesk — 左鍵展開/隱藏（F7 同效）",
            ContextFlyout = menu,
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick,
            NoLeftClickDelay = true
        };

        TrySetIcon();
        // DeskBox: left-click = toggle, not show-only.
        _icon.LeftClickCommand = new RelayAction(() => _host.ToggleFromTrayOrHotkey());
        _icon.ForceCreate(true);
    }

    private void TrySetIcon()
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "rdesk.ico");
            if (File.Exists(icoPath))
            {
                _icon!.Icon = new System.Drawing.Icon(icoPath);
            }
        }
        catch
        {
        }
    }

    public int NotificationCount { get; private set; }

    public void ShowNotification(string title, string message)
    {
        NotificationCount++;
        try
        {
            _icon?.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            StartupLog.Write("Tray notification failed", ex);
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    // The default PopupMenu mode renders a native Win32 menu that only invokes Command, never Click.
    private static MenuFlyoutItem CreateItem(string text, Action action) =>
        new() { Text = text, Command = new RelayAction(action) };

    private sealed class RelayAction : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public RelayAction(Action action) => _action = action;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
#pragma warning disable CS0067
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    }
}
