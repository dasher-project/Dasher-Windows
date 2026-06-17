using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Dasher.Windows.Engine;
using Dasher.Windows.EyeGaze;

namespace Dasher.Windows.Controls;

public partial class DasherCanvas : Control
{
    private IntPtr _handle;
    private int[]? _commands;
    private string[]? _strings;
    private readonly DispatcherTimer _timer;
    private string _dasherFont = "";

    private EyeGazeIntegration? _eyeGazeIntegration;
    private bool _useEyeGazeInput;

    private NativeBridge.MessageCallback? _messageCallback;
    private bool _callbacksRegistered;
    private int _lastScreenWidth;
    private int _lastScreenHeight;

    public event EventHandler<EngineMessageEventArgs>? EngineMessage;

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

    public void PauseTimer() => _timer.Stop();
    public void ResumeTimer() => _timer.Start();

    public void Initialize(string dataDir, string userDir)
    {
        _handle = NativeBridge.dasher_create(dataDir, userDir, out var errorPtr);
        if (_handle == IntPtr.Zero)
        {
            var errorMsg = errorPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(errorPtr) ?? "Unknown error" : "Unknown error";
            throw new InvalidOperationException($"Failed to create Dasher session: {errorMsg}");
        }
        NativeBridge.dasher_set_screen_size(_handle, 700, 640);
        _timer.Start();
    }

    public void Shutdown()
    {
        DisableEyeGaze();
        DisableJoystick();
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
        TrySetScreenSize();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_commands != null)
            CommandRenderer.Render(context, _commands, _strings ?? Array.Empty<string>(), Bounds.Size, _dasherFont);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        if (_handle != IntPtr.Zero)
        {
            NativeBridge.dasher_mouse_move(_handle, (float)pos.X, (float)pos.Y);
            var props = e.GetCurrentPoint(this).Properties;
            if (props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
                NativeBridge.dasher_key_event(_handle, 101, 1);
            else
                NativeBridge.dasher_mouse_down(_handle);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_useEyeGazeInput) return;
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
            var props = e.GetCurrentPoint(this).Properties;
            if (props.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
                NativeBridge.dasher_key_event(_handle, 101, 0);
            else
                NativeBridge.dasher_mouse_up(_handle);
        }
    }

    private void EnsureCallbacksRegistered()
    {
        if (_callbacksRegistered) return;
        _callbacksRegistered = true;

        try
        {
            _messageCallback = new NativeBridge.MessageCallback(OnEngineMessage);
            NativeBridge.dasher_set_message_callback(_handle, _messageCallback, IntPtr.Zero);
        }
        catch { }
    }

    private void TrySetScreenSize()
    {
        if (_handle == IntPtr.Zero || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var w = (int)Bounds.Width;
        var h = (int)Bounds.Height;
        if (w == _lastScreenWidth && h == _lastScreenHeight) return;
        NativeBridge.dasher_set_screen_size(_handle, w, h);
        _lastScreenWidth = w;
        _lastScreenHeight = h;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_handle == IntPtr.Zero) return;

        EnsureCallbacksRegistered();
        TrySetScreenSize();

        var timeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        try
        {
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
        }
        catch { return; }

        try
        {
            var outputPtr = NativeBridge.dasher_get_output_text(_handle);
            if (outputPtr != IntPtr.Zero)
            {
                var text = Marshal.PtrToStringUTF8(outputPtr);
                if (text != OutputText)
                    OutputText = text ?? "";
            }
        }
        catch { }

        try
        {
            var fontPtr = NativeBridge.dasher_get_string_parameter(_handle, ParameterKeys.SP_DASHER_FONT);
            _dasherFont = fontPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(fontPtr) ?? "" : "";
        }
        catch { }

        InvalidateVisual();
    }

    private void OnEngineMessage(int messageType, IntPtr textPtr, IntPtr userData)
    {
        var text = textPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(textPtr) ?? "" : "";
        var isWarning = messageType == 1;
        Dispatcher.UIThread.Post(() =>
        {
            EngineMessage?.Invoke(this, new EngineMessageEventArgs(text, isWarning));
        });
    }
}

public class EngineMessageEventArgs : EventArgs
{
    public string Text { get; }
    public bool IsWarning { get; }
    public EngineMessageEventArgs(string text, bool isWarning) { Text = text; IsWarning = isWarning; }
}
