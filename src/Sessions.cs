using System.Text.Json;
using System.Text.Json.Serialization;

namespace Toasty;

/// <summary>Min / average / max of one reading over a session.</summary>
public record Stat(double Min, double Avg, double Max);

/// <summary>Frame timing from PresentMon. FPS figures are derived from CPU frame time (time between presents).</summary>
public record FrameSummary(
    long Frames,
    double AvgFps,
    double Low1Fps,
    double Low01Fps,
    double AvgFrameTimeMs,
    double P99FrameTimeMs,
    int Stutters,
    double GpuBoundPct,
    double CpuBoundPct,
    double CappedPct,
    double GameGpuBusyPct);

/// <summary>Values sampled every few seconds for the session graphs. Nulls are gaps.</summary>
public record Timeline(
    int IntervalSeconds,
    List<double?> Fps,
    List<double?> GpuLoad,
    List<double?> BusiestCore,
    List<double?> CpuTemp,
    List<double?> GpuTemp);

/// <summary>One recorded play session.</summary>
public class GameSession
{
    public string Id { get; set; } = "";
    public string Game { get; set; } = "";
    public string Exe { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>Seconds the game was actually in the foreground (stats only cover this time).</summary>
    public double PlayedSeconds { get; set; }
    public string? CpuName { get; set; }
    public string? GpuName { get; set; }
    public double? VramTotalGb { get; set; }
    public double? RamTotalGb { get; set; }
    /// <summary>The PSU wattage set at the time, for comparing against the estimated draw.</summary>
    public int? PsuWatts { get; set; }
    public Dictionary<string, Stat> Stats { get; set; } = new();
    public FrameSummary? Frames { get; set; }
    /// <summary>"GPU-bound", "CPU-bound", "Capped", "Mixed" or "Unknown".</summary>
    public string Verdict { get; set; } = "Unknown";
    public List<string> Hints { get; set; } = [];
    public Timeline? Timeline { get; set; }

    [JsonIgnore] public long FileBytes { get; set; }
}

/// <summary>Session files in %AppData%\Toasty\sessions, one JSON file per session.</summary>
public sealed class SessionStore
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toasty", "sessions");

    private static readonly JsonSerializerOptions Json = new()
    {
        // Compact on disk; numbers are already rounded before saving.
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Backstop: a stray NaN/Infinity must never make a whole session unsaveable.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly object _lock = new();
    private List<GameSession>? _cache;

    /// <summary>All saved sessions, newest first. Loaded from disk once, then kept in memory.</summary>
    public IReadOnlyList<GameSession> All()
    {
        lock (_lock)
        {
            if (_cache == null)
            {
                _cache = [];
                if (Directory.Exists(Folder))
                {
                    foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
                    {
                        try
                        {
                            var s = JsonSerializer.Deserialize<GameSession>(File.ReadAllText(file), Json);
                            if (s == null) continue;
                            s.Id = Path.GetFileNameWithoutExtension(file);
                            s.FileBytes = new FileInfo(file).Length;
                            _cache.Add(s);
                        }
                        catch
                        {
                            // Skip unreadable files rather than losing the whole list.
                        }
                    }
                }
                _cache.Sort((a, b) => b.Start.CompareTo(a.Start));
            }
            return _cache.ToList();
        }
    }

    public void Save(GameSession session)
    {
        Directory.CreateDirectory(Folder);
        string slug = new string(session.Game.Where(char.IsLetterOrDigit).Take(40).ToArray());
        session.Id = $"{session.Start:yyyyMMdd-HHmmss}-{(slug.Length > 0 ? slug : "game")}";
        string path = Path.Combine(Folder, session.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(session, Json));
        session.FileBytes = new FileInfo(path).Length;
        lock (_lock)
        {
            All();
            _cache!.Insert(0, session);
        }
    }

    public void Delete(GameSession session)
    {
        try { File.Delete(Path.Combine(Folder, session.Id + ".json")); } catch { }
        lock (_lock) _cache?.RemoveAll(s => s.Id == session.Id);
    }

    public void DeleteAll()
    {
        foreach (var s in All()) Delete(s);
    }

    public (int Count, long Bytes) Usage()
    {
        var all = All();
        return (all.Count, all.Sum(s => s.FileBytes));
    }

    /// <summary>Applies retention: drop sessions past the age limit, then the oldest until under the size cap.</summary>
    private readonly object _pruneLock = new();

    public int Prune(int retentionDays, int maxMb)
    {
        lock (_pruneLock) return PruneCore(retentionDays, maxMb);
    }

    private int PruneCore(int retentionDays, int maxMb)
    {
        int removed = 0;
        var all = All(); // newest first
        if (retentionDays > 0)
        {
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var s in all.Where(s => s.Start < cutoff)) { Delete(s); removed++; }
        }
        long cap = (long)maxMb * 1024 * 1024;
        var remaining = All();
        long total = remaining.Sum(s => s.FileBytes);
        for (int i = remaining.Count - 1; i >= 0 && total > cap; i--)
        {
            total -= remaining[i].FileBytes;
            Delete(remaining[i]);
            removed++;
        }
        return removed;
    }

    public static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024:0.0} MB" : bytes == 0 ? "0 KB" : $"{Math.Max(1, bytes / 1024)} KB";
}
