using System.Text.Json;
using System.Text.Json.Serialization;

namespace Toasty;

public enum MetricId { CpuTemp, CpuLoad, GpuTemp, GpuLoad, RamLoad, GpuHotspot, GpuMemJunction }

/// <summary>How a metric is drawn in its tray icon.</summary>
public enum IconStyle { LabelAndNumber, NumberOnly, NumberAndBar, LabelAndBar, Meter, Dot }

public static class IconStyles
{
    public static readonly IconStyle[] All =
        [IconStyle.LabelAndNumber, IconStyle.NumberOnly, IconStyle.NumberAndBar, IconStyle.LabelAndBar, IconStyle.Meter, IconStyle.Dot];

    public static string Label(IconStyle style) => style switch
    {
        IconStyle.LabelAndNumber => "Label + number",
        IconStyle.NumberOnly => "Number only",
        IconStyle.NumberAndBar => "Number + level bar",
        IconStyle.LabelAndBar => "Label + level bar",
        IconStyle.Meter => "Meter (no number)",
        IconStyle.Dot => "Colour dot (no number)",
        _ => style.ToString(),
    };
}

public static class Metrics
{
    public static readonly MetricId[] All =
    [
        MetricId.CpuTemp, MetricId.CpuLoad, MetricId.GpuTemp, MetricId.GpuLoad, MetricId.RamLoad,
        MetricId.GpuHotspot, MetricId.GpuMemJunction,
    ];

    public static bool IsTemperature(MetricId id) =>
        id is MetricId.CpuTemp or MetricId.GpuTemp or MetricId.GpuHotspot or MetricId.GpuMemJunction;

    public static string Label(MetricId id) => id switch
    {
        MetricId.CpuTemp => "CPU temperature",
        MetricId.CpuLoad => "CPU usage",
        MetricId.GpuTemp => "GPU temperature",
        MetricId.GpuLoad => "GPU usage",
        MetricId.RamLoad => "RAM usage",
        MetricId.GpuHotspot => "GPU hotspot",
        MetricId.GpuMemJunction => "GPU memory junction",
        _ => id.ToString(),
    };

    /// <summary>The tiny pixel-font tag drawn above the number on a tray icon.</summary>
    public static string Tag(MetricId id) => id switch
    {
        MetricId.CpuTemp => "CPU°",
        MetricId.CpuLoad => "CPU%",
        MetricId.GpuTemp => "GPU°",
        MetricId.GpuLoad => "GPU%",
        MetricId.RamLoad => "RAM",
        MetricId.GpuHotspot => "HOT°",
        MetricId.GpuMemJunction => "MEM",
        _ => "",
    };

    public static string Unit(MetricId id) => IsTemperature(id) ? "°C" : "%";
}

/// <summary>
/// Five colour bands (cold, cool, warm, hot, critical) split by four ascending thresholds.
/// A value below Thresholds[0] uses Colors[0], below Thresholds[1] uses Colors[1], and so on.
/// </summary>
public class MetricSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Overrides the app-wide icon style for this metric; null = use the default.</summary>
    public IconStyle? Style { get; set; }
    public double[] Thresholds { get; set; } = [];
    public string[] Colors { get; set; } = [];

    /// <summary>
    /// How "full" a value is, 0..1, for bars and meters. Usage is simply 0-100%; temperatures
    /// span from a little below the first threshold to a little above the last, so the bar
    /// tracks the colour bands.
    /// </summary>
    public double Fraction(double value, bool temperature)
    {
        if (!temperature) return Math.Clamp(value / 100.0, 0, 1);
        double lo = Thresholds[0] - 15, hi = Thresholds[^1] + 10;
        return Math.Clamp((value - lo) / (hi - lo), 0, 1);
    }

    public int BandFor(double value)
    {
        int band = 0;
        while (band < Thresholds.Length && value >= Thresholds[band]) band++;
        return Math.Min(band, Colors.Length - 1);
    }

    public Color ColorOfBand(int band) => ColorTranslator.FromHtml(Colors[Math.Clamp(band, 0, Colors.Length - 1)]);

    public Color ColorFor(double value) => ColorOfBand(BandFor(value));

    public MetricSettings Clone() => new()
    {
        Enabled = Enabled,
        Style = Style,
        Thresholds = (double[])Thresholds.Clone(),
        Colors = (string[])Colors.Clone(),
    };
}

/// <summary>"Notify me when [metric] stays at or above [Above] for [ForSeconds]".</summary>
public class AlertRule
{
    public bool Enabled { get; set; } = true;
    public MetricId Metric { get; set; }
    public double Above { get; set; }
    public int ForSeconds { get; set; } = 30;

