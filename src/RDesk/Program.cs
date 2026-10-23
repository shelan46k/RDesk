using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using WinRT;

namespace RDesk;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // Must run before WinUI starts: applies a downloaded update from the previous session.
            VelopackApp.Build().Run();
            ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        catch (Exception ex)
        {
            StartupLog.Write("Fatal Main", ex);
            NativeMessageBox.Show(
                $"RDesk 啟動失敗：\n{ex.Message}\n\n詳細紀錄：%LocalAppData%\\RDesk\\startup.log",
                "RDesk");
        }
    }
}

internal static class StartupLog
{
    public static string Path
    {
        get
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RDesk");
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "startup.log");
        }
    }

    public static void Write(string title, Exception? ex = null, string? detail = null)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}";
            if (!string.IsNullOrWhiteSpace(detail))
            {
                line += " | " + detail;
            }

            if (ex is not null)
            {
                line += Environment.NewLine + ex;
            }

            File.AppendAllText(Path, line + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
        }
    }
}

internal static class NativeMessageBox
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static void Show(string text, string caption) =>
        MessageBoxW(nint.Zero, text, caption, 0x00000010); // MB_ICONERROR
}
