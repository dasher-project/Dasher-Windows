using System;
using System.Runtime.InteropServices;

namespace Dasher.Windows.Engine;

public static class NativeBridge
{
    private const string DllName = "dasher";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_create([MarshalAs(UnmanagedType.LPStr)] string data_dir,
        [MarshalAs(UnmanagedType.LPStr)] string? user_dir, out IntPtr out_error);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_destroy(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_screen_size(IntPtr ctx, int width, int height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_mouse_move(IntPtr ctx, float x, float y);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_mouse_down(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_mouse_up(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_key_event(IntPtr ctx, int key, int pressed);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_frame(IntPtr ctx, long time_ms,
        out IntPtr out_commands, out int out_command_count,
        out IntPtr out_strings, out int out_string_count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_output_text(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_reset_output_text(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_alphabet_id(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_alphabet_id(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string alphabet_id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_language_model_id(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_language_model_id(IntPtr ctx, int model_id);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_speed_percent(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_speed_percent(IntPtr ctx, int percent);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_bool_parameter(IntPtr ctx, int key);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_bool_parameter(IntPtr ctx, int key, int value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_long_parameter(IntPtr ctx, int key);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_long_parameter(IntPtr ctx, int key, int value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_string_parameter(IntPtr ctx, int key);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_string_parameter(IntPtr ctx, int key, [MarshalAs(UnmanagedType.LPStr)] string value);

    // Parameter schema
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_parameter_count();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_parameter_info(int index, out DasherParameterInfo info);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_parameter_enum_count(int key);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_parameter_enum_name(int key, int index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_parameter_enum_value(int key, int index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_parameter_string_values(IntPtr ctx, int key, IntPtr[] out_names, int max_out);

    // Colour palettes
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_palette_count(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_palette_name(IntPtr ctx, int index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_current_palette(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_palette_preview_colors(IntPtr ctx, int index, int[] out_colors);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_palette(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string palette_name);

    // Alphabets
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_alphabet_count(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_alphabet_name(IntPtr ctx, int index);

    // Persistence
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_save_settings(IntPtr ctx);

    // Localization
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_set_locale(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string? locale);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_locale(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_string_override(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string? value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_localized_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string key);

    // Color utilities
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_color_argb(int alpha, int red, int green, int blue);
}

[StructLayout(LayoutKind.Sequential)]
public struct DasherParameterInfo
{
    public int Key;
    public IntPtr Name;
    public IntPtr Desc;
    public int Type;
    public int UiType;
    public int MinVal;
    public int MaxVal;
    public int Step;
    public int Advanced;
    public IntPtr Group;
    public IntPtr Subgroup;
}
