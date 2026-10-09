using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Toasty;

public record GameProcess(int Pid, string Exe, string Path, string Name);

/// <summary>
/// Decides whether the foreground app is a game. Rules, in order:
/// never-track list → no; always-track list → yes after 5s; installed under a known game
/// library (Steam, Epic, GOG, EA, Ubisoft, Xbox, Riot...) → yes after 10s; otherwise a
/// fullscreen/borderless window with the GPU busy (≥40%) for 60s → yes.
/// </summary>
public sealed class GameDetector
{
    private static readonly string[] LibraryMarkers =
    [
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\", @"\ea games\",
        @"\origin games\", @"\ubisoft game launcher\games\", @"\xboxgames\", @"\riot games\",
        @"\battle.net\", @"\rockstar games\", @"\amazon games\library\", @"\itch\apps\",
    ];

    // Apps that go fullscreen and/or use the GPU but aren't games.
    private static readonly HashSet<string> BuiltInIgnore = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "toasty.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe",
        "vlc.exe", "mpc-hc64.exe", "mpc-be64.exe", "potplayermini64.exe", "mpv.exe", "wmplayer.exe", "Video.UI.exe",
        "ApplicationFrameHost.exe", "SearchHost.exe", "StartMenuExperienceHost.exe", "ShellExperienceHost.exe",
        "LockApp.exe", "TextInputHost.exe", "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe",
        "EpicWebHelper.exe", "Battle.net.exe", "EADesktop.exe", "GalaxyClient.exe", "upc.exe", "RiotClientServices.exe",
        "Discord.exe", "Spotify.exe", "Teams.exe", "ms-teams.exe", "Slack.exe", "obs64.exe", "Code.exe", "devenv.exe",
        "mstsc.exe", "vmware.exe", "VirtualBoxVM.exe", "Photos.exe", "PhotosApp.exe", "PowerPnt.exe", "POWERPNT.EXE",
        "Netflix.exe", "WindowsTerminal.exe", "Taskmgr.exe", "Claude.exe",
    };

    // Things that grab focus on top of a game without meaning "the game is no longer in front".
    private static readonly HashSet<string> PassThrough = new(StringComparer.OrdinalIgnoreCase)
    {
        "toasty.exe", "Medal.exe", "NVIDIA Overlay.exe", "nvcontainer.exe", "GameBar.exe", "GameBarFTServer.exe",
        "XboxGameBarWidgets.exe", "Overwolf.exe", "ShareX.exe", "SnippingTool.exe", "ScreenClippingHost.exe",
    };

    private int _candidatePid;
    private DateTime _candidateSince;
    private readonly Dictionary<int, (string Exe, string Path, string Name)> _cache = new();

    /// <summary>Plain-English reason the last app in front is or isn't being recorded (for the Games tab).</summary>
    public string Status { get; private set; } = "Waiting for a game to come to the front.";

    /// <summary>The last app in front that wasn't on an ignore list (for "Record now").</summary>
    public GameProcess? LastCandidate { get; private set; }

    /// <summary>The foreground process id right now (0 if none).</summary>
    public static int ForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    /// <summary>Call once per poll. Returns a game once the foreground app has qualified, else null.</summary>
    public GameProcess? Check(double? systemGpuLoad, IReadOnlyCollection<string> alwaysTrack, IReadOnlyCollection<string> neverTrack)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint upid);
        int pid = (int)upid;
        if (pid == 0) return null;
        // Toasty's own panel (someone checking the Games tab) or an overlay: pause, don't reset.
        if (pid == Environment.ProcessId) return null;

        if (!_cache.TryGetValue(pid, out var info))
        {
            string? path = ImagePath(pid);
            if (path == null)
            {
                Status = "Can't identify the app in front (Windows wouldn't say which program it is).";
                return null;
            }
            if (_cache.Count > 200) _cache.Clear();
            info = (System.IO.Path.GetFileName(path), path, FriendlyName(path));
            _cache[pid] = info;
        }

        if (PassThrough.Contains(info.Exe)) return null;

        if (BuiltInIgnore.Contains(info.Exe) || neverTrack.Contains(info.Exe, StringComparer.OrdinalIgnoreCase))
        {
            _candidatePid = 0;
            Status = $"{info.Name} is in front: not a game ({(neverTrack.Contains(info.Exe, StringComparer.OrdinalIgnoreCase) ? "on your never-track list" : "ignored")}).";
            return null;
        }

        var now = DateTime.Now;
        if (pid != _candidatePid)
        {
            _candidatePid = pid;
            _candidateSince = now;
        }
        var game = new GameProcess(pid, info.Exe, info.Path, info.Name);
        LastCandidate = game;
        double seconds = (now - _candidateSince).TotalSeconds;
        double gpu = systemGpuLoad ?? 0;

        bool always = alwaysTrack.Contains(info.Exe, StringComparer.OrdinalIgnoreCase);
        bool library = LibraryMarkers.Any(m => info.Path.Contains(m, StringComparison.OrdinalIgnoreCase));
        bool fullscreen = IsFullscreen(hwnd);

        if (always)
        {
            Status = $"{info.Name}: on your always-track list, recording in {Math.Max(0, 5 - seconds):0}s.";
            return seconds >= 5 ? game : null;
        }
        if (library)
        {
            Status = $"{info.Name}: installed in a game library, recording after {Math.Max(0, 10 - seconds):0}s more in front.";
            return seconds >= 10 ? game : null;
        }
        if (fullscreen && gpu >= 40)
        {
            Status = $"{info.Name}: fullscreen with the GPU busy, recording if that holds for {Math.Max(0, 60 - seconds):0}s more.";
            return seconds >= 60 ? game : null;
        }

        // A fullscreen candidate that lets the GPU go idle starts its 60s again.
        _candidateSince = now;
        Status = fullscreen
            ? $"{info.Name}: fullscreen, but the GPU isn't busy (under 40%), so it doesn't look like a game yet."
            : $"{info.Name}: windowed and not in a known game library, so it isn't treated as a game. Use Record now, or add {info.Exe} to \"Always treat as games\" in Settings.";
        return null;
    }
    /// <summary>A readable name: the Steam/Epic/GOG folder name if there is one, else the exe's product name.</summary>
    public static string FriendlyName(string path)
    {
        foreach (var marker in new[] { @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\", @"\xboxgames\", @"\ea games\" })
        {
            int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            string rest = path[(i + marker.Length)..];
            int slash = rest.IndexOf('\\');
            if (slash > 0) return rest[..slash];
        }
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            foreach (var candidate in new[] { v.ProductName, v.FileDescription })
            {
                var name = candidate?.Trim();
                if (!string.IsNullOrEmpty(name) && !name.Contains("Unreal Engine", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("BootstrapPackagedGame", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }
        catch { }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Is the process still alive? Uses only PROCESS_QUERY_LIMITED_INFORMATION, which anti-cheat
    /// protected games still allow (asking for SYNCHRONIZE too was refused, which read as "exited"
    /// and ended recordings a second after they started). Access denied means it exists.
    /// </summary>
    public static bool IsRunning(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero)
            return Marshal.GetLastWin32Error() == 5; // ERROR_ACCESS_DENIED: there, just protected. Anything else: gone.
        try { return GetExitCodeProcess(h, out uint code) ? code == 259 /* STILL_ACTIVE */ : true; }
        finally { CloseHandle(h); }
    }

    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>True when the window covers its whole monitor (exclusive fullscreen or borderless).</summary>
    private static bool IsFullscreen(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        return r.Left <= mi.Monitor.Left && r.Top <= mi.Monitor.Top && r.Right >= mi.Monitor.Right && r.Bottom >= mi.Monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
}
