using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Dasher.Windows.Speech;

/// <summary>
/// In-app editor for the pronunciation-override file: a grid of
/// word/say-it-as rows with add, remove, and a per-row "hear it" preview
/// (spoken through the current engine, so the override — applied by the
/// wrapper per utterance — is exactly what you hear). Saves are
/// immediate (the file is tiny and the wrapper hot-reloads on mtime),
/// and hand-written comments/blank lines in the file are preserved.
/// </summary>
public sealed class PronunciationEditorWindow : Window
{
    private readonly SpeechService _svc;
    private ListBox? _list;
    private TextBox? _wordBox;
    private TextBox? _sayBox;
    private TextBlock? _status;

    private string _fileText = "";

    public PronunciationEditorWindow(SpeechService svc)
    {
        _svc = svc;
        Title = "Pronunciations";
        Width = 560;
        Height = 480;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildUi();
        Reload();
    }

    private Control BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        // Footer hint
        var footer = new TextBlock
        {
            Text = $"Saved to {SpeechService.PronunciationsPath} — changes apply to the next sentence you speak. " +
                   "You can also edit this file with Notepad; both forms work: botox = bow tocks  or  botox = /ˈboʊ.tɑːks/",
            FontSize = 10,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Add bar (top)
        var addBar = new Grid { ColumnDefinitions = new("*,*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        _wordBox = new TextBox { PlaceholderText = "Word (e.g. botox, or a name)", FontSize = 12, Watermark = null! };
        _sayBox = new TextBox { PlaceholderText = "Say it like (e.g. bow tocks)", FontSize = 12, Watermark = null! };
        var addBtn = new Button { Content = "Add", FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
        addBtn.Click += OnAdd;
        _wordBox.SetValue(Grid.ColumnProperty, 0);
        _sayBox.SetValue(Grid.ColumnProperty, 1);
        addBtn.SetValue(Grid.ColumnProperty, 2);
        addBar.Children.Add(_wordBox);
        addBar.Children.Add(_sayBox);
        addBar.Children.Add(addBtn);
        DockPanel.SetDock(addBar, Dock.Top);
        root.Children.Add(addBar);

        _status = new TextBlock { FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);

        _list = new ListBox();
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PronunciationFile.Entry>((e, namescope) =>
        {
            if (e == null) return null;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var hear = new Button { Content = "Hear it", FontSize = 10, Padding = new Thickness(6, 2) };
            hear.Click += (_, _) => { _ = _svc.SpeakAsync(e.Word); };
            var remove = new Button { Content = "Remove", FontSize = 10, Padding = new Thickness(6, 2) };
            remove.Click += (_, _) => OnRemove(e);
            actions.Children.Add(hear);
            actions.Children.Add(remove);
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);

            var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = e.Word, FontSize = 12, FontWeight = FontWeight.Medium });
            words.Children.Add(new TextBlock { Text = $"says  {e.Say}", FontSize = 10, Foreground = Brushes.Gray });
            row.Children.Add(words);
            return row;
        });
        root.Children.Add(_list);
        return root;
    }

    private void Reload()
    {
        try
        {
            _fileText = File.Exists(SpeechService.PronunciationsPath)
                ? File.ReadAllText(SpeechService.PronunciationsPath)
                : "";
        }
        catch (Exception ex)
        {
            _fileText = "";
            _status!.Text = $"Could not read the file: {ex.Message}";
        }
        RefreshList();
    }

    private void RefreshList()
    {
        var entries = PronunciationFile.ParseEntries(_fileText);
        _list!.ItemsSource = entries;
        _status!.Text = entries.Count == 0
            ? "No pronunciations yet — add one above."
            : $"{entries.Count} pronunciation{(entries.Count == 1 ? "" : "s")}";
    }

    private void Save()
    {
        try
        {
            // Ensure the starter file's directory exists even if the
            // startup seeding was skipped (portable installs, first run
            // ordering).
            var dir = Path.GetDirectoryName(SpeechService.PronunciationsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SpeechService.PronunciationsPath, _fileText);
        }
        catch (Exception ex)
        {
            _status!.Text = $"Could not save: {ex.Message}";
        }
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var word = (_wordBox!.Text ?? "").Trim();
        var say = (_sayBox!.Text ?? "").Trim();
        if (word.Length == 0 || say.Length == 0)
        {
            _status!.Text = "Type both the word and how to say it.";
            return;
        }
        if (word.Contains('=') || say.Contains('\n'))
        {
            _status!.Text = "The word can't contain '='.";
            return;
        }
        if (PronunciationFile.Contains(_fileText, word, say))
        {
            _status!.Text = "That pronunciation is already in the list.";
            return;
        }
        _fileText = PronunciationFile.AppendEntry(_fileText, word, say);
        Save();
        _wordBox.Text = "";
        _sayBox.Text = "";
        RefreshList();
    }

    private void OnRemove(PronunciationFile.Entry entry)
    {
        _fileText = PronunciationFile.RemoveLines(_fileText, [entry.LineIndex]);
        Save();
        Reload(); // line indexes shift after removal
    }
}
