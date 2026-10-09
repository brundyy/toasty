namespace Toasty;

/// <summary>
/// The panel's Settings tab. Everything applies as you change it (after a short pause), so
/// there's no Save button and no separate window.
/// </summary>
public sealed class SettingsView : UserControl
{
    public event Action? TestAlertRequested;
    public event Action<bool>? AutostartChanged;

    private readonly MiniPanel _owner;
    private readonly FlowLayoutPanel _flow;
    private readonly System.Windows.Forms.Timer _applyTimer = new() { Interval = 300 };
    private Font _headingFont = null!, _smallFont = null!;

    private AppSettings _working = AppSettings.Defaults();
    private readonly Dictionary<MetricId, MetricEditor> _editors = new();
    private FlowLayoutPanel _alertsList = null!;
    private ComboBox _style = null!;
    private CheckBox _solid = null!;
    private NumericUpDown _interval = null!, _smoothing = null!, _cooldown = null!;
    private CheckBox _autostart = null!;
    private CheckBox _onTop = null!;
    private CheckBox _record = null!, _frames = null!, _timeline = null!;
    private NumericUpDown _retention = null!, _maxMb = null!, _timelineSeconds = null!, _minMinutes = null!;
    private TextBox _always = null!, _never = null!;
    private Label _usage = null!;
    private NumericUpDown _psu = null!, _otherWatts = null!;
    private PreviewStrip _preview = null!;
    private bool _loading;

    private sealed class MetricEditor
    {
        public required CheckBox Enabled;
        public required ComboBox Style;
        public required Button[] Swatches;
        public required NumericUpDown[] Thresholds;
        public required Label Error;
    }

    public SettingsView(MiniPanel owner)
    {
        _owner = owner;
        BackColor = MiniPanel.Bg;
        ForeColor = MiniPanel.Text1;
        DoubleBuffered = true;
        // Vertical scrolling only: switching AutoScroll off, zeroing the horizontal bar, then back on
        // is the WinForms way to stop a stray horizontal scrollbar appearing.
        AutoScroll = false;
        HorizontalScroll.Maximum = 0;
        HorizontalScroll.Visible = false;
        AutoScroll = true;
        MouseDown += (_, _) => ClearFocus();

        _flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(P(14), P(4)),
            BackColor = MiniPanel.Bg,
        };
        Controls.Add(_flow);

