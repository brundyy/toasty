using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace Toasty;

/// <summary>One sensor row for the "All sensors" view.</summary>
public record SensorReading(string Type, string Name, double Value, double? Min, double? Max, string Unit);

public record HardwareGroup(string Name, IReadOnlyList<SensorReading> Sensors);

/// <summary>One physical core. Any field can be missing depending on the CPU vendor.</summary>
public record CoreInfo(int Number, double? Load, double? ClockMhz, double? PowerW, double? TemperatureC);

/// <summary>Installed RAM as reported by SMBIOS (read once; it doesn't change while running).</summary>
public record MemoryModules(int Count, double TotalGb, string? Type, int? SpeedMts, string? PartNumber);

public record DimmTemperature(string Name, double Celsius);

/// <summary>Everything read in one poll. Immutable, so the UI can hold on to it safely.</summary>
public sealed class Snapshot
{
    public static readonly Snapshot Empty = new();

    public Dictionary<MetricId, double?> Values { get; init; } = new();
    public string? CpuName { get; init; }
    public double? CpuPower { get; init; }
    public double? CpuClockMhz { get; init; }
    public string? GpuName { get; init; }
    public double? GpuPower { get; init; }
    public double? GpuClockMhz { get; init; }
    public double? GpuFanRpm { get; init; }
    public double? GpuFanPercent { get; init; }
    public double? VramUsedMb { get; init; }
    public double? VramTotalMb { get; init; }
    public IReadOnlyList<CoreInfo> Cores { get; init; } = [];
    /// <summary>Load of the single busiest hardware thread: the best "is one thread maxed?" signal.</summary>
    public double? BusiestThread { get; init; }
    /// <summary>A USB-connected ("digital") PSU, e.g. Corsair HXi/RMi/AXi, if LibreHardwareMonitor found one.</summary>
    public string? PsuName { get; init; }
    /// <summary>Rated wattage parsed from the PSU model name ("HX1000i" → 1000).</summary>
    public int? PsuRatedWatts { get; init; }
    /// <summary>Whole-system power as measured by that PSU.</summary>
    public double? PsuPowerW { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public double? RamAvailableGb { get; init; }
    public double? RamCachedGb { get; init; }
    public double? CommitUsedGb { get; init; }
    public double? CommitLimitGb { get; init; }
    public double? KernelPagedGb { get; init; }
    public double? KernelNonPagedGb { get; init; }
    public MemoryModules? Modules { get; init; }
    /// <summary>DDR5 (and some DDR4) sticks report a temperature via their SPD hub; only read while the panel is open.</summary>
    public IReadOnlyList<DimmTemperature> DimmTemps { get; init; } = [];
    /// <summary>Only filled while the "All sensors" view is open (it polls extra hardware).</summary>
    public IReadOnlyList<HardwareGroup> All { get; init; } = [];
}

/// <summary>Reads hardware via LibreHardwareMonitor (CPU/GPU/board/drives) and Win32 (RAM).</summary>
public sealed class Sensors : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

    /// <summary>When true, also refresh motherboard and drive sensors (slower; only for the details view).</summary>
    public bool Detailed { get => _visitor.Detailed; set => _visitor.Detailed = value; }

    /// <summary>When true, also refresh RAM sticks (SPD temperatures over SMBus); only while the panel is open.</summary>
    public bool PanelOpen { get => _visitor.PanelOpen; set => _visitor.PanelOpen = value; }

    private static readonly Lazy<MemoryModules?> Modules = new(ReadModules);

