using System.Diagnostics;

namespace Toasty;

/// <summary>
/// Detects games, records sensor stats (and PresentMon frame stats) while they're in the
/// foreground, and saves a session with a bottleneck verdict when the game exits.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    public event Action<GameSession>? Saved;
    /// <summary>Raised when a session ends without being saved (too short, or the save failed), with the reason.</summary>
    public event Action<string>? NotSaved;

    /// <summary>What happened to the last session, shown on the Games tab.</summary>
    public string? LastResult { get; private set; }

    private readonly SessionStore _store;
    private readonly GameDetector _detector = new();
    private Active? _active;

    public SessionRecorder(SessionStore store) => _store = store;

    /// <summary>Why the last app in front is or isn't being treated as a game.</summary>
    public string DetectionStatus => _detector.Status;

    /// <summary>The last non-ignored app that was in front, for "Record now".</summary>
    public GameProcess? LastCandidate => _detector.LastCandidate is { } g && GameDetector.IsRunning(g.Pid) ? g : null;

    /// <summary>Start recording the last app that was in front, without waiting for detection.</summary>
    public void StartManual(AppSettings settings)
    {
        if (_active == null && LastCandidate is { } g) Begin(g, settings);
    }

    /// <summary>What's being recorded right now, for the Games tab.</summary>
    public (string Game, TimeSpan Played, double? Fps, bool Frames)? Live =>
        _active is { } a ? (a.Game.Name, TimeSpan.FromSeconds(a.PlayedSeconds), a.LastFps, a.Capture?.Running == true) : null;

    /// <summary>Running min/avg/max accumulator.</summary>
    private sealed class Acc
    {
        public double Min = double.MaxValue, Max = double.MinValue, Sum;
        public long Count;
        public void Add(double v)
        {
            if (!double.IsFinite(v)) return; // sensors occasionally report NaN; never let it reach the averages (or the file)
            Min = Math.Min(Min, v); Max = Math.Max(Max, v); Sum += v; Count++;
        }
        public Stat? ToStat() => Count == 0 ? null : new Stat(Math.Round(Min, 1), Math.Round(Sum / Count, 1), Math.Round(Max, 1));
    }

    private sealed class Active
    {
        public required GameProcess Game;
        public DateTime Start;
        public double PlayedSeconds;
        public FrameCapture? Capture;
        public double? LastFps;
        public Process? Proc;
        public TimeSpan LastCpuTime;
        public DateTime LastCpuAt;
        public readonly Dictionary<string, Acc> Stats = new();
        public string? CpuName, GpuName;
        public double? VramTotalGb, RamTotalGb;
        public int PsuWatts;
        public bool PowerMeasured;

        // Per-second shares for the sensor-only verdict (used when there's no frame data).
        public long Seconds, GpuBoundSec, CpuBoundSec, LightSec;
        public long HotCpuSec, HotGpuSec, HotHotspotSec, HotMemSec;

        // Timeline
        public int TimelineInterval;
        public Timeline? Timeline;
        public readonly Acc WinFps = new(), WinGpu = new(), WinCore = new(), WinCpuT = new(), WinGpuT = new();
        public DateTime WinStart;

        public void Add(string key, double? v) { if (v is double d) (Stats.TryGetValue(key, out var a) ? a : Stats[key] = new Acc()).Add(d); }
    }

    /// <summary>Call once per poll with the latest (raw) snapshot.</summary>
    public void Tick(Snapshot snap, AppSettings settings)
    {
        if (!settings.RecordSessions)
        {
            if (_active != null) Finish(settings);
            return;
        }

        if (_active == null)
        {
            var game = _detector.Check(snap.Values.GetValueOrDefault(MetricId.GpuLoad), settings.AlwaysTrack, settings.NeverTrack);
            if (game != null) Begin(game, settings);
            return;
        }

        var a = _active;
        if (!GameDetector.IsRunning(a.Game.Pid))
        {
            Finish(settings);
            return;
        }

        // Only count time the game is actually in front: alt-tabbed minutes would skew everything.
        if (GameDetector.ForegroundPid() != a.Game.Pid) return;

        var now = DateTime.Now;
        a.PlayedSeconds += settings.IntervalMs / 1000.0;
        a.Seconds++;
        a.CpuName ??= snap.CpuName;
        a.GpuName ??= snap.GpuName;
        if (snap.VramTotalMb is double vt) a.VramTotalGb = vt / 1024;
        if (snap.RamTotalGb > 0) a.RamTotalGb = snap.RamTotalGb;

        var v = snap.Values;
        double? busiest = snap.BusiestThread ?? (snap.Cores.Count > 0 ? snap.Cores.Max(c => c.Load ?? 0) : null);
        a.Add("CpuTemp", v.GetValueOrDefault(MetricId.CpuTemp));
        a.Add("CpuLoad", v.GetValueOrDefault(MetricId.CpuLoad));
        a.Add("BusiestCore", busiest);
        a.Add("CpuClock", snap.CpuClockMhz);
        a.Add("CpuPower", snap.CpuPower);
        a.Add("GpuTemp", v.GetValueOrDefault(MetricId.GpuTemp));
        a.Add("GpuHotspot", v.GetValueOrDefault(MetricId.GpuHotspot));
        a.Add("GpuMemJunction", v.GetValueOrDefault(MetricId.GpuMemJunction));
        a.Add("GpuLoad", v.GetValueOrDefault(MetricId.GpuLoad));
        a.Add("GpuClock", snap.GpuClockMhz);
        a.Add("GpuPower", snap.GpuPower);
        a.Add("VramGb", snap.VramUsedMb / 1024);
        a.Add("RamGb", snap.RamTotalGb > 0 ? snap.RamUsedGb : null);
        a.Add("RamLoad", v.GetValueOrDefault(MetricId.RamLoad));
        a.Add("CommitGb", snap.CommitUsedGb);
        a.Add("GameCpu", GameCpuPercent(a, now));
        a.Add("SystemPowerW", settings.EstimateSystemWatts(snap));
        if (settings.EffectivePsuWatts(snap) is int psuW and > 0) a.PsuWatts = psuW;
        if (snap.PsuPowerW != null) a.PowerMeasured = true;

        double? fps = a.Capture?.TakeIntervalFps();
        if (fps != null) a.LastFps = fps;
        a.Add("Fps", fps);

        // Sensor-only bottleneck shares (frame data, when available, takes precedence).
        double gpu = v.GetValueOrDefault(MetricId.GpuLoad) ?? 0;
        double core = busiest ?? 0;
        if (gpu >= 95) a.GpuBoundSec++;
        else if (gpu < 85 && core >= 90) a.CpuBoundSec++;
        else if (gpu < 70 && core < 80) a.LightSec++;

        // Time spent at or above each metric's "critical" colour threshold.
        bool Hot(MetricId id) => v.GetValueOrDefault(id) is double t && t >= settings.Metrics[id].Thresholds[^1];
        if (Hot(MetricId.CpuTemp)) a.HotCpuSec++;
        if (Hot(MetricId.GpuTemp)) a.HotGpuSec++;
        if (Hot(MetricId.GpuHotspot)) a.HotHotspotSec++;
        if (Hot(MetricId.GpuMemJunction)) a.HotMemSec++;

        if (a.Timeline != null)
        {
            if (fps is double f) a.WinFps.Add(f);
            if (v.GetValueOrDefault(MetricId.GpuLoad) is double gl) a.WinGpu.Add(gl);
            if (busiest is double b) a.WinCore.Add(b);
            if (v.GetValueOrDefault(MetricId.CpuTemp) is double ct) a.WinCpuT.Add(ct);
            if (v.GetValueOrDefault(MetricId.GpuTemp) is double gt) a.WinGpuT.Add(gt);
            if ((now - a.WinStart).TotalSeconds >= a.TimelineInterval) FlushWindow(a, now);
        }
    }

    private void Begin(GameProcess game, AppSettings settings)
    {
        var a = new Active
        {
            Game = game,
            Start = DateTime.Now,
            TimelineInterval = settings.SessionTimelineSeconds,
            WinStart = DateTime.Now,
        };
        if (settings.SessionTimeline)
            a.Timeline = new Timeline(settings.SessionTimelineSeconds, [], [], [], [], []);
        try
        {
            a.Proc = Process.GetProcessById(game.Pid);
            a.LastCpuTime = a.Proc.TotalProcessorTime;
            a.LastCpuAt = DateTime.Now;
        }
        catch { a.Proc = null; }
        if (settings.CaptureFrames)
        {
            a.Capture = new FrameCapture();
            a.Capture.Start(game.Pid);
        }
        _active = a;
    }

    /// <summary>The game's own CPU use, as a percentage of the whole CPU.</summary>
    private static double? GameCpuPercent(Active a, DateTime now)
    {
        if (a.Proc == null) return null;
        try
        {
            a.Proc.Refresh();
            var cpu = a.Proc.TotalProcessorTime;
            double wall = (now - a.LastCpuAt).TotalMilliseconds;
            double used = (cpu - a.LastCpuTime).TotalMilliseconds;
            a.LastCpuTime = cpu;
            a.LastCpuAt = now;
            return wall > 0 ? Math.Clamp(100 * used / (wall * Environment.ProcessorCount), 0, 100) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void FlushWindow(Active a, DateTime now)
    {
        static double? Avg(Acc w) => w.Count > 0 ? Math.Round(w.Sum / w.Count, 1) : null;
        var t = a.Timeline!;
        t.Fps.Add(Avg(a.WinFps));
        t.GpuLoad.Add(Avg(a.WinGpu));
        t.BusiestCore.Add(Avg(a.WinCore));
        t.CpuTemp.Add(Avg(a.WinCpuT));
        t.GpuTemp.Add(Avg(a.WinGpuT));
        foreach (var w in new[] { a.WinFps, a.WinGpu, a.WinCore, a.WinCpuT, a.WinGpuT })
        {
            w.Min = double.MaxValue; w.Max = double.MinValue; w.Sum = 0; w.Count = 0;
        }
        a.WinStart = now;
    }

    /// <summary>Ends the current session and saves it if it was long enough. Safe to call when idle.</summary>
    public void Finish(AppSettings settings)
    {
        var a = _active;
        if (a == null) return;
        _active = null;
        a.Capture?.Stop();
        var frames = a.Capture?.Summary();
        a.Capture?.Dispose();
        a.Proc?.Dispose();

        if (a.PlayedSeconds < settings.SessionMinMinutes * 60)
        {
            LastResult = $"{a.Game.Name} session not saved: only {FormatPlayed(a.PlayedSeconds)} with the game in front " +
                         $"(the minimum is {settings.SessionMinMinutes} min; time alt-tabbed doesn't count). You can lower the minimum in Settings.";
            NotSaved?.Invoke(LastResult);
            return;
        }

        var session = new GameSession
        {
            Game = a.Game.Name,
            Exe = a.Game.Exe,
            Start = a.Start,
            End = DateTime.Now,
            PlayedSeconds = Math.Round(a.PlayedSeconds),
            CpuName = a.CpuName,
            GpuName = a.GpuName,
            VramTotalGb = a.VramTotalGb is double vt ? Math.Round(vt, 1) : null,
            RamTotalGb = a.RamTotalGb is double rt ? Math.Round(rt, 1) : null,
            Stats = a.Stats.Select(kv => (kv.Key, Stat: kv.Value.ToStat())).Where(x => x.Stat != null)
                           .ToDictionary(x => x.Key, x => x.Stat!),
            Frames = frames,
            Timeline = a.Timeline,
        };
        (session.Verdict, session.Hints) = Analyse(session, a, settings);

        try
        {
            _store.Save(session);
            LastResult = $"Saved {session.Game}: {FormatPlayed(session.PlayedSeconds)} played, {session.Verdict}.";
        }
        catch (Exception ex)
        {
            // Disk full, folder locked... losing one session beats crashing the tray app, but say so.
            Program.LogError(ex);
            LastResult = $"Couldn't save the {session.Game} session: {ex.Message}";
            NotSaved?.Invoke(LastResult);
            return;
        }
        try { _store.Prune(settings.SessionRetentionDays, settings.SessionMaxMb); } catch (Exception ex) { Program.LogError(ex); }
        Saved?.Invoke(session);
    }

    private static string FormatPlayed(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";
    }

    /// <summary>
    /// Rules of thumb, worded as hints. Frame data (per-frame CPU vs GPU busy time) is preferred;
    /// without it, fall back to how busy the GPU and the busiest core were each second.
    /// </summary>
    private static (string Verdict, List<string> Hints) Analyse(GameSession s, Active a, AppSettings settings)
    {
        var hints = new List<string>();
        double gpuPct, cpuPct, cappedPct;
        string basis;
        if (s.Frames is { } f && f.GpuBoundPct + f.CpuBoundPct + f.CappedPct > 0)
        {
            (gpuPct, cpuPct, cappedPct) = (f.GpuBoundPct, f.CpuBoundPct, f.CappedPct);
            basis = "of frames";
        }
        else if (a.Seconds > 0)
        {
            gpuPct = 100.0 * a.GpuBoundSec / a.Seconds;
            cpuPct = 100.0 * a.CpuBoundSec / a.Seconds;
            cappedPct = 100.0 * a.LightSec / a.Seconds;
            basis = "of the time";
        }
        else
        {
            return ("Unknown", hints);
        }

        string verdict = gpuPct >= 50 ? "GPU-bound"
            : cpuPct >= 50 ? "CPU-bound"
            : cappedPct >= 50 ? "Capped"
            : "Mixed";

        switch (verdict)
        {
            case "GPU-bound":
                hints.Add($"GPU-bound for {gpuPct:0}% {basis}. That's normal and usually ideal for games. For more FPS, lower resolution or graphics settings, or turn on upscaling (DLSS/FSR/XeSS).");
                break;
            case "CPU-bound":
                hints.Add($"CPU-bound for {cpuPct:0}% {basis}: the GPU was waiting on the CPU. CPU-heavy settings (view distance, crowds, physics, simulation) are the ones to lower; raising resolution or graphics will cost little FPS.");
                break;
            case "Capped":
                hints.Add($"Neither CPU nor GPU was the limit {cappedPct:0}% {basis}: a frame cap, V-Sync or the game's own limiter was probably setting the pace.");
                break;
            default:
                hints.Add($"No single bottleneck: GPU-bound {gpuPct:0}%, CPU-bound {cpuPct:0}%, capped {cappedPct:0}% {basis}. Different scenes stressed different parts.");
                break;
        }

        if (s.Frames is { } fr)
        {
            double minutes = Math.Max(1, s.PlayedSeconds / 60);
            if (fr.Stutters / minutes >= 1)
                hints.Add($"{fr.Stutters} stutters ({fr.Stutters / minutes:0.#} a minute): frames far slower than the ones around them. Often shader compilation or asset streaming; if VRAM was nearly full, lower texture quality.");
            if (fr.AvgFps > 0 && fr.Low1Fps < fr.AvgFps * 0.5)
                hints.Add($"1% lows ({fr.Low1Fps:0} FPS) are under half the average ({fr.AvgFps:0} FPS), so frame pacing was uneven even if the average looks fine.");
        }

        if (s.VramTotalGb is double vt && s.Stats.TryGetValue("VramGb", out var vram) && vram.Max >= vt * 0.95)
            hints.Add($"VRAM peaked at {vram.Max:0.0} of {vt:0.0} GB. Running out causes stutter and texture pop-in; lower texture quality first.");
        if (s.Stats.TryGetValue("RamLoad", out var ram) && ram.Max >= 90)
            hints.Add($"System RAM peaked at {ram.Max:0}% in use. Closing background apps (browsers especially) would give the game more headroom.");

        double Share(long sec) => a.Seconds > 0 ? 100.0 * sec / a.Seconds : 0;
        void Heat(long sec, string what, MetricId id)
        {
            if (Share(sec) >= 5)
                hints.Add($"{what} was at or above {settings.Metrics[id].Thresholds[^1]:0}°C (your \"critical\" level) for {Share(sec):0}% of the session; it may have been throttling. Check airflow and fan curves.");
        }
        Heat(a.HotCpuSec, "CPU temperature", MetricId.CpuTemp);
        Heat(a.HotGpuSec, "GPU temperature", MetricId.GpuTemp);
        Heat(a.HotHotspotSec, "GPU hotspot", MetricId.GpuHotspot);
        Heat(a.HotMemSec, "GPU memory junction", MetricId.GpuMemJunction);

        if (a.PsuWatts > 0 && s.Stats.TryGetValue("SystemPowerW", out var power))
        {
            s.PsuWatts = a.PsuWatts;
            double peakPct = 100 * power.Max / a.PsuWatts;
            string how = a.PowerMeasured ? "Measured" : "Estimated";
            if (peakPct >= 70)
                hints.Add($"{how} system draw peaked around {power.Max:0} W, {peakPct:0}% of your {a.PsuWatts} W power supply (average {power.Avg:0} W). " +
                          "GPUs can spike well above their average for milliseconds (RTX 30-series especially), so if the PC ever shuts off or reboots under load, PSU headroom is a prime suspect.");
        }

        if (s.Stats.TryGetValue("CpuLoad", out var sys) && s.Stats.TryGetValue("GameCpu", out var game) && sys.Avg - game.Avg >= 20)
            hints.Add($"Other apps used about {sys.Avg - game.Avg:0}% of the CPU while you played.");

        return (verdict, hints);
    }

    public void Dispose()
    {
        _active?.Capture?.Dispose();
        _active?.Proc?.Dispose();
    }
}