    public AlertRule Clone() => new() { Enabled = Enabled, Metric = Metric, Above = Above, ForSeconds = ForSeconds };
}

public class AppSettings
{
    public const int BandCount = 5;
    public static readonly string[] BandNames = ["Cold", "Cool", "Warm", "Hot", "Critical"];
    public static readonly string[] DefaultColors = ["#22D3EE", "#4ADE80", "#FACC15", "#FB923C", "#EF4444"];

    public int IntervalMs { get; set; } = 1000;
    /// <summary>Numbers and colours use the average over this many seconds (0 = raw readings).</summary>
    public int SmoothingSeconds { get; set; } = 3;
    public IconStyle IconStyle { get; set; } = IconStyle.LabelAndNumber;
    /// <summary>Pre-0.3 setting, read once and folded into IconStyle; never written back.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowLabels { get; set; }
    public bool SolidBackground { get; set; } = false;
    public Dictionary<MetricId, MetricSettings> Metrics { get; set; } = new();
    public List<AlertRule> Alerts { get; set; } = DefaultAlerts();
    /// <summary>Minimum gap between repeat notifications for the same rule.</summary>
    public int AlertCooldownMinutes { get; set; } = 10;
    /// <summary>Remembered open/closed state of the panel's collapsible sections.</summary>
    public bool PanelCoresExpanded { get; set; } = true;
    /// <summary>Memory "Details" starts collapsed (renamed from PanelMemoryExpanded so existing installs pick up the new default).</summary>
    public bool PanelMemoryDetailsOpen { get; set; } = false;

    /// <summary>Detached ("pinned") panel: a normal window that stays open wherever it was dragged.</summary>
    public bool PanelPinned { get; set; } = false;
    public int? PanelX { get; set; }
    public int? PanelY { get; set; }
    /// <summary>Whether the detached panel was open, so it can come back after a restart.</summary>
    public bool PanelPinnedOpen { get; set; } = false;
    public bool PanelOnTop { get; set; } = true;
    /// <summary>Height the user dragged the panel to, in 96-DPI pixels (null = fit the content).</summary>
    public int? PanelHeight { get; set; }

    // ---- Game sessions ----
    public bool RecordSessions { get; set; } = true;
    /// <summary>Use PresentMon for FPS, 1% lows, stutters and per-frame CPU/GPU-bound classification.</summary>
    public bool CaptureFrames { get; set; } = true;
    /// <summary>Delete sessions older than this many days (0 = keep forever).</summary>
    public int SessionRetentionDays { get; set; } = 90;
    /// <summary>Cap on disk used by session files; the oldest are deleted first.</summary>
    public int SessionMaxMb { get; set; } = 200;
    /// <summary>Store a graph timeline with each session (the bulk of each file).</summary>
    public bool SessionTimeline { get; set; } = true;
    public int SessionTimelineSeconds { get; set; } = 5;
    /// <summary>Sessions shorter than this (time actually in the game) aren't saved.</summary>
    public int SessionMinMinutes { get; set; } = 2;
    /// <summary>Exe names (e.g. "game.exe") to always / never treat as games.</summary>
    public List<string> AlwaysTrack { get; set; } = [];
    public List<string> NeverTrack { get; set; } = [];

    // ---- Power ----
    /// <summary>The power supply's rated wattage (0 = not set). Windows can't read this, so the user enters it.</summary>
    public int PsuWatts { get; set; } = 0;
    /// <summary>Allowance for everything that isn't the CPU or GPU (board, RAM, drives, fans, USB).</summary>
    public int OtherComponentsWatts { get; set; } = 60;

    /// <summary>
    /// Whole-system draw: measured by a digital PSU when there is one, otherwise estimated as
    /// CPU package + GPU board power + the allowance. Null without the readings it needs.
    /// </summary>
    public double? EstimateSystemWatts(Snapshot s) =>
        s.PsuPowerW ?? (s.CpuPower is double cpu && s.GpuPower is double gpu ? cpu + gpu + OtherComponentsWatts : null);

    /// <summary>The PSU rating to compare against: the user's entry, else one detected from a digital PSU (0 = unknown).</summary>
    public int EffectivePsuWatts(Snapshot s) => PsuWatts > 0 ? PsuWatts : s.PsuRatedWatts ?? 0;

