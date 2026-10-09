using System.Diagnostics;
using Microsoft.Win32;

namespace Toasty;

/// <summary>Owns the tray icons, the mini panel, alerts, and the polling loop.</summary>
public sealed class TrayApp : ApplicationContext
{
    private const string PawnIoUrl = "https://pawnio.eu/";

    private AppSettings _settings = AppSettings.Load();
    private readonly Sensors _sensors = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _driverHintItem;
    private readonly ToolStripSeparator _driverHintSeparator = new() { Visible = false };

    // Created once in a fixed order so Windows remembers each icon's "always show" setting between runs.
    private readonly Dictionary<MetricId, NotifyIcon> _icons = new();
    private readonly NotifyIcon _fallbackIcon;
    private readonly Dictionary<MetricId, MetricTracker> _trackers = Metrics.All.ToDictionary(id => id, _ => new MetricTracker());
    private readonly Dictionary<MetricId, string> _lastDrawn = new();
    private readonly AlertMonitor _alerts = new();
    private readonly SessionStore _store = new();
    private readonly SessionRecorder _recorder;
    private readonly MiniPanel _panel;
    private Snapshot _latest = Snapshot.Empty;

    private bool _lightTaskbar = IconRenderer.LightTaskbar();
    private bool _reading;
    private readonly HashSet<string> _loggedErrors = [];
    private readonly EventWaitHandle _exitRequest = new(false, EventResetMode.AutoReset, Program.ExitEventName);
    private readonly RegisteredWaitHandle _exitWait;

    public TrayApp()
    {
        _driverHintItem = new ToolStripMenuItem("CPU temperature missing? Get PawnIO driver...", null,
            (_, _) => Process.Start(new ProcessStartInfo(PawnIoUrl) { UseShellExecute = true })) { Visible = false };
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = Autostart.IsEnabled(),
        };

        _menu.Items.Add(new ToolStripMenuItem("Show summary", null, (_, _) => _panel!.ShowNear(Cursor.Position)) { Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()));
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(_driverHintSeparator);
        _menu.Items.Add(_driverHintItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit Toasty", null, (_, _) => ExitThread()));

        _recorder = new SessionRecorder(_store);
        _recorder.NotSaved += reason => NoticeIcon.ShowBalloonTip(8_000, "Game session not saved", reason, ToolTipIcon.Info);
        _recorder.Saved += session =>
        {
            string fps = session.Frames is { } f ? $", {f.AvgFps:0} FPS avg" : "";
            NoticeIcon.ShowBalloonTip(6_000, $"{session.Game} session saved",
                $"{TimeSpan.FromSeconds(session.PlayedSeconds):h\\:mm} played{fps}, {session.Verdict}. Click a Toasty icon > Games for details.",
                ToolTipIcon.None);
        };
        _panel = new MiniPanel(() => _settings, SaveSettings, SaveUiState, _trackers);
        _panel.Store = _store;
        _panel.Recorder = _recorder;
        _panel.ViewChanged += (view, open) =>
        {
            // Board/drive sensors only while "All sensors" is showing; RAM sticks while the panel is open.
            bool detailed = open && view == PanelView.Sensors;
            bool refresh = detailed && !_sensors.Detailed;
            _sensors.Detailed = detailed;
            _sensors.PanelOpen = open;
            if (refresh) _ = Tick(); // fill the list straight away rather than on the next tick
        };
        _panel.SettingsView.TestAlertRequested += () =>
        {
            NoticeIcon.ShowBalloonTip(5_000, "Toasty test alert", "Alerts will look like this.", ToolTipIcon.Info);
        };
        _panel.SettingsView.AutostartChanged += want =>
        {
            if (!Autostart.Set(want))
                MessageBox.Show("Couldn't update the startup task.", "Toasty", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _autostartItem!.Checked = Autostart.IsEnabled();
        };

        foreach (var id in Metrics.All)
        {
            var icon = new NotifyIcon { ContextMenuStrip = _menu, Text = Metrics.Label(id) };
            icon.MouseUp += OnIconMouseUp;
            _icons[id] = icon;
        }
        _fallbackIcon = new NotifyIcon { ContextMenuStrip = _menu, Text = "Toasty (all metrics hidden)", Icon = IconRenderer.Logo() };
        _fallbackIcon.MouseUp += OnIconMouseUp;

        // "Toasty.exe --exit" (used by the installer) signals this; exit cleanly on the UI thread.
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitRequest,
            (_, _) => ui.Post(_ => ExitThread(), null), null, Timeout.Infinite, executeOnlyOnce: true);

        _alerts.Triggered += ShowAlert;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        ApplySettings();
        _timer.Tick += async (_, _) => await Tick();
        _timer.Start();
        _ = Tick();

        // A detached panel that was open when Toasty last closed comes back where it was.
        if (_settings.PanelPinned && _settings.PanelPinnedOpen) _panel.ShowNear(Cursor.Position);
    }

