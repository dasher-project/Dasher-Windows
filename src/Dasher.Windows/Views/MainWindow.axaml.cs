using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Dasher.Windows.Controls;
using Dasher.Windows.Engine;
using Dasher.Windows.EyeGaze;
using Dasher.Windows.Services;
using Dasher.Windows.Speech;
using Dasher.Windows.ViewModels;
using Lucide.Avalonia;

namespace Dasher.Windows.Views;

public partial class MainWindow : Window
{
    private DasherCanvas? _canvas;
    private MainWindowViewModel? _vm;
    private Button[]? _settingsTabs;
    private bool _settingsInitialized;
    private NativeBridge.SpeakCallback? _speakCallback;
    private NativeBridge.ParameterCallback? _parameterCallback;

    /// <summary>
    /// RFC 0015 direct-entry target tracking: WinEventHooks, the rooted
    /// target HWND, and foreground restoration before input injection.
    /// See KeyboardTargetTracker — MainWindow only decides what to seed.
    /// </summary>
    private readonly Services.KeyboardTargetTracker _targetTracker = new();

    // Window geometry persistence (issue #46): last-known normal-state
    // bounds, tracked continuously so closing while maximized saves the
    // restore bounds rather than the maximized ones.
    private WindowSettings _windowSettings = new();
    private PixelPoint _lastNormalPosition = new(int.MinValue, int.MinValue);
    private Size? _lastNormalSize;
    private bool _geometryRestored;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // IME composition probe (RFC 0019 clause 2 — defer seeding until commit).
    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hImc);

    [DllImport("imm32.dll")]
    private static extern int ImmGetCompositionStringW(IntPtr hImc, int dwIndex, IntPtr lpBuf, int dwBufLen);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Ex-style access is centralized in Services.WindowDialogStyles (single
    // source of truth for the 32/64-bit entry-point switch and the WS_EX_
    // constants — formerly duplicated here).
    private const long WS_EX_NOACTIVATE = Services.WindowDialogStyles.WS_EX_NOACTIVATE;
    private const long WS_EX_LAYERED = Services.WindowDialogStyles.WS_EX_LAYERED;

    private static long GetWindowExStyle(IntPtr hWnd) => Services.WindowDialogStyles.GetExStyle(hWnd);

    private static long SetWindowExStyle(IntPtr hWnd, long value) => Services.WindowDialogStyles.SetExStyle(hWnd, value);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        // MOUSEINPUT is the largest member of the Windows union — it MUST be
        // declared so Marshal.SizeOf<INPUT>() matches the native sizeof(INPUT).
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBOARDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBOARDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_RETURN = 0x0D;

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowGeometry();
        TrackNormalStateGeometry();
        WireEditingKeyRepeat();
        // (the custom-action wiring moved to OnOpened - review loop 3:
        // _canvas is null in the constructor, so the ctor-time call saw
        // null and no select event ever reached a subscriber)
    }

    /// <summary>
    /// Hold-to-repeat for the ⌫ / ← / → editing keys: press fires once,
    /// holding repeats after a 350 ms deliberate-hold delay (then every
    /// 90 ms for backspace / 120 ms for arrows). Single-click-only would
    /// make deleting a word unusable for the AAC input these buttons are
    /// for. Pointer events drive the action (Click stays unwired — press
    /// + click would double-fire).
    /// </summary>
    private sealed class KeyRepeatState
    {
        public Avalonia.Threading.DispatcherTimer? Delay;
        public Avalonia.Threading.DispatcherTimer? Repeat;
        public void Stop()
        {
            Delay?.Stop(); Delay = null;
            Repeat?.Stop(); Repeat = null;
        }
    }

    private readonly List<KeyRepeatState> _editKeyRepeatStates = [];

    private void WireEditingKeyRepeat()
    {
        // press = telemetry + action (once per press — repeat ticks are not
        // activations); repeat = action only.
        WireRepeat(KbBackspaceBtn, () => OnEditBackspaceKey(true), () => OnEditBackspaceKey(false), repeatMs: 90);
        WireRepeat(KbLeftBtn, () => OnEditArrowLeftKey(true), () => OnEditArrowLeftKey(false), repeatMs: 120);
        WireRepeat(KbRightBtn, () => OnEditArrowRightKey(true), () => OnEditArrowRightKey(false), repeatMs: 120);
        WireRepeat(PaneBackspaceBtn, () => OnEditBackspaceKey(true), () => OnEditBackspaceKey(false), repeatMs: 90);
        WireRepeat(PaneLeftBtn, () => OnEditArrowLeftKey(true), () => OnEditArrowLeftKey(false), repeatMs: 120);
        WireRepeat(PaneRightBtn, () => OnEditArrowRightKey(true), () => OnEditArrowRightKey(false), repeatMs: 120);
    }

    private void WireRepeat(Avalonia.Controls.Button button, Action press, Action repeat, int repeatMs)
    {
        var state = new KeyRepeatState();
        _editKeyRepeatStates.Add(state);

        // handledEventsToo: Button marks pointer events handled internally
        // (pressed visual state) — the repeat must still see them.
        button.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e is Avalonia.Input.PointerPressedEventArgs ppe &&
                ppe.GetCurrentPoint(button).Properties.PointerUpdateKind !=
                    Avalonia.Input.PointerUpdateKind.LeftButtonPressed)
                return; // primary presses only, like every Click-wired sibling
            state.Stop();
            press(); // immediate action (with telemetry) on press
            // Deliberate-hold delay before repeating begins
            state.Delay = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            state.Delay.Tick += (_, _) =>
            {
                state.Delay?.Stop(); state.Delay = null;
                state.Repeat = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(repeatMs) };
                state.Repeat.Tick += (_, _) => repeat();
                state.Repeat.Start();
            };
            state.Delay.Start();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        button.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent,
            (_, _) => state.Stop(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        button.AddHandler(Avalonia.Input.InputElement.PointerCaptureLostEvent,
            (_, _) => state.Stop(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        // Mid-hold detach (mode switch clears MainGrid, pane re-layout):
        // capture-lost covers most cases; this closes the rest.
        button.DetachedFromVisualTree += (_, _) => state.Stop();
    }

    // ── Window geometry persistence (issue #46) ─────────────────────────────

    /// <summary>
    /// Restore saved bounds before the window is shown. Falls back to the
    /// AXAML default (1024x768, centered) when nothing is saved or the saved
    /// geometry is stranded off every currently-connected screen (monitor
    /// unplugged / DPI change). Restores the bounds matching the saved pane
    /// mode so a direct-mode-quit's small window doesn't shrink the next
    /// normal launch.
    /// </summary>
    private void RestoreWindowGeometry()
    {
        _windowSettings = WindowSettings.Load();
        var keyboardMode = PaneSettings.Load().PanePosition == PanePosition.Keyboard.ToString();

        // Set the mode's minimum BEFORE the saved-bounds check: this runs in
        // the constructor, when MinWidth still holds the AXAML default (600)
        // — a valid saved direct-mode size of e.g. 400 would be rejected and
        // the window would open at the normal-mode default instead.
        if (keyboardMode)
        {
            MinWidth = 300;
            MinHeight = 250;
        }

        var hasBounds = keyboardMode ? _windowSettings.HasDirectBounds : _windowSettings.HasNormalBounds;
        if (hasBounds)
        {
            var x = keyboardMode ? _windowSettings.DirectX : _windowSettings.X;
            var y = keyboardMode ? _windowSettings.DirectY : _windowSettings.Y;
            var w = keyboardMode ? _windowSettings.DirectWidth : _windowSettings.Width;
            var h = keyboardMode ? _windowSettings.DirectHeight : _windowSettings.Height;

            if (IsGeometryUsableOnAnyScreen(x, y, w, h))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint((int)x, (int)y);
                if (!double.IsNaN(w) && w >= MinWidth && !double.IsNaN(h) && h >= MinHeight)
                {
                    Width = w;
                    Height = h;
                }
                if (!keyboardMode && _windowSettings.Maximized)
                    WindowState = WindowState.Maximized;
            }
        }

        // Startup restore has RUN (whether or not it found usable bounds) —
        // later mode switches must apply mode bounds even when this session
        // started fresh (review: leaving the flag false suppressed runtime
        // mode-bound application for the whole process).
        _geometryRestored = true;
    }

    /// <summary>
    /// True when the restored window RECT keeps a substantial usable area on
    /// some connected screen — an intersection at least 100x100 PHYSICAL px
    /// in both dimensions, so the visible chunk is draggable. Deliberately
    /// accepts partially off-screen placements the user chose (multi-monitor
    /// straddles, edge-parked windows with most of their body visible) and
    /// rejects only geometry stranded behind a bezel or on an unplugged
    /// monitor. (Review history: area-only tests admitted un-grabbable
    /// slivers; the fixed top-left probe rejected valid straddles; DIP sizes
    /// must be converted to physical px; and the conversion must use ONE
    /// coherent window scale — per-candidate-screen scaling inflated the
    /// rect on higher-DPI displays and admitted geometry whose real visible
    /// intersection was under the threshold.)
    /// </summary>
    private bool IsGeometryUsableOnAnyScreen(double x, double y, double w, double h)
    {
        try
        {
            const double minVisiblePx = 100;
            if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0) return false;
            if (Screens.ScreenCount == 0) return false;

            // x/y (Window.Position) and Screen.Bounds are physical pixels;
            // w/h (Window.Width/Height) are DIPs. A window renders at ONE
            // physical size: convert with the scaling of the screen owning
            // the saved origin (the best predictor of the scale the OS will
            // apply at restore), falling back to the primary screen.
            var origin = new PixelPoint((int)x, (int)y);
            var owner = Screens.All.FirstOrDefault(s => s.Bounds.Contains(origin))
                        ?? Screens.Primary
                        ?? Screens.All[0];
            var pw = w * owner.Scaling;
            var ph = h * owner.Scaling;

            return Screens.All.Any(s =>
            {
                var iw = Math.Min(x + pw, s.Bounds.Right) - Math.Max(x, s.Bounds.X);
                var ih = Math.Min(y + ph, s.Bounds.Bottom) - Math.Max(y, s.Bounds.Y);
                return iw >= minVisiblePx && ih >= minVisiblePx;
            });
        }
        catch { return false; }
    }

    /// <summary>Continuously remember the normal-state bounds so closing while
    /// maximized saves the restore bounds, not the maximized ones. Trackers are
    /// shared across modes (events don't know the pane mode), so every
    /// programmatic mode-bounds application re-seeds them — otherwise geometry
    /// captured while in keyboard mode would persist as the normal restore
    /// bounds after returning to a maximized normal window (review).</summary>
    private void TrackNormalStateGeometry()
    {
        PositionChanged += (_, _) => { if (WindowState == WindowState.Normal) _lastNormalPosition = Position; };
        Resized += (_, e) =>
        {
            if (WindowState == WindowState.Normal && e.ClientSize.Width > 0)
                _lastNormalSize = e.ClientSize;
        };
    }

    private void SeedTrackers(PixelPoint position, Size size)
    {
        _lastNormalPosition = position;
        _lastNormalSize = size;
    }

    private void SaveWindowGeometry()
    {
        var keyboardMode = _vm?.PanePosition == PanePosition.Keyboard;
        var pos = _lastNormalPosition.X != int.MinValue ? _lastNormalPosition : Position;
        var size = _lastNormalSize ?? Bounds.Size;

        if (keyboardMode)
        {
            _windowSettings.DirectX = pos.X;
            _windowSettings.DirectY = pos.Y;
            _windowSettings.DirectWidth = size.Width;
            _windowSettings.DirectHeight = size.Height;
        }
        else
        {
            // Closing while maximized with no tracker history this session
            // (e.g. started maximized, went keyboard, returned maximized,
            // closed): Position/Bounds are the MAXIMIZED frame — don't let
            // them clobber the saved normal restore bounds; keep the bucket
            // and only refresh the maximized flag (review).
            if (WindowState == WindowState.Maximized &&
                _lastNormalPosition.X == int.MinValue && _lastNormalSize == null)
            {
                _windowSettings.Maximized = true;
            }
            else
            {
                _windowSettings.X = pos.X;
                _windowSettings.Y = pos.Y;
                _windowSettings.Width = size.Width;
                _windowSettings.Height = size.Height;
                _windowSettings.Maximized = WindowState == WindowState.Maximized;
            }
        }
        _windowSettings.Save();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        _canvas = this.FindControl<DasherCanvas>("DasherCanvas");
        WireCustomActions(); // after _canvas is populated - the ctor-time call saw null (review loop 3)
        _vm = DataContext as MainWindowViewModel;
        if (_canvas == null || _vm == null) return;

        ThemeBrushes.Initialize(this);

        // Keyboard (direct) mode is a deliberately small floating overlay:
        // halve the normal-mode minimum (issue: "direct mode window has a
        // minimum size — make it smaller, at least half").
        UpdateWindowMinimum();

        // RFC 0018: the overlay ships visible in the AXAML so the first
        // rendered frame is already themed + occupied. Localise it now.
        StartupOverlayText.Text = Loc.Tr("preparing_dasher", "Preparing Dasher…");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDir = Path.Combine(appData, "Dasher");
        Directory.CreateDirectory(dataDir);

        // Check for reset-to-defaults flag (written by Settings > Privacy > Reset)
        var resetFlag = Path.Combine(dataDir, "reset_settings_pending");
        if (File.Exists(resetFlag))
        {
            var settingsFile = Path.Combine(dataDir, "dasher_settings.xml");
            try { if (File.Exists(settingsFile)) File.Delete(settingsFile); } catch { }
            try { File.Delete(resetFlag); } catch { }
        }

        var coreDataDir = FindCoreDataDir();

        this.Deactivated += (_, _) =>
        {
            // A native picker dialog legitimately takes foreground while we
            // are dialog-friendly (PickerSession); recording IT as the typing
            // target would misroute post-dialog input (review loop 1).
            if (_pickerSessionActive) return;
            var fg = GetForegroundWindow();
            var ourHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (fg != ourHandle && fg != IntPtr.Zero)
            {
                var changed = _targetTracker.RecordForeground(fg, ourHandle);
                KbLog($"Deactivated: target window = 0x{fg:X} (changed={changed})");
                // RFC 0015 tier 2/3: switching target fields re-reads and
                // re-seeds the new field's text + caret. This is the
                // PRIMARY seeding trigger — the user just clicked into the
                // target, so the caret is where they want to continue.
                // (Best-effort path: the WS_EX_NOACTIVATE overlay suppresses
                // Deactivated in keyboard mode — the tracker's hooks cover it.)
                _ = SeedContextFromTargetAsync(changed ? "target changed" : "refocus");
            }
        };

        // Tracker events arrive on the UI thread (hook callbacks run on the
        // installing thread). Both window switches and same-window field
        // changes re-seed — the field text/caret is what matters, not the HWND.
        _targetTracker.TargetChanged += reason =>
        {
            if (_pickerSessionActive) return; // a picker dialog, not the user's target
            ArmSelectionWatch();
            _ = SeedContextFromTargetAsync(reason);
        };

        // Native pickers (training import/export, Open, Save) coordinate
        // through PickerSession: window styles normalized, target tracking
        // suspended for the dialog's lifetime, previous target focused back.
        Services.PickerSession.Register(SuspendTargetTracking, ResumeTargetTracking);
        Services.PickerSession.Log = KbLog;

        // RFC 0018: everything startup-blocking (data install, engine create,
        // v5 scan) runs off the UI thread so the overlay animates; the
        // spinner must never freeze and the window must never go dead.
        _ = StartupAsync(coreDataDir, dataDir);
    }

    /// <summary>True while a native picker dialog owns the foreground: no
    /// foreground-change tracking (the DIALOG would become the "target").</summary>
    private bool _pickerSessionActive;

    private void SuspendTargetTracking()
    {
        _pickerSessionActive = true;
        KbLog("PickerSession: suspending target tracking");
        if (_vm is not { IsKeyboardMode: true }) return;
        _targetTracker.Remove();
        // Pair with the editor-mode convention (ApplyPaneLayout): a stopped
        // watch MUST clear _lastWatchTarget, else ArmSelectionWatch's
        // same-target early-out leaves the caret re-seed dead after the
        // dialog (review loop 2, issue A).
        _lastWatchTarget = IntPtr.Zero;
        Services.TargetContextReader.StopSelectionWatch();
    }

    private void ResumeTargetTracking()
    {
        _pickerSessionActive = false;
        KbLog($"PickerSession: resuming (target=0x{_targetTracker.Current:X})");
        if (_vm is not { IsKeyboardMode: true }) return;
        _targetTracker.Install(OurHwnd());
        ArmSelectionWatch();
        // Give the previous typing target its focus back BEFORE the window
        // styles make Dasher non-activatable again (review loop 1: the dead
        // dialog HWND would otherwise stay the recorded target and the first
        // post-dialog characters would go nowhere).
        Services.WindowDialogStyles.TrySetForeground(_targetTracker.Current);
    }

    private async Task StartupAsync(string coreDataDir, string dataDir)
    {
        bool needsMigration = false;
        string? error = null;

        try
        {
            await Task.Run(() =>
            {
                CopyDataIfNeeded(coreDataDir, dataDir);
                _canvas!.Initialize(dataDir, dataDir);
            });

            _canvas!.EngineMessage += OnEngineMessage;
            _canvas.EngineFaultDetected += OnEngineFault;
            _canvas.WpmUpdated += OnWpmUpdated;
            _canvas.EngineOutput += OnEngineOutput;
            _canvas.FirstEngineFrame += OnFirstEngineFrame;
            _vm!.SetHandle(_canvas.GetHandle());

            _speakCallback = new NativeBridge.SpeakCallback(OnEngineSpeak);
            NativeBridge.dasher_set_speak_callback(_vm.Handle, _speakCallback, IntPtr.Zero);

            _parameterCallback = new NativeBridge.ParameterCallback(OnParameterChanged);
            NativeBridge.dasher_set_parameter_callback(_vm.Handle, _parameterCallback, IntPtr.Zero);

            var accessConfig = AccessConfiguration.Load();
            accessConfig.Apply(_vm.Handle);

            needsMigration = await Task.Run(() =>
                V5MigrationService.HasV5Data && !V5MigrationService.HasBeenOffered);

            if (!needsMigration)
                await CompleteStartupAsync(null);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error != null)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // RFC 0018: on failure show the error on the overlay —
                // never back to a blank window.
                StartupOverlayText.IsVisible = false;
                StartupOverlayError.Text = $"Dasher failed to start:\n{error}";
                StartupOverlayError.IsVisible = true;
            });
            return;
        }

        if (needsMigration)
            ShowV5MigrationPrompt();
    }

    private void OnFirstEngineFrame(object? sender, EventArgs e)
    {
        // The canvas has drawn real content — retire the loading overlay
        // (RFC 0018). Runs on the UI thread via the compositor callback.
        StartupOverlay.IsVisible = false;
    }

    private void ShowV5MigrationPrompt()
    {
        var scan = V5MigrationService.Scan();

        var dialog = new Window
        {
            Title = Services.Loc.Tr("dasher5_settings_found"),
            Width = 460,
            Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
        };

        panel.Children.Add(new TextBlock
        {
            Text = Services.Loc.Tr("dasher5_config_found"),
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrushes.TextPrimary,
        });

        var summary = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(scan.Alphabet))
            summary.AppendLine($"  Alphabet: {scan.Alphabet}");
        if (!string.IsNullOrEmpty(scan.Colour))
            summary.AppendLine($"  Colour palette: {scan.Colour}");
        if (!string.IsNullOrEmpty(scan.Speed))
            summary.AppendLine($"  Speed: {scan.Speed}");
        if (scan.ControlMode)
            summary.AppendLine("  Control mode: enabled");
        if (scan.CustomFileCount > 0)
            summary.AppendLine($"  {scan.CustomFileCount} custom file(s)");

        panel.Children.Add(new TextBlock
        {
            Text = summary.ToString().TrimEnd(),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrushes.TextSecondary,
        });

        panel.Children.Add(new TextBlock
        {
            Text = Services.Loc.Tr("dasher5_import_prompt"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrushes.TextSecondary,
        });

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var laterBtn = new Button
        {
            Content = Services.Loc.Tr("start_fresh"),
            Padding = new Thickness(20, 8),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE6, 0xE8)),
        };

        var importBtn = new Button
        {
            Content = Services.Loc.Tr("import_settings"),
            Padding = new Thickness(20, 8),
            Background = new SolidColorBrush(Color.FromRgb(0x99, 0xD4, 0xCD)),
            Foreground = ThemeBrushes.TextPrimary,
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0),
        };

        V5MigrationResult? migrationResult = null;

        laterBtn.Click += (_, _) =>
        {
            V5MigrationService.MarkCompleted();
            dialog.Close();
        };

        importBtn.Click += (_, _) =>
        {
            migrationResult = V5MigrationService.Import(_vm!.Handle);
            dialog.Close();
        };

        btnRow.Children.Add(laterBtn);
        btnRow.Children.Add(importBtn);
        panel.Children.Add(btnRow);

        dialog.Content = panel;
        dialog.Closed += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => await CompleteStartupAsync(migrationResult));
        dialog.ShowDialog(this);
    }

    private async Task CompleteStartupAsync(V5MigrationResult? migrationResult)
    {
        // Phase 3: Start engine. Realize runs off the UI thread (RFC 0018) so
        // the startup overlay keeps animating; the frame loop starts after.
        await _canvas!.RealizeOffUiThreadAsync();
        _canvas.BeginFrameLoop();

        // Localise: resolve the OS language, push it into the engine so
        // parameter labels follow the same locale (RFC 0003 one-locale).
        Loc.InitializeFromSystem(_vm!.Handle);
        if (Loc.IsRtl)
            FlowDirection = FlowDirection.RightToLeft;

        // Phase 4: Apply deferred parameters (must be after Realize)
        if (migrationResult != null)
        {
            foreach (var (key, value) in migrationResult.DeferredParameters)
            {
                if (value == "true" || value == "false")
                    NativeBridge.dasher_set_bool_parameter(_vm!.Handle, key, value == "true" ? 1 : 0);
                else if (int.TryParse(value, out var intVal))
                    NativeBridge.dasher_set_long_parameter(_vm!.Handle, key, intVal);
                else
                    NativeBridge.dasher_set_string_parameter(_vm!.Handle, key, value);
            }

            // The access-method gate must win over migrated values: a v5
            // import can carry Autocalibrate=true, which would re-enable
            // pointer drift for non-gaze users (DasherCore #64). The access
            // method — not the old file — decides.
            var migratedConfig = AccessConfiguration.Load();
            // The filter is engine-owned now (Apply doesn't push it); this
            // just refreshes the access.json mirror to whatever the
            // migration left active, so the file reflects the live choice.
            var filterPtr = NativeBridge.dasher_get_string_parameter(_vm.Handle, ParameterKeys.SP_INPUT_FILTER);
            var activeFilter = filterPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(filterPtr) : null;
            if (!string.IsNullOrEmpty(activeFilter))
            {
                migratedConfig.InputFilter = activeFilter;
                migratedConfig.Save();
            }
            migratedConfig.Apply(_vm.Handle);
        }

        // Phase 5: Continue with UI setup
        // Read the speed from the engine (which loaded from dasher_settings.xml)
        // rather than pushing the ViewModel default (1.0) which overwrites saved settings.
        // Speed is displayed and set in v5 units (raw LP_MAX_BITRATE / 100), with
        // bounds from the engine manifest - the old percent helper clamped the
        // display to 4.0x and the steppers to 5.0x, below v5's 8.0 range.
        _vm!.LoadSpeedBounds();
        _vm.LoadSpeedFromEngine();
        _vm.AutoSpeed = NativeBridge.dasher_get_bool_parameter(_vm.Handle, ParameterKeys.BP_AUTO_SPEEDCONTROL) != 0;
        _vm.Learning = NativeBridge.dasher_get_bool_parameter(_vm.Handle, ParameterKeys.BP_LM_ADAPTIVE) != 0;
        _controlModeActive = NativeBridge.dasher_get_bool_parameter(_vm.Handle, ParameterKeys.BP_CONTROL_MODE) != 0;
        UpdateControlModeLabel();

        _vm.LoadAlphabets();

        var currentAlphaPtr = NativeBridge.dasher_get_alphabet_id(_vm.Handle);
        var currentAlpha = currentAlphaPtr != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(currentAlphaPtr) ?? "" : "";
        _vm.SelectedLanguageIndex = Math.Max(0, _vm.Languages.IndexOf(currentAlpha));

        ApplyOutputFontSettings();

        var paneSettings = PaneSettings.Load();
        if (Enum.TryParse<PanePosition>(paneSettings.PanePosition, out var savedPos))
            _vm.PanePosition = savedPos;
        _vm.IsKeyboardMode = _vm.PanePosition == PanePosition.Keyboard;
        _vm.IsStatusBarHidden = paneSettings.StatusBarHidden;
        // Startup enters the persisted mode WITHOUT SetPanePosition (that is
        // interactive-only), so the mode's minimum must be re-applied here —
        // otherwise a restart into keyboard mode keeps the AXAML 600x500
        // minimum for the whole session and the window cannot be shrunk.
        UpdateWindowMinimum();
        ApplyPaneLayout();

        _vm.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.OutputText))
                OnOutputTextChanged();
        };

        // RFC 0019 (the editor contract): editable pane + caret re-anchor.
        WireEditorSync();

        // RFC 0019 clause 5 — New buttons, localized from the shared
        // catalogue (new + new_session_tooltip; dasher-shared-resources#3).
        var newTip = Loc.Tr("new_session_tooltip", "New session (clear text and context)");
        ToolTip.SetTip(BtnNewSession, newTip);
        ToolTip.SetTip(KbNewSession, newTip);
        BtnNewSessionLabel.Text = Loc.Tr("new", "New");

        // Wire speech volume slider to SpeechService
        var volSlider = this.FindControl<Avalonia.Controls.Slider>("SpeakVolumeSlider");
        if (volSlider != null)
        {
            volSlider.Value = SpeechService.Instance.SpeechVolume;
            volSlider.ValueChanged += OnSpeakVolumeChanged;
            // setting_changed fires on commit (drag release / keyboard
            // adjust incl. Page/Home keys), deduped — never per tick.
            int lastVol = SpeechService.Instance.SpeechVolume;
            void VolCommit()
            {
                var v = (int)Math.Round(volSlider.Value);
                if (v == lastVol) return;
                lastVol = v;
                OnSpeakVolumeCommitted();
            }
            volSlider.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent,
                (_, _) => VolCommit(), Avalonia.Interactivity.RoutingStrategies.Bubble);
            volSlider.KeyUp += (s, e) =>
            {
                if (e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right or Avalonia.Input.Key.Up
                    or Avalonia.Input.Key.Down or Avalonia.Input.Key.PageUp or Avalonia.Input.Key.PageDown
                    or Avalonia.Input.Key.Home or Avalonia.Input.Key.End)
                    VolCommit();
            };
        }

        // RFC 0001 2026-10-28: status-bar Auto/Learning toggles are bound
        // TwoWay to VM properties that also change programmatically
        // (startup/reset), so instrumentation is attached view-side here —
        // AFTER the startup value load — and suppressed around programmatic
        // writes (see OnResetSettingsRequested).
        InstrumentStatusToggles();

