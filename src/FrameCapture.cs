using System.Diagnostics;
using System.Globalization;

namespace Toasty;

/// <summary>
/// Runs PresentMon against one game process and turns its per-frame CSV (streamed over
/// stdout, never written to disk) into running statistics. PresentMon reads the frame events
/// Windows already emits (ETW), so it doesn't inject anything into the game.
/// </summary>
public sealed class FrameCapture : IDisposable
{
    private const string SessionName = "ToastyCapture";
    private const double BucketMs = 0.1;
    private const int Buckets = 10_000; // 0-1000 ms in 0.1 ms steps; slower frames land in the last bucket

    private readonly object _lock = new();
    private readonly long[] _histogram = new long[Buckets];
    private Process? _process;

    private long _frames;
    private double _frameTimeSum;
    private double _gpuBusySum;
    private long _gpuBound, _cpuBound, _capped, _classified;
    private int _stutters;
    private double _ema;

    // Since the last TakeInterval() call, for live FPS and the timeline.
    private long _intervalFrames;
    private double _intervalTime;

    public bool Running => _process is { HasExited: false };
    public string? Error { get; private set; }

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "PresentMon.exe");
    public static bool Available => File.Exists(ExePath);

    public void Start(int pid)
    {
        if (!Available)
        {
            Error = "PresentMon.exe not found next to Toasty.exe";
            return;
        }
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
        {
            "--process_id", pid.ToString(), "--output_stdout", "--no_console_stats", "--v2_metrics",
            "--session_name", SessionName, "--stop_existing_session", "--terminate_on_proc_exit",
            "--no_track_input", "--no_track_display",
        })
            psi.ArgumentList.Add(arg);

        try
        {
            _process = Process.Start(psi);
            if (_process == null) { Error = "PresentMon didn't start"; return; }
            ChildProcessJob.Add(_process); // dies with Toasty, however Toasty ends
            _ = Task.Run(() => ReadLoop(_process));
            _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Error = e.Data.Trim(); };
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void ReadLoop(Process p)
    {
        try
        {
            int colFrame = -1, colGpuBusy = -1, colCpuBusy = -1;
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                var cells = line.Split(',');
                if (colFrame < 0)
                {
                    // Header row. v2 names first, v1 names as a fallback.
                    colFrame = Index(cells, "FrameTime", "msBetweenPresents");
                    colGpuBusy = Index(cells, "GPUBusy", "msGPUActive");
                    colCpuBusy = Index(cells, "CPUBusy");
                    continue;
                }
                if (colFrame >= cells.Length || !TryNumber(cells[colFrame], out double ft) || !double.IsFinite(ft) || ft <= 0) continue;
                double? gpu = colGpuBusy >= 0 && colGpuBusy < cells.Length && TryNumber(cells[colGpuBusy], out double g) ? g : null;
                double? cpu = colCpuBusy >= 0 && colCpuBusy < cells.Length && TryNumber(cells[colCpuBusy], out double c) ? c : null;
                AddFrame(ft, gpu, cpu);
            }
        }
        catch
        {
            // Process killed or pipe closed.
        }
    }

    private static int Index(string[] header, params string[] names)
    {
        foreach (var n in names)
        {
            int i = Array.FindIndex(header, h => h.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    private static bool TryNumber(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    private void AddFrame(double ft, double? gpuBusy, double? cpuBusy)
    {
        lock (_lock)
        {
            _frames++;
            _frameTimeSum += ft;
            _intervalFrames++;
            _intervalTime += ft;
            _histogram[Math.Min(Buckets - 1, (int)(ft / BucketMs))]++;

            // A stutter is a frame far slower than the recent norm (and slow in absolute terms).
            _ema = _ema == 0 ? ft : _ema * 0.95 + ft * 0.05;
            if (_frames > 30 && ft > 2.5 * _ema && ft > 25) _stutters++;

            if (gpuBusy is double gb)
            {
                _gpuBusySum += Math.Min(gb, ft);
                // GPU busy for (nearly) the whole frame: the GPU set the pace.
                if (gb >= 0.9 * ft) _gpuBound++;
                // CPU busy for the whole frame while the GPU had slack: the CPU set the pace.
                else if (cpuBusy is double cb && cb >= 0.9 * ft) _cpuBound++;
                // Both had slack: something else (V-Sync, a frame limiter) set the pace.
                else if (cpuBusy is double cb2 && cb2 < 0.8 * ft && gb < 0.8 * ft) _capped++;
                _classified++;
            }
        }
    }

    /// <summary>FPS over the frames since the last call (null if none arrived).</summary>
    public double? TakeIntervalFps()
    {
        lock (_lock)
        {
            double? fps = _intervalFrames > 0 && _intervalTime > 0 ? _intervalFrames * 1000.0 / _intervalTime : null;
            _intervalFrames = 0;
            _intervalTime = 0;
            return fps;
        }
    }

    public FrameSummary? Summary()
    {
        lock (_lock)
        {
            if (_frames < 10 || _frameTimeSum <= 0) return null;
            double avgFt = _frameTimeSum / _frames;
            double p99 = Percentile(0.99), p999 = Percentile(0.999);
            double Pct(long n) => _classified > 0 ? Math.Round(100.0 * n / _classified, 1) : 0;
            return new FrameSummary(
                Frames: _frames,
                AvgFps: Math.Round(1000 / avgFt, 1),
                Low1Fps: Math.Round(1000 / p99, 1),
                Low01Fps: Math.Round(1000 / p999, 1),
                AvgFrameTimeMs: Math.Round(avgFt, 2),
                P99FrameTimeMs: Math.Round(p99, 2),
                Stutters: _stutters,
                GpuBoundPct: Pct(_gpuBound),
                CpuBoundPct: Pct(_cpuBound),
                CappedPct: Pct(_capped),
                GameGpuBusyPct: Math.Round(100 * _gpuBusySum / _frameTimeSum, 1));
        }
    }

    /// <summary>Frame time at the given percentile (e.g. 0.99 → the "1% low" frame).</summary>
    private double Percentile(double p)
    {
        long target = (long)Math.Ceiling(_frames * p);
        long seen = 0;
        for (int i = 0; i < Buckets; i++)
        {
            seen += _histogram[i];
            if (seen >= target) return Math.Max(BucketMs, (i + 0.5) * BucketMs);
        }
        return Buckets * BucketMs;
    }

    public void Stop()
    {
        if (_process == null) return;
        bool wasRunning = false;
        try
        {
            wasRunning = !_process.HasExited;
            if (wasRunning) _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process.Dispose();
        _process = null;

        // Killing PresentMon can leave its ETW trace session behind; close it so it doesn't linger.
        if (wasRunning && Available)
        {
            try
            {
                var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("--terminate_existing_session");
                psi.ArgumentList.Add("--session_name");
                psi.ArgumentList.Add(SessionName);
                Process.Start(psi)?.Dispose();
            }
            catch { }
        }
    }

    public void Dispose() => Stop();
}
