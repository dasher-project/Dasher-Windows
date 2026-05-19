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
    private long _handle;
    private int[]? _commands;
    private string[]? _strings;
    private bool _mouseDown;
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

    public long GetHandle() => _handle;

    public void Initialize(string dataDir)
    {
        _handle = NativeBridge.dasher_create(dataDir);
        if (_handle == 0)
            throw new InvalidOperationException("Failed to create Dasher session");

        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            NativeBridge.dasher_set_screen_size(_handle, (int)Bounds.Width, (int)Bounds.Height);
        }

        _timer.Start();
    }

    public void Shutdown()
    {
        _timer.Stop();
        if (_handle != 0)
        {
            NativeBridge.dasher_destroy(_handle);
            _handle = 0;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return availableSize;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_handle != 0 && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            NativeBridge.dasher_set_screen_size(_handle, (int)e.NewSize.Width, (int)e.NewSize.Height);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_commands != null)
        {
            CommandRenderer.Render(context, _commands, _strings ?? Array.Empty<string>(), Bounds.Size);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _mouseDown = true;
        var pos = e.GetPosition(this);
        if (_handle != 0)
        {
            NativeBridge.dasher_mouse_move(_handle, (float)pos.X, (float)pos.Y);
            NativeBridge.dasher_mouse_down(_handle);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_handle != 0)
        {
            NativeBridge.dasher_mouse_move(_handle, (float)pos.X, (float)pos.Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _mouseDown = false;
        var pos = e.GetPosition(this);
        if (_handle != 0)
        {
            NativeBridge.dasher_mouse_move(_handle, (float)pos.X, (float)pos.Y);
            NativeBridge.dasher_mouse_up(_handle);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_handle == 0) return;

        var timeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = NativeBridge.dasher_frame(_handle, timeMs);

        if (result.CommandCount > 0 && result.Commands != IntPtr.Zero)
        {
            _commands = new int[result.CommandCount];
            Marshal.Copy(result.Commands, _commands, 0, result.CommandCount);
        }
        else
        {
            _commands = null;
        }

        if (result.StringCount > 0 && result.Strings != IntPtr.Zero)
        {
            _strings = new string[result.StringCount];
            var stringPtrs = new IntPtr[result.StringCount];
            Marshal.Copy(result.Strings, stringPtrs, 0, result.StringCount);
            for (int i = 0; i < result.StringCount; i++)
            {
                _strings[i] = Marshal.PtrToStringUTF8(stringPtrs[i]) ?? "";
            }
        }
        else
        {
            _strings = null;
        }

        NativeBridge.dasher_free_frame_result(ref result);

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
