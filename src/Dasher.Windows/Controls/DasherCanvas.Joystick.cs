using System;
using Avalonia;
using Avalonia.Controls;
using Dasher.Windows.Engine;
using Dasher.Windows.Input;

namespace Dasher.Windows.Controls
{
    public partial class DasherCanvas
    {
        private JoystickInputService? _joystickService;
        private bool _useJoystickInput;
        private float _joyX;
        private float _joyY;

        public bool InitializeJoystick()
        {
            _joystickService = new JoystickInputService();
            if (!_joystickService.Connect())
            {
                _joystickService.Dispose();
                _joystickService = null;
                return false;
            }

            _useJoystickInput = true;
            _joyX = (float)(Bounds.Width / 2);
            _joyY = (float)(Bounds.Height / 2);
            _joystickService.PositionChanged += OnJoystickPositionChanged;
            _joystickService.StartPolling();
            return true;
        }

        public void DisableJoystick()
        {
            _useJoystickInput = false;
            if (_joystickService != null)
            {
                _joystickService.PositionChanged -= OnJoystickPositionChanged;
                _joystickService.StopPolling();
                _joystickService.Dispose();
                _joystickService = null;
            }
        }

        private void OnJoystickPositionChanged(object? sender, (float Dx, float Dy) delta)
        {
            if (!_useJoystickInput || _handle == IntPtr.Zero) return;

            _joyX = Math.Clamp(_joyX + delta.Dx, 0, (float)Bounds.Width);
            _joyY = Math.Clamp(_joyY + delta.Dy, 0, (float)Bounds.Height);
            NativeBridge.dasher_mouse_move(_handle, _joyX, _joyY);
        }
    }
}
