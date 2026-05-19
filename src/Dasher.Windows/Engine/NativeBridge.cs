using System;
using System.Runtime.InteropServices;

namespace Dasher.Windows.Engine;

[StructLayout(LayoutKind.Sequential)]
public struct FrameResult
{
    public IntPtr Commands;
    public int CommandCount;
    public IntPtr Strings;
    public int StringCount;
}

public static class NativeBridge
{
    private const string DllName = "dasher_native";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern long dasher_create([MarshalAs(UnmanagedType.LPStr)] string dataDir);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_destroy(long handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_screen_size(long handle, int width, int height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_mouse_move(long handle, float x, float y);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_mouse_down(long handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_mouse_up(long handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern FrameResult dasher_frame(long handle, long timeMs);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_free_frame_result(ref FrameResult result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_output_text(long handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_reset_output_text(long handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dasher_get_alphabet_id(long handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_alphabet_id(long handle, [MarshalAs(UnmanagedType.LPStr)] string alphabetId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_language_model_id(long handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_language_model_id(long handle, int modelId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dasher_get_speed_percent(long handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void dasher_set_speed_percent(long handle, int percent);
}
