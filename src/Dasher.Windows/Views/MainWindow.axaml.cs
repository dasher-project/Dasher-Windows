using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Dasher.Windows.Controls;
using Dasher.Windows.Engine;
using Dasher.Windows.Services;
using Dasher.Windows.Speech;
using Dasher.Windows.ViewModels;
using Lucide.Avalonia;

namespace Dasher.Windows.Views;

public partial class MainWindow : Window
{
    private DasherCanvas? _canvas;
    private MainWindowViewModel? _vm;
    private string _previousOutput = "";
    private Button[]? _settingsTabs;
    private bool _settingsInitialized;
    private NativeBridge.SpeakCallback? _speakCallback;
    private NativeBridge.ParameterCallback? _parameterCallback;
    private int _bitrateKey;
    private IntPtr _lastTargetWindow = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static IntPtr GetWindowExStyle(IntPtr hWnd)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE) : GetWindowLong32(hWnd, GWL_EXSTYLE);
    }

    private static IntPtr SetWindowExStyle(IntPtr hWnd, IntPtr value)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, GWL_EXSTYLE, value) : SetWindowLong32(hWnd, GWL_EXSTYLE, value);
    }

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

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        _canvas = this.FindControl<DasherCanvas>("DasherCanvas");
        _vm = DataContext as MainWindowViewModel;
        if (_canvas == null || _vm == null) return;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDir = Path.Combine(appData, "Dasher");
        Directory.CreateDirectory(dataDir);

        var coreDataDir = FindCoreDataDir();
        CopyDataIfNeeded(coreDataDir, dataDir);

        _canvas.Initialize(dataDir, dataDir);
        _canvas.EngineMessage += OnEngineMessage;
        _vm.SetHandle(_canvas.GetHandle());

        this.Deactivated += (_, _) =>
        {
            var fg = GetForegroundWindow();
            var ourHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (fg != ourHandle && fg != IntPtr.Zero)
            {
                _lastTargetWindow = fg;
                KbLog($"Deactivated: target window = 0x{fg:X}");
            }
        };

        _speakCallback = new NativeBridge.SpeakCallback(OnEngineSpeak);
        NativeBridge.dasher_set_speak_callback(_vm.Handle, _speakCallback, IntPtr.Zero);

        _bitrateKey = NativeBridge.dasher_find_parameter_key("LP_MAX_BITRATE");
        _parameterCallback = new NativeBridge.ParameterCallback(OnParameterChanged);
        NativeBridge.dasher_set_parameter_callback(_vm.Handle, _parameterCallback, IntPtr.Zero);

        var accessConfig = AccessConfiguration.Load();
        accessConfig.Apply(_vm.Handle);

        _vm.ApplySpeed();
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
        ApplyPaneLayout();

        _vm.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.OutputText))
                OnOutputTextChanged();
        };

#if !STORE
        _ = CheckForUpdatesAsync();
