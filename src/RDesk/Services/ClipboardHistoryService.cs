using System.Runtime.InteropServices;
using RDesk.Models;
using Windows.ApplicationModel.DataTransfer;

namespace RDesk.Services;

/// <summary>
/// "最近" clipboard buffer for 隨記: keeps the last text / file copies locally (never uploaded).
/// Can be turned off in settings; RDesk's own copy-backs are not re-captured.
/// </summary>
public sealed class ClipboardHistoryService
{
    public const int MaxItems = 40;
    private const int MaxTextLength = 20000;

    private readonly ConfigService _config;
    private bool _started;
    private DateTime _suppressUntil;

    public event EventHandler? Changed;

    public ClipboardHistoryService(ConfigService config)
    {
        _config = config;
    }

    public IReadOnlyList<ClipItemModel> Items => _config.Current.ClipboardHistory;

    /// <summary>
    /// WinRT Clipboard.ContentChanged only fires while RDesk has focus, so listen for WM_CLIPBOARDUPDATE
    /// on the sidebar window instead to also record copies made in other programs.
    /// </summary>
    public void Start(Microsoft.UI.Xaml.Window window)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _dispatcher = window.DispatcherQueue;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _subclassProc = OnWindowMessage;
        SetWindowSubclass(_hwnd, _subclassProc, 0x434C, 0);
        if (!AddClipboardFormatListener(_hwnd))
        {
            StartupLog.Write("AddClipboardFormatListener failed", detail: Marshal.GetLastWin32Error().ToString());
        }
    }

    public void Stop()
    {
        if (_hwnd != nint.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    private nint OnWindowMessage(nint hwnd, uint msg, nint wParam, nint lParam, nint id, nint data)
    {
        if (msg == WmClipboardUpdate && !_capturePending)
        {
            _capturePending = true;
            _dispatcher?.TryEnqueue(async () =>
            {
                await Task.Delay(120);
                _capturePending = false;
                await CaptureAsync();
            });
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private const uint WmClipboardUpdate = 0x031D;
    private nint _hwnd;
    private bool _capturePending;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private SubclassProc? _subclassProc;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    public async Task CaptureAsync()
    {
        if (!_config.Current.ClipboardHistoryEnabled || DateTime.Now < _suppressUntil)
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var view = Clipboard.GetContent();
                ClipItemModel? item = null;
                if (view.Contains(StandardDataFormats.StorageItems))
                {
                    var files = await view.GetStorageItemsAsync();
                    var paths = files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                    if (paths.Count > 0)
                    {
                        item = new ClipItemModel { Kind = "Files", Paths = paths, Text = string.Join(Environment.NewLine, paths) };
                    }
                }
                else if (view.Contains(StandardDataFormats.Text))
                {
                    var text = await view.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text) && text.Length <= MaxTextLength)
                    {
                        item = new ClipItemModel { Kind = "Text", Text = text };
                    }
                }

                if (item is not null)
                {
                    Add(item);
                }

                return;
            }
            catch (Exception ex) when (attempt < 2)
            {
                // Another app may still hold the clipboard open.
                StartupLog.Write("Clipboard read retry", detail: ex.Message);
                await Task.Delay(150);
            }
            catch (Exception ex)
            {
                StartupLog.Write("Clipboard read failed", ex);
            }
        }
    }

    public void Add(ClipItemModel item)
    {
        var list = _config.Current.ClipboardHistory;
        list.RemoveAll(c => c.Kind == item.Kind && c.Text == item.Text);
        list.Insert(0, item);
        if (list.Count > MaxItems)
        {
            list.RemoveRange(MaxItems, list.Count - MaxItems);
        }

        _config.SaveDebounced();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(ClipItemModel item)
    {
        _config.Current.ClipboardHistory.Remove(item);
        _config.SaveDebounced();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _config.Current.ClipboardHistory.Clear();
        _config.SaveDebounced();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies an entry back to the clipboard without re-recording it.</summary>
    public async Task RestoreAsync(ClipItemModel item)
    {
        _suppressUntil = DateTime.Now.AddMilliseconds(800);
        if (item.Kind == "Files")
        {
            await Helpers.FileOps.CopyFilesAsync(item.Paths);
        }
        else
        {
            Helpers.FileOps.CopyText(item.Text);
        }
    }

    /// <summary>Copies text from RDesk UI (notes, todos) without recording it as history.</summary>
    public void CopyTextQuietly(string text)
    {
        _suppressUntil = DateTime.Now.AddMilliseconds(800);
        Helpers.FileOps.CopyText(text);
    }
}
