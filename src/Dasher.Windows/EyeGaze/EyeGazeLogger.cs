using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Dasher.Windows.EyeGaze;

public static class EyeGazeLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dasher", "eyegaze.log");

    private static long _gazeLogCounter;
    private const long GazeLogInterval = 60; // log every 60th sample (~2s at 33Hz)

    // Buffered writing: queue messages and flush on a timer to avoid
    // disk I/O on the gaze callback hot path.
    private static readonly BlockingCollection<string> _queue = new(1024);
    private static Thread? _writerThread;
    private static int _writerStarted;

    public static void Log(string message)
    {
        try
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [EyeGaze] {message}";
            if (_queue.IsAddingCompleted)
            {
                // Fallback if queue is shut down
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            else
            {
                _queue.Add(line);
            }
        }
        catch { }
    }

    public static void LogGazeData(float x, float y, bool valid)
    {
        var count = Interlocked.Increment(ref _gazeLogCounter);
        if (count == 1)
            Log($"First gaze data received: x={x:F1} y={y:F1} valid={valid}");
        else if (count % GazeLogInterval == 0)
            Log($"Gaze data flowing (sample #{count}): x={x:F1} y={y:F1} valid={valid}");
    }

    public static string GetLogPath() => LogPath;

    static EyeGazeLogger()
    {
        EnsureWriterStarted();
    }

    private static void EnsureWriterStarted()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) == 1) return;

        _writerThread = new Thread(() =>
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var writer = new StreamWriter(LogPath, append: true) { AutoFlush = false };
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out var line, 500))
                {
                    writer.WriteLine(line);
                }
                writer.Flush();
            }
            // Drain remaining
            while (_queue.TryTake(out var remaining))
                writer.WriteLine(remaining);
            writer.Flush();
        })
        {
            IsBackground = true,
            Name = "EyeGazeLogger",
        };
        _writerThread.Start();
    }
}