#endif
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
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

        if (_vm.IsKeyboardMode)
        {
            var current = _vm.OutputText;
            if (current.Length > _previousOutput.Length)
            {
                var newChars = current[_previousOutput.Length..];
                KbLog($"OnOutputTextChanged: new chars=\"{newChars}\" (total len={current.Length})");
                SendTextToForeground(newChars);
            }
            _previousOutput = current;
        }
        else
        {
            _previousOutput = _vm.OutputText;
        }

        if (_gameModeActive)
            SyncGameModeState();
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
        var newStyle = enable
            ? (IntPtr)(exStyle.ToInt64() | WS_EX_NOACTIVATE)
            : (IntPtr)(exStyle.ToInt64() & ~WS_EX_NOACTIVATE);
        SetWindowExStyle(handle, newStyle);

        // Verify it stuck
        var verify = GetWindowExStyle(handle);
        var hasFlag = (verify.ToInt64() & WS_EX_NOACTIVATE) != 0;
        KbLog($"SetNoActivate({enable}): style=0x{exStyle:X} → 0x{newStyle:X}, verified={hasFlag}");
    }

    private void SendTextToForeground(string text)
    {
        var ourHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var fg = GetForegroundWindow();

        KbLog($"SendTextToForeground: text=\"{text}\" fg=0x{fg:X} us=0x{ourHandle:X} lastTarget=0x{_lastTargetWindow:X}");

        // Track or restore the target window
        if (fg != ourHandle && fg != IntPtr.Zero)
        {
            _lastTargetWindow = fg;
            KbLog("  → foreground is target, tracking it");
        }
        else if (_lastTargetWindow != IntPtr.Zero)
        {
            KbLog("  → Dasher has focus, restoring target...");
            var targetThread = GetWindowThreadProcessId(_lastTargetWindow, out _);
            var ourThread = GetCurrentThreadId();
            KbLog($"  → targetThread={targetThread} ourThread={ourThread}");

            if (targetThread != 0 && targetThread != ourThread)
            {
                var attached = AttachThreadInput(ourThread, targetThread, true);
                var set = SetForegroundWindow(_lastTargetWindow);
                AttachThreadInput(ourThread, targetThread, false);
                KbLog($"  → AttachThreadInput={attached} SetForegroundWindow={set}");
            }
            else
            {
                var set = SetForegroundWindow(_lastTargetWindow);
                KbLog($"  → SetForegroundWindow={set}");
            }
        }
        else
        {
            KbLog("  → no known target window!");
        }

        foreach (char c in text)
        {
            SendUnicodeChar(c);
        }

        var fgAfter = GetForegroundWindow();
        KbLog($"  → after SendInput, fg=0x{fgAfter:X}");
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

    private void OnModeRightSide(object? sender, RoutedEventArgs e) => SetPanePosition(PanePosition.Right);
    private void OnModeLeftSide(object? sender, RoutedEventArgs e) => SetPanePosition(PanePosition.Left);
    private void OnModeBottom(object? sender, RoutedEventArgs e) => SetPanePosition(PanePosition.Bottom);
    private void OnModeTop(object? sender, RoutedEventArgs e) => SetPanePosition(PanePosition.Top);
    private void OnModeKeyboard(object? sender, RoutedEventArgs e) => SetPanePosition(PanePosition.Keyboard);

    private void OnToggleMode(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        SetPanePosition(_vm.IsKeyboardMode ? PanePosition.Right : PanePosition.Keyboard);
    }

    private void SetPanePosition(PanePosition position)
    {
        if (_vm == null) return;
        _vm.PanePosition = position;
        _vm.IsKeyboardMode = position == PanePosition.Keyboard;
        ApplyPaneLayout();
        new PaneSettings { PanePosition = position.ToString(), StatusBarHidden = _vm.IsStatusBarHidden }.Save();
    }

    private void OnToggleStatusBar(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.IsStatusBarHidden = !_vm.IsStatusBarHidden;
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
            // Keyboard mode: hide full toolbar, show mini floating bar
            TopBar.IsVisible = false;
            KeyboardMiniBar.IsVisible = true;

            // Canvas only, no message pane
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            MainGrid.Children.Add(DasherCanvas);
            Grid.SetColumn(DasherCanvas, 0);

            // Remember the window that currently has focus so we can send text to it
            var fg = GetForegroundWindow();
            var ourHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (fg != ourHandle && fg != IntPtr.Zero)
                _lastTargetWindow = fg;

            Topmost = true;
            this.Opacity = _vm.KeyboardModeOpacity;
            TxtModeLabel.Text = "Keyboard";
            BtnMode.Classes.Add("accent");
            if (txtKeyboardLabel != null) txtKeyboardLabel.Text = "Exit";
            BtnKeyboard.Classes.Add("accent");

            // Prevent Dasher from stealing focus when clicked, so keystrokes
            // go to the target app (same technique as Windows On-Screen Keyboard).
            // Apply now and again after render — Avalonia may overwrite the
            // extended style when processing Topmost/Opacity changes.
            SetNoActivate(true);
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => SetNoActivate(true), Avalonia.Threading.DispatcherPriority.Render);
        }
        else
        {
            TopBar.IsVisible = true;
            KeyboardMiniBar.IsVisible = false;

            Topmost = false;
            this.Opacity = 1.0;
            SetNoActivate(false);

            TxtModeLabel.Text = position switch
            {
                PanePosition.Right => "Right side",
                PanePosition.Left => "Left side",
                PanePosition.Bottom => "Bottom",
                PanePosition.Top => "Top",
                _ => "Right side",
            };
            ModeIcon.Kind = position switch
            {
                PanePosition.Left => LucideIconKind.PanelLeft,
                PanePosition.Bottom => LucideIconKind.PanelBottom,
                PanePosition.Top => LucideIconKind.PanelTop,
                _ => LucideIconKind.PanelRight,
            };
            BtnMode.Classes.Remove("accent");
            if (txtKeyboardLabel != null) txtKeyboardLabel.Text = "Keyboard";
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

        _previousOutput = _vm.OutputText;
    }

    private void OnEngineMessage(object? sender, EngineMessageEventArgs e)
    {
        var title = e.IsWarning ? "Dasher Warning" : "Dasher";
        ToastNotifier.Show(title, e.Text, e.IsWarning);
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

        BuildSettingsTabs(panel);
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
        for (int i = 0; i < _settingsTabs.Length; i++)
        {
            if (i == index) _settingsTabs[i].Classes.Add("active");
            else _settingsTabs[i].Classes.Remove("active");
        }
    }

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        if (_canvas == null || _vm == null) return;
        NativeBridge.dasher_reset(_vm.Handle);
        _vm.OutputText = "";
        _previousOutput = "";
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var storage = StorageProvider;
        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open text file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Text files") { Patterns = ["*.txt"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ]
        });

        if (result.Count == 0) return;

        try
        {
            await using var stream = await result[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            NativeBridge.dasher_reset_output_text(_vm.Handle);
            _vm.OutputText = text;
            _previousOutput = text;
        }
        catch { }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var result = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save text file",
            SuggestedFileName = "dasher_output.txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Text files") { Patterns = ["*.txt"] },
            ]
        });

        if (result == null) return;

        try
        {
            await using var stream = await result.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_vm.OutputText);
        }
        catch { }
    }



    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _vm.Handle == IntPtr.Zero || _vm.SelectedLanguageIndex < 0) return;
        if (_vm.SelectedLanguageIndex < _vm.Languages.Count)
        {
            var alphabet = _vm.Languages[_vm.SelectedLanguageIndex];
            NativeBridge.dasher_set_alphabet_id(_vm.Handle, alphabet);
            AnalyticsService.Capture("alphabet_selected", new() { ["alphabet_id"] = alphabet });
        }
    }

    private void OnSpeedDown(object? sender, RoutedEventArgs e) => _vm?.DecreaseSpeed();
    private void OnSpeedUp(object? sender, RoutedEventArgs e) => _vm?.IncreaseSpeed();

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
            await _canvas.InitializeEyeGazeAsync(args.trackerType, args.udpPort);
            AnalyticsService.Capture("input_method_changed", new() { ["method"] = "eye_gaze" });
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
        SetClipboardText(_vm.OutputText);
    }

    private async void OnCopySelection(object? sender, RoutedEventArgs e)
    {
        var messageArea = this.FindControl<TextBox>("MessageArea");
        if (messageArea?.SelectedText == null) return;
        SetClipboardText(messageArea.SelectedText);
    }

    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var text = GetClipboardText();
        if (text != null)
            _vm.OutputText += text;
    }

    private void OnQuickSpeak(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || string.IsNullOrWhiteSpace(_vm.OutputText)) return;
        _ = SpeechService.Instance.SpeakAsync(_vm.OutputText);
    }

    private void OnStopSpeak(object? sender, RoutedEventArgs e)
    {
        SpeechService.Instance.Stop();
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
        if (parameterKey == _bitrateKey)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_vm == null || _vm.Handle == IntPtr.Zero) return;
                _vm.Speed = NativeBridge.dasher_get_speed_percent(_vm.Handle) / 100.0;
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
        NativeBridge.dasher_set_bool_parameter(_vm.Handle, ParameterKeys.BP_CONTROL_MODE, _controlModeActive ? 1 : 0);
        UpdateControlModeLabel();
    }

    private void UpdateControlModeLabel()
    {
        var label = this.FindControl<TextBlock>("TxtControlLabel");
        if (label != null)
            label.Text = _controlModeActive ? "Leave" : "Control";

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
            NativeBridge.dasher_leave_game_mode(_vm.Handle);
            _gameModeActive = false;
            NativeBridge.dasher_game_set_canvas_text(_vm.Handle, 1);
            GameIcon.Kind = LucideIconKind.Gamepad2;
            TxtGameLabel.Text = "Game";
            var gameBar = this.FindControl<Border>("GameTargetBar");
            if (gameBar != null) gameBar.IsVisible = false;
        }
        else
        {
            var result = NativeBridge.dasher_enter_game_mode(_vm.Handle);
            if (result == 0)
            {
                _gameModeActive = true;
                NativeBridge.dasher_game_set_canvas_text(_vm.Handle, 0);
                GameIcon.Kind = LucideIconKind.Pause;
                TxtGameLabel.Text = "Leave";
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

        gameTarget.Text = $"{correctText}[{wrong}]{remaining}";
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
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (!info.IsUpdateAvailable) return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var dialog = new Window
                {
                    Title = "Dasher Update Available",
                    Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xF6)),
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(24),
                        Spacing = 12,
                        MaxWidth = 380,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Update Available",
                                FontSize = 18,
                                FontWeight = FontWeight.Bold,
                                Foreground = new SolidColorBrush(Color.FromRgb(0x2C, 0x3E, 0x50)),
                            },
                            new TextBlock
                            {
                                Text = $"Dasher {info.LatestTag} is now available (you're running v{info.CurrentVersion}).",
                                FontSize = 13,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x4A, 0x42)),
                            },
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                Children =
                                {
                                    new Button
                                    {
                                        Content = "Later",
                                        Padding = new Thickness(16, 6),
                                        Background = Brushes.Transparent,
                                        BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                                    },
                                    new Button
                                    {
                                        Content = "Download",
                                        Padding = new Thickness(16, 6),
                                        Background = new SolidColorBrush(Color.FromRgb(0x00, 0xA8, 0xA8)),
                                        Foreground = Brushes.White,
                                        BorderThickness = new Thickness(0),
                                        Tag = info.ReleaseUrl,
                                    },
                                },
                            },
                        },
                    },
                };

                var buttons = ((StackPanel)dialog.Content).Children.OfType<StackPanel>().Last();
                var laterBtn = buttons.Children.OfType<Button>().First(b => b.Content is "Later");
                var downloadBtn = buttons.Children.OfType<Button>().First(b => b.Content is "Download");

                laterBtn.Click += (_, _) => dialog.Close();
                downloadBtn.Click += (_, _) =>
                {
                    if (downloadBtn.Tag is string url)
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true,
                        });
                    dialog.Close();
                };

                dialog.ShowDialog(this);
            });
        }
        catch { }
    }
#endif
}
