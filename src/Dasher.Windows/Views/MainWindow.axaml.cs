using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    private static readonly string[] DefaultLanguages =
    [
        "English (limited punctuation)",
        "English (numerals + punctuation)",
        "English (lowercase)",
        "English (accents + numerals)",
        "English (no punctuation)",
        "English (LaTeX)",
        "German (limited punctuation)",
        "German (numerals + punctuation)",
    ];

    private static readonly (string alphabetId, string display)[] AlphabetMap =
    [
        ("English with limited punctuation", "English (limited punctuation)"),
        ("English with numerals and lots of punctuation", "English (numerals + punctuation)"),
        ("English lower case", "English (lowercase)"),
        ("English with accents numerals punctuation", "English (accents + numerals)"),
        ("English without punctuation", "English (no punctuation)"),
        ("English LaTeX", "English (LaTeX)"),
        ("German with limited punctuation", "German (limited punctuation)"),
        ("German with numerals and punctuation", "German (numerals + punctuation)"),
    ];

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

        _prefsTabs = [PrefsTabColour, PrefsTabSpeed, PrefsTabSpeech, PrefsTabPrediction, PrefsTabDisplay, PrefsTabMessages, PrefsTabManage];

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDir = Path.Combine(appData, "Dasher");
        Directory.CreateDirectory(dataDir);

        var coreDataDir = FindCoreDataDir();
        CopyDataIfNeeded(coreDataDir, dataDir);

        _canvas.Initialize(dataDir);
        _vm.SetHandle(_canvas.GetHandle());
        _vm.ApplySpeed();

        _vm.Languages = new AvaloniaList<string>(DefaultLanguages);
        _vm.SelectedLanguageIndex = 0;

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

    private void ApplyMode()
    {
        if (_vm == null) return;

        if (_vm.IsKeyboardMode)
        {
            Topmost = true;
            MessagePane.IsVisible = false;
            MessageSplitter.IsVisible = false;
            BtnMode.Content = "App";
            BtnMode.Classes.Add("keyboard-active");

            var oldWidth = Width;
            Width = Math.Min(oldWidth, 600);
        }
        else
        {
            Topmost = false;
            MessagePane.IsVisible = true;
            MessageSplitter.IsVisible = true;
            BtnMode.Content = "Keyboard";
            BtnMode.Classes.Remove("keyboard-active");

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
            btn.Classes.Remove("accent");
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

    private void OnPrefsColour(object? s, RoutedEventArgs e) => ActivatePrefsTab(0);
    private void OnPrefsSpeed(object? s, RoutedEventArgs e) => ActivatePrefsTab(1);
    private void OnPrefsSpeech(object? s, RoutedEventArgs e) => ActivatePrefsTab(2);
    private void OnPrefsPrediction(object? s, RoutedEventArgs e) => ActivatePrefsTab(3);
    private void OnPrefsDisplay(object? s, RoutedEventArgs e) => ActivatePrefsTab(4);
    private void OnPrefsMessages(object? s, RoutedEventArgs e) => ActivatePrefsTab(5);
    private void OnPrefsManage(object? s, RoutedEventArgs e) => ActivatePrefsTab(6);

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        if (_canvas == null || _vm == null) return;
        NativeBridge.dasher_reset_output_text(_vm.Handle);
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
        if (_vm == null || sender is not Button btn) return;
        _vm.IsPlaying = !_vm.IsPlaying;
        btn.Content = _vm.IsPlaying ? "Pause" : "Play";
        if (_vm.IsPlaying)
            btn.Classes.Add("accent");
        else
            btn.Classes.Remove("accent");
    }

    private void OnStats(object? sender, RoutedEventArgs e)
    {
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _vm.Handle == IntPtr.Zero || _vm.SelectedLanguageIndex < 0) return;
        if (_vm.SelectedLanguageIndex < AlphabetMap.Length)
        {
            NativeBridge.dasher_set_alphabet_id(_vm.Handle, AlphabetMap[_vm.SelectedLanguageIndex].alphabetId);
        }
    }

    private void OnSpeedDown(object? sender, RoutedEventArgs e) => _vm?.DecreaseSpeed();
    private void OnSpeedUp(object? sender, RoutedEventArgs e) => _vm?.IncreaseSpeed();

    private void OnColour0(object? sender, RoutedEventArgs e) => SelectColour(0, sender);
    private void OnColour1(object? sender, RoutedEventArgs e) => SelectColour(1, sender);
    private void OnColour2(object? sender, RoutedEventArgs e) => SelectColour(2, sender);

    private void SelectColour(int index, object? sender)
    {
        if (_vm == null) return;
        _vm.SelectedColourIndex = index;

        Colour0?.Classes.Remove("selected");
        Colour1?.Classes.Remove("selected");
        Colour2?.Classes.Remove("selected");

        var btn = sender as Button;
        btn?.Classes.Add("selected");
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
