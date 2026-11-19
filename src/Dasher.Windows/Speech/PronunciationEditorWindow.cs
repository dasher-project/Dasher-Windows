using System;
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
/// word/say-it-as rows with add, remove, and per-row "hear it" preview
/// (spoken through the current engine, so the override — applied by the
/// wrapper per utterance — is exactly what you hear). Saves are
/// immediate (the file is tiny and the wrapper hot-reloads on mtime),
/// and hand-written comments/blank lines in the file are preserved.
///
/// Concurrency contract with Notepad (and a second editor): every
/// mutation RE-READS the file first and matches removals by content, so
/// external edits between our reads are never clobbered by a stale
/// snapshot; a failed read blocks the mutation instead of risking a
/// destructive write.
/// </summary>
public sealed class PronunciationEditorWindow : Window
{
    private readonly SpeechService _svc;
    private ListBox? _list;
    private TextBox? _wordBox;
    private TextBox? _sayBox;
    private TextBlock? _status;

    private string _fileText = "";
    private string? _loadError;

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

        // Add bar (top): labelled fields with a gap, plus a preview that
        // speaks the SAY-AS text directly — checking a respelling before
        // committing it (the per-row "Hear it" is the end-to-end check:
        // it speaks the word through the active override).
        var addPanel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };

        var labelRow = new Grid { ColumnDefinitions = new("*,12,*,Auto") };
        var wordLabel = new TextBlock { Text = "Word", FontSize = 9, Foreground = Brushes.Gray };
        wordLabel.SetValue(Grid.ColumnProperty, 0);
        var sayLabel = new TextBlock { Text = "Say it like (respell, or /IPA/)", FontSize = 9, Foreground = Brushes.Gray };
        sayLabel.SetValue(Grid.ColumnProperty, 2);
        labelRow.Children.Add(wordLabel);
        labelRow.Children.Add(sayLabel);
        addPanel.Children.Add(labelRow);

        var inputRow = new Grid { ColumnDefinitions = new("*,12,*,Auto") };
        _wordBox = new TextBox { PlaceholderText = "e.g. botox, or a name", FontSize = 12 };
        _wordBox.SetValue(Grid.ColumnProperty, 0);
        _sayBox = new TextBox { PlaceholderText = "e.g. bow tocks", FontSize = 12 };
        _sayBox.SetValue(Grid.ColumnProperty, 2);

        var addButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 0, 0, 0) };
        addButtons.SetValue(Grid.ColumnProperty, 3);
        var previewBtn = new Button { Content = "Preview", FontSize = 12 };
        previewBtn.Click += OnPreview;
        var addBtn = new Button { Content = "Add", FontSize = 12 };
        addBtn.Click += OnAdd;
        addButtons.Children.Add(previewBtn);
        addButtons.Children.Add(addBtn);

        inputRow.Children.Add(_wordBox);
        inputRow.Children.Add(_sayBox);
        inputRow.Children.Add(addButtons);
        addPanel.Children.Add(inputRow);

        DockPanel.SetDock(addPanel, Dock.Top);
        root.Children.Add(addPanel);

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
            hear.Click += async (_, _) =>
            {
                await _svc.SpeakAsync(e.Word);
                if (!string.IsNullOrEmpty(_svc.ErrorMessage))
                    _status!.Text = _svc.ErrorMessage;
            };
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

    /// <summary>Reads the file. On failure the PREVIOUS text is kept and
    /// the error recorded — mutations check _loadError and refuse, so a
    /// transient read failure can never cascade into an empty-file
    /// write.</summary>
    private void Reload()
    {
        try
        {
            _fileText = File.Exists(SpeechService.PronunciationsPath)
                ? File.ReadAllText(SpeechService.PronunciationsPath)
                : "";
            _loadError = null;
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
        }
        RefreshList();
    }

    private void RefreshList()
    {
        var entries = PronunciationFile.ParseEntries(_fileText);
        _list!.ItemsSource = entries;
        var unreadable = PronunciationFile.CountUnreadable(_fileText);
        if (_loadError != null)
            _status!.Text = $"Cannot read the file right now — editing is paused. ({_loadError})";
        else if (entries.Count == 0 && unreadable == 0)
            _status!.Text = "No pronunciations yet — add one above.";
        else
        {
            _status!.Text = $"{entries.Count} pronunciation{(entries.Count == 1 ? "" : "s")}";
            if (unreadable > 0)
                _status.Text += $" · {unreadable} unreadable line{(unreadable == 1 ? "" : "s")} (skipped — check spelling in the file)";
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SpeechService.PronunciationsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SpeechService.PronunciationsPath, _fileText);
        }
        catch (Exception ex)
        {
            _status!.Text = $"Could not save: {ex.Message}";
        }
    }

    private void OnPreview(object? sender, RoutedEventArgs e)
    {
        var say = (_sayBox!.Text ?? "").Trim();
        if (say.Length == 0)
        {
            _status!.Text = "Type how to say it first, then Preview.";
            return;
        }
        // Preview speaks the say-as text literally — meaningful for
        // respellings, not for /IPA/ (the engine would read the slashes
        // aloud). IPA is auditioned with the per-row Hear it after Add,
        // which speaks the word through the active override.
        if (say.StartsWith('/') || say.EndsWith('/'))
        {
            _status!.Text = "Preview reads respellings aloud — for /IPA/, Add first, then use the row's Hear it.";
            return;
        }
        HearThroughEngine(say);
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
        if (word.Contains('='))
        {
            _status!.Text = "The word can't contain '='.";
            return;
        }
        // Re-read first: an external edit (Notepad, another window)
        // between our reads must not be clobbered by a stale snapshot.
        Reload();
        if (_loadError != null) return;
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
        // Re-read first (line indexes from the list's parse may be stale
        // after external edits), then match by CONTENT — word (with the
        // wrapper's whitespace-collapsed comparison) + say — so we delete
        // the intended line even if lines shifted.
        Reload();
        if (_loadError != null) return;
        var fresh = PronunciationFile.ParseEntries(_fileText);
        var target = fresh.FirstOrDefault(f =>
            string.Equals(PronunciationFile.NormalizeKey(f.Word), PronunciationFile.NormalizeKey(entry.Word), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.Say, entry.Say, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            // Already gone (edited externally) — nothing to do.
            RefreshList();
            return;
        }
        _fileText = PronunciationFile.RemoveLines(_fileText, [target.LineIndex]);
        Save();
        Reload();
    }

    private async void HearThroughEngine(string text)
    {
        await _svc.SpeakAsync(text);
        if (!string.IsNullOrEmpty(_svc.ErrorMessage))
            _status!.Text = _svc.ErrorMessage;
    }
}