    private void OnIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_panel.Visible) _panel.HidePanel();
        else if (!_panel.JustHidden) _panel.ShowNear(Cursor.Position);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Taskbar switched between light and dark: redraw labels/outlines to match.
        bool light = IconRenderer.LightTaskbar();
        if (light == _lightTaskbar) return;
        _lightTaskbar = light;
        _lastDrawn.Clear();
        Render();
    }

    private void ApplySettings()
    {
        _timer.Interval = _settings.IntervalMs;
        _lastDrawn.Clear(); // force a redraw with the new colours/labels/background
        foreach (var t in _trackers.Values) t.ResetBand();
        foreach (var (id, icon) in _icons) icon.Visible = _settings.Metrics[id].Enabled;
        _fallbackIcon.Visible = !_icons.Values.Any(i => i.Visible);
        _alerts.Configure(_settings.Alerts, _settings.AlertCooldownMinutes);
        // Retention / size cap. Off the UI thread: the first call reads every session file.
        int days = _settings.SessionRetentionDays, mb = _settings.SessionMaxMb;
        _ = Task.Run(() => { try { _store.Prune(days, mb); } catch { } });
        Render();
    }

    private async Task Tick()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            // Sensor polling can take tens of ms; keep it off the UI thread.
            var snapshot = await Task.Run(_sensors.Read);
            var now = DateTime.Now;
            _latest = snapshot;
            foreach (var (id, tracker) in _trackers)
                tracker.Add(snapshot.Values.GetValueOrDefault(id), now, _settings.SmoothingSeconds);
            // Alerts use raw readings: "stays above X for Y seconds" is already its own smoothing.
            _alerts.Check(snapshot, now);
            _recorder.Tick(snapshot, _settings);
            Render();
            _panel.UpdateData(snapshot);
        }
        catch (Exception ex)
        {
            // A transient driver hiccup shouldn't crash the tray; log it (once per kind) and try again next tick.
            if (_loggedErrors.Add(ex.GetType().FullName + ex.Message)) Program.LogError(ex);
        }
        finally
        {
            _reading = false;
        }
    }

    private void Render()
    {
        foreach (var (id, icon) in _icons)
        {
            if (!icon.Visible) continue;

            var metric = _settings.Metrics[id];
            var tracker = _trackers[id];
            double? value = tracker.Smoothed;
            int band = tracker.Band(metric);
            Color color = band >= 0 ? metric.ColorOfBand(band) : Color.Gray;
            var style = metric.Style ?? _settings.IconStyle;
            double fraction = value is double v ? metric.Fraction(v, Metrics.IsTemperature(id)) : 0;

            // Only redraw when what you'd see actually changes (bars: when they move by a pixel).
            string key = $"{style}|{(value.HasValue ? Math.Round(value.Value).ToString("0") : "-")}|{Math.Round(fraction * IconRenderer.Size)}|{color.ToArgb()}|{_settings.SolidBackground}|{_lightTaskbar}";
            if (_lastDrawn.GetValueOrDefault(id) != key)
            {
                _lastDrawn[id] = key;
                var old = icon.Icon;
                icon.Icon = IconRenderer.Render(style, value, fraction, color, Metrics.Tag(id), _settings.SolidBackground, _lightTaskbar);
                old?.Dispose();
            }
            icon.Text = Tooltip(id, tracker);
        }

        bool cpuTempMissing = _latest.Values.Count > 0 && _latest.Values.GetValueOrDefault(MetricId.CpuTemp) == null;
        _driverHintItem.Visible = _driverHintSeparator.Visible = cpuTempMissing;
    }

    private static string Tooltip(MetricId id, MetricTracker tracker)
    {
        string unit = Metrics.Unit(id);
        string reading = tracker.Smoothed is double v ? $"{v:0}{unit}" : "unavailable";
        string peak = tracker.SessionMax is double max ? $" (peak {max:0}{unit})" : "";
        string text = $"{Metrics.Label(id)}: {reading}{peak}\nClick for summary";
        return text.Length <= 127 ? text : text[..127];
    }

    /// <summary>Notifications need a visible tray icon to come from.</summary>
    private NotifyIcon NoticeIcon => _icons.Values.FirstOrDefault(i => i.Visible) ?? _fallbackIcon;

    private void ShowAlert(AlertRule rule, double value)
    {
        string unit = Metrics.Unit(rule.Metric);
        NoticeIcon.ShowBalloonTip(10_000, $"{Metrics.Label(rule.Metric)} is high",
            $"Now {value:0}{unit}, at or above {rule.Above:0}{unit} for over {AlertMonitor.FormatDuration(rule.ForSeconds)}.",
            ToolTipIcon.Warning);
    }

    private void ShowSettings() => _panel.ShowNear(Cursor.Position, PanelView.Settings);

    /// <summary>Panel position / open sections: persist only, nothing to re-apply.</summary>
    private void SaveUiState(AppSettings updated)
    {
        _settings = updated;
        try { _settings.Save(); } catch { }
    }

    private void SaveSettings(AppSettings updated)
    {
        _settings = updated;
        try { _settings.Save(); } catch { /* a locked file shouldn't break the UI; the next change retries */ }
        ApplySettings();
    }
    private void ToggleAutostart()
    {
        bool want = !_autostartItem.Checked;
        if (!Autostart.Set(want))
        {
            MessageBox.Show("Couldn't update the startup task.", "Toasty", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _autostartItem.Checked = Autostart.IsEnabled();
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        // Save a game session that's still running when Toasty closes (while the tray icons still exist).
        try { _recorder.Finish(_settings); } catch { }
        _recorder.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        foreach (var icon in _icons.Values)
        {
            icon.Visible = false;
            icon.Icon?.Dispose();
            icon.Dispose();
        }
        _fallbackIcon.Visible = false;
        _fallbackIcon.Dispose();

        _panel.Dispose();
        _sensors.Dispose();
        _exitWait.Unregister(null);
        _exitRequest.Dispose();
        base.ExitThreadCore();
    }
}
