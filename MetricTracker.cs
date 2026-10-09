namespace Toasty;

/// <summary>
/// Per-metric state between polls: a time-windowed average (smoothing), a colour band with
/// hysteresis so it doesn't flicker on a threshold, a short history for the panel's graphs,
/// and session min/max of the raw readings.
/// </summary>
public sealed class MetricTracker
{
    public const int HistoryCapacity = 600;

    /// <summary>How far below a threshold a value must fall before the colour steps back down.</summary>
    private const double Hysteresis = 2;

    private readonly Queue<(DateTime At, double Value)> _window = new();
    private readonly double[] _history = new double[HistoryCapacity];
    private int _historyStart, _historyCount;
    private int _band = -1;

    public double? Raw { get; private set; }
    public double? Smoothed { get; private set; }
    public double? SessionMin { get; private set; }
    public double? SessionMax { get; private set; }

    public void Add(double? value, DateTime now, int smoothingSeconds)
    {
        Raw = value;
        if (value is not double v)
        {
            Smoothed = null;
            _window.Clear();
            return;
        }

        SessionMin = SessionMin is double min ? Math.Min(min, v) : v;
        SessionMax = SessionMax is double max ? Math.Max(max, v) : v;

        _window.Enqueue((now, v));
        var cutoff = now - TimeSpan.FromSeconds(Math.Max(0, smoothingSeconds));
        while (_window.Count > 1 && _window.Peek().At <= cutoff) _window.Dequeue();
        Smoothed = smoothingSeconds <= 0 ? v : _window.Average(s => s.Value);

        int idx = (_historyStart + _historyCount) % HistoryCapacity;
        _history[idx] = Smoothed.Value;
        if (_historyCount < HistoryCapacity) _historyCount++;
        else _historyStart = (_historyStart + 1) % HistoryCapacity;
    }

    /// <summary>The colour band for the current smoothed value, stepping down only once clearly below.</summary>
    public int Band(MetricSettings settings)
    {
        if (Smoothed is not double v) return -1;
        int target = settings.BandFor(v);
        if (_band < 0 || target > _band) _band = target;
        else if (target < _band)
        {
            // Drop one band at a time, and only when we're past the threshold by the margin.
            while (_band > target && v < settings.Thresholds[_band - 1] - Hysteresis) _band--;
        }
        return _band;
    }

    /// <summary>Forget the remembered band, e.g. after thresholds change.</summary>
    public void ResetBand() => _band = -1;

    /// <summary>Most recent samples, oldest first.</summary>
    public IEnumerable<double> History(int maxCount)
    {
        int n = Math.Min(maxCount, _historyCount);
        for (int i = _historyCount - n; i < _historyCount; i++)
            yield return _history[(_historyStart + i) % HistoryCapacity];
    }
}