    [JsonIgnore]
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toasty", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static MetricSettings DefaultsFor(MetricId id) => new()
    {
        // The original five show by default; the extra GPU temperatures are opt-in.
        Enabled = id is not (MetricId.GpuHotspot or MetricId.GpuMemJunction),
        Thresholds = id switch
        {
            MetricId.GpuHotspot => [55, 70, 85, 95],
            // GDDR6X memory runs hot by design; throttles around 110.
            MetricId.GpuMemJunction => [60, 80, 95, 105],
            _ when Toasty.Metrics.IsTemperature(id) => [45, 60, 75, 85],
            _ => [20, 50, 70, 90],
        },
        Colors = (string[])DefaultColors.Clone(),
    };

    public static List<AlertRule> DefaultAlerts() =>
    [
        new() { Metric = MetricId.CpuTemp, Above = 85, ForSeconds = 30 },
        new() { Metric = MetricId.GpuTemp, Above = 83, ForSeconds = 30 },
        new() { Metric = MetricId.GpuMemJunction, Above = 100, ForSeconds = 60 },
    ];

    public static AppSettings Defaults()
    {
        var s = new AppSettings();
        foreach (var id in Toasty.Metrics.All) s.Metrics[id] = DefaultsFor(id);
        return s;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded != null) return loaded.Repaired();
            }
        }
        catch
        {
            // A corrupt settings file shouldn't stop the app; fall back to defaults.
        }
        return Defaults();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public AppSettings Clone() => new()
    {
        IntervalMs = IntervalMs,
        SmoothingSeconds = SmoothingSeconds,
        IconStyle = IconStyle,
        SolidBackground = SolidBackground,
        Metrics = Metrics.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
        Alerts = Alerts.Select(a => a.Clone()).ToList(),
        AlertCooldownMinutes = AlertCooldownMinutes,
        PanelCoresExpanded = PanelCoresExpanded,
        PanelMemoryDetailsOpen = PanelMemoryDetailsOpen,
        PanelPinned = PanelPinned,
        PanelX = PanelX,
        PanelY = PanelY,
        PanelPinnedOpen = PanelPinnedOpen,
        PanelOnTop = PanelOnTop,
        PanelHeight = PanelHeight,
        RecordSessions = RecordSessions,
        CaptureFrames = CaptureFrames,
        SessionRetentionDays = SessionRetentionDays,
        SessionMaxMb = SessionMaxMb,
        SessionTimeline = SessionTimeline,
        SessionTimelineSeconds = SessionTimelineSeconds,
        SessionMinMinutes = SessionMinMinutes,
        AlwaysTrack = [.. AlwaysTrack],
        NeverTrack = [.. NeverTrack],
        PsuWatts = PsuWatts,
        OtherComponentsWatts = OtherComponentsWatts,
    };

    /// <summary>Fills in anything missing or malformed (e.g. a hand-edited file, or one from an older version).</summary>
    private AppSettings Repaired()
    {
        IntervalMs = Math.Clamp(IntervalMs, 250, 10_000);
        SmoothingSeconds = Math.Clamp(SmoothingSeconds, 0, 60);
        AlertCooldownMinutes = Math.Clamp(AlertCooldownMinutes, 1, 1440);
        SessionRetentionDays = Math.Clamp(SessionRetentionDays, 0, 3650);
        SessionMaxMb = Math.Clamp(SessionMaxMb, 1, 100_000);
        SessionTimelineSeconds = Math.Clamp(SessionTimelineSeconds, 1, 60);
        SessionMinMinutes = Math.Clamp(SessionMinMinutes, 0, 120);
        AlwaysTrack ??= [];
        PsuWatts = Math.Clamp(PsuWatts, 0, 5000);
        OtherComponentsWatts = Math.Clamp(OtherComponentsWatts, 0, 1000);
        NeverTrack ??= [];
        Alerts ??= DefaultAlerts();
        if (ShowLabels == false) IconStyle = IconStyle.NumberOnly;
        ShowLabels = null;
        if (!Enum.IsDefined(IconStyle)) IconStyle = IconStyle.LabelAndNumber;
        foreach (var m in Metrics.Values) if (m.Style is IconStyle s && !Enum.IsDefined(s)) m.Style = null;
        foreach (var id in Toasty.Metrics.All)
        {
            if (!Metrics.TryGetValue(id, out var m)
                || m.Thresholds.Length != BandCount - 1
                || m.Colors.Length != BandCount
                || !m.Colors.All(IsValidColor))
            {
                var fresh = DefaultsFor(id);
                if (m != null) fresh.Enabled = m.Enabled;
                Metrics[id] = fresh;
            }
        }
        return this;
    }

    private static bool IsValidColor(string hex)
    {
        try { ColorTranslator.FromHtml(hex); return true; }
        catch { return false; }
    }
}
