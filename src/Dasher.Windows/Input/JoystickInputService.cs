using System;
using System.Linq;
using Windows.Gaming.Input;

namespace Dasher.Windows.Input
{
    public sealed class JoystickInputService : IDisposable
    {
        public event EventHandler<(float X, float Y)>? PositionChanged;
        public bool IsConnected => _gamepad != null;
        public string DeviceName => _gamepad != null ? "Gamepad" : "No gamepad";

        private Gamepad? _gamepad;
        private bool _polling;
        private readonly float _deadZone = 0.15f;
        private const float MaxSpeed = 800f;

        public bool Connect()
        {
            _gamepad = Gamepad.Gamepads.FirstOrDefault();
            if (_gamepad == null) return false;

            Gamepad.GamepadAdded += OnGamepadAdded;
            Gamepad.GamepadRemoved += OnGamepadRemoved;
            return true;
        }

        public void StartPolling()
        {
            if (_polling) return;
            _polling = true;
            PollLoop();
        }

        public void StopPolling()
        {
            _polling = false;
        }

        private async void PollLoop()
        {
            while (_polling)
            {
                if (_gamepad != null)
                {
                    var reading = _gamepad.GetCurrentReading();
                    var lx = ApplyDeadZone((float)reading.LeftThumbstickX);
                    var ly = ApplyDeadZone((float)reading.LeftThumbstickY);

                    if (lx != 0 || ly != 0)
                    {
                        var dx = lx * MaxSpeed * (1f / 60f);
                        var dy = -ly * MaxSpeed * (1f / 60f);
                        PositionChanged?.Invoke(this, (dx, dy));
                    }
                }

                await System.Threading.Tasks.Task.Delay(16);
            }
        }

        private float ApplyDeadZone(float value)
        {
            return Math.Abs(value) < _deadZone ? 0f : value;
        }

        private void OnGamepadAdded(object? sender, Gamepad e)
        {
            _gamepad = Gamepad.Gamepads.FirstOrDefault();
        }

        private void OnGamepadRemoved(object? sender, Gamepad e)
        {
            _gamepad = Gamepad.Gamepads.FirstOrDefault();
        }

        public void Dispose()
        {
            _polling = false;
            Gamepad.GamepadAdded -= OnGamepadAdded;
            Gamepad.GamepadRemoved -= OnGamepadRemoved;
            _gamepad = null;
        }
    }
}
