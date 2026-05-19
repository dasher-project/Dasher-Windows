using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Dasher.Windows.Engine;

namespace Dasher.Windows.Controls;

public class DasherCanvas : Control
{
    private IntPtr _handle;
    private int[]? _commands;
    private string[]? _strings;
    private readonly DispatcherTimer _timer;

    public static readonly StyledProperty<string> OutputTextProperty =
        AvaloniaProperty.Register<DasherCanvas, string>(nameof(OutputText));

    public string OutputText
    {
        get => GetValue(OutputTextProperty);
        set => SetValue(OutputTextProperty, value);
    }

    public DasherCanvas()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    public IntPtr GetHandle() => _handle;

    public void Initialize(string dataDir)
    {
        _handle = NativeBridge.dasher_create(dataDir);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create Dasher session");

        if (Bounds.Width > 0 && Bounds.Height > 0)
            NativeBridge.dasher_set_screen_size(_handle, (int)Bounds.Width, (int)Bounds.Height);

        _timer.Start();
    }

    public void Shutdown()
    {
        _timer.Stop();
        if (_handle != IntPtr.Zero)
        {
            NativeBridge.dasher_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_handle != IntPtr.Zero && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            NativeBridge.dasher_set_screen_size(_handle, (int)e.NewSize.Width, (int)e.NewSize.Height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_commands != null)
            CommandRenderer.Render(context, _commands, _strings ?? Array.Empty<string>(), Bounds.Size);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        if (_handle != IntPtr.Zero)
        {
            NativeBridge.dasher_mouse_move(_handle, (float)pos.X, (float)pos.Y);
            NativeBridge.dasher_mouse_down(_handle);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_handle != IntPtr.Zero)
        {
            var pos = e.GetPosition(this);
            NativeBridge.dasher_mouse_move(_handle, (float)pos.X, (float)pos.Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_handle != IntPtr.Zero)
        {
            var pos = e.GetPosition(this);
            NativeBridge.dasher_mouse_move(_handle, (float)pos.X, (float)pos.Y);
            NativeBridge.dasher_mouse_up(_handle);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_handle == IntPtr.Zero) return;

        var timeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        NativeBridge.dasher_frame(_handle, timeMs,
            out IntPtr cmdPtr, out int cmdCount,
            out IntPtr strPtr, out int strCount);

        if (cmdCount > 0 && cmdPtr != IntPtr.Zero)
        {
            _commands = new int[cmdCount];
            Marshal.Copy(cmdPtr, _commands, 0, cmdCount);
        }
        else
        {
            _commands = null;
        }

        if (strCount > 0 && strPtr != IntPtr.Zero)
        {
            _strings = new string[strCount];
            var ptrs = new IntPtr[strCount];
            Marshal.Copy(strPtr, ptrs, 0, strCount);
            for (int i = 0; i < strCount; i++)
                _strings[i] = Marshal.PtrToStringUTF8(ptrs[i]) ?? "";
        }
        else
        {
            _strings = null;
        }

        var outputPtr = NativeBridge.dasher_get_output_text(_handle);
        if (outputPtr != IntPtr.Zero)
        {
            var text = Marshal.PtrToStringUTF8(outputPtr);
            if (text != OutputText)
                OutputText = text ?? "";
        }

        InvalidateVisual();
    }
}
