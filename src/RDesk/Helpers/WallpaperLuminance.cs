using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace RDesk.Helpers;

/// <summary>Samples the desktop wallpaper so overlay text can flip to a readable colour.</summary>
public static class WallpaperLuminance
{
    private const uint SpiGetDeskWallpaper = 0x0073;
    private static readonly object Gate = new();
    private static string? _path;
    private static DateTime _mtime;
    private static double _luma = 0.35;
    private static DateTime _nextRefreshUtc;

    /// <summary>True when the wallpaper is dark enough that black text would disappear on it.</summary>
    public static bool IsDark => Luma < 0.45;

    /// <summary>0 = black wallpaper, 1 = white. Cached; refreshes at most every few seconds.</summary>
    public static double Luma
    {
        get
        {
            EnsureFresh();
            return _luma;
        }
    }

    public static void Refresh()
    {
        lock (Gate)
        {
            _nextRefreshUtc = DateTime.MinValue;
        }

        EnsureFresh();
    }

    private static void EnsureFresh()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow < _nextRefreshUtc)
            {
                return;
            }

            _nextRefreshUtc = DateTime.UtcNow.AddSeconds(8);
            try
            {
                var path = ResolveWallpaperPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return;
                }

                var mtime = File.GetLastWriteTimeUtc(path);
                if (path == _path && mtime == _mtime)
                {
                    return;
                }

                _luma = Sample(path);
                _path = path;
                _mtime = mtime;
            }
            catch
            {
                // Keep the last sample; a missing file should not crash theme apply.
            }
        }
    }

    private static string? ResolveWallpaperPath()
    {
        var buffer = new StringBuilder(520);
        if (SystemParametersInfo(SpiGetDeskWallpaper, (uint)buffer.Capacity, buffer, 0))
        {
            var fromSpi = buffer.ToString();
            if (!string.IsNullOrWhiteSpace(fromSpi) && File.Exists(fromSpi))
            {
                return fromSpi;
            }
        }

        var transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    private static double Sample(string path)
    {
        using var original = Image.FromFile(path);
        using var thumb = original.GetThumbnailImage(48, 48, () => false, nint.Zero);
        using var bmp = new Bitmap(thumb);
        double sum = 0;
        var count = bmp.Width * bmp.Height;
        if (count <= 0)
        {
            return 0.35;
        }

        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                sum += (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            }
        }

        return sum / count;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);
}
