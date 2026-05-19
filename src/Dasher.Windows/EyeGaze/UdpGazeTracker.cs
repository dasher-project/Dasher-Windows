using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Dasher.Windows.EyeGaze
{
    public class UdpGazeTracker : IEyeTrackerService
    {
        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;

        public event EventHandler<GazePoint>? GazeDataReceived;
        public string TrackerName => "UDP Gaze Tracker";
        public bool IsConnected { get; private set; }
        public int UdpPort { get; set; } = 5555;

        private static readonly Regex GazeTrackerRegex = new(
            @"^STREAM_DATA\s+(?<instanceTime>\d+)\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<y>-?\d+(?:\.\d+)?)",
            RegexOptions.Compiled);

        private static readonly Regex SimpleRegex = new(
            @"GazePoint\s+X:(?<x>-?\d+(?:\.\d+)?)\s+Y:(?<y>-?\d+(?:\.\d+)?)\s+Timestamp:(?<timestamp>\d+)",
            RegexOptions.Compiled);

        public Task<bool> ConnectAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    _udpClient = new UdpClient();
                    _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _udpClient.Client.Bind(new IPEndPoint(IPAddress.Loopback, UdpPort));
                    _cts = new CancellationTokenSource();
                    _receiveTask = ReceiveLoop(_cts.Token);
                    IsConnected = true;
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UDP gaze tracker failed: {ex.Message}");
                    IsConnected = false;
                    return false;
                }
            });
        }

        public void StartTracking() { }
        public void StopTracking()
        {
            _cts?.Cancel();
        }

        public void Dispose()
        {
            StopTracking();
            _receiveTask?.Wait(TimeSpan.FromSeconds(2));
            _cts?.Dispose();
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
            IsConnected = false;
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync();
                    var message = Encoding.ASCII.GetString(result.Buffer);
                    ProcessMessage(message);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private void ProcessMessage(string message)
        {
            var match = GazeTrackerRegex.Match(message);
            if (match.Success)
            {
                var x = float.Parse(match.Groups["x"].Value);
                var y = float.Parse(match.Groups["y"].Value);
                var ts = long.Parse(match.Groups["instanceTime"].Value);
                GazeDataReceived?.Invoke(this, new GazePoint(x, y,
                    DateTimeOffset.FromUnixTimeMilliseconds(ts)));
                return;
            }

            match = SimpleRegex.Match(message);
            if (match.Success)
            {
                var x = float.Parse(match.Groups["x"].Value);
                var y = float.Parse(match.Groups["y"].Value);
                var ts = long.Parse(match.Groups["timestamp"].Value);
                GazeDataReceived?.Invoke(this, new GazePoint(x, y,
                    DateTimeOffset.FromUnixTimeMilliseconds(ts)));
            }
        }
    }
}
