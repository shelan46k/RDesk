using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace RDesk.Services;

/// <summary>
/// Process-wide Win32 hotkeys: reveal the panel (F7), open search (Ctrl+Alt+F),
/// and raise desktop file boxes (Ctrl+Alt+B).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int RevealId = 0x5244; // 'RD'
    private const int SearchId = 0x5253; // 'RS'
    private const int BoxesId = 0x5242;  // 'RB'
    private const int WmHotkey = 0x0312;
    private const uint ModNorepeat = 0x4000;

    private readonly ConfigService _config;
    private nint _hwnd;
    private bool _registered;
    private bool _searchRegistered;
    private bool _boxesRegistered;

    public event EventHandler? RevealRequested;
    public event EventHandler? SearchRequested;
    public event EventHandler? BoxesRequested;

    /// <summary>False when the key combo is invalid or already taken by another app.</summary>
    public bool IsRegistered => _registered;
    public bool IsSearchRegistered => _searchRegistered;
    public bool IsBoxesRegistered => _boxesRegistered;

    public HotkeyService(ConfigService config)
    {
        _config = config;
    }

    public void Start(Window messageWindow)
    {
        if (_hwnd != nint.Zero)
        {
            return;
        }

        _hwnd = WindowNative.GetWindowHandle(messageWindow);
        SetWindowSubclass(_hwnd, _subclassProc, 1, 0);
        RefreshRegistration();
    }

    public void RefreshRegistration()
    {
        Unregister();
        if (_hwnd == nint.Zero)
        {
            return;
        }

        var cfg = _config.Current;
        if (cfg.EnableRevealHotkey)
        {
            if (!TryParseHotkey(cfg.RevealHotkey, out var modifiers, out var vk))
            {
                modifiers = 0;
                vk = 0x76; // F7
            }

            _registered = RegisterHotKey(_hwnd, RevealId, modifiers | ModNorepeat, vk);
            StartupLog.Write(_registered ? "Hotkey registered" : "Hotkey register failed", detail: cfg.RevealHotkey);
        }

        if (cfg.EnableSearchHotkey && TryParseHotkey(cfg.SearchHotkey, out var sm, out var svk))
        {
            _searchRegistered = RegisterHotKey(_hwnd, SearchId, sm | ModNorepeat, svk);
            StartupLog.Write(_searchRegistered ? "Search hotkey registered" : "Search hotkey register failed", detail: cfg.SearchHotkey);
        }

        if (cfg.EnableBoxesHotkey && TryParseHotkey(cfg.BoxesHotkey, out var bm, out var bvk))
        {
            _boxesRegistered = RegisterHotKey(_hwnd, BoxesId, bm | ModNorepeat, bvk);
            StartupLog.Write(_boxesRegistered ? "Boxes hotkey registered" : "Boxes hotkey register failed", detail: cfg.BoxesHotkey);
        }
    }

    public static bool IsValidHotkey(string? text) => TryParseHotkey(text, out _, out _);

    public void Dispose()
    {
        Unregister();
    }

    private void Unregister()
    {
        if (_hwnd == nint.Zero)
        {
            return;
        }

        if (_registered)
        {
            UnregisterHotKey(_hwnd, RevealId);
            _registered = false;
        }

        if (_searchRegistered)
        {
            UnregisterHotKey(_hwnd, SearchId);
            _searchRegistered = false;
        }

        if (_boxesRegistered)
        {
            UnregisterHotKey(_hwnd, BoxesId);
            _boxesRegistered = false;
        }
    }

    private void OnHotkey(int id)
    {
        if (id == SearchId)
        {
            SearchRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (id == BoxesId)
        {
            BoxesRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            RevealRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool TryParseHotkey(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0002;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0001;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0004;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0008;
            }
            else if (part.Equals("Space", StringComparison.OrdinalIgnoreCase))
            {
                vk = 0x20;
            }
            else if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
            {
                vk = char.ToUpperInvariant(part[0]);
            }
            else if (part.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(part[1..], out var fn) && fn is >= 1 and <= 24)
            {
                vk = (uint)(0x70 + fn - 1);
            }
            else
            {
                return false;
            }
        }

        return vk != 0;
    }

    private readonly SubclassProc _subclassProc = static (hwnd, msg, wParam, lParam, _, _) =>
    {
        if (msg == WmHotkey && wParam.ToInt32() is RevealId or SearchId or BoxesId)
        {
            InstanceBridge?.OnHotkey(wParam.ToInt32());
            return nint.Zero;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    };

    // Set by BindInstanceBridge so the static subclass callback can raise the instance events.
    private static HotkeyService? InstanceBridge;

    public HotkeyService BindInstanceBridge()
    {
        InstanceBridge = this;
        return this;
    }

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}