        _flow.MouseDown += (_, _) => ClearFocus();
        _applyTimer.Tick += (_, _) =>
        {
            _applyTimer.Stop();
            Apply();
        };
    }

    private int P(float v) => _owner.Px(v);

    /// <summary>Height needed to show everything without scrolling.</summary>
    public int ContentHeight => _flow.GetPreferredSize(Size.Empty).Height + P(20);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MiniPanel.SetWindowTheme(Handle, "DarkMode_Explorer", null); // dark scrollbar
    }

    protected override void WndProc(ref Message m)
    {
        if (!_owner.PassEdgeHitTest(ref m)) base.WndProc(ref m); // let the panel's edges be dragged
    }

    /// <summary>
    /// WinForms scrolls a focused control into view, which made the page jump (and left a blank
    /// band at the top) whenever a dropdown or number box got focus. Stay where the user put it.
    /// </summary>
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    /// <summary>Clicking empty space deselects the current dropdown/number box (so the wheel scrolls the page).</summary>
    private void ClearFocus() => _owner.ActiveControl = null;

    /// <summary>
    /// Scrolls the page by a mouse-wheel delta. The panel routes every wheel over it here (see
    /// MiniPanel.PreFilterMessage), so dropdowns and number boxes never change value as you scroll past.
    /// </summary>
    public void ScrollByWheel(int wheelDelta)
    {
        int step = P(48) * Math.Max(1, SystemInformation.MouseWheelScrollLines) / 3;
        int y = -AutoScrollPosition.Y - wheelDelta * step / 120;
        AutoScrollPosition = new Point(0, Math.Max(0, y));
    }

    /// <summary>True while a dropdown's list is open (the wheel should scroll that list instead).</summary>
    public bool HasOpenDropDown => AnyOpen(_flow);

    private static bool AnyOpen(Control parent)
    {
        foreach (Control child in parent.Controls)
            if (child is ComboBox { DroppedDown: true } || AnyOpen(child)) return true;
        return false;
    }

    /// <summary>After building: clicking any non-input area deselects the current control.</summary>
    private void WireUp(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Label or FlowLayoutPanel or PreviewStrip) child.MouseDown += (_, _) => ClearFocus();
            WireUp(child);
        }
    }

    /// <summary>Rebuilds the controls from the current settings (called each time the tab opens).</summary>
    public void LoadFrom(AppSettings settings)
    {
        _loading = true;
        _applyTimer.Stop();
        _working = settings.Clone();

        // Pixel-sized fonts from the panel's current DPI (rebuilt when it moves between monitors).
        _headingFont?.Dispose(); _smallFont?.Dispose();
        Font = MiniPanel.PixelFont("Segoe UI", 9f, _owner.S);
        _headingFont = MiniPanel.PixelFont("Segoe UI Semibold", 10f, _owner.S);
        _smallFont = MiniPanel.PixelFont("Segoe UI", 8f, _owner.S);
        // Scroll back to the top *before* placing the content: positions inside a scrolled panel
        // are relative to the current scroll offset, which is what left the blank band at the top.
        AutoScrollPosition = Point.Empty;
        _flow.Location = new Point(P(14), P(4));

        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls.Cast<Control>().ToList()) c.Dispose();
        _flow.Controls.Clear();
        _editors.Clear();

        BuildIconsSection();
        BuildAlertsSection();
        BuildSessionsSection();
        BuildPowerSection();
        BuildGeneralSection();

        _flow.ResumeLayout();
        WireUp(_flow);
        _loading = false;
        AutoScrollPosition = Point.Empty;
        RefreshPreview();
    }

    // ---------- building blocks ----------

    private int Inner => P(MiniPanel.BaseWidth) - P(14) * 2 - SystemInformation.VerticalScrollBarWidth;

    private Label Heading(string text) => new()
    {
        Text = text,
        Font = _headingFont,
        ForeColor = MiniPanel.Text1,
        AutoSize = true,
        Margin = new Padding(0, P(14), 0, P(4)),
    };

    private Label Hint(string text) => new()
    {
        Text = text,
        Font = _smallFont,
        ForeColor = MiniPanel.Text3,
        AutoSize = true,
        MaximumSize = new Size(Inner, 0),
        Margin = new Padding(0, 0, 0, P(6)),
    };

    private static FlowLayoutPanel Row(int top = 2) => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        Margin = new Padding(0, top, 0, 2),
        BackColor = MiniPanel.Bg,
    };

    private Label Caption(string text, int width = 0, Color? color = null)
    {
        var l = new Label
        {
            Text = text,
            ForeColor = color ?? MiniPanel.Text2,
            AutoSize = width == 0,
            Margin = new Padding(0, P(6), P(4), 0),
        };
        if (width > 0) l.Size = new Size(P(width), P(20));
        return l;
    }

    private ComboBox Combo(int width, IEnumerable<string> items, int selected)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = MiniPanel.Card,
            ForeColor = MiniPanel.Text1,
            Width = P(width),
            Margin = new Padding(0, P(2), P(6), P(2)),
        };
        c.Items.AddRange(items.ToArray<object>());
        c.DropDownWidth = Math.Max(c.Width, P(190));
        c.SelectedIndex = Math.Max(0, selected);
        c.SelectedIndexChanged += (_, _) => Changed();
        return c;
    }

    private NumericUpDown Number(int width, decimal min, decimal max, decimal value, decimal step = 1)
    {
        var n = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Increment = step,
            Value = Math.Clamp(value, min, max),
            Width = P(width),
            BackColor = MiniPanel.Card,
            ForeColor = MiniPanel.Text1,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Right,
            Margin = new Padding(0, P(2), P(3), P(2)),
        };
        n.ValueChanged += (_, _) => Changed();
        return n;
    }

    private CheckBox Check(string text, bool value, int width = 0)
    {
        var c = new CheckBox
        {
            Text = text,
            Checked = value,
            ForeColor = MiniPanel.Text1,
            AutoSize = width == 0,
            Margin = new Padding(0, P(4), P(6), P(2)),
        };
        if (width > 0) c.Size = new Size(P(width), P(24));
        c.CheckedChanged += (_, _) => Changed();
        return c;
    }

    private Button FlatButton(string text, Action click, int width = 0)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = MiniPanel.Card,
            ForeColor = MiniPanel.Text1,
            AutoSize = width == 0,
            Margin = new Padding(0, P(4), P(6), P(2)),
            Cursor = Cursors.Hand,
        };
        if (width > 0) b.Size = new Size(P(width), P(26));
        b.FlatAppearance.BorderColor = MiniPanel.Line;
        b.Click += (_, _) => click();
        return b;
    }

    // ---------- sections ----------

    private void BuildIconsSection()
    {
        _flow.Controls.Add(Heading("Tray icons"));

        var styleRow = Row();
        styleRow.Controls.Add(Caption("Style", 70));
        _style = Combo(190, IconStyles.All.Select(IconStyles.Label), Array.IndexOf(IconStyles.All, _working.IconStyle));
        styleRow.Controls.Add(_style);
        _flow.Controls.Add(styleRow);

        _solid = Check("Dark background behind icons", _working.SolidBackground);
        _flow.Controls.Add(_solid);

        _preview = new PreviewStrip(P) { Margin = new Padding(0, P(8), 0, P(4)) };
        _flow.Controls.Add(_preview);

        var timing = Row(P(6));
        timing.Controls.Add(Caption("Update every", 86));
        _interval = Number(62, 250, 10_000, _working.IntervalMs, 250);
        timing.Controls.Add(_interval);
        timing.Controls.Add(Caption("ms"));
        _flow.Controls.Add(timing);

        var smooth = Row();
        smooth.Controls.Add(Caption("Smooth over", 86));
        _smoothing = Number(62, 0, 60, _working.SmoothingSeconds);
        smooth.Controls.Add(_smoothing);
        smooth.Controls.Add(Caption("seconds (0 = raw)"));
        _flow.Controls.Add(smooth);

        _flow.Controls.Add(Heading("Icons and colours"));
        _flow.Controls.Add(Hint("Tick the icons to show. Colours go cold → critical, left to right; each number is where the next colour starts. Click a colour to change it."));

        foreach (var id in Metrics.All) BuildMetricEditor(id);

        _flow.Controls.Add(FlatButton("Reset icons and colours to defaults", () =>
        {
            var d = AppSettings.Defaults();
            foreach (var (id, ed) in _editors) LoadEditor(ed, d.Metrics[id]);
            _style.SelectedIndex = Array.IndexOf(IconStyles.All, d.IconStyle);
            _solid.Checked = d.SolidBackground;
            Changed();
        }));
    }

    private void BuildMetricEditor(MetricId id)
    {
        var m = _working.Metrics[id];
        bool temp = Metrics.IsTemperature(id);

        var top = Row(P(8));
        var enabled = Check(Metrics.Label(id), m.Enabled, width: 200);
        enabled.Font = new Font(Font, FontStyle.Bold);
        top.Controls.Add(enabled);
        var style = Combo(140, new[] { "Default style" }.Concat(IconStyles.All.Select(IconStyles.Label)),
            m.Style is IconStyle s ? 1 + Array.IndexOf(IconStyles.All, s) : 0);
        top.Controls.Add(style);
        _flow.Controls.Add(top);

        var bands = Row(0);
        var swatches = new Button[AppSettings.BandCount];
        var thresholds = new NumericUpDown[AppSettings.BandCount - 1];
        for (int b = 0; b < AppSettings.BandCount; b++)
        {
            var sw = new Button
            {
                Size = new Size(P(22), P(22)),
                FlatStyle = FlatStyle.Flat,
                BackColor = ColorTranslator.FromHtml(m.Colors[b]),
                Margin = new Padding(0, P(3), P(3), P(2)),
                Cursor = Cursors.Hand,
            };
            sw.FlatAppearance.BorderColor = MiniPanel.Line;
            sw.Click += (_, _) => PickColor(sw);
            swatches[b] = sw;
            bands.Controls.Add(sw);
            if (b < AppSettings.BandCount - 1)
            {
                thresholds[b] = Number(42, 0, temp ? 125 : 100, (decimal)m.Thresholds[b]);
                bands.Controls.Add(thresholds[b]);
            }
        }
        bands.Controls.Add(Caption(Metrics.Unit(id)));
        _flow.Controls.Add(bands);

        var error = new Label
        {
            Text = "Each number must be higher than the one before it.",
            Font = _smallFont,
            ForeColor = Color.FromArgb(248, 113, 113),
            AutoSize = true,
            Visible = false,
            Margin = new Padding(0, 0, 0, P(2)),
        };
        _flow.Controls.Add(error);

        _editors[id] = new MetricEditor { Enabled = enabled, Style = style, Swatches = swatches, Thresholds = thresholds, Error = error };
    }

    private void LoadEditor(MetricEditor ed, MetricSettings m)
    {
        ed.Enabled.Checked = m.Enabled;
        ed.Style.SelectedIndex = m.Style is IconStyle s ? 1 + Array.IndexOf(IconStyles.All, s) : 0;
        for (int b = 0; b < ed.Swatches.Length; b++) ed.Swatches[b].BackColor = ColorTranslator.FromHtml(m.Colors[b]);
        for (int b = 0; b < ed.Thresholds.Length; b++) ed.Thresholds[b].Value = (decimal)m.Thresholds[b];
    }

    private void PickColor(Button swatch)
    {
        using var dialog = new ColorDialog
        {
            Color = swatch.BackColor,
            FullOpen = true,
            CustomColors = AppSettings.DefaultColors.Select(h => ColorTranslator.ToOle(ColorTranslator.FromHtml(h))).ToArray(),
        };
        if (_owner.WithoutAutoHide(() => dialog.ShowDialog(_owner)) == DialogResult.OK)
        {
            swatch.BackColor = dialog.Color;
            Changed();
        }
    }

    private void BuildAlertsSection()
    {
        _flow.Controls.Add(Heading("Alerts"));
        _flow.Controls.Add(Hint("Get a Windows notification when a reading stays at or above a limit for a while. Brief spikes are ignored."));

        _alertsList = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            BackColor = MiniPanel.Bg,
        };
        _flow.Controls.Add(_alertsList);
        RebuildAlerts();

        var buttons = Row(P(4));
        buttons.Controls.Add(FlatButton("+ Add alert", () =>
        {
            _working.Alerts.Add(new AlertRule { Metric = MetricId.CpuTemp, Above = 85, ForSeconds = 30 });
            RebuildAlerts();
            Changed();
        }));
        buttons.Controls.Add(FlatButton("Send test notification", () => TestAlertRequested?.Invoke()));
        _flow.Controls.Add(buttons);

        var cooldown = Row(P(6));
        cooldown.Controls.Add(Caption("Repeat an alert at most every"));
        _cooldown = Number(56, 1, 1440, _working.AlertCooldownMinutes);
        cooldown.Controls.Add(_cooldown);
        cooldown.Controls.Add(Caption("min"));
        _flow.Controls.Add(cooldown);
    }

    private void RebuildAlerts()
    {
        bool wasLoading = _loading;
        _loading = true;
        _alertsList.SuspendLayout();
        foreach (Control c in _alertsList.Controls.Cast<Control>().ToList()) c.Dispose();
        _alertsList.Controls.Clear();

        if (_working.Alerts.Count == 0)
            _alertsList.Controls.Add(Caption("No alerts set.", color: MiniPanel.Text3));

        foreach (var rule in _working.Alerts)
        {
            var row = Row(P(2));
            var on = Check("", rule.Enabled, width: 22);
            on.CheckedChanged += (_, _) => { rule.Enabled = on.Checked; };
            row.Controls.Add(on);

            var metric = Combo(128, Metrics.All.Select(Metrics.Label), Array.IndexOf(Metrics.All, rule.Metric));
            var unit = Caption(Metrics.Unit(rule.Metric), 22);
            metric.SelectedIndexChanged += (_, _) =>
            {
                rule.Metric = Metrics.All[metric.SelectedIndex];
                unit.Text = Metrics.Unit(rule.Metric);
            };
            row.Controls.Add(metric);

            row.Controls.Add(Caption("≥"));
            var above = Number(46, 0, 150, (decimal)rule.Above);
            above.ValueChanged += (_, _) => rule.Above = (double)above.Value;
            row.Controls.Add(above);
            row.Controls.Add(unit);

            row.Controls.Add(Caption("for"));
            var seconds = Number(50, 0, 86_400, rule.ForSeconds, 5);
            seconds.ValueChanged += (_, _) => rule.ForSeconds = (int)seconds.Value;
            row.Controls.Add(seconds);
            row.Controls.Add(Caption("s"));

            var remove = FlatButton("×", () =>
            {
                _working.Alerts.Remove(rule);
                RebuildAlerts();
                Changed();
            }, width: 24);
            remove.Margin = new Padding(P(2), P(2), 0, P(2));
            row.Controls.Add(remove);

            _alertsList.Controls.Add(row);
        }
        _alertsList.ResumeLayout();
        if (!wasLoading) WireUp(_alertsList); // during a full rebuild, LoadFrom wires everything once
        _loading = wasLoading;
        if (!_loading) _owner.LayoutPanel(null);
    }

    private TextBox ListBox(IEnumerable<string> items) 
    {
        var t = new TextBox
        {
            Text = string.Join(", ", items),
            Width = Inner,
            BackColor = MiniPanel.Card,
            ForeColor = MiniPanel.Text1,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. game.exe, other.exe",
            Margin = new Padding(0, P(2), 0, P(4)),
        };
        t.TextChanged += (_, _) => Changed();
        return t;
    }

    private static List<string> ParseList(string text) =>
        text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x : x + ".exe")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void BuildSessionsSection()
    {
        _flow.Controls.Add(Heading("Game sessions"));
        _flow.Controls.Add(Hint("Games are detected automatically (Steam, Epic, GOG, EA, Ubisoft, Xbox and other libraries, or anything fullscreen that keeps the GPU busy) and recorded while they're in front. See them on the Games tab."));

        _record = Check("Record game sessions", _working.RecordSessions);
        _flow.Controls.Add(_record);
        _frames = Check("Capture FPS, 1% lows and stutters (PresentMon)", _working.CaptureFrames);
        _flow.Controls.Add(_frames);
        if (!FrameCapture.Available)
            _flow.Controls.Add(Hint("PresentMon.exe wasn't found next to Toasty, so frame data is unavailable. Reinstalling Toasty restores it."));

        var keep = Row(P(6));
        keep.Controls.Add(Caption("Keep sessions for", 120));
        _retention = Number(62, 0, 3650, _working.SessionRetentionDays, 30);
        keep.Controls.Add(_retention);
        keep.Controls.Add(Caption("days (0 = forever)"));
        _flow.Controls.Add(keep);

        var cap = Row();
        cap.Controls.Add(Caption("Use at most", 120));
        _maxMb = Number(62, 1, 100_000, _working.SessionMaxMb, 50);
        cap.Controls.Add(_maxMb);
        cap.Controls.Add(Caption("MB of disk (oldest go first)"));
        _flow.Controls.Add(cap);

        var min = Row();
        min.Controls.Add(Caption("Skip sessions under", 120));
        _minMinutes = Number(62, 0, 120, _working.SessionMinMinutes);
        min.Controls.Add(_minMinutes);
        min.Controls.Add(Caption("minutes played"));
        _flow.Controls.Add(min);

        _timeline = Check("Save graphs with each session", _working.SessionTimeline);
        _flow.Controls.Add(_timeline);
        var every = Row(0);
        every.Controls.Add(Caption("Graph point every", 120));
        _timelineSeconds = Number(62, 1, 60, _working.SessionTimelineSeconds);
        every.Controls.Add(_timelineSeconds);
        every.Controls.Add(Caption("seconds (lower = bigger files)"));
        _flow.Controls.Add(every);

        _flow.Controls.Add(Caption("Always treat as games:", color: MiniPanel.Text2));
        _always = ListBox(_working.AlwaysTrack);
        _flow.Controls.Add(_always);
        _flow.Controls.Add(Caption("Never treat as games:", color: MiniPanel.Text2));
        _never = ListBox(_working.NeverTrack);
        _flow.Controls.Add(_never);

        _usage = Caption("", color: MiniPanel.Text3);
        _flow.Controls.Add(_usage);
        UpdateUsage();

        var actions = Row(P(2));
        actions.Controls.Add(FlatButton("Open folder", () =>
        {
            Directory.CreateDirectory(SessionStore.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SessionStore.Folder) { UseShellExecute = true });
        }));
        actions.Controls.Add(FlatButton("Delete all sessions", () =>
        {
            if (_owner.Store == null) return;
            var (count, _) = _owner.Store.Usage();
            if (count == 0) return;
            var answer = _owner.WithoutAutoHide(() => MessageBox.Show(_owner, $"Delete all {count} recorded sessions? This can't be undone.",
                "Toasty", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning));
            if (answer != DialogResult.OK) return;
            _owner.Store.DeleteAll();
            UpdateUsage();
        }));
        _flow.Controls.Add(actions);
    }

    private void UpdateUsage()
    {
        if (_usage == null || _owner.Store == null) return;
        var (count, bytes) = _owner.Store.Usage();
        _usage.Text = $"{count} session{(count == 1 ? "" : "s")} using {SessionStore.FormatBytes(bytes)} (a typical hour-long session is about 50-150 KB).";
        _usage.MaximumSize = new Size(Inner, 0);
    }

    private void BuildPowerSection()
    {
        _flow.Controls.Add(Heading("Power"));
        var snap = _owner.Latest;
        if (snap.PsuName != null)
            _flow.Controls.Add(Hint($"Detected {snap.PsuName}{(snap.PsuRatedWatts is int r ? $" ({r} W)" : "")}: system power is measured by the PSU itself. Leave the wattage at 0 to use the detected rating."));
        else
            _flow.Controls.Add(Hint("Most power supplies can't report their rating to Windows (only USB-connected ones like Corsair HXi/RMi/AXi can, and those are detected automatically), so enter the wattage from its label. Estimated draw = CPU package + GPU board power + an allowance for everything else (board, RAM, drives, fans)."));

        var psu = Row();
        psu.Controls.Add(Caption("Power supply", 120));
        _psu = Number(62, 0, 5000, _working.PsuWatts, 50);
        psu.Controls.Add(_psu);
        psu.Controls.Add(Caption("W (0 = not set)"));
        _flow.Controls.Add(psu);

        var other = Row();
        other.Controls.Add(Caption("Rest of system", 120));
        _otherWatts = Number(62, 0, 1000, _working.OtherComponentsWatts, 5);
        other.Controls.Add(_otherWatts);
        other.Controls.Add(Caption("W allowance"));
        _flow.Controls.Add(other);
    }

    private void BuildGeneralSection()
    {
        _flow.Controls.Add(Heading("General"));
        _autostart = new CheckBox
        {
            Text = "Start Toasty when I sign in",
            Checked = Autostart.IsEnabled(),
            ForeColor = MiniPanel.Text1,
            AutoSize = true,
            Margin = new Padding(0, P(4), 0, P(2)),
        };
        _autostart.CheckedChanged += (_, _) => { if (!_loading) AutostartChanged?.Invoke(_autostart.Checked); };
        _flow.Controls.Add(_autostart);

        _onTop = Check("Keep the detached panel above other windows", _working.PanelOnTop);
        _flow.Controls.Add(_onTop);
        _flow.Controls.Add(Hint("Click the pin next to \"Toasty\", or drag the header, to detach the panel: it stays open wherever you put it (another screen too) and comes back there after a restart. Click the pin again to dock it."));
        _flow.Controls.Add(Hint("Drag the panel's top or bottom edge to change its height; double-click the edge to fit the content again."));

        var version = typeof(SettingsView).Assembly.GetName().Version;
        _flow.Controls.Add(Hint($"Toasty {version?.ToString(3)}  ·  settings are saved automatically"));
    }

    // ---------- applying ----------

    private void Changed()
    {
        if (_loading) return;
        RefreshPreview();
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    /// <summary>Reads the controls into a settings object. Metrics with out-of-order thresholds keep their previous values.</summary>
    private AppSettings Build()
    {
        var current = _owner.CurrentSettings;
        var s = current.Clone();
        s.IconStyle = IconStyles.All[Math.Max(0, _style.SelectedIndex)];
        s.SolidBackground = _solid.Checked;
        s.IntervalMs = (int)_interval.Value;
        s.SmoothingSeconds = (int)_smoothing.Value;
        s.AlertCooldownMinutes = (int)_cooldown.Value;
        s.Alerts = _working.Alerts.Select(a => a.Clone()).ToList();
        s.PanelOnTop = _onTop.Checked;
        s.RecordSessions = _record.Checked;
        s.CaptureFrames = _frames.Checked;
        s.SessionRetentionDays = (int)_retention.Value;
        s.SessionMaxMb = (int)_maxMb.Value;
        s.SessionMinMinutes = (int)_minMinutes.Value;
        s.SessionTimeline = _timeline.Checked;
        s.SessionTimelineSeconds = (int)_timelineSeconds.Value;
        s.AlwaysTrack = ParseList(_always.Text);
        s.NeverTrack = ParseList(_never.Text);
        s.PsuWatts = (int)_psu.Value;
        s.OtherComponentsWatts = (int)_otherWatts.Value;

        foreach (var (id, ed) in _editors)
        {
            var m = ReadEditor(ed);
            bool ascending = true;
            for (int i = 1; i < m.Thresholds.Length; i++) ascending &= m.Thresholds[i] > m.Thresholds[i - 1];
            ed.Error.Visible = !ascending;
            if (!ascending)
            {
                // Still honour visibility/style changes; keep the last valid thresholds and colours.
                var keep = current.Metrics[id].Clone();
                keep.Enabled = m.Enabled;
                keep.Style = m.Style;
                m = keep;
            }
            s.Metrics[id] = m;
        }
        return s;
    }

    private static MetricSettings ReadEditor(MetricEditor ed) => new()
    {
        Enabled = ed.Enabled.Checked,
        Style = ed.Style.SelectedIndex > 0 ? IconStyles.All[ed.Style.SelectedIndex - 1] : null,
        Thresholds = ed.Thresholds.Select(t => (double)t.Value).ToArray(),
        Colors = ed.Swatches.Select(b => ColorTranslator.ToHtml(Color.FromArgb(b.BackColor.R, b.BackColor.G, b.BackColor.B))).ToArray(),
    };

    private void Apply()
    {
        _owner.SaveSettings(Build());
        _owner.ApplyPinState();
        UpdateUsage(); // retention or the size cap may have just removed sessions
        _owner.LayoutPanel(null); // an error line may have appeared or gone
    }

    public void RefreshPreview()
    {
        if (_editors.Count == 0 || _preview == null) return;
        var latest = _owner.Latest.Values;
        bool light = IconRenderer.LightTaskbar();
        var defaultStyle = IconStyles.All[Math.Max(0, _style.SelectedIndex)];
        var icons = new List<(Bitmap, string)>();
        foreach (var (id, ed) in _editors)
        {
            var m = ReadEditor(ed);
            if (!m.Enabled) continue;
            double? v = latest.GetValueOrDefault(id);
            var style = m.Style ?? defaultStyle;
            double fraction = v is double x ? m.Fraction(x, Metrics.IsTemperature(id)) : 0;
            var color = v is double y ? m.ColorFor(y) : Color.Gray;
            icons.Add((IconRenderer.RenderBitmap(style, v, fraction, color, Metrics.Tag(id), _solid.Checked, light, 16),
                Metrics.Tag(id)));
        }
        _preview.SetIcons(icons, light);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _applyTimer.Dispose();
            _headingFont?.Dispose();
            _smallFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Shows the icons as they'd appear in the tray, magnified 2x, on a taskbar-coloured strip.</summary>
internal sealed class PreviewStrip : Control
{
    private const int Zoom = 2;
    private readonly Func<float, int> _px;
    private List<(Bitmap Icon, string Caption)> _icons = [];
    private bool _light;

    public PreviewStrip(Func<float, int> px)
    {
        _px = px;
        DoubleBuffered = true;
        Size = new Size(px(340), px(58));
    }

    private int Cell => _px(46);

    public void SetIcons(List<(Bitmap, string)> icons, bool light)
    {
        foreach (var (bmp, _) in _icons) bmp.Dispose();
        _icons = icons;
        _light = light;
        Width = Math.Max(_px(160), Cell * icons.Count + _px(12));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var bar = _light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(32, 32, 32);
        var caption = _light ? Color.DimGray : Color.Silver;
        g.Clear(bar);
        if (_icons.Count == 0)
        {
            TextRenderer.DrawText(g, "No icons enabled", Font, new Point(_px(10), _px(20)), caption);
            return;
        }
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        int x = _px(6);
        foreach (var (icon, text) in _icons)
        {
            int s = _px(icon.Width * Zoom);
            g.DrawImage(icon, x + (Cell - s) / 2, _px(5), s, s);
            TextRenderer.DrawText(g, text, Font, new Rectangle(x, _px(5) + s + _px(2), Cell, _px(16)), caption,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            x += Cell;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var (bmp, _) in _icons) bmp.Dispose();
        base.Dispose(disposing);
    }
}
