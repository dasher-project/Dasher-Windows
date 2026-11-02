using System;
using System.IO;
using System.Linq;

namespace Dasher.Windows.Services;

/// <summary>
/// Centralised log management (the November 2026 "40MB keyboard_debug.log"
/// lesson). Owns:
///
///  - Rotation: on app-version change, the previous session's log is
///    archived (keyboard_debug.v0.1.36.log etc.) so each release starts
///    clean — a tester's log is then scoped to the build that produced it.
///  - Size cap: if a log exceeds the cap mid-session (advanced logging can
///    generate megabytes), it is truncated to its most recent lines at the
///    NEXT startup (never mid-write — that would corrupt a line).
///  - The "Advanced logging" toggle: gates verbose per-sample/per-keystroke
///    logging (eyegaze detail, SendInput traces, UIA reads). Basic
///    lifecycle events (engine created, tracker connected, errors) are
///    always logged — they are the minimum for remote debugging.
///
/// All logs live in %APPDATA%\Dasher\ alongside the settings.
/// </summary>
public static class LogManager
{
    /// <summary>Files managed by this class (relative to the Dasher dir).</summary>
    private static readonly string[] ManagedLogs =
    {
        "keyboard_debug.log",
        "eyegaze.log",
    };

    private static readonly string VersionMarker = "last_version.txt";

    /// <summary>Cap per log file before truncation at next startup.</summary>
    public const long MaxLogBytes = 10 * 1024 * 1024; // 10 MB

    /// <summary>Maximum number of archived versions to keep.</summary>
    private const int MaxArchives = 2;

    /// <summary>When true, verbose logging is enabled (the settings toggle).</summary>
    public static bool AdvancedLogging { get; private set; }

    private static string DasherDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher");

    /// <summary>
    /// Call once at startup, BEFORE any log writes. Handles version-change
    /// rotation and size-cap truncation.
    /// </summary>
    public static void Initialize(string currentVersion)
    {
        try
        {
            Directory.CreateDirectory(DasherDir);
            RotateOnVersionChange(currentVersion);
            TruncateOversized();
            PruneOldArchives();
        }
        catch { }
    }

    /// <summary>
    /// Set the advanced-logging flag (called from the settings toggle and
    /// loaded from AccessConfiguration at startup).
    /// </summary>
    public static void SetAdvancedLogging(bool enabled) => AdvancedLogging = enabled;

    /// <summary>
    /// Deletes all managed logs and archives. Returns the bytes freed.
    /// </summary>
    public static long ClearLogs()
    {
        long freed = 0;
        try
        {
            foreach (var name in ManagedLogs)
            {
                var path = Path.Combine(DasherDir, name);
                if (File.Exists(path))
                {
                    freed += new FileInfo(path).Length;
                    File.Delete(path);
                }
            }
            // also remove archives
            foreach (var f in Directory.GetFiles(DasherDir, "keyboard_debug.v*.log"))
            {
                freed += new FileInfo(f).Length;
                File.Delete(f);
            }
            foreach (var f in Directory.GetFiles(DasherDir, "eyegaze.v*.log"))
            {
                freed += new FileInfo(f).Length;
                File.Delete(f);
            }
        }
        catch { }
        return freed;
    }

    /// <summary>Total size of all managed logs (for the settings display).</summary>
    public static long GetTotalLogBytes()
    {
        long total = 0;
        try
        {
            foreach (var name in ManagedLogs)
            {
                var path = Path.Combine(DasherDir, name);
                if (File.Exists(path)) total += new FileInfo(path).Length;
            }
            foreach (var f in Directory.GetFiles(DasherDir, "keyboard_debug.v*.log"))
                total += new FileInfo(f).Length;
            foreach (var f in Directory.GetFiles(DasherDir, "eyegaze.v*.log"))
                total += new FileInfo(f).Length;
        }
        catch { }
        return total;
    }

    /// <summary>Human-readable size (for the settings button).</summary>
    public static string GetTotalLogSize()
    {
        var bytes = GetTotalLogBytes();
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
        };
    }

    private static void RotateOnVersionChange(string currentVersion)
    {
        var markerPath = Path.Combine(DasherDir, VersionMarker);
        var lastVersion = File.Exists(markerPath)
            ? File.ReadAllText(markerPath).Trim() : "";

        if (lastVersion == currentVersion) return; // same build: keep appending

        // Version changed: archive current logs with the OLD version tag
        foreach (var name in ManagedLogs)
        {
            var path = Path.Combine(DasherDir, name);
            if (!File.Exists(path)) continue;
            var archiveName = name.Replace(".log", $".v{SanitizeVersion(lastVersion)}.log");
            var archivePath = Path.Combine(DasherDir, archiveName);
            try
            {
                if (File.Exists(archivePath)) File.Delete(archivePath);
                File.Move(path, archivePath);
            }
            catch { }
        }

        File.WriteAllText(markerPath, currentVersion);
    }

    private static void TruncateOversized()
    {
        foreach (var name in ManagedLogs)
        {
            var path = Path.Combine(DasherDir, name);
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            if (info.Length <= MaxLogBytes) continue;

            // Keep the last ~1000 lines (the most recent diagnostics)
            try
            {
                var lines = File.ReadAllLines(path);
                var keep = Math.Min(lines.Length, 1000);
                var recent = lines[^keep..];
                File.WriteAllLines(path, recent);
            }
            catch { }
        }
    }

    private static void PruneOldArchives()
    {
        foreach (var pattern in new[] { "keyboard_debug.v*.log", "eyegaze.v*.log" })
        {
            var archives = Directory.GetFiles(DasherDir, pattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var old in archives.Skip(MaxArchives))
            {
                try { old.Delete(); } catch { }
            }
        }
    }

    private static string SanitizeVersion(string version) =>
        string.IsNullOrEmpty(version) ? "unknown" :
        version.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
}
