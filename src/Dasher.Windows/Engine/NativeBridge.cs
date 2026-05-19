using System;
using System.Runtime.InteropServices;

namespace Dasher.Windows.Engine;

public static class NativeBridge
{
    private const string DllName = "dasher";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_create([MarshalAs(UnmanagedType.LPStr)] string data_dir);

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
}
