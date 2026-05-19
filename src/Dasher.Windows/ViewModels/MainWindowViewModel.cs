using System;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using Dasher.Windows.Engine;

namespace Dasher.Windows.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private IntPtr _handle;

    [ObservableProperty]
    private string _outputText = "";

    [ObservableProperty]
    private AvaloniaList<string> _languages = [];

    [ObservableProperty]
    private int _selectedLanguageIndex;

    [ObservableProperty]
    private double _speed = 1.0;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _autoSpeed;

    [ObservableProperty]
    private int _selectedColourIndex;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isKeyboardMode;

    [ObservableProperty]
    private bool _isPrefsVisible;

    [ObservableProperty]
    private int _selectedPrefsIndex = -1;

    public IntPtr Handle => _handle;

    public void SetHandle(IntPtr handle)
    {
        _handle = handle;
    }

    public void ApplySpeed()
    {
        if (_handle == IntPtr.Zero) return;
        var percent = (int)Math.Round(Speed * 100);
        NativeBridge.dasher_set_speed_percent(_handle, percent);
    }

    public void IncreaseSpeed()
    {
        Speed = Math.Round(Math.Min(Speed + 0.1, 5.0), 1);
        ApplySpeed();
    }

    public void DecreaseSpeed()
    {
        Speed = Math.Round(Math.Max(Speed - 0.1, 0.1), 1);
        ApplySpeed();
    }
}