#if !STORE
        _ = CheckForUpdatesAsync();
#endif

        RestoreEyeGazeFromConfig();
    }

    private async void RestoreEyeGazeFromConfig()
    {
        try
        {
            var config = AccessConfiguration.Load();
            if (config.Method != AccessMethod.EyeGaze) return;

            var trackerType = config.EyeTrackerType switch
            {
                "UdpGaze" => EyeGazeIntegration.TrackerType.UdpGazeTracker,
                "Eyetuitive" => EyeGazeIntegration.TrackerType.Eyetuitive,
                "Tobii" => EyeGazeIntegration.TrackerType.TobiiStreamEngine,
                _ => EyeGazeIntegration.TrackerType.WindowsNative,
            };

            EyeGazeLogger.Log($"=== Startup: restoring eye gaze ({config.EyeTrackerType}) ===");

            if (_canvas == null) return;
            _canvas.DisableJoystick();

            // Detailed eye-tracker diagnostics (per-sample lines, raw dumps,
            // filter stats). Lifecycle logging is always on.
            EyeGaze.EyeGazeLogger.Verbose = config.GazeDiagnostics;

            // Apply the gaze filter configuration (transit zoom fix)
            if (config.GazeFilterEnabled)
            {
                _canvas.ConfigureGazeFilter(
                    config.GazeDeadZonePx,
                    config.GazeMaxVelocityPxPerMs,
                    config.GazeSettleMs);
            }
            else
            {
                _canvas.ConfigureGazeFilter(); // disable (raw passthrough)
            }

            var ok = await _canvas.InitializeEyeGazeAsync(trackerType, config.UdpPort);
            if (!ok)
            {
                ToastNotifier.Show("Eye Tracker Error",
                    $"Could not connect to eye tracker on startup. Check the device and try re-selecting it in Settings > Input.",
                    isWarning: true);
            }
        }
        catch (Exception ex)
        {
            EyeGazeLogger.Log($"RestoreEyeGazeFromConfig exception: {ex}");
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Editing-key hold-repeat timers must not outlive the window
        // (review loop 1: a mid-hold close left the dispatcher firing).
        foreach (var s in _editKeyRepeatStates) s.Stop();
        Services.PickerSession.Unregister(SuspendTargetTracking, ResumeTargetTracking);
        Services.PickerSession.Log = null;
        SaveWindowGeometry();
        _canvas?.Shutdown();
        _ = AnalyticsService.ShutdownAsync();
        base.OnClosing(e);
    }

    private static SolidColorBrush ArgbToBrush(int argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);
        if (a == 0) a = 255;
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    private void OnOutputTextChanged()
    {
        if (_vm == null) return;

        // Keyboard-mode injection is driven by engine output events
        // (OnEngineOutput, RFC 0015 §7), not by diffing OutputText.
        // (OutputText itself is now pushed event-driven too — the canvas
        // marks it dirty from the output callback, so pane edits that
        // don't fire engine events no longer race a per-frame push.)
        if (_gameModeActive)
            SyncGameModeState();
    }

    // ── Editor contract (RFC 0019): editable pane + caret re-anchor ────────

    /// <summary>
    /// True while an engine-origin text push is being applied to the pane —
    /// the sync handlers must not feed it back into the engine.
    /// </summary>
    private bool _suppressEditorSync;

    private void WireEditorSync()
    {
        // Engine-origin pushes arrive as VM changes whose value differs from
        // the TextBox (a user keystroke changes the TextBox first; the TwoWay
        // binding then updates the VM). Applying them here lets us preserve
        // the caret and suppress the feedback, per RFC 0019 clause 4.
        // TextChanged is DEFERRED by Avalonia (dispatcher), so these handlers
        // can run after a mode switch re-parened the controls — every
        // reference is snapshotted and null-guarded (the crash report:
        // NRE on MessageArea.Text.Length after a null binding push).
        _vm!.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName != nameof(MainWindowViewModel.OutputText)) return;
            if (_vm.IsKeyboardMode) return; // pane hidden in direct mode
            var newText = _vm.OutputText ?? "";
            var box = MessageArea;
            if (box == null || newText == box.Text) return; // user-origin (binding already synced)

            _suppressEditorSync = true;
            try
            {
                // Caret preservation (clause 4): a caret at the end of the
                // old text follows the growth (zooming at the caret keeps the
                // caret with the new text); otherwise it stays at its index,
                // clamped. Insert/delete-position math against the engine
                // offset can refine this later if users need it.
                var oldCaret = box.CaretIndex;
                var oldLen = (box.Text ?? "").Length;
                box.Text = newText;
                box.CaretIndex = oldCaret >= oldLen
                    ? newText.Length
                    : Math.Min(oldCaret, newText.Length);
            }
            finally { _suppressEditorSync = false; }
        };

        MessageArea.TextChanged += (s, e) =>
        {
            var vm = _vm;
            var box = s as TextBox ?? MessageArea;
            if (vm == null || vm.Handle == IntPtr.Zero) return;
            if (box == null) return;
            if (_suppressEditorSync) return;

            var paneText = box.Text ?? "";

            // The engine buffer is the origin of truth: a pane change that
            // makes it EQUAL the buffer is an engine push landing (nothing
            // to seed); a difference is a user edit. Seeding is immediate —
            // the canvas pushes the engine text into the VM every frame, so
            // a debounced edit would be clobbered before the timer fired
            // (clause 2's "debouncing allowed" traded away for correctness
            // on this architecture).
            var enginePtr = NativeBridge.dasher_get_output_text(vm.Handle);
            var engineText = enginePtr != IntPtr.Zero
                ? Marshal.PtrToStringUTF8(enginePtr) ?? "" : "";
            if (paneText == engineText) return;

            // RFC 0019 clause 2 — IME composition deferral: mid-composition
            // TextChanged events carry the evolving composition string;
            // seeding each intermediate state is wasted rebuilds. Defer until
            // composition ends (the recheck timer completes the seed). On
            // IMMs where the probe can't see composition this degrades to
            // today's immediate seeding — always correct, merely noisier.
            if (ImeCompositionActive())
            {
                _imeSeedPending = true;
                EnsureImeRecheckTimer().Start();
                return;
            }

            SeedPaneText(vm, box, paneText);
        };

        MessageArea.PropertyChanged += (s, args) =>
        {
            if (args.Property != TextBox.CaretIndexProperty) return;
            if (_suppressEditorSync) return;
            var vm = _vm;
            var box = MessageArea;
            if (vm == null || vm.Handle == IntPtr.Zero || box == null) return;

            // Pure caret move with the text already in sync → re-anchor the
            // model (v5's SetOffset-on-click, clause 3).
            var enginePtr = NativeBridge.dasher_get_output_text(vm.Handle);
            var engineText = enginePtr != IntPtr.Zero
                ? Marshal.PtrToStringUTF8(enginePtr) ?? "" : "";
            var paneText = box.Text ?? "";
            if (engineText.Length == 0 || paneText != engineText) return;

            var caretBytes = NativeBridge.dasher_byte_offset_from_utf16(engineText, box.CaretIndex);
            if (caretBytes >= 0 && caretBytes != NativeBridge.dasher_get_offset(vm.Handle))
                NativeBridge.dasher_set_offset(vm.Handle, caretBytes);
        };
    }

    /// <summary>True while an IME composition is in progress on our window.</summary>
    private bool ImeCompositionActive()
    {
        try
        {
            var hwnd = OurHwnd();
            if (hwnd == IntPtr.Zero) return false;
            var himc = ImmGetContext(hwnd);
            if (himc == IntPtr.Zero) return false;
            try
            {
                // GCS_COMPSTR: a non-empty composition string means in-progress.
                const int GCS_COMPSTR = 0x0008;
                return ImmGetCompositionStringW(himc, GCS_COMPSTR, IntPtr.Zero, 0) > 0;
            }
            finally { ImmReleaseContext(hwnd, himc); }
        }
        catch { return false; }
    }

    private bool _imeSeedPending;
    private Avalonia.Threading.DispatcherTimer? _imeRecheckTimer;

    private Avalonia.Threading.DispatcherTimer EnsureImeRecheckTimer()
    {
        if (_imeRecheckTimer != null) return _imeRecheckTimer;
        _imeRecheckTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _imeRecheckTimer.Tick += (_, _) =>
        {
            if (_imeSeedPending && !ImeCompositionActive())
            {
                _imeSeedPending = false;
                _imeRecheckTimer!.Stop();
                var box = MessageArea;
                if (_vm != null && _vm.Handle != IntPtr.Zero && box != null && !_suppressEditorSync)
                    SeedPaneText(_vm, box, box.Text ?? "");
            }
            else if (!_imeSeedPending)
            {
                _imeRecheckTimer!.Stop();
            }
        };
        return _imeRecheckTimer;
    }

    /// <summary>
    /// Seed the engine from the pane text — FULL text, no cap: the pane IS
    /// the document and the engine buffer mirrors it back through the VM
    /// binding, so a capped seed would truncate the user's document on the
    /// next engine-origin push (the buffer-as-document invariant). The RFC
    /// 0019 clause-7 cap applies to the TARGET-field path only, where the
    /// target's app owns the full text. IME commit note: the immediate seed
    /// here supersedes any pending deferred seed.
    /// </summary>
    private void SeedPaneText(MainWindowViewModel vm, TextBox box, string paneText)
    {
        _imeSeedPending = false; // this seed supersedes the deferred one
        _imeRecheckTimer?.Stop();
        var caretBytes = NativeBridge.dasher_byte_offset_from_utf16(paneText, box.CaretIndex);
        NativeBridge.dasher_seed_buffer(vm.Handle, paneText, caretBytes);
        KbLog($"Editor sync: seeded {paneText.Length} chars, caret u16={box.CaretIndex} → byte={caretBytes}");
    }

    /// <summary>
    /// RFC 0019 clause 5 — New = full reset (v5's SetBuffer(0)): buffer AND
    /// model context AND the rate window. dasher_reset_output_text alone
    /// would keep the learned position and resume mid-sentence.
    /// </summary>
    private void OnNewSession(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || _vm.Handle == IntPtr.Zero) return;
        // RFC 0001 2026-10-28: bound by the message-pane BtnNewSession
        // (status_bar per the schema's message-pane note) and the mini-bar
        // KbNewSession (ime_toolbar).
        Services.AnalyticsService.CaptureUiAction(
            _vm.IsKeyboardMode || !ReferenceEquals(sender, BtnNewSession) ? "ime_toolbar" : "status_bar",
            "new");
        NativeBridge.dasher_reset(_vm.Handle);
        KbLog("New session: engine reset (buffer + context)");
    }

    private static readonly string KbLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher", "keyboard_debug.log");

    private static void KbLog(string msg)
    {
        var line = $"[KB] {DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}";
        try { File.AppendAllText(KbLogPath, line); } catch { }
    }

    private void SetNoActivate(bool enable)
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            KbLog("SetNoActivate: no HWND available!");
            return;
        }

        var exStyle = GetWindowExStyle(handle);
        long newStyle;
        if (enable)
        {
            // Add both WS_EX_NOACTIVATE and WS_EX_LAYERED (layered is required
            // for Window.Opacity to work — Avalonia may not set it in time)
            newStyle = exStyle | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        }
        else
        {
            newStyle = exStyle & ~WS_EX_NOACTIVATE;
        }
        SetWindowExStyle(handle, newStyle);

        // Verify it stuck
        var verify = GetWindowExStyle(handle);
        var hasFlag = (verify & WS_EX_NOACTIVATE) != 0;
        var hasLayered = (verify & WS_EX_LAYERED) != 0;
        KbLog($"SetNoActivate({enable}): style=0x{exStyle:X} → 0x{newStyle:X}, noActivate={hasFlag}, layered={hasLayered}");
    }

    private void OnEngineOutput(object? sender, EngineOutputEventArgs e)
    {
        if (_vm == null || !_vm.IsKeyboardMode) return;
        // A picker dialog owns the foreground mid-session: no injection, and
        // (belt-and-braces, review loop 2) the tracker must not record the
        // dialog as the typing target from our own output-triggered focus
        // restoration.
        if (_pickerSessionActive) return;

        switch (e.EventType)
        {
            case EngineOutputEventArgs.EventInsert:
                KbLog($"EngineOutput insert: \"{e.Text}\"");
                SendTextToForeground(e.Text);
                break;
            case EngineOutputEventArgs.EventDelete:
                // One backspace per code point, never per byte or UTF-16
                // unit (RFC 0015 §5 — "é" and emoji delete exactly once).
                var count = TextUnits.CountCodePoints(e.Text);
                KbLog($"EngineOutput delete: \"{e.Text}\" ({count} code point(s))");
                SendBackspaces(count);
                break;
            case EngineOutputEventArgs.EventBufferCleared:
                // Wholesale buffer clear (reset, alphabet change): resync
                // without injecting — backspacing the whole buffer into the
                // target document would destroy the user's text.
                KbLog("EngineOutput buffer cleared (no injection)");
                break;
        }
    }

    // ── Direct-entry context awareness (RFC 0015) ───────────────────────────

    /// <summary>Last HWND the selection watch is armed on (avoids resubscribing per focus event).</summary>
    private IntPtr _lastWatchTarget;

    /// <summary>
    /// Arm the UIA selection-changed watch on the tracked target root. With
    /// sentence-window trimming this is now SAFE: during typing the sentence
    /// and the engine buffer grow together (shadow-compare matches, skip);
    /// on a genuine caret click the sentence changes (mismatch, re-seed with
    /// the new sentence — the re-anchor the user wants). The previous
    /// full-document compare was the Outlook reset cause; the sentence window
    /// is immune to the signature/formatting inconsistencies that broke it.
    /// A manual re-anchor button on the mini-bar provides a belt-and-braces
    /// override if the watch misses an edge case.
    /// </summary>
    private void ArmSelectionWatch()
    {
        var target = _targetTracker.Current;
        if (target == IntPtr.Zero || target == _lastWatchTarget) return;
        // Set _lastWatchTarget only on CONFIRMED success — a sticky field
        // on silent failure would permanently disable re-arming for this
        // window (review round 1 #3, round 2 #1: the assignment must not
        // precede the arm attempt).
        if (TargetContextReader.StartSelectionWatch(target,
                () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_vm is { IsKeyboardMode: true })
                        _ = SeedContextFromTargetAsync("caret moved");
                })))
        {
            _lastWatchTarget = target;
        }
    }

    /// <summary>
    /// Manual re-anchor (mini-bar button): force a fresh read + seed regardless
    /// of the watch — for when the user clicks somewhere the watch missed or
    /// just wants certainty about the current context.
    /// </summary>
    private void OnManualReanchor(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { IsKeyboardMode: true }) return;
        Services.AnalyticsService.CaptureUiAction("ime_toolbar", "reanchor");
        KbLog("Manual re-anchor requested");
        _ = SeedContextFromTargetAsync("manual re-anchor");
    }

    /// <summary>
    /// Read the target field's text + caret (UIA TextPattern, Win32 fallback)
    /// and seed the engine so predictions continue from text the user did not
    /// type through Dasher. Degrades per the RFC: read failed → seed empty
    /// (session-context reset, v5 parity); never blocks the UI thread
    /// (background read, 300 ms budget).
    /// </summary>
    private async Task SeedContextFromTargetAsync(string reason)
    {
        if (_vm == null || !_vm.IsKeyboardMode || _vm.Handle == IntPtr.Zero) return;
        if (_targetTracker.Current == IntPtr.Zero) return;

        // Debounce: rapid focus churn (mode entry -> target re-focus) must
        // not stack concurrent reads/seeds. 500ms (not 150ms): mid-word
        // context corrections (target cleared/switched while the user is
        // typing) must NOT fire during continuous output — the canvas reset
        // mid-word is far more disruptive than a 350ms-later correction.
        // During continuous typing the debounce keeps getting pushed forward
        // (each output increments the generation), so no re-seed fires at
        // all until the user pauses.
        _contextSeedGeneration++;
        var generation = _contextSeedGeneration;
        await Task.Delay(500); // let the target's caret settle AND the user finish their word
        if (generation != _contextSeedGeneration) return; // superseded

        // Paragraph-settle guard (Nov 2026 field evidence): our own VK_RETURN
        // makes Outlook rewrite the paragraph mark and relocate its caret over
        // the next ~second; reads in that window legitimately differ from the
        // engine buffer, and re-seeding them resets the canvas immediately
        // after the paragraph ("first paragraph goes crazy, the rest are
        // fine" - the ContextSeedPolicy mirror guard catches the text-level
        // cases; this covers the caret-position churn it cannot see).
        // Accepted trade-off: a GENUINE click elsewhere within the window is
        // dropped with no retry - context stays anchored at the paragraph
        // until the next UIA event. Matches the policy's stated asymmetry (a
        // false Skip delays re-sync; a false Seed resets the canvas), and a
        // sub-1.5s deliberate click right after our own Enter is rare.
        const long ParagraphSettleMs = 1500;
        if (reason == "caret moved" && _lastOwnParagraphInject != 0 &&
            Environment.TickCount64 - _lastOwnParagraphInject < ParagraphSettleMs)
        {
            KbLog($"Context seed ({reason}): paragraph settle window " +
                  $"({Environment.TickCount64 - _lastOwnParagraphInject}ms since our Enter) - skipping");
            return;
        }

        // Stale-seed guard: a queued focus event whose target was superseded
        // mid-debounce must not leak the OLD window's text — verified here at
        // seed time, right before the read ("stale focus bypasses foreground
        // guard"). When dropped, the new foreground's own event chain seeds.
        if (!_targetTracker.TargetIsForegroundOrUnknown(OurHwnd()))
        {
            KbLog($"Context seed ({reason}): stale — target no longer foreground, dropping");
            return;
        }

        var context = await TargetContextReader.ReadAsync(_targetTracker.Current);
        if (generation != _contextSeedGeneration) return;

        if (_vm.Handle == IntPtr.Zero) return;

        // CARET-GLITCH GUARD: Outlook's UIA returns caret position 0 or 1
        // during rapid text transitions (insert → delete → insert), even when
        // the user is typing at position 27+. The sentence window then reads
        // from the WRONG position (beginning of text instead of the typing
        // point) → bogus re-seed → canvas jumps. If the caret is near the
        // START of a long text AND this is a selection-watch event (the user
        // is mid-typing, not clicking), the read is glitched — skip.
        if (reason == "caret moved" &&
            context != null &&
            context.CaretUtf16 < 3 &&
            context.Text.Length > 10)
        {
            KbLog($"Context seed ({reason}): caret glitch (pos {context.CaretUtf16} in {context.Text.Length}-char text) — skipping");
            return;
        }

        if (context == null)
        {
            // Read failed (timeout, unsupported control, elevated target):
            // KEEP the existing engine context (RFC 0015 Tier 1 — session
            // context). The engine continues accumulating from what the user
            // has typed through Dasher. Seeding empty here was wiping the
            // buffer on every UIA timeout in Outlook (9231-char email body
            // exceeding the 300ms read budget), resetting predictions to
            // sentence-start (the "Hello space → uppercase" bug). The next
            // successful read will re-seed with the correct sentence.
            KbLog($"Context seed ({reason}): read failed → keeping existing context");
            return;
        }

        // Shadow-compare (#58): the engine buffer vs the target field, both
        // sentence-trimmed and normalised, decides whether a re-seed is
        // warranted at all. Every skip reason maps to a canvas-reset class
        // this chain has fixed — see ContextSeedPolicy for the rationale
        // (notably: the sentence-match branch must NOT call set_offset; the
        // engine is buffer-absolute, the trimmed sentence is not).
        var enginePtr = NativeBridge.dasher_get_output_text(_vm.Handle);
        var engineFullText = enginePtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(enginePtr) ?? "" : "";
        var decision = ContextSeedPolicy.Decide(engineFullText, context.Text, context.CaretUtf16);
        if (decision.Action == ContextSeedAction.Skip)
        {
            KbLog($"Context seed ({reason}): {decision.Reason}");
            return;
        }

        // UIA carets are UTF-16 units; convert to the engine's UTF-8 bytes.
        var byteOffset = NativeBridge.dasher_byte_offset_from_utf16(decision.Text, decision.CaretUtf16);

        // No sentence CONTENT in the log — lengths only (review P1 security:
        // "private target text is logged"; the sentence can contain up to
        // 200 chars of a private email or message).
        KbLog($"Context seed ({reason}): {decision.Text.Length} of {context.Text.Length} chars, caret u16={decision.CaretUtf16} → byte={byteOffset}");
        NativeBridge.dasher_seed_buffer(_vm.Handle, decision.Text, byteOffset);
    }


    // ── RFC 0021 select dispatch ─────────────────────────────────────────

    private void WireCustomActions()
    {
        if (_canvas == null) return;
        _canvas.CustomAction += OnEngineCustomAction;
    }

    private void OnEngineCustomAction(string name, System.Collections.Generic.Dictionary<string, string> attrs)
    {
        if (name != "select") return;
        if (_pickerSessionActive) return; // keystrokes must not reach a picker dialog

        var parsed = Services.SelectActions.Parse(
            attrs.TryGetValue("dist", out var d) ? d : null,
            attrs.TryGetValue("forward", out var f) ? f : null);
        if (parsed is not { } action)
        {
            KbLog($"select: unknown attributes dist={d ?? "(null)"} forward={f ?? "(null)"} - no-op");
            return;
        }
        var (dist, dir) = action;

        if (_vm is { IsKeyboardMode: true })
        {
            // Direct mode: native OS selection in the TARGET (the target's
            // own Copy/Cut/Delete then act on it without further work).
            var ks = Services.SelectActions.Keystroke(dist, dir);
            _targetTracker.EnsureForeground(OurHwnd());

            // RFC 0021 / #79 interaction: the caret events our selection
            // keystrokes provoke land in the existing caret-moved debounce;
            // a backward paragraph select changes the caret-anchored
            // sentence window and would re-seed mid-selection. Extend the
            // settle-window pattern to select chords. Timestamp BEFORE the
            // send (the established #79 ordering).
            _lastOwnParagraphInject = Environment.TickCount64;
            SendSelectChord(ks.Ctrl, ks.Shift, ks.Arrow, ks.Letter);
            // Field-test note (review loops 2-4): Shift-key injection is new
            // exposure for this codebase (existing chords inject Ctrl only) —
            // repeated Shift injection can trigger Windows Sticky/Filter Keys
            // prompts in some configurations; watch for it in tester feedback.
            Services.AnalyticsService.CaptureUiAction("control_tree", "select",
                $"dist={dist.ToString().ToLowerInvariant()},forward={(dir == Services.SelectDirection.Forward ? "yes" : "no")}");
        }
        else
        {
            // Editor mode: measured selection over the pane text with the
            // active-end state machine (SelectActions.EditorBounds).
            var box = MessageArea;
            if (box == null) return;
            var text = box.Text ?? "";
            var hadSelection = box.SelectionStart != box.SelectionEnd;
            var (start, end) = Services.SelectActions.EditorBounds(
                text, box.SelectionStart, box.SelectionEnd, dist, dir, hadSelection);
            // Suppress the editor-sync while setting the selection: a
            // selection change with identical text is not a user edit.
            box.SelectionStart = start;
            box.SelectionEnd = end;
            Services.AnalyticsService.CaptureUiAction("control_tree", "select",
                $"dist={dist.ToString().ToLowerInvariant()},forward={(dir == Services.SelectDirection.Forward ? "yes" : "no")}");
        }
    }

    /// <summary>Send a Shift(+Ctrl)+Arrow chord or Ctrl+<letter> to the
    /// target as ONE atomic SendInput (events within a single call are
    /// non-interleaved - the documented Windows contract; splitting them
    /// across calls lets concurrent input land between Shift-down and
    /// Arrow-down). Mirrors SendCtrlChord's atomicity. Releases in reverse
    /// order. The return count is logged - a silent UIPI/elevation block
    /// leaves a diagnostic in keyboard_debug.log.</summary>
    private void SendSelectChord(bool ctrl, bool shift, ushort? arrow, ushort? letter)
    {
        const ushort VK_SHIFT = 0x10;
        // presses in order, then releases in reverse
        var keys = new System.Collections.Generic.List<ushort>();
        if (shift) keys.Add(VK_SHIFT);
        if (ctrl) keys.Add(VK_CONTROL);
        if (arrow is { } a) keys.Add(a);
        if (letter is { } l) keys.Add(l);
        for (var i = keys.Count - 1; i >= 0; i--) keys.Add(keys[i]);

        var inputs = new INPUT[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            inputs[i].type = INPUT_KEYBOARD;
            inputs[i].u.ki.wVk = keys[i];
            if (i >= keys.Count / 2) inputs[i].u.ki.dwFlags = KEYEVENTF_KEYUP;
        }

        var sent = SendInput((uint)keys.Count, inputs, Marshal.SizeOf<INPUT>());
        KbLog($"  select chord: ctrl={ctrl} shift={shift} arrow={arrow?.ToString("X") ?? "-"} " +
              $"letter={letter?.ToString() ?? "-"} sent={sent}/{keys.Count}");
    }

    private int _contextSeedGeneration;

    /// <summary>TickCount64 of our last VK_RETURN injection (paragraph settle guard).</summary>
    private long _lastOwnParagraphInject;

    /// <summary>Dasher's own HWND (Zero before the window is shown).</summary>
    private IntPtr OurHwnd() => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    private void SendTextToForeground(string text)
    {
        _targetTracker.EnsureForeground(OurHwnd());

        foreach (char c in text)
        {
            // Newlines must go as VK_RETURN: target applications ignore a
            // Unicode 0x0A injected via KEYEVENTF_UNICODE (they expect the
            // Enter key). Dropping the insert while the engine still emits
            // a matching delete ("\n" = one backspace) made zooming back
            // across a paragraph eat the last character of the preceding
            // word — the build .24 "messes up the previous word" report.
            if (c == '\n' || c == '\r')
            {
                // Paragraph-settle guard timestamp (see SeedContextFromTargetAsync):
                // our Enter makes Outlook rewrite the paragraph mark and relocate
                // its caret for the next ~second.
                _lastOwnParagraphInject = Environment.TickCount64;
                SendVirtualKey(VK_RETURN);
            }
            else
                SendUnicodeChar(c);
        }

        var fgAfter = GetForegroundWindow();
        KbLog($"  → after SendInput, fg=0x{fgAfter:X}");
    }

    private void SendBackspaces(int count)
    {
        if (count <= 0) return;

        _targetTracker.EnsureForeground(OurHwnd());

        for (var i = 0; i < count; i++)
            SendVirtualKey(VK_BACK);

        var fgAfter = GetForegroundWindow();
        KbLog($"  → after SendInput ({count} backspace(s)), fg=0x{fgAfter:X}");
    }

    private void SendUnicodeChar(char c)
    {
        var inputs = new INPUT[2];

        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wScan = c;
        inputs[0].u.ki.dwFlags = KEYEVENTF_UNICODE;

        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wScan = c;
        inputs[1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;

        var cbSize = Marshal.SizeOf<INPUT>();
        var sent = SendInput(2, inputs, cbSize);
        KbLog($"  SendInput('{c}'): cbSize={cbSize} sent={sent} (expected 2)");
    }

    private void SendVirtualKey(ushort vk)
    {
        var inputs = new INPUT[2];

        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = vk;

        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = vk;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;

        var cbSize = Marshal.SizeOf<INPUT>();
        var sent = SendInput(2, inputs, cbSize);
        KbLog($"  SendInput(VK 0x{vk:X}): cbSize={cbSize} sent={sent} (expected 2)");
    }

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_KEY_C = 0x43;
    private const ushort VK_KEY_X = 0x58;
    private const ushort VK_KEY_V = 0x56;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;

    // ── Editing keys strip (Android parity — Heide's request) ───────────────
    //
    // ⌫ / ← / → on the mini-bar (direct mode, the strip an eye-gaze user
    // actually has) and the message-pane header (editor mode). Hold repeats:
    // deleting a word one click at a time is not usable for AAC input.
    //
    // Direct mode: real keystrokes to the TARGET (VK_BACK / VK_LEFT /
    // VK_RIGHT) — the target owns the caret. Editor mode: MessageArea caret
    // moves + backspace; the CaretIndex/TextChanged handlers already
    // re-anchor the engine (RFC 0019 clause 3/4).

    private void OnEditBackspaceKey(bool reportTelemetry = true)
    {
        // A picker dialog owns the foreground mid-session: injecting
        // keystrokes now would send them into the filename box and retarget
        // the tracker at the dialog (the bug class #73's review spent two
        // loops on).
        if (_pickerSessionActive) return;
        if (_vm is { IsKeyboardMode: true })
        {
            if (reportTelemetry) Services.AnalyticsService.CaptureUiAction("ime_toolbar", "backspace");
            SendBackspaces(1);
        }
        else
        {
            if (reportTelemetry) Services.AnalyticsService.CaptureUiAction("status_bar", "backspace");
            EditorBackspace();
        }
    }

    private void OnEditArrowLeftKey(bool reportTelemetry = true)
    {
        if (_pickerSessionActive) return;
        if (_vm is { IsKeyboardMode: true })
        {
            if (reportTelemetry) Services.AnalyticsService.CaptureUiAction("ime_toolbar", "arrow_left");
            _targetTracker.EnsureForeground(OurHwnd());
            SendVirtualKey(VK_LEFT);
        }
        else
        {
            if (reportTelemetry) Services.AnalyticsService.CaptureUiAction("status_bar", "arrow_left");
            EditorMoveCaret(-1);
        }
    }

    private void OnEditArrowRightKey(bool reportTelemetry = true)
    {
        if (_pickerSessionActive) return;
        if (_vm is { IsKeyboardMode: true })
        {
            if (reportTelemetry) Services.AnalyticsService.CaptureUiAction("ime_toolbar", "arrow_right");
            _targetTracker.EnsureForeground(OurHwnd());
            SendVirtualKey(VK_RIGHT);
        }
        else
        {
            if (reportTelemetry) Services.AnalyticsService.CaptureUiAction("status_bar", "arrow_right");
            EditorMoveCaret(1);
        }
    }

    /// <summary>Backspace in the editor pane: delete the code point before
    /// the caret as a USER edit (not suppressed → TextChanged seeds the
    /// engine, RFC 0019 clause 4). Surrogate-safe (EditorKeyActions).</summary>
    private void EditorBackspace()
    {
        var box = MessageArea;
        if (box == null) return;
        var (text, caret) = Services.EditorKeyActions.Backspace(box.Text ?? "", box.CaretIndex);
        if (text == box.Text) return;
        box.Text = text;
        box.CaretIndex = caret;
    }

    /// <summary>Caret move in the editor pane; the CaretIndexProperty handler
    /// re-anchors the model when the text is in sync (RFC 0019 clause 3).
    /// Surrogate-safe (EditorKeyActions).</summary>
    private void EditorMoveCaret(int delta)
    {
        var box = MessageArea;
        if (box == null) return;
        box.CaretIndex = Services.EditorKeyActions.MoveCaret(box.Text ?? "", box.CaretIndex, delta);
    }

    /// <summary>
    /// Inject a Ctrl+<key> chord to the target app (RFC 0015 clipboard
    /// bridge): these act on the TARGET's selection, which only the target
    /// can do — same reason v5 implemented them frontend-side.
    /// </summary>
    private void SendCtrlChord(ushort key, string name)
    {
        _targetTracker.EnsureForeground(OurHwnd());
        var inputs = new INPUT[4];

        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_CONTROL;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = key;
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].u.ki.wVk = key;
        inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].u.ki.wVk = VK_CONTROL;
        inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;

        var cbSize = Marshal.SizeOf<INPUT>();
        SendInput(4, inputs, cbSize);
        KbLog($"  Ctrl+{name} sent to target 0x{_targetTracker.Current:X}");
    }

    private void OnKbCopy(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || _vm.Handle == IntPtr.Zero) return;
        Services.AnalyticsService.CaptureUiAction("ime_toolbar", "copy");
        // Copy ALL engine text via the system clipboard (v5 toolbar Copy).
        var text = _vm.OutputText;
        if (!string.IsNullOrEmpty(text))
            SetClipboardText(text);
    }

    private void OnKbCut(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("ime_toolbar", "cut");
        SendCtrlChord(VK_KEY_X, "X");
    }

    private void OnKbPaste(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("ime_toolbar", "paste");
        SendCtrlChord(VK_KEY_V, "V");
    }

    private void UpdateWindowMinimum()
    {
        if (_vm == null) return;
        if (_vm.IsKeyboardMode)
        {
            // Half the normal minimum: enough for the canvas + mini-bar
            MinWidth = 300;
            MinHeight = 250;
        }
        else
        {
            MinWidth = 600;
            MinHeight = 500;
        }
    }

    private void OnToggleMode(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        // RFC 0001 2026-10-28: three affordances route here — the top-bar
        // BtnKeyboard (enter, main_toolbar; hidden in keyboard mode), the
        // message-pane eye button (enter, status_bar per the schema's
        // message-pane note), and the mini-bar exit (leave, ime_toolbar).
        var entering = !_vm.IsKeyboardMode;
        if (entering)
            Services.AnalyticsService.CaptureUiAction(
                ReferenceEquals(sender, PaneEyeModeBtn) ? "status_bar" : "main_toolbar",
                "direct_mode", "on");
        else
            Services.AnalyticsService.CaptureUiAction("ime_toolbar", "direct_mode", "off");
        SetPanePosition(entering ? PanePosition.Keyboard : PanePosition.Right);
    }

    private void OnModeRightSide(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("main_toolbar", "layout", "right");
        SetPanePosition(PanePosition.Right);
    }

    private void OnModeLeftSide(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("main_toolbar", "layout", "left");
        SetPanePosition(PanePosition.Left);
    }

    private void OnModeBottom(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("main_toolbar", "layout", "bottom");
        SetPanePosition(PanePosition.Bottom);
    }

    private void OnModeTop(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("main_toolbar", "layout", "top");
        SetPanePosition(PanePosition.Top);
    }

    private void OnModeKeyboard(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("main_toolbar", "direct_mode", "on");
        SetPanePosition(PanePosition.Keyboard);
    }

    private void SetPanePosition(PanePosition position)
    {
        if (_vm == null) return;
        // Capture the outgoing mode's bounds before the layout (and possibly
        // the window chrome) changes (issue #46).
        SaveWindowGeometry();
        _vm.PanePosition = position;
        _vm.IsKeyboardMode = position == PanePosition.Keyboard;
        UpdateWindowMinimum();
        ApplyPaneLayout();
        ApplyModeWindowBounds(position);
        new PaneSettings { PanePosition = position.ToString(), StatusBarHidden = _vm.IsStatusBarHidden }.Save();
    }

    /// <summary>
    /// On a runtime mode switch, apply the entering mode's saved bounds if it
    /// has any (first-ever switch keeps the current size). Startup restore
    /// already ran in the constructor and is skipped.
    /// </summary>
    private void ApplyModeWindowBounds(PanePosition position)
    {
        if (!_geometryRestored) return; // constructor restore handles startup

        if (position == PanePosition.Keyboard)
        {
            if (_windowSettings.HasDirectBounds &&
                IsGeometryUsableOnAnyScreen(_windowSettings.DirectX, _windowSettings.DirectY,
                    _windowSettings.DirectWidth, _windowSettings.DirectHeight))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                var p = new PixelPoint((int)_windowSettings.DirectX, (int)_windowSettings.DirectY);
                Position = p;
                var size = new Size(_windowSettings.DirectWidth, _windowSettings.DirectHeight);
                if (_windowSettings.DirectWidth >= MinWidth && _windowSettings.DirectHeight >= MinHeight)
                {
                    Width = _windowSettings.DirectWidth;
                    Height = _windowSettings.DirectHeight;
                }
                else
                {
                    size = Bounds.Size;
                }
                // The shared trackers must describe the mode we just applied —
                // not retain the previous mode's geometry (review).
                SeedTrackers(p, size);
            }
        }
        else
        {
            if (_windowSettings.HasNormalBounds &&
                IsGeometryUsableOnAnyScreen(_windowSettings.X, _windowSettings.Y,
                    _windowSettings.Width, _windowSettings.Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                var p = new PixelPoint((int)_windowSettings.X, (int)_windowSettings.Y);
                Position = p;
                var size = new Size(_windowSettings.Width, _windowSettings.Height);
                if (_windowSettings.Width >= MinWidth && _windowSettings.Height >= MinHeight)
                {
                    Width = _windowSettings.Width;
                    Height = _windowSettings.Height;
                }
                else
                {
                    size = Bounds.Size;
                }
                // Seed BEFORE the Maximized switch: these ARE the normal
                // restore bounds, so a later close-while-maximized saves
                // them instead of stale keyboard geometry (review).
                SeedTrackers(p, size);
            }
            else
            {
                // Saved normal bounds unusable (unplugged monitor, bezel) or
                // absent: the window keeps its live geometry, but the shared
                // trackers may still hold KEYBOARD values — and the
                // Maximized switch below follows. Empty them so
                // SaveWindowGeometry's close-while-maximized guard keeps the
                // saved normal bucket instead of persisting the keyboard
                // geometry as restore bounds (review).
                _lastNormalPosition = new PixelPoint(int.MinValue, int.MinValue);
                _lastNormalSize = null;
            }
            WindowState = _windowSettings.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
    }

    private void OnToggleStatusBar(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.IsStatusBarHidden = !_vm.IsStatusBarHidden;
        Services.AnalyticsService.CaptureUiAction("status_bar", "hide", _vm.IsStatusBarHidden ? "on" : "off");
        new PaneSettings { PanePosition = _vm.PanePosition.ToString(), StatusBarHidden = _vm.IsStatusBarHidden }.Save();
    }

    private void ApplyPaneLayout()
    {
        if (_vm == null) return;

        var position = _vm.PanePosition;
        var isKeyboard = position == PanePosition.Keyboard;

        var txtKeyboardLabel = this.FindControl<TextBlock>("TxtKeyboardLabel");

        // Remove old children and defs
        MainGrid.Children.Clear();
        MainGrid.ColumnDefinitions.Clear();
        MainGrid.RowDefinitions.Clear();

        if (isKeyboard)
        {
            // Keyboard mode: hide full toolbar + status bar, show mini floating bar
            TopBar.IsVisible = false;
            KeyboardMiniBar.IsVisible = true;
            StatusBarShowBtn.IsVisible = false;
            BottomBar.IsVisible = false;

            // Mini-bar WPM follows the typing-rate setting (RFC 0012)
            var miniBarWpm = this.FindControl<TextBlock>("MiniBarWpm");
            if (miniBarWpm != null)
                miniBarWpm.IsVisible = Controls.OutputTextSettings.Load().ShowTypingRate;

            // Canvas only, no message pane
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            MainGrid.Children.Add(DasherCanvas);
            Grid.SetColumn(DasherCanvas, 0);

            // Remember the window that currently has focus so we can send
            // text to it — rooted by the tracker (never a raw focus handle).
            _targetTracker.RecordForeground(GetForegroundWindow(), OurHwnd());

            Topmost = true;
            this.Opacity = _vm.KeyboardModeOpacity;
            TxtModeLabel.Text = Services.Loc.Tr("keyboard");
            BtnMode.Classes.Add("accent");
            if (txtKeyboardLabel != null) txtKeyboardLabel.Text = Services.Loc.Tr("exit");
            BtnKeyboard.Classes.Add("accent");

            // Prevent Dasher from stealing focus when clicked, so keystrokes
            // go to the target app (same technique as Windows On-Screen Keyboard).
            // Apply now and again after render — Avalonia may overwrite the
            // extended style when processing Topmost/Opacity changes.
            SetNoActivate(true);
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => SetNoActivate(true), Avalonia.Threading.DispatcherPriority.Render);

            // RFC 0015 context seeding: the tracker's WinEventHooks detect
            // when the user focuses their target application. The Deactivated
            // event does NOT fire reliably in keyboard mode (WS_EX_NOACTIVATE
            // suppresses it), but the hooks fire on every system-wide
            // foreground change — the same mechanism v5 used
            // (DasherWindow.cpp HandleWinEvent). Install on the UI thread so
            // callbacks arrive here.
            _targetTracker.Install(OurHwnd());
            ArmSelectionWatch();
        }
        else
        {
            TopBar.IsVisible = true;
            KeyboardMiniBar.IsVisible = false;
            StatusBarShowBtn.IsVisible = _vm.IsStatusBarHidden;
            BottomBar.IsVisible = !_vm.IsStatusBarHidden;

            Topmost = false;
            this.Opacity = 1.0;

            _targetTracker.Remove();
            TargetContextReader.StopSelectionWatch(); // review #6: don't leak the UIA subscription
            _lastWatchTarget = IntPtr.Zero;
            SetNoActivate(false);

            TxtModeLabel.Text = position switch
            {
                PanePosition.Right => Services.Loc.Tr("layout_right"),
                PanePosition.Left => Services.Loc.Tr("layout_left"),
                PanePosition.Bottom => Services.Loc.Tr("layout_bottom"),
                PanePosition.Top => Services.Loc.Tr("layout_top"),
                _ => Services.Loc.Tr("layout_right"),
            };
            ModeIcon.Kind = position switch
            {
                PanePosition.Left => LucideIconKind.PanelLeft,
                PanePosition.Bottom => LucideIconKind.PanelBottom,
                PanePosition.Top => LucideIconKind.PanelTop,
                _ => LucideIconKind.PanelRight,
            };
            BtnMode.Classes.Remove("accent");
            if (txtKeyboardLabel != null) txtKeyboardLabel.Text = Services.Loc.Tr("keyboard");
            BtnKeyboard.Classes.Remove("accent");

            bool horizontal = position == PanePosition.Right || position == PanePosition.Left;
            bool paneFirst = position == PanePosition.Left || position == PanePosition.Top;

            if (horizontal)
            {
                // Canvas gets ~78%, pane gets ~22% via star ratio
                MainGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(7, GridUnitType.Star)));
                MainGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5)));
                MainGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(2, GridUnitType.Star)));

                MessageSplitter.ResizeDirection = GridResizeDirection.Columns;
                MessageSplitter.Width = 5;
                MessageSplitter.Height = double.NaN;

                if (paneFirst)
                {
                    Grid.SetColumn(MessagePane, 0);
                    Grid.SetColumn(MessageSplitter, 1);
                    Grid.SetColumn(DasherCanvas, 2);
                }
                else
                {
                    Grid.SetColumn(DasherCanvas, 0);
                    Grid.SetColumn(MessageSplitter, 1);
                    Grid.SetColumn(MessagePane, 2);
                }
            }
            else
            {
                // Canvas gets ~75%, pane gets ~25% via star ratio
                MainGrid.RowDefinitions.Add(new RowDefinition(new GridLength(3, GridUnitType.Star)));
                MainGrid.RowDefinitions.Add(new RowDefinition(new GridLength(5)));
                MainGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));

                MessageSplitter.ResizeDirection = GridResizeDirection.Rows;
                MessageSplitter.Height = 5;
                MessageSplitter.Width = double.NaN;

                if (paneFirst) // Top
                {
                    Grid.SetRow(MessagePane, 0);
                    Grid.SetRow(MessageSplitter, 1);
                    Grid.SetRow(DasherCanvas, 2);
                }
                else // Bottom
                {
                    Grid.SetRow(DasherCanvas, 0);
                    Grid.SetRow(MessageSplitter, 1);
                    Grid.SetRow(MessagePane, 2);
                }
            }

            MainGrid.Children.Add(DasherCanvas);
            MainGrid.Children.Add(MessageSplitter);
            MainGrid.Children.Add(MessagePane);

            MessagePane.IsVisible = true;
            MessageSplitter.IsVisible = true;
        }
    }

    private void OnEngineMessage(object? sender, EngineMessageEventArgs e)
    {
        var title = e.IsWarning ? "Dasher Warning" : "Dasher";
        ToastNotifier.Show(title, e.Text, e.IsWarning);
    }

    private void OnWpmUpdated(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        _vm.UpdateTypingStats();

        var settings = Controls.OutputTextSettings.Load();
        if (!settings.ShowTypingRate) return;

        var label = this.FindControl<TextBlock>("WpmLabel");
        if (label != null)
        {
            label.Text = _vm.MaxWpm > 0
                ? $"{(int)_vm.CurrentWpm} wpm  max {(int)_vm.MaxWpm}  avg {(int)_vm.AvgWpm}"
                : $"{(int)_vm.CurrentWpm} wpm";
        }

        // Direct Mode: live WPM in the mini-bar (RFC 0012) — compact form,
        // the mini-bar has no room for max/avg.
        var miniBarWpm = this.FindControl<TextBlock>("MiniBarWpm");
        if (miniBarWpm != null && _vm.IsKeyboardMode)
            miniBarWpm.Text = $"{(int)_vm.CurrentWpm} wpm";

        // Game mode: show WPM in game target bar
        if (_gameModeActive)
        {
            var gameText = this.FindControl<TextBlock>("GameTargetText");
            if (gameText != null && !string.IsNullOrEmpty(gameText.Text))
            {
                SyncGameModeState();
            }
        }
    }

    private void OnTypingRateVisibilityChanged(bool visible)
    {
        var label = this.FindControl<TextBlock>("WpmLabel");
        if (label != null)
        {
            label.IsVisible = visible;
            if (!visible) label.Text = "";
        }

        // Direct Mode mini-bar WPM follows the same setting (RFC 0012).
        var miniBarWpm = this.FindControl<TextBlock>("MiniBarWpm");
        if (miniBarWpm != null)
        {
            miniBarWpm.IsVisible = visible && _vm?.IsKeyboardMode == true;
            if (!visible) miniBarWpm.Text = "";
        }
    }

    private void OnResetTypingStats()
    {
        _vm?.ResetTypingStats();
        var label = this.FindControl<TextBlock>("WpmLabel");
        if (label != null) label.Text = Services.Loc.Tr("wpm_idle");
    }

    private void OnEngineFault(object? sender, EventArgs e)
    {
        // RFC 0009 A2: engine caught a C++ exception internally and latched the error flag.
        // The ring buffer already captured the fault message via the log callback.
        // Write crash file so PostHog gets it on next launch (or immediate send if alive).
        try
        {
            AnalyticsService.WriteCrashFile(
                new Exception("Dasher engine fault — internal exception caught by C API boundary"),
                "EngineFaultDetected");
        }
        catch { }

        // Show dialog on UI thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var btn = new Button { Content = Services.Loc.Tr("ok"), Padding = new Thickness(24, 6), HorizontalAlignment = HorizontalAlignment.Right };
                var dialog = new Window
                {
                    Title = Services.Loc.Tr("dasher_engine_error"),
                    Width = 400,
                    Height = 180,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xF2)),
                    Content = new Border
                    {
                        Padding = new Thickness(24),
                        Child = new StackPanel
                        {
                            Spacing = 16,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = Services.Loc.Tr("engine_error"),
                                    FontSize = 16,
                                    FontWeight = FontWeight.Bold,
                                    Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
                                },
                                new TextBlock
                                {
                                    Text = Services.Loc.Tr("engine_error_message"),
                                    FontSize = 13,
                                    TextWrapping = TextWrapping.Wrap,
                                },
                                btn,
                            },
                        },
                    },
                };
                btn.Click += (_, _) => dialog.Close();
                dialog.ShowDialog(this);
            }
            catch { }
        });
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
    }

    private void OnTogglePrefs(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var dock = this.FindControl<Border>("SettingsDock");
        if (dock == null) return;

        var wasVisible = dock.IsVisible;
        // RFC 0001 2026-10-28: count OPENINGS, not toggles (the dock close
        // button routes here too). In keyboard (direct) mode the only entry
        // point is the mini-bar (ime_toolbar); otherwise the top bar.
        if (!wasVisible)
            Services.AnalyticsService.CaptureUiAction(
                _vm.IsKeyboardMode ? "ime_toolbar" : "main_toolbar",
                "settings");
        dock.IsVisible = !wasVisible;

        if (sender is Button btn)
        {
            if (!wasVisible) btn.Classes.Add("accent");
            else btn.Classes.Remove("accent");
        }

        if (!wasVisible && !_settingsInitialized)
        {
            InitializeSettingsPanel();
            _settingsInitialized = true;
        }

        // In keyboard mode, hide mini-bar while settings are open
        if (_vm.IsKeyboardMode)
            KeyboardMiniBar.IsVisible = wasVisible; // show when closing settings, hide when opening

        // Pause/resume canvas timer when settings are open
        if (_canvas != null)
        {
            if (!wasVisible)
                _canvas.PauseTimer();
            else
                _canvas.ResumeTimer();
        }
    }

    private static readonly Dictionary<string, LucideIconKind> SettingsTabIcons = new()
    {
        { "Input", LucideIconKind.MousePointerClick },
        { "Language", LucideIconKind.Languages },
        { "Customization", LucideIconKind.Palette },
        { "Speed", LucideIconKind.Gauge },
        { "Output", LucideIconKind.Type },
        { "Speech", LucideIconKind.Volume2 },
        { "Appearance", LucideIconKind.Eye },
        { "Advanced", LucideIconKind.Wrench },
        { "Other", LucideIconKind.Ellipsis },
        { "Privacy", LucideIconKind.ShieldCheck },
    };

    private void InitializeSettingsPanel()
    {
        var panel = this.FindControl<SettingsPanel>("DockedSettingsPanel");
        if (panel == null || _vm == null) return;

        panel.Initialize(_vm.Handle);
        panel.OutputFontChanged += OnOutputFontChanged;
        panel.KeyboardOpacityChanged += OnKeyboardOpacityChanged;
        panel.InputSourceChanged += OnInputSourceChanged;
        panel.JoystickRequested += OnJoystickRequested;
        panel.ResetSettingsRequested += OnResetSettingsRequested;
        panel.TypingRateVisibilityChanged += OnTypingRateVisibilityChanged;
        panel.ResetTypingStatsRequested += OnResetTypingStats;

        BuildSettingsTabs(panel);
    }

    private async void OnResetSettingsRequested()
    {
        if (_canvas == null || _vm == null) return;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDir = Path.Combine(appData, "Dasher");

        // RFC 0018: engine restart is startup-blocking work — show the
        // loading overlay while it runs off the UI thread.
        StartupOverlayText.Text = Loc.Tr("preparing_dasher", "Preparing Dasher…");
        StartupOverlayText.IsVisible = true;
        StartupOverlayError.IsVisible = false;
        StartupOverlay.IsVisible = true;

        // Destroy engine (this saves current settings to file)
        _canvas.Shutdown();

        // Delete the saved settings so defaults load
        var settingsFile = Path.Combine(dataDir, "dasher_settings.xml");
        try { if (File.Exists(settingsFile)) File.Delete(settingsFile); } catch { }

        // Recreate engine with defaults, off the UI thread. The canvas's C#
        // events (EngineMessage, EngineFaultDetected, WpmUpdated,
        // EngineOutput, FirstEngineFrame) stay wired from OnOpened — the
        // canvas object survives Shutdown/Initialize, so re-subscribing here
        // would run each handler once per reset (duplicate toasts, and double
        // keyboard-mode injection). Native callbacks are re-registered on the
        // first tick after Initialize (per-handle state is reset there).
        await Task.Run(() => _canvas.Initialize(dataDir, dataDir));
        _vm.SetHandle(_canvas.GetHandle());

        // Re-wire callbacks
        if (_speakCallback != null)
            NativeBridge.dasher_set_speak_callback(_vm.Handle, _speakCallback, IntPtr.Zero);
        if (_parameterCallback != null)
            NativeBridge.dasher_set_parameter_callback(_vm.Handle, _parameterCallback, IntPtr.Zero);

        var accessConfig = AccessConfiguration.Load();
        accessConfig.Apply(_vm.Handle);

        // Start engine (realize off-thread, then frame loop)
        await _canvas.RealizeOffUiThreadAsync();
        _canvas.BeginFrameLoop();

        // Refresh UI state — read from engine defaults
        _vm.LoadSpeedBounds();
        _vm.LoadSpeedFromEngine();
        // Programmatic writes: suppress the view-side toggle telemetry
        // (RFC: programmatic changes must not emit setting_changed).
        _suppressStatusToggleTelemetry = true;
        try
        {
            _vm.AutoSpeed = NativeBridge.dasher_get_bool_parameter(_vm.Handle, ParameterKeys.BP_AUTO_SPEEDCONTROL) != 0;
            _vm.Learning = NativeBridge.dasher_get_bool_parameter(_vm.Handle, ParameterKeys.BP_LM_ADAPTIVE) != 0;
        }
        finally { _suppressStatusToggleTelemetry = false; }
        _controlModeActive = NativeBridge.dasher_get_bool_parameter(_vm.Handle, ParameterKeys.BP_CONTROL_MODE) != 0;
        UpdateControlModeLabel();
        _vm.LoadAlphabets();
        var currentAlphaPtr = NativeBridge.dasher_get_alphabet_id(_vm.Handle);
        var currentAlpha = currentAlphaPtr != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(currentAlphaPtr) ?? "" : "";
        _vm.SelectedLanguageIndex = Math.Max(0, _vm.Languages.IndexOf(currentAlpha));

        // Re-initialize settings panel with new handle
        _settingsInitialized = false;
        var panel = this.FindControl<SettingsPanel>("DockedSettingsPanel");
        if (panel != null)
        {
            panel.Initialize(_vm.Handle);
            panel.ShowCategory("Privacy");
        }

        // Clear output
        _vm.OutputText = "";
    }

    private void BuildSettingsTabs(SettingsPanel settingsPanel)
    {
        var container = this.FindControl<StackPanel>("SettingsTabContainer");
        if (container == null) return;

        container.Children.Clear();
        _settingsTabs = [];

        foreach (var category in settingsPanel.GetCategoryNames())
        {
            var btn = new Button
            {
                Classes = { "settings-tab" },
                Tag = category,
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };
            var iconKind = SettingsTabIcons.GetValueOrDefault(category, LucideIconKind.Circle);
            stack.Children.Add(new LucideIcon { Kind = iconKind, Size = 16 });
            stack.Children.Add(new TextBlock { Text = category });

            btn.Content = stack;
            btn.Click += OnSettingsTabClick;
            container.Children.Add(btn);
            _settingsTabs = [.. _settingsTabs, btn];
        }

        ActivateSettingsTab(0);
        settingsPanel.ShowCategory(settingsPanel.GetCategoryNames().FirstOrDefault() ?? "");
    }

    private void OnSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var category = btn.Tag as string ?? "";
        var idx = Array.FindIndex(_settingsTabs!, t => t.Tag as string == category);
        if (idx < 0) return;
        ActivateSettingsTab(idx);
        var panel = this.FindControl<SettingsPanel>("DockedSettingsPanel");
        panel?.ShowCategory(category);
        AnalyticsService.Capture("settings_viewed", new() { ["tab_name"] = category });
    }

    private void ActivateSettingsTab(int index)
    {
        if (_settingsTabs == null) return;
        var onAccent = Application.Current?.FindResource("OnAccent") as IBrush;
        var textSecondary = Application.Current?.FindResource("TextSecondary") as IBrush;

        for (int i = 0; i < _settingsTabs.Length; i++)
        {
            var isActive = i == index;
            if (isActive)
            {
                _settingsTabs[i].Classes.Add("active");
                _settingsTabs[i].Foreground = onAccent ?? Brushes.Black;
            }
            else
            {
                _settingsTabs[i].Classes.Remove("active");
                _settingsTabs[i].Foreground = textSecondary ?? Brushes.Gray;
            }
        }
    }

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("main_toolbar", "new");
        if (_canvas == null || _vm == null) return;
        NativeBridge.dasher_reset(_vm.Handle);
        _vm.OutputText = "";
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("main_toolbar", "open");
        if (_vm == null) return;
        // Whole-handler guard (async void): a picker exception must not
        // take down the process (see the training export/import handlers).
        try
        {
            var storage = StorageProvider;
            System.Collections.Generic.IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> result;
            using (Services.PickerSession.Begin(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero))
            {
                result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Services.Loc.Tr("open_text_file"),
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Text files") { Patterns = ["*.txt"] },
                        new FilePickerFileType("All files") { Patterns = ["*"] },
                    ]
                });
            }

            if (result.Count == 0) return;

            await using var stream = await result[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            NativeBridge.dasher_reset_output_text(_vm.Handle);
            _vm.OutputText = text;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                Services.AnalyticsService.WriteCrashFile(ex, "open file picker");
            Services.ToastNotifier.Show("Open failed", ex.Message, isWarning: true);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("main_toolbar", "save");
        if (_vm == null) return;
        // Whole-handler guard (async void): a picker exception must not
        // take down the process (see the training export/import handlers).
        try
        {
            Avalonia.Platform.Storage.IStorageFile? result;
            using (Services.PickerSession.Begin(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero))
            {
                result = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Services.Loc.Tr("save_text_file"),
                    SuggestedFileName = "dasher_output.txt",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("Text files") { Patterns = ["*.txt"] },
                    ]
                });
            }

            if (result == null) return;

            await using var stream = await result.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_vm.OutputText);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                Services.AnalyticsService.WriteCrashFile(ex, "save file picker");
            Services.ToastNotifier.Show("Save failed", ex.Message, isWarning: true);
        }
    }



    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _vm.Handle == IntPtr.Zero || _vm.SelectedLanguageIndex < 0) return;
        if (_vm.SelectedLanguageIndex < _vm.Languages.Count)
        {
            var alphabet = _vm.Languages[_vm.SelectedLanguageIndex];
            // Programmatic guard (RFC 0001 2026-10-28: programmatic changes
            // must not emit): startup/reset assign SelectedLanguageIndex for
            // the engine's CURRENT alphabet — only emit when the pick is an
            // actual change. This also fixes the pre-existing startup
            // alphabet_selected false-fire for non-default alphabets.
            var currentPtr = NativeBridge.dasher_get_alphabet_id(_vm.Handle);
            var current = currentPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(currentPtr) ?? "" : "";
            if (alphabet == current) return;
            NativeBridge.dasher_set_alphabet_id(_vm.Handle, alphabet);
            AnalyticsService.Capture("alphabet_selected", new() { ["alphabet_id"] = alphabet });
            // RFC 0001 2026-10-28 amendment: also a settings change made
            // from the main UI (the picker sits in the status bar; surface
            // stays main_ui = anywhere outside the settings windows).
            AnalyticsService.CaptureSettingChanged("Alphabet", "enum", alphabet, "main_ui");
        }
    }

    private void OnSpeedDown(object? sender, RoutedEventArgs e)
    {
        // The speed stepper sits in the bottom status bar.
        Services.AnalyticsService.CaptureUiAction("status_bar", "speed_down");
        if (_vm == null) return;
        var before = _vm.Handle == IntPtr.Zero ? 0 : NativeBridge.dasher_get_long_parameter(_vm.Handle, ParameterKeys.LP_MAX_BITRATE);
        _vm.DecreaseSpeed();
        EmitSpeedIfChanged(before);
    }

    private void OnSpeedUp(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("status_bar", "speed_up");
        if (_vm == null) return;
        var before = _vm.Handle == IntPtr.Zero ? 0 : NativeBridge.dasher_get_long_parameter(_vm.Handle, ParameterKeys.LP_MAX_BITRATE);
        _vm.IncreaseSpeed();
        EmitSpeedIfChanged(before);
    }

    /// <summary>setting_changed only when the value actually moved (the
    /// steppers clamp at their bounds — a no-op click is not a change).</summary>
    private void EmitSpeedIfChanged(long before)
    {
        if (_vm == null || _vm.Handle == IntPtr.Zero || before == 0) return;
        var after = NativeBridge.dasher_get_long_parameter(_vm.Handle, ParameterKeys.LP_MAX_BITRATE);
        if (after != before)
            Services.AnalyticsService.CaptureSettingChanged("Max Speed (bits/sec)", "number", after, "main_ui");
    }

    private async void OnInputSourceChanged(object? sender, (EyeGazeIntegration.TrackerType trackerType, int udpPort) args)
    {
        if (_canvas == null) return;
        _canvas.DisableJoystick();

        if (args.trackerType == EyeGazeIntegration.TrackerType.None)
        {
            _canvas.DisableEyeGaze();
        }
        else
        {
            var ok = await _canvas.InitializeEyeGazeAsync(args.trackerType, args.udpPort);
            if (!ok)
            {
                var trackerName = args.trackerType switch
                {
                    EyeGazeIntegration.TrackerType.TobiiStreamEngine => "Tobii Stream Engine",
                    EyeGazeIntegration.TrackerType.Eyetuitive => "eyetuitive",
                    EyeGazeIntegration.TrackerType.UdpGazeTracker => "UDP Gaze Tracker",
                    _ => "Eye tracker",
                };
                ToastNotifier.Show("Eye Tracker Error",
                    $"Could not connect to {trackerName}. " +
                    (args.trackerType == EyeGazeIntegration.TrackerType.TobiiStreamEngine
                        ? "Make sure tobii_stream_engine.dll is installed and a Tobii device is connected."
                        : "Check the device is connected and drivers are installed."),
                    isWarning: true);
            }
            var methodName = args.trackerType switch
            {
                EyeGazeIntegration.TrackerType.TobiiStreamEngine => "eye_gaze_tobii",
                EyeGazeIntegration.TrackerType.Eyetuitive => "eye_gaze_eyetuitive",
                EyeGazeIntegration.TrackerType.UdpGazeTracker => "eye_gaze_udp",
                _ => "eye_gaze_native",
            };
            AnalyticsService.Capture("input_method_changed", new() { ["method"] = methodName });
        }
    }

    private void OnJoystickRequested(object? sender, EventArgs e)
    {
        if (_canvas == null) return;
        _canvas.DisableEyeGaze();
        _canvas.InitializeJoystick();
        AnalyticsService.Capture("input_method_changed", new() { ["method"] = "joystick" });
    }

    private async void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        Services.AnalyticsService.CaptureUiAction("status_bar", "copy");
        SetClipboardText(_vm.OutputText);
    }

    private async void OnCopySelection(object? sender, RoutedEventArgs e)
    {
        var messageArea = this.FindControl<TextBox>("MessageArea");
        if (messageArea?.SelectedText == null) return;
        Services.AnalyticsService.CaptureUiAction("status_bar", "copy_selection");
        SetClipboardText(messageArea.SelectedText);
    }

    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        Services.AnalyticsService.CaptureUiAction("status_bar", "paste");
        var text = GetClipboardText();
        if (text != null)
            _vm.OutputText += text;
    }

    private void OnQuickSpeak(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || string.IsNullOrWhiteSpace(_vm.OutputText)) return;
        Services.AnalyticsService.CaptureUiAction("status_bar", "speak");
        _ = SpeechService.Instance.SpeakAsync(_vm.OutputText);
    }

    private void OnStopSpeak(object? sender, RoutedEventArgs e)
    {
        Services.AnalyticsService.CaptureUiAction("status_bar", "stop_speak");
        SpeechService.Instance.Stop();
    }

    private void OnSpeakVolumeChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        SpeechService.Instance.SpeechVolume = (int)Math.Round(e.NewValue);
        SpeechService.Instance.SaveSettings();
    }

    /// <summary>Commit-time telemetry for the status-bar speech volume (the
    /// ValueChanged storm is a drag, not N commits — RFC: setting_changed is
    /// for committed changes).</summary>
    private void OnSpeakVolumeCommitted()
    {
        Services.AnalyticsService.CaptureSettingChanged("speech_volume", "number",
            SpeechService.Instance.SpeechVolume, "main_ui");
    }

    private bool _statusTogglesInstrumented;
    private bool _suppressStatusToggleTelemetry;

    /// <summary>
    /// RFC 0001 2026-10-28: attach setting_changed to the status-bar
    /// Auto/Learning toggles at the VIEW, after startup values are loaded —
    /// the VM properties are written programmatically at startup and by
    /// Settings > Reset, and programmatic changes must not emit. Reset
    /// suppresses via _suppressStatusToggleTelemetry.
    /// </summary>
    private void InstrumentStatusToggles()
    {
        if (_statusTogglesInstrumented) return;
        _statusTogglesInstrumented = true;
        var auto = this.FindControl<Avalonia.Controls.ToggleSwitch>("AutoSpeedToggle");
        if (auto != null)
            auto.IsCheckedChanged += (_, _) =>
            {
                if (!_suppressStatusToggleTelemetry)
                    Services.AnalyticsService.CaptureSettingChanged("Automatic Speed Control", "bool", auto.IsChecked == true, "main_ui");
            };
        var learning = this.FindControl<Avalonia.Controls.ToggleSwitch>("LearningToggle");
        if (learning != null)
            learning.IsCheckedChanged += (_, _) =>
            {
                if (!_suppressStatusToggleTelemetry)
                    Services.AnalyticsService.CaptureSettingChanged("Adaptive Language Model", "bool", learning.IsChecked == true, "main_ui");
            };
    }

    private void OnEngineSpeak(IntPtr textPtr, int interrupt, IntPtr user_data)
    {
        if (textPtr == IntPtr.Zero) return;
        var text = Marshal.PtrToStringUTF8(textPtr);
        if (string.IsNullOrEmpty(text)) return;
        _ = SpeechService.Instance.SpeakAsync(text, interrupt != 0);
    }

    private void OnParameterChanged(int parameterKey, IntPtr userData)
    {
        if (parameterKey == ParameterKeys.LP_MAX_BITRATE)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_vm == null || _vm.Handle == IntPtr.Zero) return;
                _vm.LoadSpeedFromEngine();
            });
        }
        else if (parameterKey == ParameterKeys.BP_CONTROL_MODE)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_vm == null || _vm.Handle == IntPtr.Zero) return;
                _controlModeActive = NativeBridge.dasher_get_bool_parameter(_vm.Handle, ParameterKeys.BP_CONTROL_MODE) != 0;
                UpdateControlModeLabel();
            });
        }
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static void SetClipboardText(string text)
    {
        OpenClipboard(IntPtr.Zero);
        EmptyClipboard();
        var bytes = (System.Text.Encoding.Unicode.GetByteCount(text) + 2);
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
        var ptr = GlobalLock(hMem);
        Marshal.Copy(System.Text.Encoding.Unicode.GetBytes(text + "\0"), 0, ptr, text.Length * 2 + 2);
        GlobalUnlock(hMem);
        SetClipboardData(CF_UNICODETEXT, hMem);
        CloseClipboard();
    }

    private static string? GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var hMem = GetClipboardData(CF_UNICODETEXT);
            if (hMem == IntPtr.Zero) return null;
            var size = (int)GlobalSize(hMem);
            var ptr = GlobalLock(hMem);
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            GlobalUnlock(hMem);
            return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally { CloseClipboard(); }
    }

    private void OnOutputFontChanged(string fontFamily, int fontSize)
    {
        var messageArea = this.FindControl<TextBox>("MessageArea");
        if (messageArea != null)
        {
            messageArea.FontFamily = new FontFamily(fontFamily);
            messageArea.FontSize = fontSize;
        }
    }

    private void ApplyOutputFontSettings()
    {
        var settings = OutputTextSettings.Load();
        OnOutputFontChanged(settings.FontFamily, settings.FontSize);
        _vm!.KeyboardModeOpacity = settings.KeyboardOpacity;
    }

    private void OnKeyboardOpacityChanged(double opacity)
    {
        if (_vm != null)
        {
            _vm.KeyboardModeOpacity = opacity;
            if (_vm.IsKeyboardMode)
                this.Opacity = opacity;
        }
    }

    private bool _gameModeActive;

    private bool _controlModeActive;

    private void OnToggleControlMode(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;

        _controlModeActive = !_controlModeActive;
        // RFC 0001 2026-10-28: the main-toolbar Control button and the
        // keyboard-mode mini-bar's KbControlBtn share this handler.
        Services.AnalyticsService.CaptureUiAction(
            ReferenceEquals(sender, KbControlBtn) ? "ime_toolbar" : "main_toolbar",
            "control_mode", _controlModeActive ? "on" : "off");
        NativeBridge.dasher_set_bool_parameter(_vm.Handle, ParameterKeys.BP_CONTROL_MODE, _controlModeActive ? 1 : 0);
        UpdateControlModeLabel();
    }

    private void UpdateControlModeLabel()
    {
        var label = this.FindControl<TextBlock>("TxtControlLabel");
        if (label != null)
            label.Text = _controlModeActive ? Services.Loc.Tr("leave") : Services.Loc.Tr("control");

        // Sync mini-bar control button accent state
        if (_controlModeActive)
            KbControlBtn.Classes.Add("accent");
        else
            KbControlBtn.Classes.Remove("accent");
    }

    private void OnToggleGameMode(object? sender, RoutedEventArgs e)
    {
        if (_canvas == null || _vm == null) return;

        if (_gameModeActive)
        {
            Services.AnalyticsService.CaptureUiAction("main_toolbar", "game_mode", "off");
            NativeBridge.dasher_leave_game_mode(_vm.Handle);
            _gameModeActive = false;
            NativeBridge.dasher_game_set_canvas_text(_vm.Handle, 1);
            GameIcon.Kind = LucideIconKind.Gamepad2;
            TxtGameLabel.Text = Services.Loc.Tr("play");
            var gameBar = this.FindControl<Border>("GameTargetBar");
            if (gameBar != null) gameBar.IsVisible = false;
        }
        else
        {
            var result = NativeBridge.dasher_enter_game_mode(_vm.Handle);
            if (result == 0)
            {
                Services.AnalyticsService.CaptureUiAction("main_toolbar", "game_mode", "on");
                _gameModeActive = true;
                NativeBridge.dasher_game_set_canvas_text(_vm.Handle, 0);
                GameIcon.Kind = LucideIconKind.Pause;
                TxtGameLabel.Text = Services.Loc.Tr("leave");
                var gameBar = this.FindControl<Border>("GameTargetBar");
                if (gameBar != null) gameBar.IsVisible = true;
            }
        }
    }

    private void SyncGameModeState()
    {
        if (!_gameModeActive || _vm == null) return;

        var targetPtr = NativeBridge.dasher_game_get_target_text(_vm.Handle);
        var target = targetPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(targetPtr) ?? "" : "";
        var correct = NativeBridge.dasher_game_get_correct_count(_vm.Handle);
        var wrongPtr = NativeBridge.dasher_game_get_wrong_text(_vm.Handle);
        var wrong = wrongPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(wrongPtr) ?? "" : "";
        var total = NativeBridge.dasher_game_get_target_length(_vm.Handle);

        if (total < 0 || string.IsNullOrEmpty(target)) return;

        var gameTarget = this.FindControl<TextBlock>("GameTargetText");
        if (gameTarget == null) return;

        var correctText = correct > 0 ? target[..Math.Min(correct, target.Length)] : "";
        var remaining = correct < target.Length ? target[Math.Min(correct, target.Length)..] : "";

        gameTarget.Text = $"{correctText}[{wrong}]{remaining}\n{(int)_vm.CurrentWpm} wpm" + 
            (_vm.MaxWpm > 0 ? $"  max {(int)_vm.MaxWpm}" : "");
    }

    private static string FindCoreDataDir()
    {
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "DasherCore", "Data")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "DasherCore", "Data")),
            Path.Combine(AppContext.BaseDirectory, "Data"),
        ];

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "alphabets")))
                return candidate;
        }

        return AppContext.BaseDirectory;
    }

    private static void CopyDataIfNeeded(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir)) return;

        Directory.CreateDirectory(targetDir);

        // Copy files in this directory
        foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, targetFile, true);
        }

        // Recursively copy subdirectories
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            var dirName = Path.GetFileName(dir);
            CopyDataIfNeeded(dir, Path.Combine(targetDir, dirName));
        }
    }

