using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Dasher.Windows.Controls;
using Dasher.Windows.Engine;
using Dasher.Windows.Services;
using Dasher.Windows.Speech;
using Dasher.Windows.ViewModels;

namespace Dasher.Windows.Views;

public partial class MainWindow : Window
{
    private DasherCanvas? _canvas;
    private MainWindowViewModel? _vm;
    private string _previousOutput = "";
    private Button[]? _prefsTabs;
    private Border? _prefsTabStrip;
    private Border? _prefsContentPanel;
    private NativeBridge.SpeakCallback? _speakCallback;
    private NativeBridge.ParameterCallback? _parameterCallback;
    private int _bitrateKey;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBOARDINPUT ki;
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

        _prefsTabStrip = this.FindControl<Border>("PrefsTabStrip");
        _prefsContentPanel = this.FindControl<Border>("PrefsContentPanel");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDir = Path.Combine(appData, "Dasher");
        Directory.CreateDirectory(dataDir);

        var coreDataDir = FindCoreDataDir();
        CopyDataIfNeeded(coreDataDir, dataDir);

        _canvas.Initialize(coreDataDir, dataDir);
        _canvas.EngineMessage += OnEngineMessage;
        _vm.SetHandle(_canvas.GetHandle());

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

        _vm.LoadAlphabets();

        var currentAlphaPtr = NativeBridge.dasher_get_alphabet_id(_vm.Handle);
        var currentAlpha = currentAlphaPtr != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(currentAlphaPtr) ?? "" : "";
        _vm.SelectedLanguageIndex = Math.Max(0, _vm.Languages.IndexOf(currentAlpha));

        var settingsPanel = this.FindControl<SettingsPanel>("SettingsPanel");
        if (settingsPanel != null)
        {
            settingsPanel.Initialize(_vm.Handle);
            settingsPanel.BackRequested += OnSettingsBack;
            settingsPanel.InputSourceChanged += OnInputSourceChanged;
            settingsPanel.JoystickRequested += OnJoystickRequested;
            settingsPanel.OutputFontChanged += OnOutputFontChanged;
            settingsPanel.KeyboardOpacityChanged += OnKeyboardOpacityChanged;
            BuildPrefsTabs(settingsPanel);
        }

        ApplyOutputFontSettings();

        var paneSettings = PaneSettings.Load();
        if (Enum.TryParse<PanePosition>(paneSettings.PanePosition, out var savedPos))
            _vm.PanePosition = savedPos;
        _vm.IsKeyboardMode = _vm.PanePosition == PanePosition.Keyboard;
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

    private void SendTextToForeground(string text)
    {
        foreach (char c in text)
        {
            SendUnicodeChar(c);
        }
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

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
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
        new PaneSettings { PanePosition = position.ToString() }.Save();
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
            // Keyboard mode: canvas only, no message pane
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            MainGrid.Children.Add(DasherCanvas);
            Grid.SetColumn(DasherCanvas, 0);

            Topmost = true;
            this.Opacity = _vm.KeyboardModeOpacity;
            TxtModeLabel.Text = "Keyboard";
            BtnMode.Classes.Add("accent");
            if (txtKeyboardLabel != null) txtKeyboardLabel.Text = "Exit";
            BtnKeyboard.Classes.Add("accent");
        }
        else
        {
            Topmost = false;
            this.Opacity = 1.0;

            TxtModeLabel.Text = position switch
            {
                PanePosition.Right => "Right side",
                PanePosition.Left => "Left side",
                PanePosition.Bottom => "Bottom",
                PanePosition.Top => "Top",
                _ => "Right side",
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

    private async void OnEngineMessage(object? sender, EngineMessageEventArgs e)
    {
        var icon = e.IsWarning ? "⚠" : "ℹ";
        var title = e.IsWarning ? "Dasher Warning" : "Dasher";
        var notification = new Window
        {
            Title = title,
            Content = new TextBlock
            {
                Text = $"{icon} {e.Text}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
                MaxWidth = 400,
                FontSize = 13,
            },
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xF6)),
        };
        _ = notification.ShowDialog(this);
        await Task.Delay(5000);
        notification.Close();
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
    }

    private void ApplyMode()
    {
        if (_vm == null) return;

        var txtKeyboardLabel = this.FindControl<TextBlock>("TxtKeyboardLabel");

        if (_vm.IsKeyboardMode)
        {
            Topmost = true;
            MessagePane.IsVisible = false;
            MessageSplitter.IsVisible = false;
            TxtModeLabel.Text = "Keyboard";
            BtnMode.Classes.Add("accent");
            if (txtKeyboardLabel != null) txtKeyboardLabel.Text = "Exit";
            BtnKeyboard.Classes.Add("accent");
            Width = Math.Min(Width, 600);
        }
        else
        {
            Topmost = false;
            MessagePane.IsVisible = true;
            MessageSplitter.IsVisible = true;
            TxtModeLabel.Text = "Right side";
            BtnMode.Classes.Remove("accent");
            if (txtKeyboardLabel != null) txtKeyboardLabel.Text = "Keyboard";
            BtnKeyboard.Classes.Remove("accent");
            if (Width < 700) Width = 900;
        }

        _previousOutput = _vm.OutputText;
    }

