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
    private readonly Dictionary<string, (long Read, long Written, DateTime At)> _prevDisk = new();

    public HardwareSnapshot Sample()
    {
        var cpu = SampleCpu();
        var ram = SampleRam();
        var drives = SampleDrives();
        var disk = drives.FirstOrDefault(d => d.IsSystem)?.UsedPercent
                   ?? drives.FirstOrDefault()?.UsedPercent
                   ?? 0;
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var (down, up, connected, nicLabel, ip) = SampleNetwork();
        SampleDiskIo(drives);

        return new HardwareSnapshot
        {
            CpuPercent = cpu,
            RamPercent = ram,
            DiskPercent = disk,
            Drives = drives,
            DiskReadBytesPerSec = drives.Sum(d => d.ReadBytesPerSec),
            DiskWriteBytesPerSec = drives.Sum(d => d.WriteBytesPerSec),
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

    private static List<DiskDriveInfo> SampleDrives()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var list = new List<DiskDriveInfo>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                var name = drive.Name.TrimEnd('\\');
                list.Add(new DiskDriveInfo
                {
                    Name = name.Length > 0 ? name : drive.Name,
                    UsedPercent = (drive.TotalSize - drive.TotalFreeSpace) * 100.0 / drive.TotalSize,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.TotalFreeSpace,
                    IsSystem = string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch
        {
        }

        return list
            .OrderByDescending(d => d.IsSystem)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
    }

    private void SampleDiskIo(List<DiskDriveInfo> drives)
    {
        var now = DateTime.UtcNow;
        foreach (var drive in drives)
        {
            if (!TryReadDiskPerf(drive.Name, out var read, out var written))
            {
                continue;
            }

            if (_prevDisk.TryGetValue(drive.Name, out var prev))
            {
                var sec = (now - prev.At).TotalSeconds;
                if (sec > 0.2)
                {
                    drive.ReadBytesPerSec = Math.Max(0, (read - prev.Read) / sec);
                    drive.WriteBytesPerSec = Math.Max(0, (written - prev.Written) / sec);
                }
            }

            _prevDisk[drive.Name] = (read, written, now);
        }
    }

    private static bool TryReadDiskPerf(string name, out long bytesRead, out long bytesWritten)
    {
        bytesRead = 0;
        bytesWritten = 0;
        var path = name.EndsWith(':') ? $@"\\.\{name}" : $@"\\.\{name.TrimEnd('\\')}";
        var handle = CreateFile(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle == InvalidHandle || handle == 0)
        {
            return false;
        }

        try
        {
            var size = Math.Max(Marshal.SizeOf<DISK_PERFORMANCE>(), 128);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!DeviceIoControl(handle, IoctlDiskPerformance, 0, 0, buffer, (uint)size, out _, 0))
                {
                    return false;
                }

                var perf = Marshal.PtrToStructure<DISK_PERFORMANCE>(buffer);
                bytesRead = perf.BytesRead;
                bytesWritten = perf.BytesWritten;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
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

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 40)
        {
            return $"{bytes / (double)(1L << 40):0.0} TB";
        }

        if (bytes >= 1L << 30)
        {
            return $"{bytes / (double)(1L << 30):0.0} GB";
        }

        return $"{Math.Max(0, bytes) / (double)(1L << 20):0} MB";
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

    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint IoctlDiskPerformance = 0x00070020;
    private static readonly nint InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct DISK_PERFORMANCE
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        public long StorageManagerName0;
        public long StorageManagerName1;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint hDevice, uint dwIoControlCode, nint lpInBuffer,
        uint nInBufferSize, nint lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}

public sealed class HardwareSnapshot
{
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public double DiskPercent { get; set; }
    public List<DiskDriveInfo> Drives { get; set; } = new();
    public double DiskReadBytesPerSec { get; set; }
    public double DiskWriteBytesPerSec { get; set; }
    public TimeSpan Uptime { get; set; }
    public double DownloadBytesPerSec { get; set; }
    public double UploadBytesPerSec { get; set; }
    public bool IsConnected { get; set; }
    public string NicLabel { get; set; } = "-";
    public string IPv4 { get; set; } = "-";
}

public sealed class DiskDriveInfo
{
    public string Name { get; set; } = "C:";
    public double UsedPercent { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsSystem { get; set; }
    public double ReadBytesPerSec { get; set; }
    public double WriteBytesPerSec { get; set; }
}
