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
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Dasher.Windows.Controls;
using Dasher.Windows.Engine;
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
        _vm.ApplySpeed();
        _vm.AutoSpeed = NativeBridge.dasher_get_bool_parameter(_vm.Handle, ParameterKeys.BP_AUTO_SPEEDCONTROL) != 0;

        _vm.LoadAlphabets();
        _vm.SelectedLanguageIndex = 0;

        _vm.LoadPalettes();
        BuildPaletteSwatches();

        var settingsPanel = this.FindControl<SettingsPanel>("SettingsPanel");
        if (settingsPanel != null)
        {
            settingsPanel.Initialize(_vm.Handle);
            settingsPanel.BackRequested += OnSettingsBack;
            settingsPanel.InputSourceChanged += OnInputSourceChanged;
            BuildPrefsTabs(settingsPanel);
        }

        _vm.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.OutputText))
                OnOutputTextChanged();
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _canvas?.Shutdown();
        base.OnClosing(e);
    }

    private void BuildPaletteSwatches()
    {
        var container = this.FindControl<UniformGrid>("PaletteContainer");
        if (container == null || _vm == null) return;

        container.Children.Clear();
        foreach (var palette in _vm.Palettes.Take(4))
        {
            var btn = new Button
            {
                Classes = { "ui-colour-dot" },
                Tag = palette.Name,
                Background = ArgbToBrush(palette.Color0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            ToolTip.SetTip(btn, palette.Name);
            btn.Click += OnPaletteSelect;
            container.Children.Add(btn);
        }
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

    private void OnToggleMode(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.IsKeyboardMode = !_vm.IsKeyboardMode;
        ApplyMode();
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
        notification.ShowDialog(this);
        await Task.Delay(5000);
        notification.Close();
    }

    private void OnModeRightSide(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.IsKeyboardMode = false;
        ApplyMode();
    }

    private void OnModeKeyboard(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.IsKeyboardMode = true;
        ApplyMode();
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
    }

    private void ApplyMode()
    {
        if (_vm == null) return;

        if (_vm.IsKeyboardMode)
        {
            Topmost = true;
            MessagePane.IsVisible = false;
            MessageSplitter.IsVisible = false;
            TxtModeLabel.Text = "Keyboard";
            BtnMode.Classes.Add("accent");

            var oldWidth = Width;
            Width = Math.Min(oldWidth, 600);
        }
        else
        {
            Topmost = false;
            MessagePane.IsVisible = true;
            MessageSplitter.IsVisible = true;
            TxtModeLabel.Text = "Right side";
            BtnMode.Classes.Remove("accent");

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

    private void OnPaletteSelect(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not Button btn) return;
        var name = btn.Tag as string;
        if (name == null) return;

        NativeBridge.dasher_set_palette(_vm.Handle, name);

        foreach (var child in PaletteContainer.Children)
        {
            if (child is Button b)
                b.Classes.Remove("selected");
        }
        btn.Classes.Add("selected");
    }

    private async void OnInputSourceChanged(object? sender, EyeGazeIntegration.TrackerType trackerType)
    {
        if (_canvas == null) return;

        if (trackerType == EyeGazeIntegration.TrackerType.None)
        {
            _canvas.DisableEyeGaze();
        }
        else
        {
            await _canvas.InitializeEyeGazeAsync(trackerType);
        }
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
        try
        {
            dynamic synth = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")!)!;
            synth.Speak(_vm.OutputText, 1 | 0);
        }
        catch { }
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
}
