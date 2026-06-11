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

    [ObservableProperty]
    private AvaloniaList<PaletteInfo> _palettes = [];

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

    partial void OnAutoSpeedChanged(bool value)
    {
        if (_handle != IntPtr.Zero)
            NativeBridge.dasher_set_bool_parameter(_handle, ParameterKeys.BP_AUTO_SPEEDCONTROL, value ? 1 : 0);
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
