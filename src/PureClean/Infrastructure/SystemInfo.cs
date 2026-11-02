using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PureClean.Infrastructure;

public sealed record MemoryInfo(ulong Total, ulong Available)
{
    public double UsedPercent => Total == 0 ? 0 : 100.0 * (Total - Available) / Total;
}

public static class SystemInfo
{
    public static MemoryInfo Memory()
    {
        var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status)
            ? new MemoryInfo(status.ullTotalPhys, status.ullAvailPhys)
            : new MemoryInfo(0, 0);
    }

    /// <summary>Ex. « Windows 11 Professionnel 24H2 (build 26100) ».</summary>
    public static string WindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        var display = key?.GetValue("DisplayVersion") as string;
        var build = key?.GetValue("CurrentBuildNumber") as string;

        // Windows 11 indique encore « Windows 10 » dans ProductName : seul le numéro de build fait foi.
        if (int.TryParse(build, out var number) && number >= 22000)
            product = product.Replace("Windows 10", "Windows 11");

        return $"{product}{(display is null ? "" : " " + display)}{(build is null ? "" : $" (build {build})")}";
    }

    public static string Uptime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays} j {uptime.Hours} h"
            : uptime.TotalHours >= 1 ? $"{(int)uptime.TotalHours} h {uptime.Minutes} min"
            : $"{uptime.Minutes} min";
    }
}
