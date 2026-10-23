using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace RDesk.Services;

public sealed class HardwareMonitorService
{
    private FILETIME _prevIdle, _prevKernel, _prevUser;
    private bool _cpuPrimed;
    private long _prevRecv;
    private long _prevSent;
    private DateTime _prevNetSample = DateTime.MinValue;

    public HardwareSnapshot Sample()
    {
        var cpu = SampleCpu();
        var ram = SampleRam();
        var disk = SampleSystemDisk();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var (down, up, connected, nicLabel, ip) = SampleNetwork();

        return new HardwareSnapshot
        {
            CpuPercent = cpu,
            RamPercent = ram,
            DiskPercent = disk,
            Uptime = uptime,
            DownloadBytesPerSec = down,
            UploadBytesPerSec = up,
            IsConnected = connected,
            NicLabel = nicLabel,
            IPv4 = ip
        };
    }

    private double SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        if (!_cpuPrimed)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _cpuPrimed = true;
            return 0;
        }

        var idleDiff = Sub(idle, _prevIdle);
        var kernelDiff = Sub(kernel, _prevKernel);
        var userDiff = Sub(user, _prevUser);
        var total = kernelDiff + userDiff;
        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;
        if (total <= 0)
        {
            return 0;
        }

        var busy = total - idleDiff;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    private static double SampleRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return 0;
        }

        return status.dwMemoryLoad;
    }

    private static double SampleSystemDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return 0;
            }

            var used = drive.TotalSize - drive.TotalFreeSpace;
            return used * 100.0 / drive.TotalSize;
        }
        catch
        {
            return 0;
        }
    }

    private (double Down, double Up, bool Connected, string Nic, string Ip) SampleNetwork()
    {
        long recv = 0, sent = 0;
        var connected = false;
        var nicName = "-";
        var ip = "-";

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            connected = true;
            var stats = nic.GetIPStatistics();
            recv += stats.BytesReceived;
            sent += stats.BytesSent;
            if (nicName == "-")
            {
                nicName = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet => "Ethernet",
                    _ => nic.Name
                };
                try
                {
                    var addr = nic.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (addr is not null)
                    {
                        ip = addr.Address.ToString();
                    }
                }
                catch
                {
                }
            }
        }

        var now = DateTime.UtcNow;
        double down = 0, up = 0;
        if (_prevNetSample != DateTime.MinValue)
        {
            var sec = (now - _prevNetSample).TotalSeconds;
            if (sec > 0.2)
            {
                down = Math.Max(0, (recv - _prevRecv) / sec);
                up = Math.Max(0, (sent - _prevSent) / sec);
            }
        }

        _prevRecv = recv;
        _prevSent = sent;
        _prevNetSample = now;
        return (down, up, connected, nicName, ip);
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024)
        {
            return $"{bytesPerSec:0} B/s";
        }

        if (bytesPerSec < 1048576)
        {
            return $"{bytesPerSec / 1024:0.0} KB/s";
        }

        return $"{bytesPerSec / 1048576:0.0} MB/s";
    }

    private static ulong Sub(FILETIME a, FILETIME b)
    {
        var av = ((ulong)a.dwHighDateTime << 32) | a.dwLowDateTime;
        var bv = ((ulong)b.dwHighDateTime << 32) | b.dwLowDateTime;
        return av >= bv ? av - bv : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

public sealed class HardwareSnapshot
{
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public double DiskPercent { get; set; }
    public TimeSpan Uptime { get; set; }
    public double DownloadBytesPerSec { get; set; }
    public double UploadBytesPerSec { get; set; }
    public bool IsConnected { get; set; }
    public string NicLabel { get; set; } = "-";
    public string IPv4 { get; set; } = "-";
}
