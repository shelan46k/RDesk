using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RDesk.Services;

/// <summary>
/// SystemBackdrop whose acrylic / mica tint and luminosity can be driven by a 0–1 transparency
/// slider. The built-in DesktopAcrylicBackdrop / MicaBackdrop types have no TintOpacity.
/// </summary>
internal sealed class ConfigurableSystemBackdrop : SystemBackdrop, IDisposable
{
    private string _material = "Acrylic";
    private bool _isDark;
    private double _transparency = 0.15;
    private ICompositionSupportsSystemBackdrop? _target;
    private SystemBackdropConfiguration? _configuration;
    private MicaController? _mica;
    private DesktopAcrylicController? _acrylic;
    private bool _disposed;

    public void Update(string material, bool isDark, double transparency)
    {
        _material = material;
        _isDark = isDark;
        _transparency = Math.Clamp(transparency, 0.0, 1.0);
        if (_disposed)
        {
            return;
        }

        ApplyConfigurationTheme();
        ApplyController();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        DisposeControllers();
        _target = connectedTarget;
        _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        ApplyConfigurationTheme();
        ApplyController();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(
        ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        _configuration = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        ApplyConfigurationTheme();
        _mica?.SetSystemBackdropConfiguration(_configuration);
        if (_acrylic is { IsClosed: false })
        {
            _acrylic.SetSystemBackdropConfiguration(_configuration);
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            base.OnTargetDisconnected(disconnectedTarget);
        }
        finally
        {
            DisposeControllers();
            _target = null;
            _configuration = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeControllers();
        _target = null;
        _configuration = null;
    }

    private void ApplyConfigurationTheme()
    {
        if (_configuration is null)
        {
            return;
        }

        // Desktop-pinned windows are often "inactive"; keep the material at full strength.
        _configuration.IsInputActive = true;
        _configuration.Theme = _isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }

    private void ApplyController()
    {
        if (_disposed || _target is null || _configuration is null)
        {
            return;
        }

        try
        {
            // Never drop the material at 100% transparency: a null backdrop paints an opaque
            // white island. Tint/luminosity of 0 still samples the wallpaper (blurred).
            if (string.Equals(_material, "Solid", StringComparison.OrdinalIgnoreCase))
            {
                DisposeControllers();
                return;
            }

            if (string.Equals(_material, "Mica", StringComparison.OrdinalIgnoreCase) &&
                MicaController.IsSupported())
            {
                ApplyMica();
                return;
            }

            if (DesktopAcrylicController.IsSupported())
            {
                ApplyAcrylic();
                return;
            }

            DisposeControllers();
        }
        catch
        {
            DisposeControllers();
        }
    }

    private void ApplyAcrylic()
    {
        DisposeMica();
        if (_acrylic is null || _acrylic.IsClosed)
        {
            _acrylic = new DesktopAcrylicController();
            _acrylic.SetSystemBackdropConfiguration(_configuration);
            if (!_acrylic.AddSystemBackdropTarget(_target!))
            {
                DisposeAcrylic();
                return;
            }
        }

        var strength = 1.0 - _transparency;
        var tint = _isDark
            ? Color.FromArgb(0xFF, 0x14, 0x17, 0x1F)
            : Color.FromArgb(0xFF, 0xDC, 0xE8, 0xF5);
        _acrylic.Kind = DesktopAcrylicKind.Thin;
        _acrylic.TintColor = tint;
        _acrylic.FallbackColor = tint;
        _acrylic.TintOpacity = (float)((_isDark ? 0.50 : 0.38) * strength);
        _acrylic.LuminosityOpacity = (float)((_isDark ? 0.72 : 0.80) * strength);
    }

    private void ApplyMica()
    {
        DisposeAcrylic();
        if (_mica is null)
        {
            _mica = new MicaController();
            _mica.SetSystemBackdropConfiguration(_configuration);
            if (!_mica.AddSystemBackdropTarget(_target!))
            {
                DisposeMica();
                return;
            }
        }

        var strength = 1.0 - _transparency;
        var tint = _isDark
            ? Color.FromArgb(0xFF, 0x14, 0x17, 0x1F)
            : Color.FromArgb(0xFF, 0xF7, 0xFA, 0xFC);
        _mica.Kind = MicaKind.Base;
        _mica.TintColor = tint;
        _mica.FallbackColor = tint;
        _mica.TintOpacity = (float)((_isDark ? 0.46 : 0.40) * strength);
        _mica.LuminosityOpacity = (float)((_isDark ? 0.90 : 0.94) * strength);
    }

    private void DisposeControllers()
    {
        DisposeMica();
        DisposeAcrylic();
    }

    private void DisposeMica()
    {
        if (_mica is null)
        {
            return;
        }

        try
        {
            _mica.RemoveAllSystemBackdropTargets();
            _mica.Dispose();
        }
        catch
        {
        }
        finally
        {
            _mica = null;
        }
    }

    private void DisposeAcrylic()
    {
        if (_acrylic is null)
        {
            return;
        }

        try
        {
            _acrylic.RemoveAllSystemBackdropTargets();
            _acrylic.Dispose();
        }
        catch
        {
        }
        finally
        {
            _acrylic = null;
        }
    }
}