    public Sensors()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsMemoryEnabled = true,
            IsPsuEnabled = true,
        };
        _computer.Open();
    }

    public Snapshot Read()
    {
        bool detailed = Detailed;
        _computer.Accept(_visitor);

        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        var gpu = PickGpu();
        var (ramUsed, ramTotal, ramLoad) = Ram();
        var psu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Psu);
        var perf = Performance();
        // Kick off the one-time SMBIOS read in the background so it never stalls a poll.
        if (!Modules.IsValueCreated) _ = Task.Run(() => Modules.Value);

        var coreClocks = AllSensors(cpu)
            .Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #") && s.Value is > 0)
            .Select(s => (double)s.Value!.Value).ToList();

        return new Snapshot
        {
            Values = new()
            {
                [MetricId.CpuTemp] = CpuTemperature(cpu),
                [MetricId.CpuLoad] = Find(cpu, SensorType.Load, "CPU Total"),
                [MetricId.GpuTemp] = Find(gpu, SensorType.Temperature, "GPU Core") ?? First(gpu, SensorType.Temperature),
                [MetricId.GpuLoad] = Find(gpu, SensorType.Load, "GPU Core") ?? Find(gpu, SensorType.Load, "D3D 3D"),
                [MetricId.RamLoad] = ramLoad,
                [MetricId.GpuHotspot] = Find(gpu, SensorType.Temperature, "GPU Hot Spot"),
                [MetricId.GpuMemJunction] = Find(gpu, SensorType.Temperature, "GPU Memory Junction"),
            },
            CpuName = cpu?.Name,
            CpuPower = Find(cpu, SensorType.Power, "Package") ?? Find(cpu, SensorType.Power, "CPU Package"),
            CpuClockMhz = coreClocks.Count > 0 ? coreClocks.Average() : null,
            GpuName = gpu?.Name,
            GpuPower = Find(gpu, SensorType.Power, "GPU Package") ?? First(gpu, SensorType.Power),
            GpuClockMhz = Find(gpu, SensorType.Clock, "GPU Core"),
            GpuFanRpm = First(gpu, SensorType.Fan),
            GpuFanPercent = First(gpu, SensorType.Control),
            VramUsedMb = Find(gpu, SensorType.SmallData, "GPU Memory Used"),
            VramTotalMb = Find(gpu, SensorType.SmallData, "GPU Memory Total"),
            Cores = Cores(cpu),
            BusiestThread = Find(cpu, SensorType.Load, "CPU Core Max")
                ?? AllSensors(cpu).Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #") && s.Value.HasValue)
                                  .Select(s => (double?)s.Value!.Value).DefaultIfEmpty(null).Max(),
            PsuName = psu?.Name,
            PsuRatedWatts = RatedWatts(psu?.Name),
            // The biggest power reading a PSU reports is its total (input or combined output).
            PsuPowerW = AllSensors(psu).Where(s => s.SensorType == SensorType.Power && s.Value is > 0)
                                       .Select(s => (double?)s.Value!.Value).DefaultIfEmpty(null).Max(),
            RamUsedGb = ramUsed,
            RamTotalGb = ramTotal,
            RamAvailableGb = perf?.AvailableGb,
            RamCachedGb = perf?.CachedGb,
            CommitUsedGb = perf?.CommitGb,
            CommitLimitGb = perf?.CommitLimitGb,
            KernelPagedGb = perf?.PagedGb,
            KernelNonPagedGb = perf?.NonPagedGb,
            Modules = Modules.IsValueCreated ? Modules.Value : null,
            DimmTemps = DimmTemps(),
            All = detailed ? Everything() : [],
        };
    }

    /// <summary>
    /// Picks the dedicated card when there's also integrated graphics (e.g. RTX 3080 + Ryzen's
    /// built-in Radeon, where the iGPU is also "GpuAmd" and reports no temperature).
    /// </summary>
    private IHardware? PickGpu() =>
        _computer.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .OrderByDescending(h => h.HardwareType == HardwareType.GpuNvidia)
            .ThenByDescending(h => Find(h, SensorType.SmallData, "GPU Memory Total") ?? 0)
            .ThenByDescending(h => First(h, SensorType.Temperature) != null)
            .ThenByDescending(h => h.HardwareType != HardwareType.GpuIntel)
            .FirstOrDefault();

    private static readonly Regex CoreNumber = new(@"Core #(\d+)", RegexOptions.Compiled);
    private static readonly Regex Wattage = new(@"(?<!\d)(\d{3,4})(?!\d)", RegexOptions.Compiled);

    private static int? RatedWatts(string? model)
    {
        if (model == null) return null;
        foreach (Match m in Wattage.Matches(model))
            if (int.TryParse(m.Groups[1].Value, out int w) && w is >= 300 and <= 2000) return w;
        return null;
    }

    /// <summary>
    /// Groups the CPU's per-core sensors. Names vary by vendor and LHM version:
    /// load is "CPU Core #N" (or per-thread "CPU Core #N Thread #T"), clock "Core #N",
    /// power "Core #N (SMU)" on AMD, temperature "Core #N" / "CPU Core #N" on Intel.
    /// </summary>
    private static List<CoreInfo> Cores(IHardware? cpu)
    {
        var cores = new SortedDictionary<int, (double? Load, List<double> Threads, double? Clock, double? Power, double? Temp)>();
        foreach (var s in AllSensors(cpu))
        {
            if (s.Value is not float value) continue;
            var m = CoreNumber.Match(s.Name);
            if (!m.Success) continue;
            int n = int.Parse(m.Groups[1].Value);
            var c = cores.TryGetValue(n, out var existing) ? existing : (null, new List<double>(), null, null, null);
            switch (s.SensorType)
            {
                case SensorType.Load when s.Name.Contains("Thread"): c.Threads.Add(value); break;
                case SensorType.Load: c.Load = value; break;
                case SensorType.Clock when !s.Name.Contains("Effective"): c.Clock ??= value; break;
                case SensorType.Power: c.Power ??= value; break;
                case SensorType.Temperature when !s.Name.Contains("Distance"): c.Temp ??= value; break;
            }
            cores[n] = c;
        }
        // With SMT, some CPUs (e.g. Ryzen) report load per *thread* as "CPU Core #1..#16" while
        // clocks and power are per physical core "#1..#8". Fold each pair of threads into its core.
        int physical = cores.Count(kv => kv.Value.Clock != null || kv.Value.Power != null);
        int loads = cores.Count(kv => kv.Value.Load != null);
        if (physical > 0 && loads == physical * 2)
        {
            for (int core = 1; core <= physical; core++)
            {
                var t1 = cores.GetValueOrDefault(core * 2 - 1).Load;
                var t2 = cores.GetValueOrDefault(core * 2).Load;
                var c = cores[core];
                c.Load = t1 is double a && t2 is double b ? (a + b) / 2 : t1 ?? t2;
                cores[core] = c;
            }
            foreach (int extra in cores.Keys.Where(k => k > physical).ToList()) cores.Remove(extra);
        }

        return cores
            .Select(kv => new CoreInfo(kv.Key, kv.Value.Load ?? (kv.Value.Threads.Count > 0 ? kv.Value.Threads.Average() : null),
                kv.Value.Clock, kv.Value.Power, kv.Value.Temp))
            .Where(c => c.Load != null || c.ClockMhz != null)
            .ToList();
    }

    private static readonly string[] LimitWords = ["limit", "threshold", "critical", "warning", "high", "low", "max", "min"];

    /// <summary>
    /// One reading per RAM stick. Each stick's SPD hub also exposes its alarm limits
    /// (e.g. 0 / 55 / 85°C) as temperature sensors; skip those and keep the actual reading.
    /// </summary>
    private List<DimmTemperature> DimmTemps()
    {
        var result = new List<DimmTemperature>();
        foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Memory).SelectMany(h => new[] { h }.Concat(h.SubHardware)))
        {
            var reading = hw.Sensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0)
                .Where(s => !LimitWords.Any(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s.Index)
                .FirstOrDefault();
            if (reading != null) result.Add(new DimmTemperature(hw.Name, reading.Value!.Value));
        }
        return result;
    }

    private static MemoryModules? ReadModules()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType, PartNumber FROM Win32_PhysicalMemory");
            var sticks = searcher.Get().Cast<ManagementObject>().ToList();
            if (sticks.Count == 0) return null;
            double total = sticks.Sum(s => Convert.ToDouble(s["Capacity"] ?? 0)) / (1024d * 1024 * 1024);
            int? speed = sticks.Select(s => Convert.ToInt32(s["ConfiguredClockSpeed"] ?? s["Speed"] ?? 0)).Where(v => v > 0).DefaultIfEmpty().Max();
            string? type = Convert.ToInt32(sticks[0]["SMBIOSMemoryType"] ?? 0) switch
            {
                34 => "DDR5", 26 => "DDR4", 24 => "DDR3", 35 => "LPDDR5", 30 => "LPDDR4", _ => null,
            };
            var parts = sticks.Select(s => (s["PartNumber"] as string)?.Trim()).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            return new MemoryModules(sticks.Count, total, type, speed > 0 ? speed : null, parts.Count == 1 ? parts[0] : null);
        }
        catch
        {
            return null;
        }
    }

    private static double? CpuTemperature(IHardware? cpu)
    {
        // Intel reports "CPU Package"; AMD reports "Core (Tctl/Tdie)" or similar.
        foreach (var name in new[] { "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "Core Max" })
        {
            var v = Find(cpu, SensorType.Temperature, name);
            if (v != null) return v;
        }
        // Fall back to the hottest temperature sensor the CPU exposes.
        var temps = AllSensors(cpu).Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0)
                                   .Select(s => (double)s.Value!.Value).ToList();
        return temps.Count > 0 ? temps.Max() : null;
    }

    private List<HardwareGroup> Everything()
    {
        var groups = new List<HardwareGroup>();
        void Add(IHardware hw)
        {
            var rows = hw.Sensors
                .Where(s => s.Value.HasValue)
                .OrderBy(s => s.SensorType).ThenBy(s => s.Index)
                .Select(s => new SensorReading(s.SensorType.ToString(), s.Name, s.Value!.Value, s.Min, s.Max, UnitOf(s.SensorType)))
                .ToList();
            if (rows.Count > 0) groups.Add(new HardwareGroup(hw.Name, rows));
            foreach (var sub in hw.SubHardware) Add(sub);
        }
        foreach (var hw in _computer.Hardware) Add(hw);
        return groups;
    }

    private static string UnitOf(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => "%",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        SensorType.TimeSpan => "s",
        _ => "",
    };

    private static double? Find(IHardware? hw, SensorType type, string name) =>
        AllSensors(hw).FirstOrDefault(s => s.SensorType == type && s.Name == name && s.Value.HasValue)?.Value;

    private static double? First(IHardware? hw, SensorType type) =>
        AllSensors(hw).FirstOrDefault(s => s.SensorType == type && s.Value.HasValue)?.Value;

    private static IEnumerable<ISensor> AllSensors(IHardware? hw) =>
        hw == null ? [] : hw.Sensors.Concat(hw.SubHardware.SelectMany(AllSensors));

    private static (double UsedGb, double TotalGb, double? Load) Ram()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0, null);
        const double gb = 1024d * 1024 * 1024;
        return ((status.TotalPhys - status.AvailPhys) / gb, status.TotalPhys / gb, status.MemoryLoad);
    }

    private record Perf(double AvailableGb, double CachedGb, double CommitGb, double CommitLimitGb, double PagedGb, double NonPagedGb);

    private static Perf? Performance()
    {
        var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GetPerformanceInfo(ref info, info.Size)) return null;
        double page = (double)(ulong)info.PageSize / (1024d * 1024 * 1024);
        double Gb(UIntPtr pages) => (ulong)pages * page;
        return new Perf(Gb(info.PhysicalAvailable), Gb(info.SystemCache), Gb(info.CommitTotal), Gb(info.CommitLimit),
            Gb(info.KernelPaged), Gb(info.KernelNonpaged));
    }

    public void Dispose() => _computer.Close();

    private sealed class UpdateVisitor : IVisitor
    {
        public volatile bool Detailed;
        public volatile bool PanelOpen;

        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            // Board (Super I/O) and drive (SMART) polling is comparatively slow; skip it unless
            // someone is actually looking at the full sensor list.
            bool core = hardware.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia
                or HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.Psu;
            bool memory = hardware.HardwareType == HardwareType.Memory;
            if (!core && !(memory && (PanelOpen || Detailed)) && !Detailed) return;

            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);
}
