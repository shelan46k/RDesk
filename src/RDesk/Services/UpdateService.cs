using System.Reflection;
using RDesk.Models;
using Velopack;
using Velopack.Sources;

namespace RDesk.Services;

/// <summary>
/// Checks GitHub Releases for a newer Velopack package, downloads it, and applies it
/// the next time the installed app starts (or immediately if the user asks to restart).
/// Debug builds and unzipped copies are not "installed", so they skip applying the package.
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    private readonly ConfigService _config;
    private readonly object _gate = new();
    private UpdateManager? _manager;
    private string _boundUrl = string.Empty;
    private UpdateInfo? _available;
    private bool _busy;

    public UpdateService(ConfigService config)
    {
        _config = config;
        Status = DefaultStatus();
    }

    public string Status { get; private set; }

    public bool CanRestartNow
    {
        get
        {
            try
            {
                return TryManager()?.UpdatePendingRestart is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public event EventHandler? StatusChanged;

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    public async Task CheckAndDownloadAsync(bool userRequested = false)
    {
        if (SelfTest.IsRequested && !userRequested)
        {
            return;
        }

        if (!TryBegin(userRequested))
        {
            return;
        }

        try
        {
            await Task.Run(() => RunCheckAsync(userRequested)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StartupLog.Write("Update check failed", ex);
            SetStatus("檢查更新失敗：" + Friendly(ex));
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    private bool TryBegin(bool userRequested)
    {
        lock (_gate)
        {
            if (_busy)
            {
                if (userRequested)
                {
                    SetStatus("正在檢查中，請稍候…");
                }

                return false;
            }

            _busy = true;
            return true;
        }
    }

    private async Task RunCheckAsync(bool userRequested)
    {
        var mgr = TryManager();
        if (mgr is null)
        {
            SetStatus(DefaultStatus());
            return;
        }

        if (!mgr.IsInstalled)
        {
            SetStatus($"目前版本 {AppVersion}（開發版）。自動更新只在用 GitHub 上的安裝包裝過之後才有效。");
            return;
        }

        if (!_config.Current.AutoUpdateEnabled && !userRequested)
        {
            SetStatus($"目前版本 {VersionOf(mgr)}。已關閉自動檢查。");
            return;
        }

        SetStatus("正在檢查 GitHub 上的新版本…");
        UpdateInfo? info;
        try
        {
            info = await mgr.CheckForUpdatesAsync().WaitAsync(CheckTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SetStatus("檢查更新逾時，請確認網路後再試。");
            return;
        }

        _available = info;
        var current = VersionOf(mgr);
        if (info is null)
        {
            SetStatus($"目前版本 {current}，已是最新。");
            return;
        }

        SetStatus($"發現 {info.TargetFullRelease.Version}，正在下載…");
        await mgr.DownloadUpdatesAsync(info).WaitAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        SetStatus($"已下載 {info.TargetFullRelease.Version}。下次開啟 RDesk 會自動換成新版，或按「立即重啟並更新」。");
    }

    public void InvalidateSource()
    {
        lock (_gate)
        {
            _manager = null;
            _boundUrl = string.Empty;
        }
    }

    public void RestartAndApply()
    {
        var mgr = TryManager();
        if (mgr?.UpdatePendingRestart is null && _available is null)
        {
            return;
        }

        mgr?.ApplyUpdatesAndRestart(_available?.TargetFullRelease);
    }

    private UpdateManager? TryManager()
    {
        var url = (_config.Current.UpdateGithubUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.Length == 0 ||
            !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
            url.Count(c => c == '/') < 4)
        {
            _manager = null;
            _boundUrl = string.Empty;
            return null;
        }

        if (_manager is not null && string.Equals(_boundUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            return _manager;
        }

        _boundUrl = url;
        _manager = new UpdateManager(new GithubSource(url, accessToken: null, prerelease: false));
        return _manager;
    }

    private static string VersionOf(UpdateManager mgr)
    {
        try
        {
            var version = mgr.CurrentVersion?.ToString();
            return string.IsNullOrWhiteSpace(version) ? AppVersion : version;
        }
        catch
        {
            return AppVersion;
        }
    }

    private string DefaultStatus()
    {
        var url = (_config.Current.UpdateGithubUrl ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            return $"目前版本 {AppVersion}。填上 GitHub 專案網址後，用安裝包裝過的電腦就會自動檢查更新。";
        }

        return $"目前版本 {AppVersion}。";
    }

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Friendly(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null && current is TimeoutException or TaskCanceledException or AggregateException)
        {
            current = current.InnerException;
        }

        if (current is TimeoutException or TaskCanceledException)
        {
            return "連線逾時，請確認網路後再試。";
        }

        var message = current.Message ?? string.Empty;
        if (message.Contains("not installed", StringComparison.OrdinalIgnoreCase))
        {
            return $"目前版本 {AppVersion}（開發版）。請先安裝 GitHub 上的 Setup，自動更新才會生效。";
        }

        return string.IsNullOrWhiteSpace(message) ? current.GetType().Name : message;
    }
}
