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
    private readonly DispatcherTimer? _fallbackTimer;
    private bool _frameLoopRunning;
    private bool _paused;
    private int _frameLoopGeneration;
    private double _lastWallMs = -1;
    private long _engineTimeMs;
    private long _lastWpmTimeMs = -1000;
    private long _lastFontPollMs = -250;

    // Grow-only frame buffers: the engine's command/string blocks are
    // ephemeral, so every frame used to allocate fresh arrays (~60/s) —
    // reused here to keep the render loop allocation-free in steady state.
    private int[] _commandBuffer = [];
    private int _commandCount;
    private IntPtr[] _stringPtrBuffer = [];
    private string[] _stringBuffer = [];
    private int _stringCount;

    private string _dasherFont = "";
    private string _lastTextMetricsFont = "";

    // Measurement results for the engine's text-size callback, keyed by
    // (text, font, size). The engine caches per label object, but a fresh
    // label (new node entering the view) re-asks; this keeps repeats cheap.
    private readonly System.Collections.Generic.Dictionary<(string text, string font, int size), (int w, int h)> _measureCache = new();

    private EyeGazeIntegration? _eyeGazeIntegration;
    private bool _useEyeGazeInput;

    private NativeBridge.MessageCallback? _messageCallback;
    private NativeBridge.LogCallback? _logCallback;
    private NativeBridge.OutputCallback? _outputCallback;
    private NativeBridge.TextSizeCallback? _textSizeCallback;
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
        // Fallback only — used when no TopLevel is attached (see StartEngine).
        _fallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _fallbackTimer.Tick += (s, e) => StepFrame();
    }

    public IntPtr GetHandle() => _handle;

    public void PauseTimer() => _paused = true;

    public void ResumeTimer()
    {
        _paused = false;
        // Drop the wall-clock baseline so the pause gap is not fed to the
        // engine as one huge time delta on resume.
        _lastWallMs = -1;
    }

    public void Initialize(string dataDir, string userDir)
    {
        _handle = NativeBridge.dasher_create(dataDir, userDir, out var errorPtr);
        if (_handle == IntPtr.Zero)
        {
            var errorMsg = errorPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(errorPtr) ?? "Unknown error" : "Unknown error";
            throw new InvalidOperationException($"Failed to create Dasher session: {errorMsg}");
        }

        // Per-handle state must not survive handle replacement (Settings >
        // Reset calls Shutdown + Initialize on the same canvas): without
        // this, EnsureCallbacksRegistered skips the new handle and the
        // output/message/log callbacks are never attached to it.
        _callbacksRegistered = false;
        _lastScreenWidth = 0;
        _lastScreenHeight = 0;
    }

    /// <summary>
    /// Starts the engine (sets screen size, triggers Realize, starts the
    /// frame loop). Call AFTER any pre-Realize parameter migration.
    /// </summary>
    public void StartEngine()
    {
        NativeBridge.dasher_set_screen_size(_handle, 700, 640);
        StartFrameLoop();
    }

    private void StartFrameLoop()
    {
        if (_frameLoopRunning) return;
        _frameLoopRunning = true;
        _lastWallMs = -1;

        // Generation token: a compositor callback queued before Shutdown()
        // (e.g. Settings > Reset destroying and recreating the engine on this
        // same canvas) must not revive itself alongside the new loop after
        // the restart - it would double-step the engine every frame.
        _frameLoopGeneration++;
        var generation = _frameLoopGeneration;

        // Drive the engine from the compositor's frame clock (RequestAnimationFrame)
        // so engine steps, invalidation and presentation share one cadence. A
        // free-running 16 ms DispatcherTimer drifts against the display refresh
        // and runs at Background priority (starved by input/render work), which
        // measured as ~26 ms average tick spacing with 32-64 ms spikes — the
        // choppiness reported in #35. GTK (frame clock) and Android (Choreographer)
        // already do the equivalent.
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel != null)
        {
            topLevel.RequestAnimationFrame(ts => OnAnimationFrame(ts, generation));
        }
        else
        {
            _fallbackTimer?.Start();
        }
    }

    private void OnAnimationFrame(TimeSpan timestamp, int generation)
    {
        if (!_frameLoopRunning || generation != _frameLoopGeneration) return;

        StepFrame();

        if (_frameLoopRunning && generation == _frameLoopGeneration)
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(ts => OnAnimationFrame(ts, generation));
    }

    private void StepFrame()
    {
        if (_handle == IntPtr.Zero || _paused) return;

        var wallMs = DateTimeOffset.UtcNow.Ticks / 10000.0;
        if (_lastWallMs < 0) _lastWallMs = wallMs;
        var delta = wallMs - _lastWallMs;
        _lastWallMs = wallMs;

        // Clamp the engine timeline: real deltas while running; bounded
        // steps after pauses/resumes so the engine never sees multi-second
        // jumps (it consumes raw deltas as zoom amount).
        _engineTimeMs += (long)Math.Clamp(delta, 1, 50);

        Tick(_engineTimeMs);
    }

    public void Shutdown()
    {
        DisableEyeGazeNonBlocking();
        DisableJoystick();
        _frameLoopRunning = false;
        _frameLoopGeneration++; // invalidate any still-queued compositor callback
        _fallbackTimer?.Stop();
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
        if (_commandCount > 0)
            CommandRenderer.Render(context, _commandBuffer, _commandCount, _stringBuffer, _stringCount, Bounds.Size, _dasherFont);

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

        try
        {
            // DasherCore v0.2.4: real label measurements instead of the
            // engine's glyphs-x-fontSize/2 estimate, which under-measured
            // wide Segoe UI glyphs and compounded into the jumbled right
            // edge at deep zoom (#28 / DasherCore #56). Fires on the UI
            // thread (the dasher_frame caller) so FormattedText is safe.
            _textSizeCallback = new NativeBridge.TextSizeCallback(OnTextSize);
            NativeBridge.dasher_set_text_size_callback(_handle, _textSizeCallback, IntPtr.Zero);
        }
        catch { }
    }

    private int OnTextSize(IntPtr textPtr, int fontSize, IntPtr outWidth, IntPtr outHeight, IntPtr userData)
    {
        // Contract (dasher.h): fill *out_width/*out_height and return 0 on
        // success; return non-zero to fall back to the engine's estimate.
        try
        {
            if (textPtr == IntPtr.Zero || fontSize <= 0 || outWidth == IntPtr.Zero || outHeight == IntPtr.Zero)
                return 1;

            var text = Marshal.PtrToStringUTF8(textPtr);
            if (string.IsNullOrEmpty(text)) return 1;

            var key = (text, _dasherFont, fontSize);
            if (!_measureCache.TryGetValue(key, out var size2))
            {
                // Same font the canvas draws opcode-5 text with (user-selected
                // Dasher font, or Segoe UI), so layout matches rendering.
                var formatted = new FormattedText(
                    text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    CommandRenderer.ResolveTypeface(_dasherFont),
                    fontSize,
                    Brushes.Black);

                size2 = ((int)Math.Ceiling(formatted.Width), (int)Math.Ceiling(formatted.Height));
                if (_measureCache.Count > 4096) _measureCache.Clear();
                _measureCache[key] = size2;
            }

            Marshal.WriteInt32(outWidth, size2.w);
            Marshal.WriteInt32(outHeight, size2.h);
            return 0;
        }
        catch
        {
            // Not-yet-ready font system etc. — the engine falls back to its
            // estimate without caching, and retries next frame.
            return 1;
        }
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

    private void Tick(long timeMs)
    {
        if (_handle == IntPtr.Zero) return;

        // RFC 0009 A2: check engine error flag — if set, stop driving the engine
        if (NativeBridge.dasher_has_engine_error(_handle) != 0)
        {
            _frameLoopRunning = false;
            _frameLoopGeneration++; // invalidate any still-queued compositor callback
            _fallbackTimer?.Stop();
            EngineFaultDetected?.Invoke(this, EventArgs.Empty);
            return;
        }

        EnsureCallbacksRegistered();
        TrySetScreenSize();

        // Poll the canvas font at a low rate (settings actions, not per-frame
        // state): a per-frame parameter read cost a P/Invoke + string
        // allocation 60x/s. The 250 ms latency only delays applying a new
        // font; measurement and rendering still share the same value.
        if (timeMs - _lastFontPollMs >= 250)
        {
            _lastFontPollMs = timeMs;
            try
            {
                var fontPtr = NativeBridge.dasher_get_string_parameter(_handle, ParameterKeys.SP_DASHER_FONT);
                _dasherFont = fontPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(fontPtr) ?? "" : "";
            }
            catch { }

            // Invalidate the engine's cached label measurements whenever the
            // font actually being drawn changes (font-picker, settings
            // import), so upcoming frames re-measure via the text-size
            // callback with the new font.
            if (_dasherFont != _lastTextMetricsFont)
            {
                _lastTextMetricsFont = _dasherFont;
                _measureCache.Clear();
                try { NativeBridge.dasher_text_metrics_changed(_handle); }
                catch { }
            }
        }

        try
        {
            NativeBridge.dasher_frame(_handle, timeMs,
                out IntPtr cmdPtr, out int cmdCount,
                out IntPtr strPtr, out int strCount);

            if (cmdCount > 0 && cmdPtr != IntPtr.Zero)
            {
                if (_commandBuffer.Length < cmdCount)
                    _commandBuffer = new int[Math.Max(cmdCount, _commandBuffer.Length * 2)];
                Marshal.Copy(cmdPtr, _commandBuffer, 0, cmdCount);
                _commandCount = cmdCount;
            }
            else
            {
                _commandCount = 0;
            }

            if (strCount > 0 && strPtr != IntPtr.Zero)
            {
                if (_stringPtrBuffer.Length < strCount)
                    _stringPtrBuffer = new IntPtr[Math.Max(strCount, _stringPtrBuffer.Length * 2)];
                Marshal.Copy(strPtr, _stringPtrBuffer, 0, strCount);

                if (_stringBuffer.Length < strCount)
                    _stringBuffer = new string[Math.Max(strCount, _stringBuffer.Length * 2)];
                for (int i = 0; i < strCount; i++)
                    _stringBuffer[i] = Marshal.PtrToStringUTF8(_stringPtrBuffer[i]) ?? "";
                _stringCount = strCount;
            }
            else
            {
                _stringCount = 0;
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

        // Update WPM stats (RFC 0012) — throttled to once per second
        if (timeMs - _lastWpmTimeMs >= 1000)
        {
            _lastWpmTimeMs = timeMs;
            WpmUpdated?.Invoke(this, EventArgs.Empty);
        }

        if (_commandCount > 0)
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
