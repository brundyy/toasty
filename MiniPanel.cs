using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Toasty;

public enum PanelView { Summary, Sensors, Games, Settings }

/// <summary>
/// The pop-up shown when you click a tray icon. Three tabs: a summary (every metric with a
/// live graph and session peak, per-core CPU, memory details, GPU extras), every sensor
/// LibreHardwareMonitor can see, and all of Toasty's settings.
/// </summary>
public sealed class MiniPanel : Form, IMessageFilter
{
    /// <summary>Raised when the open view changes or the panel opens/closes; the tray app adjusts sensor polling.</summary>
    public event Action<PanelView, bool>? ViewChanged;

    internal static readonly Color Bg = Color.FromArgb(24, 24, 27);
    internal static readonly Color Card = Color.FromArgb(32, 32, 36);
    internal static readonly Color Line = Color.FromArgb(48, 48, 54);
    internal static readonly Color Text1 = Color.FromArgb(244, 244, 245);
    internal static readonly Color Text2 = Color.FromArgb(161, 161, 170);
    internal static readonly Color Text3 = Color.FromArgb(113, 113, 122);
    internal static readonly Color Accent = Color.FromArgb(251, 146, 60);

    internal const int BaseWidth = 420;
    private const int HeaderHeight = 48;
    private const int GraphSamples = 120; // two minutes at the default 1s interval
    private const int MinBodyHeight = 120;
    private const int ResizeGrip = 6;

    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _saveSettings;
    private readonly Action<AppSettings> _saveUiState;
    private readonly Dictionary<MetricId, MetricTracker> _trackers;
    private readonly HeaderBar _header;
    private readonly BodyPanel _body;
    public SettingsView SettingsView { get; }
    // Fonts are sized in pixels from the panel's own DPI so text rescales when it's dragged
    // to a monitor with different scaling (point sizes would stay at the primary screen's DPI).
    private Font _sectionFont = null!, _font = null!, _valueFont = null!, _smallFont = null!;
    private readonly DateTime _sessionStart = DateTime.Now;
    private readonly List<(Rectangle Area, Action Click)> _hotspots = [];

    /// <summary>Set by the tray app; the Games tab reads saved sessions and the live recording from these.</summary>
    internal SessionStore? Store;
    internal SessionRecorder? Recorder;
    private GameSession? _selected;

    private Snapshot _snapshot = Snapshot.Empty;
    private PanelView _view = PanelView.Summary;
    private DateTime _hiddenAt = DateTime.MinValue;
    private int _suppressHide;
    private int? _anchorBottom;
    private bool _sizing;
    private int _heightAtSizeStart;

    /// <param name="saveSettings">Saves and applies a settings change (icons, alerts...).</param>
    /// <param name="saveUiState">Saves panel-only state (position, open sections) without re-applying anything.</param>
    public MiniPanel(Func<AppSettings> settings, Action<AppSettings> saveSettings, Action<AppSettings> saveUiState,
        Dictionary<MetricId, MetricTracker> trackers)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _saveUiState = saveUiState;
        _trackers = trackers;
        RebuildFonts();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        ForeColor = Text1;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "Toasty";
        Icon = IconRenderer.Logo(32);

        _body = new BodyPanel(this) { Dock = DockStyle.Fill, BackColor = Bg };
        SettingsView = new SettingsView(this) { Dock = DockStyle.Fill, Visible = false };
        _header = new HeaderBar(this) { Dock = DockStyle.Top };
        // Dock order: fill controls first, the top bar last so it claims its strip.
        Controls.Add(_body);
        Controls.Add(SettingsView);
        Controls.Add(_header);

