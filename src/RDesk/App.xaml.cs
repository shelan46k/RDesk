using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RDesk.Services;
using RDesk.Views;

namespace RDesk;

public partial class App : Application
{
    private const string MutexName = "RDesk_SingleInstance_Mutex";
    private const string ActivateEventName = "RDesk_Activate_Event";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;
    private AppHost? _host;
    private Window? _mainWindow;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            StartupLog.Write("UnhandledException", e.Exception);
            Debug.WriteLine(e.Exception);
            e.Handled = true;
            NativeMessageBox.Show(
                $"RDesk 發生錯誤：\n{e.Exception.Message}\n\n詳見 %AppData%\\RDesk\\startup.log",
                "RDesk");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupLog.Write("OnLaunched begin");

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Already running: wake the existing instance's sidebar instead of starting a second one.
            StartupLog.Write("Second instance — signalling existing instance");
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ActivateEventName);
                signal.Set();
            }
            catch (Exception ex)
            {
                StartupLog.Write("Signal existing instance failed", ex);
            }

            Exit();
            return;
        }

        try
        {
            _host = new AppHost();
            var sidebar = new MainSidebarWindow(_host);
            _host.AttachWindows(sidebar);

            _mainWindow = sidebar;
            sidebar.Activate();
            StartupLog.Write("Sidebar activated");

            var tray = new TrayService(_host);
            tray.Start();
            _host.Tray = tray;
            StartupLog.Write("Tray started");

            var dispatcher = DispatcherQueue.GetForCurrentThread();
            ListenForActivationRequests(dispatcher);

            if (SelfTest.IsRequested)
            {
                dispatcher.TryEnqueue(async () => await SelfTest.RunAsync(_host));
            }
        }
        catch (Exception ex)
        {
            StartupLog.Write("OnLaunched failed", ex);
            NativeMessageBox.Show(
                $"RDesk 啟動失敗：\n{ex.Message}\n\n詳見 %AppData%\\RDesk\\startup.log",
                "RDesk");
            Exit();
        }
    }

    private void ListenForActivationRequests(DispatcherQueue dispatcher)
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            while (_activateEvent.WaitOne())
            {
                dispatcher.TryEnqueue(() => _host?.ShowSidebar());
            }
        })
        {
            IsBackground = true,
            Name = "RDesk activation listener"
        };
        thread.Start();
    }
}
