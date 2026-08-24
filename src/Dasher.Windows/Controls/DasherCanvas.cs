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
    private NativeBridge.LogCallback? _logCallback;
    private NativeBridge.OutputCallback? _outputCallback;
    private bool _callbacksRegistered;
    private int _lastScreenWidth;
    private int _lastScreenHeight;

    public event EventHandler<EngineMessageEventArgs>? EngineMessage;
    public event EventHandler? EngineFaultDetected;
    public event EventHandler<EngineOutputEventArgs>? EngineOutput;

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
    }

    /// <summary>
    /// Starts the engine (sets screen size, triggers Realize, starts timer).
    /// Call AFTER any pre-Realize parameter migration.
    /// </summary>
    public void StartEngine()
    {
        NativeBridge.dasher_set_screen_size(_handle, 700, 640);
        _timer.Start();
    }

    public void Shutdown()
    {
        DisableEyeGazeNonBlocking();
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

        // Cache canvas screen origin for the gaze callback thread (avoids
        // calling Avalonia APIs from a non-UI thread)
        if (_useEyeGazeInput)
        {
            var origin = Avalonia.VisualExtensions.PointToScreen(this, new Point(0, 0));
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            _cachedOriginX = (float)(origin.X / scaling);
            _cachedOriginY = (float)(origin.Y / scaling);
        }

        DrawEyeTrackIndicator(context);
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

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_useEyeGazeInput) return;
        if (_handle != IntPtr.Zero)
        {
            // Only send out-of-bounds coordinates if BP_STOP_OUTSIDE is enabled.
            // Without it, the engine should keep using the last known position
            // (which is inside the canvas — normal continuous zooming).
            var stopOutsideKey = NativeBridge.dasher_find_parameter_key("BP_STOP_OUTSIDE");
            if (stopOutsideKey >= 0 && NativeBridge.dasher_get_bool_parameter(_handle, stopOutsideKey) != 0)
                NativeBridge.dasher_mouse_move(_handle, -10000f, -10000f);
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

        try
        {
            _logCallback = new NativeBridge.LogCallback(OnEngineLog);
            NativeBridge.dasher_set_log_callback(_handle, _logCallback, IntPtr.Zero, 0);
        }
        catch { }

        try
        {
            // RFC 0015 §7: keyboard-mode injection consumes output-callback
            // events (0 insert / 1 delete / 2 buffer clear), not text diffs.
            _outputCallback = new NativeBridge.OutputCallback(OnEngineOutputEvent);
            NativeBridge.dasher_set_output_callback(_handle, _outputCallback, IntPtr.Zero);
        }
        catch { }
    }

    private static void OnEngineLog(int level, IntPtr messagePtr, IntPtr userData)
    {
        if (messagePtr == IntPtr.Zero) return;
        var msg = Marshal.PtrToStringUTF8(messagePtr) ?? "";

        // RFC 0009: feed engine log ring buffer for crash reports (info+ only)
        if (level >= 1)
            Services.AnalyticsService.AppendEngineLog(level, msg);

        System.Diagnostics.Debug.WriteLine($"[DasherCore:{level}] {msg}");
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

        // RFC 0009 A2: check engine error flag — if set, stop driving the engine
        if (NativeBridge.dasher_has_engine_error(_handle) != 0)
        {
            _timer.Stop();
            EngineFaultDetected?.Invoke(this, EventArgs.Empty);
            return;
        }

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

        // Update WPM stats (RFC 0012) — throttled to once per second
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000 < 16)
        {
            WpmUpdated?.Invoke(this, EventArgs.Empty);
        }

        InvalidateVisual();
    }

    public event EventHandler? WpmUpdated;

    private void OnEngineMessage(int messageType, IntPtr textPtr, IntPtr userData)
    {
        var text = textPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(textPtr) ?? "" : "";
        var isWarning = messageType == 1;
        Dispatcher.UIThread.Post(() =>
        {
            EngineMessage?.Invoke(this, new EngineMessageEventArgs(text, isWarning));
        });
    }

    private void OnEngineOutputEvent(int eventType, IntPtr textPtr, IntPtr userData)
    {
        var text = textPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(textPtr) ?? "" : "";
        // Events normally fire on the dasher_frame() thread; buffer-clear
        // events may fire on the thread calling the reset function itself.
        // Marshal to the UI thread preserving order.
        Dispatcher.UIThread.Post(() =>
        {
            EngineOutput?.Invoke(this, new EngineOutputEventArgs(eventType, text));
        });
    }
}

public class EngineMessageEventArgs : EventArgs
{
    public string Text { get; }
    public bool IsWarning { get; }
    public EngineMessageEventArgs(string text, bool isWarning) { Text = text; IsWarning = isWarning; }
}

public class EngineOutputEventArgs : EventArgs
{
    public const int EventInsert = 0;
    public const int EventDelete = 1;
    public const int EventBufferCleared = 2;

    public int EventType { get; }
    public string Text { get; }
    public EngineOutputEventArgs(int eventType, string text) { EventType = eventType; Text = text; }
}
