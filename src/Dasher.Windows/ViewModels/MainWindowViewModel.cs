using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using Dasher.Windows.Engine;

namespace Dasher.Windows.ViewModels;

public partial class MainWindowViewModel : ObservableObject
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

    // Bounds taken from the engine's LP_MAX_BITRATE manifest (raw units are
    // v5's MaxBitRateTimes100, so Speed 5.0 == raw 500). Safe defaults until
    // LoadSpeedBounds() runs; the manifest (raw 1-1000) widens the top end
    // past the old hardcoded 5.0 cap that truncated v5's 0.1-8.0 range.
    [ObservableProperty]
    private double _minSpeed = 0.1;

    [ObservableProperty]
    private double _maxSpeed = 5.0;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _autoSpeed;

    [ObservableProperty]
    private bool _learning;

    [ObservableProperty]
    private int _selectedColourIndex;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isKeyboardMode;

    [ObservableProperty]
    private PanePosition _panePosition = PanePosition.Right;

    [ObservableProperty]
    private bool _isStatusBarHidden;

    [ObservableProperty]
    private double _keyboardModeOpacity = 0.85;

    [ObservableProperty]
    private bool _isPrefsVisible;

    [ObservableProperty]
    private int _selectedPrefsIndex = -1;

    [ObservableProperty]
    private AvaloniaList<PaletteInfo> _palettes = [];

    // WPM tracking (RFC 0012)
    [ObservableProperty]
    private double _currentWpm;

    [ObservableProperty]
    private double _maxWpm;

    [ObservableProperty]
    private double _avgWpm;

    private double _wpmSum;
    private int _wpmCount;

    public void UpdateTypingStats()
    {
        if (_handle == IntPtr.Zero) return;
        CurrentWpm = NativeBridge.dasher_get_wpm(_handle);
        if (CurrentWpm > 0)
        {
            MaxWpm = Math.Max(MaxWpm, CurrentWpm);
            _wpmSum += CurrentWpm;
            _wpmCount++;
            AvgWpm = _wpmSum / _wpmCount;
        }
    }

    public void ResetTypingStats()
    {
        MaxWpm = 0;
        _wpmSum = 0;
        _wpmCount = 0;
        AvgWpm = 0;
        CurrentWpm = 0;
        // Also clear the engine's rolling 5-second window (v0.1.9, RFC 0012).
        if (_handle != IntPtr.Zero)
            NativeBridge.dasher_reset_cps(_handle);
    }

    public IntPtr Handle => _handle;

    public void SetHandle(IntPtr handle)
    {
        _handle = handle;
    }

    public void LoadAlphabets()
    {
        if (_handle == IntPtr.Zero) return;

        var count = NativeBridge.dasher_get_alphabet_count(_handle);
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var ptr = NativeBridge.dasher_get_alphabet_name(_handle, i);
            if (ptr != IntPtr.Zero)
                names.Add(Marshal.PtrToStringUTF8(ptr) ?? "");
        }
        Languages = new AvaloniaList<string>(names);
    }

    public void LoadPalettes()
    {
        if (_handle == IntPtr.Zero) return;

        var count = NativeBridge.dasher_get_palette_count(_handle);
        var palettes = new AvaloniaList<PaletteInfo>();
        var colors = new int[4];

        for (int i = 0; i < count; i++)
        {
            var namePtr = NativeBridge.dasher_get_palette_name(_handle, i);
            var name = namePtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(namePtr) ?? "" : "";

            if (NativeBridge.dasher_get_palette_preview_colors(_handle, i, colors) == 0)
            {
                palettes.Add(new PaletteInfo
                {
                    Name = name,
                    Color0 = colors[0],
                    Color1 = colors[1],
                    Color2 = colors[2],
                    Color3 = colors[3]
                });
            }
        }

        Palettes = palettes;
    }

    public void LoadSpeedBounds()
    {
        if (_handle == IntPtr.Zero) return;
        var count = NativeBridge.dasher_get_parameter_count();
        for (int i = 0; i < count; i++)
        {
            if (NativeBridge.dasher_get_parameter_info(i, out var info) != 0) continue;
            if (info.Key != ParameterKeys.LP_MAX_BITRATE) continue;
            if (info.MaxVal > info.MinVal)
            {
                MinSpeed = info.MinVal / 100.0;
                MaxSpeed = info.MaxVal / 100.0;
            }
            break;
        }
    }

    public void LoadSpeedFromEngine()
    {
        if (_handle == IntPtr.Zero) return;
        var raw = NativeBridge.dasher_get_long_parameter(_handle, ParameterKeys.LP_MAX_BITRATE);
        Speed = Math.Clamp(raw / 100.0, MinSpeed, MaxSpeed);
    }

    public void ApplySpeed()
    {
        if (_handle == IntPtr.Zero) return;
        var raw = (int)Math.Round(Speed * 100);
        NativeBridge.dasher_set_long_parameter(_handle, ParameterKeys.LP_MAX_BITRATE, raw);
    }

    public void IncreaseSpeed()
    {
        Speed = Math.Clamp(Math.Round(Speed + 0.1, 1), MinSpeed, MaxSpeed);
        ApplySpeed();
    }

    public void DecreaseSpeed()
    {
        Speed = Math.Clamp(Math.Round(Math.Max(MinSpeed, Speed - 0.1), 1), MinSpeed, MaxSpeed);
        ApplySpeed();
    }

    partial void OnAutoSpeedChanged(bool value)
    {
        if (_handle != IntPtr.Zero)
            NativeBridge.dasher_set_bool_parameter(_handle, ParameterKeys.BP_AUTO_SPEEDCONTROL, value ? 1 : 0);
    }

    partial void OnLearningChanged(bool value)
    {
        if (_handle != IntPtr.Zero)
            NativeBridge.dasher_set_bool_parameter(_handle, ParameterKeys.BP_LM_ADAPTIVE, value ? 1 : 0);
    }
}

public class PaletteInfo
{
    public string Name { get; set; } = "";
    public int Color0 { get; set; }
    public int Color1 { get; set; }
    public int Color2 { get; set; }
    public int Color3 { get; set; }
}

public enum PanePosition
{
    Right,
    Left,
    Bottom,
    Top,
    Keyboard,
}
