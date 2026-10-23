using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace RDesk.Helpers;

public static class ShellHelper
{
    private static readonly ConcurrentDictionary<string, byte[]> IconPngCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string? target = shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<BitmapImage?> GetFileIconAsync(string path)
    {
        try
        {
            var png = await RunOnStaAsync(() => GetFileIconPng(path));
            if (png is null || png.Length == 0)
            {
                return null;
            }

            using var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync(png.AsBuffer());
            ms.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(ms);
            return image;
        }
        catch
        {
            return null;
        }
    }

    // Shell icon handlers (.url / .lnk overlays, custom IconFile) only work on an STA thread.
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> StaQueue = StartStaWorker();

    private static System.Collections.Concurrent.BlockingCollection<Action> StartStaWorker()
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable())
            {
                work();
            }
        })
        {
            IsBackground = true,
            Name = "RDesk shell icons"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return queue;
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        StaQueue.Add(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private static byte[]? GetFileIconPng(string path)
    {
        if (IconPngCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var jumbo = GetShellImagePng(path, 64);
        if (jumbo is not null)
        {
            IconPngCache[path] = jumbo;
            return jumbo;
        }

        var shinfo = new SHFILEINFO();
        var result = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON);
        if (result == nint.Zero || shinfo.hIcon == nint.Zero)
        {
            return null;
        }

        try
        {
            using var icon = System.Drawing.Icon.FromHandle(shinfo.hIcon);
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            var bytes = ms.ToArray();
            IconPngCache[path] = bytes;
            return bytes;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    /// <summary>
    /// Large icon / thumbnail exactly as Explorer draws it (shortcut targets, folders, image thumbnails),
    /// returned as an alpha-correct PNG. Null when the shell cannot render the item.
    /// </summary>
    private static byte[]? GetShellImagePng(string path, int size)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        nint hbmp = nint.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, nint.Zero, ref iid, out var factory) != 0 || factory is null)
            {
                return null;
            }

            try
            {
                if (factory.GetImage(new NativeSize { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK, out hbmp) != 0 || hbmp == nint.Zero)
                {
                    return null;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }

            return HBitmapToPng(hbmp);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbmp != nint.Zero)
            {
                DeleteObject(hbmp);
            }
        }
    }

    private static byte[]? HBitmapToPng(nint hbmp)
    {
        var bm = new BITMAP();
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0)
        {
            return null;
        }

        int w = bm.bmWidth, h = bm.bmHeight;
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32
        };
        var pixels = new byte[w * h * 4];
        var dc = GetDC(nint.Zero);
        try
        {
            if (GetDIBits(dc, hbmp, 0, (uint)h, pixels, ref info, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(nint.Zero, dc);
        }

        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }
        }

        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(pixels, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out nint phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint h, int c, ref BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);
}