    private void OnTogglePrefs(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not Button btn) return;
        _vm.IsPrefsVisible = !_vm.IsPrefsVisible;

        if (_vm.IsPrefsVisible)
            btn.Classes.Add("accent");
        else
        {
            btn.Classes.Remove("accent");
            if (_prefsTabStrip != null) _prefsTabStrip.IsVisible = true;
            if (_prefsContentPanel != null) _prefsContentPanel.IsVisible = false;
            ActivatePrefsTab(-1);
        }
    }

    private void ActivatePrefsTab(int index)
    {
        if (_prefsTabs == null) return;
        for (int i = 0; i < _prefsTabs.Length; i++)
        {
            if (i == index) _prefsTabs[i].Classes.Add("active");
            else _prefsTabs[i].Classes.Remove("active");
        }
    }

    private void BuildPrefsTabs(SettingsPanel settingsPanel)
    {
        var container = this.FindControl<StackPanel>("PrefsTabContainer");
        if (container == null) return;

        container.Children.Clear();
        _prefsTabs = [];

        foreach (var category in settingsPanel.GetCategoryNames())
        {
            var btn = new Button
            {
                Classes = { "prefs-tab" },
                Content = category,
                Tag = category,
            };
            btn.Click += OnPrefsTabClick;
            container.Children.Add(btn);
            _prefsTabs = [.. _prefsTabs, btn];
        }
    }

    private void OnPrefsTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || _vm == null) return;
        var category = btn.Tag as string ?? "";
        ActivatePrefsTab(Array.FindIndex(_prefsTabs!, t => t.Tag as string == category));

        var settingsPanel = this.FindControl<SettingsPanel>("SettingsPanel");
        if (settingsPanel != null && _prefsTabStrip != null && _prefsContentPanel != null)
        {
            settingsPanel.ShowCategory(category);
            _prefsTabStrip.IsVisible = false;
            _prefsContentPanel.IsVisible = true;
        }
    }

    private void OnSettingsBack(object? sender, EventArgs e)
    {
        if (_prefsTabStrip != null && _prefsContentPanel != null)
        {
            _prefsTabStrip.IsVisible = true;
            _prefsContentPanel.IsVisible = false;
        }
        ActivatePrefsTab(-1);
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

    private void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.IsPlaying = !_vm.IsPlaying;
        TxtPlayLabel.Text = _vm.IsPlaying ? "Pause" : "Play";
        if (_vm.IsPlaying)
            BtnPlay.Classes.Add("accent");
        else
            BtnPlay.Classes.Remove("accent");
    }

    private void OnStats(object? sender, RoutedEventArgs e)
    {
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _vm.Handle == IntPtr.Zero || _vm.SelectedLanguageIndex < 0) return;
        if (_vm.SelectedLanguageIndex < _vm.Languages.Count)
        {
            NativeBridge.dasher_set_alphabet_id(_vm.Handle, _vm.Languages[_vm.SelectedLanguageIndex]);
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
        }
    }

    private void OnJoystickRequested(object? sender, EventArgs e)
    {
        if (_canvas == null) return;
        _canvas.DisableEyeGaze();
        _canvas.InitializeJoystick();
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

    private void OnEngineSpeak(IntPtr textPtr, int interrupt, IntPtr user_data)
    {
        if (textPtr == IntPtr.Zero) return;
        var text = Marshal.PtrToStringUTF8(textPtr);
        if (string.IsNullOrEmpty(text)) return;
        _ = SpeechService.Instance.SpeakAsync(text, interrupt != 0);
    }

    private void OnParameterChanged(int parameterKey, IntPtr userData)
    {
        if (parameterKey != _bitrateKey) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_vm == null || _vm.Handle == IntPtr.Zero) return;
            _vm.Speed = NativeBridge.dasher_get_speed_percent(_vm.Handle) / 100.0;
        });
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
        _vm.KeyboardModeOpacity = settings.KeyboardOpacity;
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

    private void OnToggleGameMode(object? sender, RoutedEventArgs e)
    {
        if (_canvas == null || _vm == null) return;

        if (_gameModeActive)
        {
            NativeBridge.dasher_leave_game_mode(_vm.Handle);
            _gameModeActive = false;
            NativeBridge.dasher_game_set_canvas_text(_vm.Handle, 1);
            var txtGameLabel = this.FindControl<TextBlock>("TxtGameLabel");
            if (txtGameLabel != null) txtGameLabel.Text = "Game";
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
                var txtGameLabel = this.FindControl<TextBlock>("TxtGameLabel");
                if (txtGameLabel != null) txtGameLabel.Text = "Leave";
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

        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            var dirName = Path.GetFileName(dir);
            var targetSubDir = Path.Combine(targetDir, dirName);
            Directory.CreateDirectory(targetSubDir);

            foreach (var file in Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var targetFile = Path.Combine(targetSubDir, Path.GetFileName(file));
                if (!File.Exists(targetFile))
                {
                    File.Copy(file, targetFile, false);
                }
            }
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
