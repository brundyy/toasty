namespace Toasty;

/// <summary>
/// Watches raw readings against the alert rules. A rule fires once a value has stayed at or
/// above its limit for the whole duration, then stays quiet until the value drops back below
/// the limit and the cooldown has passed.
/// </summary>
public sealed class AlertMonitor
{
    public event Action<AlertRule, double>? Triggered;

    private sealed class State
    {
        public DateTime? AboveSince;
        public DateTime? LastFired;
        public bool Armed = true;
    }

    private List<(AlertRule Rule, State State)> _rules = [];
    private TimeSpan _cooldown = TimeSpan.FromMinutes(10);

    public void Configure(IEnumerable<AlertRule> rules, int cooldownMinutes)
    {
        _rules = rules.Where(r => r.Enabled).Select(r => (r.Clone(), new State())).ToList();
        _cooldown = TimeSpan.FromMinutes(cooldownMinutes);
    }

    public void Check(Snapshot snapshot, DateTime now)
    {
        foreach (var (rule, state) in _rules)
        {
            if (snapshot.Values.GetValueOrDefault(rule.Metric) is not double value || value < rule.Above)
            {
                state.AboveSince = null;
                state.Armed = true;
                continue;
            }

            state.AboveSince ??= now;
            bool sustained = now - state.AboveSince.Value >= TimeSpan.FromSeconds(rule.ForSeconds);
            bool cooledDown = state.LastFired is not DateTime last || now - last >= _cooldown;
            if (state.Armed && sustained && cooledDown)
            {
                state.Armed = false;
                state.LastFired = now;
                Triggered?.Invoke(rule, value);
            }
        }
    }

    public static string Describe(AlertRule rule) =>
        $"{Metrics.Label(rule.Metric)} at or above {rule.Above:0}{Metrics.Unit(rule.Metric)} for {FormatDuration(rule.ForSeconds)}";

    public static string FormatDuration(int seconds) =>
        seconds % 60 == 0 && seconds >= 60 ? $"{seconds / 60} min" : $"{seconds}s";
}