#if !STORE
    // RFC 0017: passive update check for self-managed builds (MSI/portable).
    // At most weekly, opt-out in Settings > Privacy, never a modal — a toast
    // that opens the release page when tapped. Skip-version: the toast for a
    // given version is shown once per check window, not nagged per launch.
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var settings = UpdateCheckSettings.Load();
            if (!settings.ShouldCheck) return;

            var info = await UpdateChecker.CheckAsync();
            settings.RecordCheck();
            if (!info.IsUpdateAvailable) return;

            // Re-read state: the user may have opted out (Settings > Privacy)
            // or skipped this version while the request was in flight. The
            // check that already ran is recorded, but no toast may follow an
            // opt-out or a skip.
            var persisted = UpdateCheckSettings.Load();
            if (!persisted.Enabled) return;
            var skipped = persisted.SkippedVersion;
            if (!string.IsNullOrEmpty(skipped) &&
                string.Equals(skipped.TrimStart('v'), info.LatestTag?.TrimStart('v'), StringComparison.OrdinalIgnoreCase))
                return;

            var url = info.ReleaseUrl;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ToastNotifier.Show(
                    "Dasher update available",
                    $"Dasher {info.LatestTag} is available (you're running v{info.CurrentVersion}). Tap to download — disable in Settings > Privacy.",
                    url));
        }
        catch { }
    }
#endif
}