        // A detached panel stays open; the pop-up closes when you click elsewhere or press Esc.
        Deactivate += (_, _) => { if (_suppressHide == 0 && !Pinned) HidePanel(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && !Pinned) HidePanel(); };
        ResizeEnd += (_, _) => OnMoved();
        Application.AddMessageFilter(this);
    }

    /// <summary>
    /// The mouse wheel scrolls whichever tab is showing whenever the pointer is over the panel,
    /// regardless of which control (if any) has focus or whether Windows' "scroll inactive
    /// windows" setting is on. Otherwise the wheel went to a focused dropdown (changing its value)
    /// or, with nothing focused, to the form itself (scrolling nothing).
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != 0x020A /* WM_MOUSEWHEEL */ || !Visible) return false;
        if (!Bounds.Contains(Cursor.Position)) return false;
        if (_view == PanelView.Settings && SettingsView.HasOpenDropDown) return false; // let the open list scroll
        int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
        if (_view == PanelView.Settings) SettingsView.ScrollByWheel(delta);
        else
        {
            int step = Px(48) * Math.Max(1, SystemInformation.MouseWheelScrollLines) / 3;
            int y = -_body.AutoScrollPosition.Y - delta * step / 120;
            _body.AutoScrollPosition = new Point(0, Math.Max(0, y));
            _body.Invalidate();
        }
        return true;
    }

    internal static Font PixelFont(string family, float points, float scale) =>
        new(family, points * 96f / 72f * scale, GraphicsUnit.Pixel);

    private void RebuildFonts()
    {
        _sectionFont?.Dispose(); _font?.Dispose(); _valueFont?.Dispose(); _smallFont?.Dispose();
        _sectionFont = PixelFont("Segoe UI Semibold", 9.5f, S);
        _font = PixelFont("Segoe UI", 9f, S);
        _valueFont = PixelFont("Segoe UI Semibold", 11f, S);
        _smallFont = PixelFont("Segoe UI", 7.5f, S);
    }

    internal bool Pinned => _settings().PanelPinned;

    internal float S => DeviceDpi / 96f;
    internal int Px(float v) => (int)Math.Round(v * S);
    internal AppSettings CurrentSettings => _settings();
    internal Snapshot Latest => _snapshot;
    internal PanelView View => _view;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW: keep it out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounded corners; ignored on Windows 10.
        int round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    /// <summary>Clicking the tray icon while the panel is open first deactivates (hides) it; don't immediately reopen.</summary>
    public bool JustHidden => (DateTime.Now - _hiddenAt).TotalMilliseconds < 350;

    /// <summary>Keeps the panel open while a modal dialog (e.g. a colour picker) owned by it is showing.</summary>
    internal T WithoutAutoHide<T>(Func<T> action)
    {
        _suppressHide++;
        try { return action(); }
        finally
        {
            _suppressHide--;
            Activate();
        }
    }

    public void ShowNear(Point cursor, PanelView? view = null)
    {
        if (view is PanelView v) SetView(v, notify: false);
        else if (!Visible && _view == PanelView.Settings) SetView(PanelView.Summary, notify: false);
        if (_view == PanelView.Settings) SettingsView.LoadFrom(_settings());
        if (Pinned) PlacePinned(cursor); else LayoutPanel(cursor);
        TopMost = !Pinned || _settings().PanelOnTop;
        Show();
        Activate();
        if (_view == PanelView.Settings) SettingsView.Focus(); else _body.Focus();
        if (Pinned) SaveUiState(s => s.PanelPinnedOpen = true);
        ViewChanged?.Invoke(_view, true);
    }

    public void HidePanel()
    {
        if (!Visible) return;
        Hide();
        _hiddenAt = DateTime.Now;
        if (Pinned) SaveUiState(s => s.PanelPinnedOpen = false);
        ViewChanged?.Invoke(_view, false);
    }

    private void SaveUiState(Action<AppSettings> change)
    {
        var s = _settings().Clone();
        change(s);
        _saveUiState(s);
    }

    // ---------- detached mode ----------

    /// <summary>Puts a detached panel back where it was left, if that spot is still on a screen.</summary>
    private void PlacePinned(Point cursor)
    {
        var s = _settings();
        if (s.PanelX is int x && s.PanelY is int y
            && Screen.AllScreens.Any(scr => scr.WorkingArea.Contains(x + Px(40), y + Px(20))))
        {
            _anchorBottom = null;
            Location = new Point(x, y);
            LayoutPanel(null);
        }
        else
        {
            LayoutPanel(cursor); // monitor unplugged or never placed: start by the tray
            _anchorBottom = null;
        }
    }

    /// <summary>Detach (stays open, movable, remembers its spot) or dock back to the tray pop-up.</summary>
    internal void SetPinned(bool pinned)
    {
        if (pinned == Pinned) return;
        SaveUiState(s =>
        {
            s.PanelPinned = pinned;
            s.PanelPinnedOpen = pinned && Visible;
            s.PanelX = Left;
            s.PanelY = Top;
        });
        if (pinned) _anchorBottom = null;
        TopMost = !pinned || _settings().PanelOnTop;
        _header.Invalidate();
    }

    /// <summary>Starts a native window drag from the header (detaching first if needed).</summary>
    internal void BeginDrag()
    {
        if (!Pinned) SetPinned(true);
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    /// <summary>After a drag: remember the spot and refit to the (possibly different) screen.</summary>
    private void OnMoved()
    {
        if (!Pinned) return;
        LayoutPanel(null);
        SaveUiState(s => { s.PanelX = Left; s.PanelY = Top; });
    }

    // ---------- height resizing ----------

    private const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDBLCLK = 0xA3, WM_ENTERSIZEMOVE = 0x231, WM_EXITSIZEMOVE = 0x232;
    private const int HTTOP = 12, HTBOTTOM = 15, HTTRANSPARENT = -1;

    internal static Point ScreenPoint(IntPtr lParam)
    {
        long v = lParam.ToInt64();
        return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    /// <summary>Is this screen point on the panel's top or bottom resize strip?</summary>
    internal int EdgeAt(Point screen)
    {
        var p = PointToClient(screen);
        if (p.X < 0 || p.X >= ClientSize.Width) return 0;
        if (p.Y >= 0 && p.Y < Px(ResizeGrip)) return HTTOP;
        if (p.Y < ClientSize.Height && p.Y >= ClientSize.Height - Px(ResizeGrip)) return HTBOTTOM;
        return 0;
    }

    /// <summary>For child controls: let edge hit-tests fall through to the panel so it can be resized.</summary>
    internal bool PassEdgeHitTest(ref Message m)
    {
        if (m.Msg != WM_NCHITTEST || EdgeAt(ScreenPoint(m.LParam)) == 0) return false;
        m.Result = (IntPtr)HTTRANSPARENT;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
                base.WndProc(ref m);
                if (EdgeAt(ScreenPoint(m.LParam)) is int edge and > 0) m.Result = (IntPtr)edge;
                return;
            case WM_ENTERSIZEMOVE:
                _sizing = true;
                _heightAtSizeStart = Height;
                break;
            case WM_EXITSIZEMOVE:
                _sizing = false;
                if (Height != _heightAtSizeStart)
                    SaveUiState(s => s.PanelHeight = (int)Math.Round(Height / S));
                break;
            case WM_NCLBUTTONDBLCLK when (int)m.WParam is HTTOP or HTBOTTOM:
                // Double-click an edge: go back to fitting the content.
                SaveUiState(s => s.PanelHeight = null);
                LayoutPanel(null);
                _body.Invalidate();
                return;
        }
        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // While dragging the top edge of the docked pop-up, the bottom (taskbar side) stays put.
        if (_sizing && _anchorBottom != null) _anchorBottom = Bottom;
        _body?.Invalidate();
    }

    /// <summary>Re-reads "keep on top" after a settings change.</summary>
    internal void ApplyPinState() => TopMost = !Pinned || _settings().PanelOnTop;

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RebuildFonts();
        _header.RebuildForDpi();
        if (_view == PanelView.Settings) SettingsView.LoadFrom(_settings());
        LayoutPanel(null);
        _header.Invalidate();
        _body.Invalidate();
    }

    internal void SetView(PanelView view, bool notify = true)
    {
        if (view == PanelView.Settings && _view != PanelView.Settings) SettingsView.LoadFrom(_settings());
        if (view != PanelView.Games) _selected = null;
        _view = view;
        SettingsView.Visible = view == PanelView.Settings;
        _body.Visible = view != PanelView.Settings;
        _body.AutoScrollPosition = Point.Empty;
        _header.Invalidate();
        if (!notify) return;
        LayoutPanel(null);
        _body.Invalidate();
        if (view == PanelView.Settings) SettingsView.Focus(); else _body.Focus();
        ViewChanged?.Invoke(view, Visible);
    }

    public void UpdateData(Snapshot snapshot)
    {
        _snapshot = snapshot;
        if (!Visible) return;
        if (_view == PanelView.Settings)
        {
            SettingsView.RefreshPreview();
            return;
        }
        LayoutPanel(null);
        _body.Invalidate();
    }

    private void ToggleCores()
    {
        SaveUiState(s => s.PanelCoresExpanded = !s.PanelCoresExpanded);
        LayoutPanel(null);
        _body.Invalidate();
    }

    private void ToggleMemory()
    {
        SaveUiState(s => s.PanelMemoryDetailsOpen = !s.PanelMemoryDetailsOpen);
        LayoutPanel(null);
        _body.Invalidate();
    }

    internal void SaveSettings(AppSettings s) => _saveSettings(s);

    // ---------- sizing ----------

    /// <summary>
    /// Sizes the panel to its content, up to the screen's height, and places it. While open,
    /// the edge nearest the taskbar stays put so expanding a section grows away from it.
    /// </summary>
    internal void LayoutPanel(Point? cursor)
    {
        var wa = UsableArea(cursor is Point c ? Screen.FromPoint(c) : Screen.FromControl(this));
        int gap = Px(12);

        int content = _view == PanelView.Settings ? SettingsView.ContentHeight : Render(null, 0, Px(BaseWidth));
        if (_view != PanelView.Settings) _body.AutoScrollMinSize = new Size(0, content);
        if (_sizing) return; // the user is dragging an edge; don't fight them

        int maxBody = wa.Height - Px(HeaderHeight) - gap * 2;
        // A height the user dragged to wins over fitting the content (content scrolls or leaves spare room).
        int bodyHeight = _settings().PanelHeight is int chosen
            ? Math.Clamp(Px(chosen) - Px(HeaderHeight), Px(MinBodyHeight), maxBody)
            : Math.Min(content, maxBody);
        var size = new Size(Px(BaseWidth), Px(HeaderHeight) + bodyHeight);
        if (cursor is Point p)
        {
            int x = Math.Clamp(p.X - size.Width / 2, wa.Left + gap, wa.Right - size.Width - gap);
            int y = p.Y >= wa.Bottom ? wa.Bottom - size.Height - gap          // taskbar at the bottom
                  : p.Y <= wa.Top ? wa.Top + gap                              // taskbar at the top
                  : Math.Clamp(p.Y - size.Height - gap, wa.Top + gap, wa.Bottom - size.Height - gap);
            _anchorBottom = p.Y <= wa.Top ? null : y + size.Height;
            Bounds = new Rectangle(new Point(x, y), size);
        }
        else if (Size != size)
        {
            int top = _anchorBottom is int bottom ? Math.Max(wa.Top + gap, bottom - size.Height) : Top;
            // Never let growth push the panel past the usable area (i.e. over the taskbar).
            top = Math.Min(top, wa.Bottom - gap - size.Height);
            Bounds = new Rectangle(Left, Math.Max(wa.Top + gap, top), size.Width, size.Height);
        }
    }

    /// <summary>
    /// The screen's working area, additionally excluding the taskbar's real rectangle. The
    /// working area alone includes the taskbar when it's set to auto-hide, which let the panel
    /// sit on top of it.
    /// </summary>
    private static Rectangle UsableArea(Screen screen)
    {
        var area = screen.WorkingArea;
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
        if (SHAppBarMessage(5 /* ABM_GETTASKBARPOS */, ref data) == IntPtr.Zero) return area;
        var bar = Rectangle.FromLTRB(data.Rect.Left, data.Rect.Top, data.Rect.Right, data.Rect.Bottom);
        if (!bar.IntersectsWith(screen.Bounds)) return area;
        return data.Edge switch
        {
            0 => Rectangle.FromLTRB(Math.Max(area.Left, bar.Right), area.Top, area.Right, area.Bottom),   // left
            1 => Rectangle.FromLTRB(area.Left, Math.Max(area.Top, bar.Bottom), area.Right, area.Bottom),  // top
            2 => Rectangle.FromLTRB(area.Left, area.Top, Math.Min(area.Right, bar.Left), area.Bottom),    // right
            _ => Rectangle.FromLTRB(area.Left, area.Top, area.Right, Math.Min(area.Bottom, bar.Top)),     // bottom
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Rect;
        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

    // ---------- painting ----------

    /// <summary>Draws (or, with g == null, just measures) the body at width w. Returns the content height.</summary>
    private int Render(Graphics? g, int scrollY, int w)
    {
        if (g != null) _hotspots.Clear();
        int pad = Px(14);
        int y = scrollY;

        y = _view switch
        {
            PanelView.Sensors => RenderAllSensors(g, y, w, pad),
            PanelView.Games => _selected != null ? RenderSession(g, y, w, pad, _selected) : RenderGames(g, y, w, pad),
            _ => RenderSummary(g, y, w, pad),
        };
        y += Px(6);
        if (g != null)
        {
            string footer = _view switch
            {
                PanelView.Sensors => "Max is since each sensor was first read",
                PanelView.Games => StoreFooter(),
                _ => $"Smoothed over {_settings().SmoothingSeconds}s  ·  peaks since {_sessionStart:HH:mm}",
            };
            DrawText(g, footer, _smallFont, Text3, new Rectangle(pad, y, w - pad * 2, Px(16)), TextFormatFlags.Left);
        }
        y += Px(24);
        return y - scrollY;
    }

    private int RenderSummary(Graphics? g, int y, int w, int pad)
    {
        var s = _snapshot;
        var settings = _settings();

        y = Section(g, y, w, pad, "CPU", s.CpuName, (rg, sy) =>
        {
            sy = MetricRow(rg, sy, w, pad, "Temperature", MetricId.CpuTemp);
            sy = MetricRow(rg, sy, w, pad, "Usage", MetricId.CpuLoad);
            sy = DetailRow(rg, sy, w, pad, "Power", Fmt(s.CpuPower, "0", " W"));
            sy = DetailRow(rg, sy, w, pad, "Clock (avg)", s.CpuClockMhz is double c ? $"{c / 1000:0.00} GHz" : null);
            if (s.Cores.Count > 0)
            {
                sy = Expander(rg, sy, w, pad, $"Cores ({s.Cores.Count})", settings.PanelCoresExpanded, ToggleCores);
                if (settings.PanelCoresExpanded) sy = CoreTable(rg, sy, w, pad, s.Cores);
            }
            return sy;
        });

        y = Section(g, y, w, pad, "GPU", s.GpuName, (rg, sy) =>
        {
            sy = MetricRow(rg, sy, w, pad, "Temperature", MetricId.GpuTemp);
            if (s.Values.GetValueOrDefault(MetricId.GpuHotspot) != null)
                sy = MetricRow(rg, sy, w, pad, "Hotspot", MetricId.GpuHotspot);
            if (s.Values.GetValueOrDefault(MetricId.GpuMemJunction) != null)
                sy = MetricRow(rg, sy, w, pad, "Memory junction", MetricId.GpuMemJunction);
            sy = MetricRow(rg, sy, w, pad, "Usage", MetricId.GpuLoad);
            sy = DetailRow(rg, sy, w, pad, "VRAM",
                s.VramUsedMb is double u && s.VramTotalMb is double t ? $"{u / 1024:0.0} / {t / 1024:0.0} GB" : null);
            sy = DetailRow(rg, sy, w, pad, "Power", Fmt(s.GpuPower, "0", " W"));
            string? fan = s.GpuFanRpm is double rpm
                ? (s.GpuFanPercent is double pct ? $"{rpm:0} RPM ({pct:0}%)" : $"{rpm:0} RPM")
                : Fmt(s.GpuFanPercent, "0", "%");
            sy = DetailRow(rg, sy, w, pad, "Fan", fan);
            sy = DetailRow(rg, sy, w, pad, "Core clock", Fmt(s.GpuClockMhz, "0", " MHz"));
            return sy;
        });

        y = Section(g, y, w, pad, "Memory", ModulesSummary(s.Modules), (rg, sy) =>
        {
            sy = MetricRow(rg, sy, w, pad, "Usage", MetricId.RamLoad);
            sy = DetailRow(rg, sy, w, pad, "In use", s.RamTotalGb > 0 ? $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} GB" : null);
            sy = DetailRow(rg, sy, w, pad, "Available", Fmt(s.RamAvailableGb, "0.0", " GB"));
            sy = Expander(rg, sy, w, pad, "Details", settings.PanelMemoryDetailsOpen, ToggleMemory);
            if (settings.PanelMemoryDetailsOpen)
            {
                sy = DetailRow(rg, sy, w, pad, "Cached", Fmt(s.RamCachedGb, "0.0", " GB"));
                sy = DetailRow(rg, sy, w, pad, "Committed",
                    s.CommitUsedGb is double cu && s.CommitLimitGb is double cl ? $"{cu:0.0} / {cl:0.0} GB" : null);
                sy = DetailRow(rg, sy, w, pad, "Kernel paged / non-paged",
                    s.KernelPagedGb is double kp && s.KernelNonPagedGb is double kn ? $"{kp:0.00} / {kn:0.00} GB" : null);
                if (s.Modules is { } m)
                {
                    sy = DetailRow(rg, sy, w, pad, "Modules", $"{m.Count} × {m.TotalGb / m.Count:0} GB{(m.Type != null ? " " + m.Type : "")}");
                    sy = DetailRow(rg, sy, w, pad, "Speed", m.SpeedMts is int mts ? $"{mts} MT/s" : null);
                    sy = DetailRow(rg, sy, w, pad, "Part number", m.PartNumber);
                }
                if (s.DimmTemps.Count > 0)
                    sy = DetailRow(rg, sy, w, pad, s.DimmTemps.Count == 1 ? "DIMM temperature" : "DIMM temperatures",
                        string.Join(" / ", s.DimmTemps.Select(d => $"{d.Celsius:0}°C")));
            }
            return sy;
        });

        if (s.CpuPower != null || s.GpuPower != null || s.PsuPowerW != null)
        {
            bool measured = s.PsuPowerW != null;
            y = Section(g, y, w, pad, "Power", measured ? $"measured by {s.PsuName}" : "estimated", (rg, sy) =>
            {
                sy = DetailRow(rg, sy, w, pad, "CPU package", Fmt(s.CpuPower, "0", " W"));
                sy = DetailRow(rg, sy, w, pad, "GPU board", Fmt(s.GpuPower, "0", " W"));
                if (!measured)
                    sy = DetailRow(rg, sy, w, pad, "Rest of system (allowance)", $"~{settings.OtherComponentsWatts} W");
                double? total = settings.EstimateSystemWatts(s);
                sy = DetailRow(rg, sy, w, pad, measured ? "Whole system" : "Total", Fmt(total, "0", " W"));
                int psu = settings.EffectivePsuWatts(s);
                if (psu > 0 && total is double t)
                    sy = LoadBarRow(rg, sy, w, pad, $"of {psu} W PSU", 100 * t / psu);
                else
                    sy = DetailRow(rg, sy, w, pad, "Power supply", "set its wattage in Settings");
                return sy;
            });
        }
        return y;
    }

    /// <summary>Label, a bar, and a percentage coloured green → red as it fills (PSU load).</summary>
    private int LoadBarRow(Graphics? g, int y, int w, int pad, string label, double pct)
    {
        int h = Px(22);
        if (g == null) return y + h;
        int left = pad + Px(4), right = w - pad - Px(4);
        var color = pct < 50 ? Color.FromArgb(0x4A, 0xDE, 0x80) : pct < 70 ? Color.FromArgb(0xFA, 0xCC, 0x15)
                  : pct < 85 ? Color.FromArgb(0xFB, 0x92, 0x3C) : Color.FromArgb(0xEF, 0x44, 0x44);
        DrawText(g, label, _font, Text3, new Rectangle(left, y, Px(150), h), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        var bar = new Rectangle(left + Px(156), y + h / 2 - Px(3), right - left - Px(156) - Px(52), Px(6));
        using (var track = new SolidBrush(Color.FromArgb(22, 255, 255, 255))) g.FillRectangle(track, bar);
        using (var b = new SolidBrush(color)) g.FillRectangle(b, bar.X, bar.Y, (int)(bar.Width * Math.Clamp(pct / 100, 0, 1)), bar.Height);
        DrawText(g, $"{pct:0}%", _font, color, new Rectangle(right - Px(48), y, Px(48), h), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        return y + h;
    }

    private static string? ModulesSummary(MemoryModules? m) =>
        m == null ? null : $"{m.TotalGb:0} GB{(m.Type != null ? " " + m.Type : "")}{(m.SpeedMts is int s ? $" @ {s}" : "")}";

    private int Section(Graphics? g, int y, int w, int pad, string title, string? subtitle, Func<Graphics?, int, int> rows)
    {
        y += Px(10);
        int top = y;
        int inner = y + Px(30);
        int end = rows(null, inner) + Px(6); // measure first so the card can be drawn behind the rows

        if (g != null)
        {
            var rect = new Rectangle(pad - Px(6), top, w - (pad - Px(6)) * 2, end - top);
            using (var path = Rounded(rect, Px(8)))
            using (var b = new SolidBrush(Card))
                g.FillPath(b, path);

            rows(g, inner);

            var titleSize = TextRenderer.MeasureText(g, title, _sectionFont);
            DrawText(g, title, _sectionFont, Text1, new Rectangle(pad + Px(4), top + Px(8), titleSize.Width, Px(18)), TextFormatFlags.Left);
            if (!string.IsNullOrEmpty(subtitle))
            {
                int sx = pad + Px(4) + titleSize.Width + Px(6);
                DrawText(g, subtitle, _font, Text3, new Rectangle(sx, top + Px(8), w - sx - pad - Px(4), Px(18)),
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }
        return end;
    }

    /// <summary>A headline metric: name and peak on the left, a two-minute graph, the coloured value on the right.</summary>
    private int MetricRow(Graphics? g, int y, int w, int pad, string label, MetricId id)
    {
        int h = Px(38);
        if (g == null || !_trackers.TryGetValue(id, out var tracker)) return y + h;

        var metric = _settings().Metrics[id];
        int band = tracker.Band(metric);
        var color = band >= 0 ? metric.ColorOfBand(band) : Text3;
        string unit = Metrics.Unit(id);

        int left = pad + Px(4);
        int right = w - pad - Px(4);
        int valueW = Px(62);
        int labelW = Px(112);
        DrawText(g, label, _font, Text2, new Rectangle(left, y + Px(3), labelW, Px(18)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (tracker.SessionMax is double peak)
            DrawText(g, $"peak {peak:0}{unit}", _smallFont, Text3, new Rectangle(left, y + Px(20), labelW, Px(14)), TextFormatFlags.Left);

        int graphX = left + labelW + Px(6);
        var graph = new Rectangle(graphX, y + Px(5), right - valueW - Px(8) - graphX, Px(26));
        Sparkline(g, graph, tracker.History(GraphSamples).ToList(), color, Metrics.IsTemperature(id));

        string value = tracker.Smoothed is double v ? $"{v:0}{unit}" : "-";
        DrawText(g, value, _valueFont, color, new Rectangle(right - valueW, y + Px(4), valueW, Px(26)),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        return y + h;
    }

    private int DetailRow(Graphics? g, int y, int w, int pad, string label, string? value)
    {
        int h = Px(20);
        if (value == null) return y; // hide rows the hardware doesn't report
        if (g == null) return y + h;
        int left = pad + Px(4);
        int right = w - pad - Px(4);
        int labelW = Px(160);
        DrawText(g, label, _font, Text3, new Rectangle(left, y, labelW, h), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        DrawText(g, value, _font, Text1, new Rectangle(left + labelW + Px(4), y, right - left - labelW - Px(4), h),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        return y + h;
    }

    /// <summary>A clickable chevron + title line that opens or closes the rows beneath it.</summary>
    private int Expander(Graphics? g, int y, int w, int pad, string title, bool expanded, Action toggle)
    {
        int h = Px(26);
        if (g == null) return y + h;
        int left = pad + Px(4);
        int right = w - pad - Px(4);
        using (var pen = new Pen(Line)) g.DrawLine(pen, left, y + Px(3), right, y + Px(3));

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = left + Px(5), cy = y + h / 2f + Px(2);
        float a = Px(3.5f);
        PointF[] chevron = expanded
            ? [new(cx - a, cy - a / 2), new(cx, cy + a / 2), new(cx + a, cy - a / 2)]
            : [new(cx - a / 2, cy - a), new(cx + a / 2, cy), new(cx - a / 2, cy + a)];
        using (var pen = new Pen(Accent, Math.Max(1.5f, 1.5f * S)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLines(pen, chevron);
        g.SmoothingMode = old;

        DrawText(g, title, _font, Accent, new Rectangle(left + Px(16), y + Px(5), Px(220), h - Px(6)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        _hotspots.Add((new Rectangle(left, y + Px(4), right - left, h - Px(4)), toggle));
        return y + h;
    }

    /// <summary>Per-core rows: number, a load bar coloured like CPU usage, load %, clock, and power or temperature.</summary>
    private int CoreTable(Graphics? g, int y, int w, int pad, IReadOnlyList<CoreInfo> cores)
    {
        int rowH = Px(19);
        int headerH = Px(16);
        if (g == null) return y + headerH + rowH * cores.Count + Px(4);

        bool hasPower = cores.Any(c => c.PowerW != null);
        bool hasTemp = cores.Any(c => c.TemperatureC != null);
        string lastCol = hasPower ? "power" : hasTemp ? "temp" : "";

        int left = pad + Px(4);
        int right = w - pad - Px(4);
        int colLast = right - Px(50);
        int colClock = colLast - Px(70);
        int colPct = colClock - Px(42);
        int barX = left + Px(52);
        int barW = colPct - barX - Px(8);

        DrawText(g, "load", _smallFont, Text3, new Rectangle(barX, y, barW, headerH), TextFormatFlags.Left);
        DrawText(g, "clock", _smallFont, Text3, new Rectangle(colClock, y, Px(66), headerH), TextFormatFlags.Right);
        if (lastCol != "") DrawText(g, lastCol, _smallFont, Text3, new Rectangle(colLast, y, Px(50), headerH), TextFormatFlags.Right);
        y += headerH;

        var loadSettings = _settings().Metrics[MetricId.CpuLoad];
        double maxClock = cores.Max(c => c.ClockMhz ?? 0);
        foreach (var core in cores)
        {
            DrawText(g, $"Core {core.Number}", _font, Text2, new Rectangle(left, y, Px(50), rowH), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            var bar = new Rectangle(barX, y + rowH / 2 - Px(3), barW, Px(6));
            using (var track = new SolidBrush(Color.FromArgb(22, 255, 255, 255))) g.FillRectangle(track, bar);
            if (core.Load is double load)
            {
                var c = loadSettings.ColorFor(load);
                using (var b = new SolidBrush(c))
                    g.FillRectangle(b, bar.X, bar.Y, (int)Math.Round(bar.Width * Math.Clamp(load / 100, 0, 1)), bar.Height);
                DrawText(g, $"{load:0}%", _font, c, new Rectangle(colPct, y, Px(40), rowH), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }

            if (core.ClockMhz is double mhz)
            {
                // The fastest core(s) right now are highlighted.
                var clockColor = maxClock > 0 && mhz >= maxClock - 1 ? Text1 : Text2;
                DrawText(g, $"{mhz / 1000:0.00} GHz", _font, clockColor, new Rectangle(colClock, y, Px(66), rowH), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }

            string? last = hasPower ? Fmt(core.PowerW, "0.0", " W") : hasTemp ? Fmt(core.TemperatureC, "0", "°C") : null;
            if (last != null)
                DrawText(g, last, _font, Text2, new Rectangle(colLast, y, Px(50), rowH), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            y += rowH;
        }
        return y + Px(4);
    }

    private void Sparkline(Graphics g, Rectangle r, List<double> samples, Color color, bool temperature)
    {
        if (r.Width < 10) return;
        using (var b = new SolidBrush(Color.FromArgb(20, 255, 255, 255)))
        using (var path = Rounded(r, Px(4)))
            g.FillPath(b, path);
        if (samples.Count < 2) return;

        // Usage is always 0-100%; temperatures zoom to their recent range (at least 10° tall).
        double lo, hi;
        if (temperature)
        {
            lo = samples.Min() - 2;
            hi = samples.Max() + 2;
            if (hi - lo < 10) { double mid = (hi + lo) / 2; lo = mid - 5; hi = mid + 5; }
        }
        else
        {
            lo = 0; hi = 100;
        }

        var pts = new PointF[samples.Count];
        float step = (float)(r.Width - 2) / (GraphSamples - 1);
        float x0 = r.Right - 1 - step * (samples.Count - 1);
        for (int i = 0; i < samples.Count; i++)
        {
            float t = (float)((samples[i] - lo) / (hi - lo));
            pts[i] = new PointF(x0 + i * step, r.Bottom - 2 - Math.Clamp(t, 0, 1) * (r.Height - 4));
        }

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new GraphicsPath())
        {
            fill.AddLines(pts);
            fill.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, r.Bottom - 1);
            fill.AddLine(pts[^1].X, r.Bottom - 1, pts[0].X, r.Bottom - 1);
            fill.CloseFigure();
            using var b = new SolidBrush(Color.FromArgb(40, color));
            g.FillPath(b, fill);
        }
        using (var pen = new Pen(color, Math.Max(1.25f, 1.5f * S)) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, pts);
        g.SmoothingMode = old;
    }

    private int RenderAllSensors(Graphics? g, int y, int w, int pad)
    {
        var groups = _snapshot.All;
        if (groups.Count == 0)
        {
            if (g != null)
                DrawText(g, "Reading sensors...", _font, Text2, new Rectangle(pad, y + Px(12), w - pad * 2, Px(20)), TextFormatFlags.Left);
            return y + Px(40);
        }

        int left = pad + Px(4);
        int valueW = Px(84), maxW = Px(70);
        int maxX = w - pad - maxW;
        int valueX = maxX - valueW - Px(6);

        foreach (var group in groups)
        {
            y += Px(10);
            if (g != null)
            {
                DrawText(g, group.Name, _sectionFont, Text1, new Rectangle(pad, y, w - pad * 2 - maxW, Px(20)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                DrawText(g, "max", _smallFont, Text3, new Rectangle(maxX, y + Px(4), maxW, Px(14)), TextFormatFlags.Right);
            }
            y += Px(22);

            string? lastType = null;
            foreach (var r in group.Sensors)
            {
                if (r.Type != lastType)
                {
                    if (g != null)
                        DrawText(g, r.Type.ToUpperInvariant(), _smallFont, Accent, new Rectangle(left, y + Px(2), Px(200), Px(14)), TextFormatFlags.Left);
                    y += Px(16);
                    lastType = r.Type;
                }
                if (g != null)
                {
                    DrawText(g, r.Name, _font, Text2, new Rectangle(left + Px(6), y, valueX - left - Px(10), Px(18)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                    DrawText(g, FormatSensor(r.Value, r.Unit), _font, Text1, new Rectangle(valueX, y, valueW, Px(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    if (r.Max is double max)
                        DrawText(g, FormatSensor(max, r.Unit), _font, Text3, new Rectangle(maxX, y, maxW, Px(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
                y += Px(18);
            }
        }
        return y;
    }

    // ---------- games ----------

    private static readonly Color VerdictGpu = Color.FromArgb(0x4A, 0xDE, 0x80);
    private static readonly Color VerdictCpu = Color.FromArgb(0xFB, 0x92, 0x3C);
    private static readonly Color VerdictCapped = Color.FromArgb(0x22, 0xD3, 0xEE);
    private static readonly Color VerdictMixed = Color.FromArgb(0xFA, 0xCC, 0x15);
    private static readonly Color Danger = Color.FromArgb(0xF8, 0x71, 0x71);

    private static Color VerdictColor(string verdict) => verdict switch
    {
        "GPU-bound" => VerdictGpu,
        "CPU-bound" => VerdictCpu,
        "Capped" => VerdictCapped,
        "Mixed" => VerdictMixed,
        _ => Text3,
    };

    private static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(1, t.Minutes)}m";
    }

    private string StoreFooter()
    {
        if (Store == null) return "";
        var (count, bytes) = Store.Usage();
        var s = _settings();
        string keep = s.SessionRetentionDays > 0 ? $"kept {s.SessionRetentionDays} days" : "kept forever";
        return $"{count} sessions  ·  {SessionStore.FormatBytes(bytes)} of {s.SessionMaxMb} MB  ·  {keep}";
    }

    private int RenderGames(Graphics? g, int y, int w, int pad)
    {
        var settings = _settings();
        var live = Recorder?.Live;

        y = Section(g, y, w, pad, live != null ? "Recording" : "Game sessions", null, (rg, sy) =>
        {
            if (live is { } l)
            {
                string fps = l.Fps is double f ? $"{f:0} FPS now" : l.Frames ? "waiting for frames..." : "no frame data";
                sy = DetailRow(rg, sy, w, pad, l.Game, $"{Duration(l.Played.TotalSeconds)}  ·  {fps}");
                if (rg != null)
                    using (var dot = new SolidBrush(Danger))
                        rg.FillEllipse(dot, w - pad - Px(14), sy - Px(52), Px(8), Px(8));
            }
            else if (!settings.RecordSessions)
            {
                sy = Wrapped(rg, sy, w, pad, "Recording is off. Turn it on in Settings > Game sessions.", _font, Text2);
            }
            else
            {
                sy = Wrapped(rg, sy, w, pad, "Play a game and Toasty records it automatically: highs, lows and averages, FPS and 1% lows, and a bottleneck verdict. Sessions appear here when the game closes.", _font, Text2);
                if (Recorder != null)
                {
                    if (Recorder.LastResult is { } last)
                        sy = Wrapped(rg, sy + Px(2), w, pad, "Last session: " + last, _smallFont, Text2);
                    sy = Wrapped(rg, sy + Px(2), w, pad, Recorder.DetectionStatus, _smallFont, Text3);
                    if (Recorder.LastCandidate is { } candidate)
                    {
                        int h = Px(24);
                        if (rg != null)
                        {
                            var r = new Rectangle(pad + Px(4), sy + Px(2), w - pad * 2 - Px(8), h - Px(4));
                            DrawText(rg, $"●  Record {candidate.Name} now", _font, Accent, r, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                            _hotspots.Add((r, () =>
                            {
                                Recorder.StartManual(_settings());
                                LayoutPanel(null);
                                _body.Invalidate();
                            }));
                        }
                        sy += h;
                    }
                }
            }
            return sy;
        });

        var sessions = Store?.All() ?? [];
        if (sessions.Count == 0) return y;

        y = Section(g, y, w, pad, "History", $"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")}", (rg, sy) =>
        {
            foreach (var s in sessions.Take(200))
                sy = SessionRow(rg, sy, w, pad, s);
            return sy;
        });
        return y;
    }

    private int SessionRow(Graphics? g, int y, int w, int pad, GameSession s)
    {
        int h = Px(44);
        if (g == null) return y + h;
        int left = pad + Px(4), right = w - pad - Px(4);
        using (var pen = new Pen(Line)) g.DrawLine(pen, left, y, right, y);

        string fps = s.Frames is { } f ? $"{f.AvgFps:0} FPS" : "";
        int fpsW = fps.Length > 0 ? TextRenderer.MeasureText(g, fps, _sectionFont, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
        DrawText(g, s.Game, _sectionFont, Text1, new Rectangle(left, y + Px(5), right - left - fpsW - Px(8), Px(18)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (fps.Length > 0)
            DrawText(g, fps, _sectionFont, Text1, new Rectangle(right - fpsW, y + Px(5), fpsW, Px(18)), TextFormatFlags.Right);

        DrawText(g, $"{s.Start:ddd d MMM, HH:mm}  ·  {Duration(s.PlayedSeconds)}", _smallFont, Text3,
            new Rectangle(left, y + Px(25), Px(220), Px(14)), TextFormatFlags.Left);
        string tag = s.Frames is { } fr ? $"{s.Verdict}  ·  1% low {fr.Low1Fps:0}" : s.Verdict;
        DrawText(g, tag, _smallFont, VerdictColor(s.Verdict), new Rectangle(right - Px(170), y + Px(25), Px(170), Px(14)), TextFormatFlags.Right);

        _hotspots.Add((new Rectangle(left, y, right - left, h), () =>
        {
            _selected = s;
            _body.AutoScrollPosition = Point.Empty;
            LayoutPanel(null);
            _body.Invalidate();
        }));
        return y + h;
    }

    private int RenderSession(Graphics? g, int y, int w, int pad, GameSession s)
    {
        int left = pad + Px(4), right = w - pad - Px(4);

        // Back link
        y += Px(8);
        if (g != null)
        {
            DrawText(g, "‹  All sessions", _font, Accent, new Rectangle(left, y, Px(140), Px(20)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            _hotspots.Add((new Rectangle(left, y, Px(140), Px(20)), () =>
            {
                _selected = null;
                _body.AutoScrollPosition = Point.Empty;
                LayoutPanel(null);
                _body.Invalidate();
            }));
        }
        y += Px(24);
        if (g != null)
        {
            DrawText(g, s.Game, _valueFont, Text1, new Rectangle(left, y, right - left, Px(24)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            DrawText(g, $"{s.Start:ddd d MMM yyyy, HH:mm}  ·  played {Duration(s.PlayedSeconds)}", _smallFont, Text3,
                new Rectangle(left, y + Px(24), right - left, Px(14)), TextFormatFlags.Left);
        }
        y += Px(40);

        // Verdict and hints
        y = Section(g, y, w, pad, s.Verdict, "bottleneck hints", (rg, sy) =>
        {
            if (rg != null)
                using (var b = new SolidBrush(VerdictColor(s.Verdict)))
                    rg.FillRectangle(b, pad - Px(6), sy - Px(30), Px(3), Px(26));
            foreach (var hint in s.Hints)
                sy = Wrapped(rg, sy, w, pad, "•  " + hint, _font, Text2) + Px(4);
            if (s.Hints.Count == 0) sy = Wrapped(rg, sy, w, pad, "Not enough data for hints.", _font, Text3);
            return sy;
        });

        if (s.Frames is { } f)
        {
            y = Section(g, y, w, pad, "Frames", "PresentMon", (rg, sy) =>
            {
                sy = DetailRow(rg, sy, w, pad, "Average FPS", $"{f.AvgFps:0}");
                sy = DetailRow(rg, sy, w, pad, "1% low / 0.1% low", $"{f.Low1Fps:0} / {f.Low01Fps:0} FPS");
                sy = DetailRow(rg, sy, w, pad, "Frame time avg / 99th pct", $"{f.AvgFrameTimeMs:0.0} / {f.P99FrameTimeMs:0.0} ms");
                sy = DetailRow(rg, sy, w, pad, "Stutters", $"{f.Stutters}");
                sy = DetailRow(rg, sy, w, pad, "GPU / CPU-bound / capped", $"{f.GpuBoundPct:0}% / {f.CpuBoundPct:0}% / {f.CappedPct:0}%");
                sy = DetailRow(rg, sy, w, pad, "Game's GPU busy time", $"{f.GameGpuBusyPct:0}%");
                return sy;
            });
        }

        if (s.Timeline is { } t && t.CpuTemp.Count >= 2)
        {
            y = Section(g, y, w, pad, "Timeline", $"every {t.IntervalSeconds}s", (rg, sy) =>
            {
                if (t.Fps.Any(v => v != null))
                    sy = Chart(rg, sy, w, pad, "FPS", null, (t.Fps, VerdictGpu, "FPS"));
                sy = Chart(rg, sy, w, pad, "Usage", (0, 100), (t.GpuLoad, VerdictCapped, "GPU"), (t.BusiestCore, VerdictCpu, "busiest core"));
                sy = Chart(rg, sy, w, pad, "Temperature", null, (t.CpuTemp, VerdictCpu, "CPU"), (t.GpuTemp, VerdictCapped, "GPU"));
                return sy;
            });
        }

        y = Section(g, y, w, pad, "CPU", s.CpuName, (rg, sy) =>
        {
            sy = StatHeader(rg, sy, w, pad);
            sy = StatRow(rg, sy, w, pad, s, "Temperature", "CpuTemp", "°C");
            sy = StatRow(rg, sy, w, pad, s, "Usage", "CpuLoad", "%");
            sy = StatRow(rg, sy, w, pad, s, "Busiest core", "BusiestCore", "%");
            sy = StatRow(rg, sy, w, pad, s, "Game's share", "GameCpu", "%");
            sy = StatRow(rg, sy, w, pad, s, "Clock", "CpuClock", " MHz", 0);
            sy = StatRow(rg, sy, w, pad, s, "Power", "CpuPower", " W");
            return sy;
        });

        y = Section(g, y, w, pad, "GPU", s.GpuName, (rg, sy) =>
        {
            sy = StatHeader(rg, sy, w, pad);
            sy = StatRow(rg, sy, w, pad, s, "Temperature", "GpuTemp", "°C");
            sy = StatRow(rg, sy, w, pad, s, "Hotspot", "GpuHotspot", "°C");
            sy = StatRow(rg, sy, w, pad, s, "Memory junction", "GpuMemJunction", "°C");
            sy = StatRow(rg, sy, w, pad, s, "Usage", "GpuLoad", "%");
            sy = StatRow(rg, sy, w, pad, s, "Clock", "GpuClock", " MHz", 0);
            sy = StatRow(rg, sy, w, pad, s, "Power", "GpuPower", " W");
            sy = StatRow(rg, sy, w, pad, s, s.VramTotalGb is double vt ? $"VRAM (GB, of {vt:0})" : "VRAM", "VramGb", " GB", 1);
            return sy;
        });

        y = Section(g, y, w, pad, "Memory and power", null, (rg, sy) =>
        {
            sy = StatHeader(rg, sy, w, pad);
            sy = StatRow(rg, sy, w, pad, s, s.RamTotalGb is double rt ? $"RAM used (GB, of {rt:0})" : "RAM used", "RamGb", " GB", 1);
            sy = StatRow(rg, sy, w, pad, s, "Committed", "CommitGb", " GB", 1);
            sy = StatRow(rg, sy, w, pad, s, "System power (est.)", "SystemPowerW", " W");
            if (s.PsuWatts is int psu && s.Stats.TryGetValue("SystemPowerW", out var p))
                sy = LoadBarRow(rg, sy, w, pad, $"Peak vs {psu} W PSU", 100 * p.Max / psu);
            return sy;
        });

        // Delete
        y += Px(10);
        if (g != null)
        {
            var r = new Rectangle(left, y, Px(140), Px(20));
            DrawText(g, "Delete this session", _font, Danger, r, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            _hotspots.Add((r, () =>
            {
                var answer = WithoutAutoHide(() => MessageBox.Show(this, $"Delete the {s.Game} session from {s.Start:d MMM, HH:mm}?",
                    "Toasty", MessageBoxButtons.OKCancel, MessageBoxIcon.Question));
                if (answer != DialogResult.OK) return;
                Store?.Delete(s);
                _selected = null;
                _body.AutoScrollPosition = Point.Empty;
                LayoutPanel(null);
                _body.Invalidate();
            }));
        }
        return y + Px(24);
    }

    private int StatHeader(Graphics? g, int y, int w, int pad)
    {
        int h = Px(16);
        if (g == null) return y + h;
        int right = w - pad - Px(4);
        int col = Px(58);
        foreach (var (label, i) in new[] { ("min", 2), ("avg", 1), ("max", 0) })
            DrawText(g, label, _smallFont, Text3, new Rectangle(right - col * (i + 1), y, col, h), TextFormatFlags.Right);
        return y + h;
    }

    private int StatRow(Graphics? g, int y, int w, int pad, GameSession s, string label, string key, string unit, int decimals = 0)
    {
        if (!s.Stats.TryGetValue(key, out var st)) return y;
        int h = Px(19);
        if (g == null) return y + h;
        int left = pad + Px(4), right = w - pad - Px(4);
        int col = Px(58);
        string F(double v) => v.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals)) + (unit.StartsWith(' ') ? "" : unit);
        DrawText(g, label + (unit.StartsWith(' ') && !label.Contains('(') ? $" ({unit.Trim()})" : ""), _font, Text3,
            new Rectangle(left, y, right - left - col * 3, h), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        DrawText(g, F(st.Min), _font, Text2, new Rectangle(right - col * 3, y, col, h), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        DrawText(g, F(st.Avg), _font, Text1, new Rectangle(right - col * 2, y, col, h), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        DrawText(g, F(st.Max), _font, Text2, new Rectangle(right - col, y, col, h), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        return y + h;
    }

    /// <summary>Word-wrapped text across the card width; returns the y below it.</summary>
    private int Wrapped(Graphics? g, int y, int w, int pad, string text, Font font, Color color)
    {
        int left = pad + Px(4), width = w - pad * 2 - Px(8);
        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.NoPadding;
        int h = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), flags).Height;
        if (g != null) DrawText(g, text, font, color, new Rectangle(left, y, width, h), flags);
        return y + h + Px(2);
    }

    /// <summary>A small multi-line chart of a session timeline, with a legend.</summary>
    private int Chart(Graphics? g, int y, int w, int pad, string title, (double Lo, double Hi)? range,
        params (List<double?> Values, Color Color, string Name)[] series)
    {
        int h = Px(76);
        if (g == null) return y + h;
        int left = pad + Px(4), right = w - pad - Px(4);

        // Title and legend
        DrawText(g, title, _smallFont, Text2, new Rectangle(left, y, Px(100), Px(14)), TextFormatFlags.Left);
        int lx = right;
        foreach (var (_, color, name) in series.Reverse())
        {
            int tw = TextRenderer.MeasureText(g, name, _smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            lx -= tw;
            DrawText(g, name, _smallFont, color, new Rectangle(lx, y, tw, Px(14)), TextFormatFlags.Left);
            lx -= Px(12);
        }

        var r = new Rectangle(left, y + Px(16), right - left, Px(52));
        using (var b = new SolidBrush(Color.FromArgb(20, 255, 255, 255))) g.FillRectangle(b, r);

        var all = series.SelectMany(s => s.Values).Where(v => v != null).Select(v => v!.Value).ToList();
        if (all.Count < 2) return y + h;
        double lo = range?.Lo ?? Math.Floor(all.Min() - 2), hi = range?.Hi ?? Math.Ceiling(all.Max() + 2);
        if (hi - lo < 10) { double mid = (hi + lo) / 2; lo = mid - 5; hi = mid + 5; }
        DrawText(g, $"{hi:0}", _smallFont, Text3, new Rectangle(r.Right - Px(40), r.Y + Px(1), Px(38), Px(12)), TextFormatFlags.Right);
        DrawText(g, $"{lo:0}", _smallFont, Text3, new Rectangle(r.Right - Px(40), r.Bottom - Px(13), Px(38), Px(12)), TextFormatFlags.Right);

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var (values, color, _) in series)
        {
            int n = values.Count;
            if (n < 2) continue;
            using var pen = new Pen(color, Math.Max(1.25f, 1.25f * S)) { LineJoin = LineJoin.Round };
            var run = new List<PointF>();
            void FlushRun() { if (run.Count >= 2) g.DrawLines(pen, run.ToArray()); run.Clear(); }
            for (int i = 0; i < n; i++)
            {
                if (values[i] is not double v) { FlushRun(); continue; } // gaps where there was no data
                float x = r.X + (float)i / (n - 1) * (r.Width - 1);
                float yy = r.Bottom - 1 - (float)Math.Clamp((v - lo) / (hi - lo), 0, 1) * (r.Height - 2);
                run.Add(new PointF(x, yy));
            }
            FlushRun();
        }
        g.SmoothingMode = old;
        return y + h;
    }
    private static string FormatSensor(double v, string unit) => unit switch
    {
        "°C" or "%" => $"{v:0}{unit}",
        "RPM" or "MHz" => $"{v:0} {unit}",
        "V" => $"{v:0.000} V",
        "B/s" => v >= 1024 * 1024 ? $"{v / 1024 / 1024:0.0} MB/s" : $"{v / 1024:0} KB/s",
        "" => $"{v:0.##}",
        _ => $"{v:0.#} {unit}",
    };

    private static string? Fmt(double? v, string format, string suffix) => v is double d ? d.ToString(format) + suffix : null;

    internal static void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags flags) =>
        TextRenderer.DrawText(g, text, font, r, color, flags | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private bool HotspotAt(Point p) => _hotspots.Any(h => h.Area.Contains(p));

    private void ClickAt(Point p)
    {
        foreach (var (area, click) in _hotspots.ToList())
        {
            if (!area.Contains(p)) continue;
            click();
            return;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            _sectionFont.Dispose();
            _font.Dispose();
            _valueFont.Dispose();
            _smallFont.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Logo, title, detach/close buttons and the three tabs. Dragging it moves (and detaches) the panel.</summary>
    private sealed class HeaderBar : Control
    {
        private const char PinGlyph = '\uE718', PinnedGlyph = '\uE841', CloseGlyph = '\uE8BB';

        private readonly MiniPanel _owner;
        private readonly Icon _logo = IconRenderer.Logo(32);
        private readonly ToolTip _tip = new() { InitialDelay = 400 };
        private readonly List<(Rectangle Area, PanelView View)> _tabs = [];
        private Font _titleFont = null!, _tabFont = null!, _glyphFont = null!;
        private Rectangle _pinButton, _closeButton;
        private string? _tipText;
        private Point _pressAt;
        private bool _pressed;

        public HeaderBar(MiniPanel owner)
        {
            _owner = owner;
            DoubleBuffered = true;
            BackColor = Bg;
            RebuildForDpi();
        }

        public void RebuildForDpi()
        {
            Height = _owner.Px(HeaderHeight);
            _titleFont?.Dispose(); _tabFont?.Dispose(); _glyphFont?.Dispose();
            _titleFont = PixelFont("Segoe UI Semibold", 11f, _owner.S);
            _tabFont = PixelFont("Segoe UI", 9f, _owner.S);
            // Windows 11 ships Segoe Fluent Icons; Windows 10 has the same glyphs in Segoe MDL2 Assets.
            string iconFont = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            _glyphFont = PixelFont(iconFont, 9f, _owner.S);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Bg);
            int pad = _owner.Px(14);
            g.DrawIcon(_logo, new Rectangle(pad, _owner.Px(12), _owner.Px(24), _owner.Px(24)));
            var titleSize = TextRenderer.MeasureText(g, "Toasty", _titleFont, Size.Empty, TextFormatFlags.NoPadding);
            int titleX = pad + _owner.Px(32);
            DrawText(g, "Toasty", _titleFont, Text1, new Rectangle(titleX, _owner.Px(12), titleSize.Width + 2, _owner.Px(24)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // Detach / dock toggle, plus a close button while detached (the pop-up closes itself).
            int b = _owner.Px(26);
            int bx = titleX + titleSize.Width + _owner.Px(10);
            _pinButton = new Rectangle(bx, (Height - b) / 2, b, b);
            bool pinned = _owner.Pinned;
            DrawGlyph(g, pinned ? PinnedGlyph : PinGlyph, _pinButton, pinned ? Accent : Text3);
            _closeButton = pinned ? new Rectangle(_pinButton.Right + _owner.Px(2), _pinButton.Y, b, b) : Rectangle.Empty;
            if (pinned) DrawGlyph(g, CloseGlyph, _closeButton, Text3);

            _tabs.Clear();
            int x = Width - pad;
            foreach (var (view, label) in new[] { (PanelView.Settings, "Settings"), (PanelView.Games, "Games"), (PanelView.Sensors, "Sensors"), (PanelView.Summary, "Summary") })
            {
                var size = TextRenderer.MeasureText(g, label, _tabFont, Size.Empty, TextFormatFlags.NoPadding);
                x -= size.Width;
                var r = new Rectangle(x, _owner.Px(13), size.Width, _owner.Px(22));
                bool active = _owner.View == view;
                DrawText(g, label, _tabFont, active ? Accent : Text2, r, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                if (active)
                    using (var brush = new SolidBrush(Accent))
                        g.FillRectangle(brush, r.X, r.Bottom + _owner.Px(3), r.Width, _owner.Px(2));
                _tabs.Add((Rectangle.Inflate(r, _owner.Px(5), _owner.Px(6)), view));
                x -= _owner.Px(14);
            }
            using var pen = new Pen(Line);
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }

        private void DrawGlyph(Graphics g, char glyph, Rectangle r, Color color)
        {
            if (r.Contains(PointToClient(Cursor.Position)))
                using (var hover = new SolidBrush(Color.FromArgb(30, 255, 255, 255)))
                    g.FillRectangle(hover, r);
            DrawText(g, glyph.ToString(), _glyphFont, color, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private bool OverTab(Point p) => _tabs.Any(t => t.Area.Contains(p));

        protected override void WndProc(ref Message m)
        {
            if (!_owner.PassEdgeHitTest(ref m)) base.WndProc(ref m);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool button = _pinButton.Contains(e.Location) || _closeButton.Contains(e.Location);
            Cursor = button || OverTab(e.Location) ? Cursors.Hand : Cursors.SizeAll;

            string? tip = _pinButton.Contains(e.Location) ? (_owner.Pinned ? "Dock back to the tray" : "Detach: keep open and move anywhere")
                : _closeButton.Contains(e.Location) ? "Close"
                : OverTab(e.Location) ? null
                : _owner.Pinned ? "Drag to move" : "Drag to detach";
            if (tip != _tipText)
            {
                _tipText = tip;
                _tip.SetToolTip(this, tip);
            }
            Invalidate(); // hover highlight on the buttons

            // Start a window drag once the mouse has actually moved with the button held.
            if (_pressed && e.Button == MouseButtons.Left
                && (Math.Abs(e.X - _pressAt.X) > 3 || Math.Abs(e.Y - _pressAt.Y) > 3))
            {
                _pressed = false;
                _owner.BeginDrag();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            bool onControl = OverTab(e.Location) || _pinButton.Contains(e.Location) || _closeButton.Contains(e.Location);
            _pressed = e.Button == MouseButtons.Left && !onControl;
            _pressAt = e.Location;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _pressed = false;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (_pinButton.Contains(e.Location))
            {
                _owner.SetPinned(!_owner.Pinned);
                return;
            }
            if (_closeButton.Contains(e.Location))
            {
                _owner.HidePanel();
                return;
            }
            // Switching view resizes the panel, which can repaint this header (and rebuild _tabs)
            // before we return, so look the tab up first and act on it after the loop.
            var hit = _tabs.ToList().FirstOrDefault(t => t.Area.Contains(e.Location));
            if (hit.Area != Rectangle.Empty && hit.View != _owner.View) _owner.SetView(hit.View);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _logo.Dispose(); _tip.Dispose(); _titleFont.Dispose(); _tabFont.Dispose(); _glyphFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
    /// <summary>Scrollable, double-buffered surface for the summary and sensor views.</summary>
    private sealed class BodyPanel : Panel
    {
        private readonly MiniPanel _owner;

        public BodyPanel(MiniPanel owner)
        {
            _owner = owner;
            DoubleBuffered = true;
            AutoScroll = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowTheme(Handle, "DarkMode_Explorer", null); // dark scrollbar
        }

        protected override void WndProc(ref Message m)
        {
            if (!_owner.PassEdgeHitTest(ref m)) base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(Bg);
            // ClientSize excludes the scrollbar when one is showing, so nothing ends up underneath it.
            _owner.Render(g, AutoScrollPosition.Y, ClientSize.Width);

            // A small grip on the bottom edge hints that the panel can be resized.
            int gw = _owner.Px(36), gh = Math.Max(2, _owner.Px(3));
            using var grip = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
            g.FillRectangle(grip, (ClientSize.Width - gw) / 2, ClientSize.Height - gh - _owner.Px(2), gw, gh);
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = _owner.HotspotAt(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left) _owner.ClickAt(e.Location);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    internal static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}
