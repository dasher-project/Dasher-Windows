using System;
using System.IO;
using System.Threading;

namespace Dasher.Windows.EyeGaze
{
    public static class EyeGazeLogger
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Dasher", "eyegaze.log");

        private static readonly object _lock = new();
        private static long _gazeLogCounter;
        private const long GazeLogInterval = 60;

        public static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    var dir = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [EyeGaze] {message}{Environment.NewLine}";
                    File.AppendAllText(LogPath, line);
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
    }
}
