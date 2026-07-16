using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

internal readonly record struct HostHardwareInfo(
    string ProcessorModel,
    int LogicalProcessorCount,
    int PhysicalCoreCount,
    long TotalMemoryBytes);

internal static class HostEnvironmentProbe
{
    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    internal static HostHardwareInfo Capture()
    {
        int logicalProcessorCount = Environment.ProcessorCount;
        string processorModel = GetProcessorModel();
        int physicalCoreCount = GetPhysicalCoreCount();
        if (physicalCoreCount <= 0 || physicalCoreCount > logicalProcessorCount)
        {
            physicalCoreCount = 0;
        }
        long totalMemoryBytes = GetTotalMemoryBytes();
        return new HostHardwareInfo(
            processorModel,
            logicalProcessorCount,
            physicalCoreCount,
            totalMemoryBytes);
    }

    private static string GetProcessorModel()
    {
        string? value = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                value = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString",
                    null) as string;
            }
            catch
            {
                // The process environment still provides a stable processor identifier.
            }

            value ??= Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        }
        else if (OperatingSystem.IsLinux())
        {
            value = ReadLinuxCpuModel();
        }
        else if (OperatingSystem.IsMacOS())
        {
            value = RunAndRead("sysctl", "-n", "machdep.cpu.brand_string")
                ?? RunAndRead("sysctl", "-n", "hw.model");
        }

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }

    private static string? ReadLinuxCpuModel()
    {
        const string CpuInfoPath = "/proc/cpuinfo";
        if (!File.Exists(CpuInfoPath))
        {
            return null;
        }

        foreach (string line in File.ReadLines(CpuInfoPath))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            if (!key.Equals("model name", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("hardware", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("cpu model", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string model = line[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(model))
            {
                return model;
            }
        }

        return null;
    }

    private static int GetPhysicalCoreCount()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                int windowsCount = GetWindowsPhysicalCoreCount();
                return windowsCount;
            }

            if (OperatingSystem.IsLinux())
            {
                int linuxCount = GetLinuxPhysicalCoreCount();
                return linuxCount;
            }

            if (OperatingSystem.IsMacOS()
                && int.TryParse(
                    RunAndRead("sysctl", "-n", "hw.physicalcpu"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int macCount)
                && macCount > 0)
            {
                return macCount;
            }
        }
        catch
        {
            // Qualification must fail closed rather than label a logical-
            // processor fallback as a measured physical-core count.
        }

        return 0;
    }

    private static int GetWindowsPhysicalCoreCount()
    {
        uint bytes = 0;
        if (GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref bytes)
            || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer
            || bytes < 8)
        {
            return 0;
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref bytes))
            {
                return 0;
            }

            int count = 0;
            int offset = 0;
            while (offset + 8 <= bytes)
            {
                int relationship = Marshal.ReadInt32(buffer, offset);
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || offset + size > bytes)
                {
                    return 0;
                }

                if (relationship == RelationProcessorCore)
                {
                    count++;
                }

                offset += size;
            }

            return offset == bytes ? count : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int GetLinuxPhysicalCoreCount()
    {
        const string CpuRoot = "/sys/devices/system/cpu";
        if (!Directory.Exists(CpuRoot))
        {
            return 0;
        }

        HashSet<int>? available = ReadLinuxAllowedCpuSet();
        var cores = new HashSet<(int Package, int Core)>();
        foreach (string directory in Directory.EnumerateDirectories(CpuRoot, "cpu*"))
        {
            string suffix = Path.GetFileName(directory).AsSpan(3).ToString();
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int cpu)
                || cpu < 0
                || (available is not null && !available.Contains(cpu)))
            {
                continue;
            }

            string packagePath = Path.Combine(directory, "topology", "physical_package_id");
            string corePath = Path.Combine(directory, "topology", "core_id");
            if (!int.TryParse(File.ReadAllText(packagePath).Trim(), out int package)
                || !int.TryParse(File.ReadAllText(corePath).Trim(), out int core))
            {
                return 0;
            }

            cores.Add((package, core));
        }

        return cores.Count;
    }

    private static HashSet<int>? ReadLinuxAllowedCpuSet()
    {
        const string StatusPath = "/proc/self/status";
        try
        {
            string? value = File.ReadLines(StatusPath)
                .FirstOrDefault(static line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal));
            if (value is null)
            {
                return null;
            }

            var cpus = new HashSet<int>();
            foreach (string item in value[(value.IndexOf(':') + 1)..].Split(',', StringSplitOptions.TrimEntries))
            {
                string[] limits = item.Split('-', 2, StringSplitOptions.TrimEntries);
                if (!int.TryParse(limits[0], NumberStyles.None, CultureInfo.InvariantCulture, out int first)
                    || first < 0
                    || (limits.Length == 2
                        && (!int.TryParse(limits[1], NumberStyles.None, CultureInfo.InvariantCulture, out int last)
                            || last < first)))
                {
                    return null;
                }

                int final = limits.Length == 1
                    ? first
                    : int.Parse(limits[1], NumberStyles.None, CultureInfo.InvariantCulture);
                for (int cpu = first; cpu <= final; cpu++)
                {
                    cpus.Add(cpu);
                    if (cpu == int.MaxValue)
                    {
                        break;
                    }
                }
            }

            return cpus.Count == 0 ? null : cpus;
        }
        catch
        {
            return null;
        }
    }

    private static long GetTotalMemoryBytes()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx
                {
                    Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
                };
                if (GlobalMemoryStatusEx(ref status) && status.TotalPhysical <= long.MaxValue)
                {
                    return (long)status.TotalPhysical;
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string digits = new(line.Where(char.IsDigit).ToArray());
                    if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long kibibytes)
                        && kibibytes > 0)
                    {
                        return checked(kibibytes * 1024);
                    }
                }
            }
            else if (OperatingSystem.IsMacOS()
                && long.TryParse(
                    RunAndRead("sysctl", "-n", "hw.memsize"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long macBytes)
                && macBytes > 0)
            {
                return macBytes;
            }
        }
        catch
        {
            // Qualification must fail closed rather than label a process
            // memory bound as total host memory.
        }

        return 0;
    }

    private static string? RunAndRead(string fileName, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            BoundedProcessResult result = BoundedProcessRunner.Run(
                start,
                TimeSpan.FromSeconds(2));
            return result.Succeeded ? result.StandardOutput.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
