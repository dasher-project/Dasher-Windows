using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _canvas?.Shutdown();
        base.OnClosing(e);
    }

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        if (_canvas == null || _vm == null) return;
        NativeBridge.dasher_reset_output_text(_vm.Handle);
        _vm.OutputText = "";
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
        if (_vm == null || _vm.Handle == 0 || _vm.SelectedLanguageIndex < 0) return;
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
