using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotNetTtsWrapper.Models;

namespace Dasher.Windows.Speech;

/// <summary>
/// Searchable, filterable voice picker (parity with Dasher-Apple's
/// SpeechVoiceBrowser). Opens as a Flyout from the speech settings:
/// search by name/id, filter by language and gender, commit with a
/// single click or Enter. Keyboard users browse with the arrow keys
/// and commit with Enter/Space (selection alone does not commit).
/// </summary>
public sealed class VoicePickerFlyout
{
    private readonly SpeechService _svc;
    private readonly Action _onSelectionChanged;
    private Flyout? _flyout;
    private TextBox? _searchBox;
    private ComboBox? _languageFilter;
    private ComboBox? _genderFilter;
    private TextBlock? _resultCount;
    private ListBox? _voiceList;
    private TextBlock? _statusText;
    private string _search = "";
    private bool _initialSelectionApplied;

    private const string AnyLanguage = "Any";
    private const string AnyGender = "Any";

    public VoicePickerFlyout(SpeechService svc, Action onSelectionChanged)
    {
        _svc = svc;
        _onSelectionChanged = onSelectionChanged;
    }

    public void Show(Control placementTarget)
    {
        _flyout = new Flyout();
        _flyout.Content = BuildPanel();
        _flyout.ShowAt(placementTarget);
        _searchBox?.Focus();
    }

    private Panel BuildPanel()
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 380, MaxWidth = 460, Margin = new Thickness(12) };

        panel.Children.Add(new TextBlock
        {
            Text = "Browse voices",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
        });

        _searchBox = new TextBox
        {
            PlaceholderText = "Search voices",
            FontSize = 12,
        };
        _searchBox.TextChanged += (s, e) => { _search = _searchBox.Text ?? ""; RefreshList(); };
        panel.Children.Add(_searchBox);

        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        _languageFilter = new ComboBox { MinWidth = 130, FontSize = 11 };
        _languageFilter.SelectionChanged += (s, e) => RefreshList();
        filterRow.Children.Add(WrapFilter("Language", _languageFilter));

        _genderFilter = new ComboBox { MinWidth = 100, FontSize = 11 };
        _genderFilter.SelectionChanged += (s, e) => RefreshList();
        filterRow.Children.Add(WrapFilter("Gender", _genderFilter));

        panel.Children.Add(filterRow);

        _resultCount = new TextBlock { FontSize = 11, Foreground = Brushes.Gray };
        panel.Children.Add(_resultCount);

        _statusText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(_statusText);

        // ListBox (not ItemsControl) for built-in virtualization — Edge and
        // Azure expose 300-500 voices and the list is refiltered per keystroke.
        _voiceList = new ListBox { MaxHeight = 280 };
        _voiceList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TtsVoice>((voice, _) =>
        {
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock { Text = voice?.Name ?? "", FontSize = 12, FontWeight = FontWeight.Medium });
            var subtitle = BuildSubtitle(voice);
            if (!string.IsNullOrEmpty(subtitle))
                sp.Children.Add(new TextBlock { Text = subtitle, FontSize = 10, Foreground = Brushes.Gray });
            // Single pointer press commits: pointer users (touch, eye gaze)
            // pay a high cost per click, so browse-then-commit is wrong here.
            sp.PointerPressed += (_, _) => Commit(voice);
            return sp;
        });
        // Keyboard: arrows browse (ListBox selection), Enter/Space commits.
        _voiceList.KeyDown += (s, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && _voiceList.SelectedItem is TtsVoice voice)
            {
                Commit(voice);
                e.Handled = true;
            }
        };
        panel.Children.Add(_voiceList);

        PopulateFilters();
        RefreshList();
        return panel;
    }

    private static StackPanel WrapFilter(string label, ComboBox combo)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = Brushes.Gray });
        sp.Children.Add(combo);
        return sp;
    }

    private void PopulateFilters()
    {
        var voices = _svc.AvailableVoices;

        var languages = new List<string> { AnyLanguage };
        languages.AddRange(voices
            .SelectMany(v => v.LanguageCodes ?? [])
            .Select(l => l.Display ?? l.Bcp47 ?? "")
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l));
        _languageFilter!.Items.Clear();
        foreach (var l in languages) _languageFilter.Items.Add(l);
        _languageFilter.SelectedIndex = 0;

        var genders = new List<string> { AnyGender };
        genders.AddRange(voices
            .Select(v => v.Gender.ToString())
            .Where(g => !string.IsNullOrEmpty(g) && g != "Unknown")
            .Distinct()
            .OrderBy(g => g));
        _genderFilter!.Items.Clear();
        foreach (var g in genders) _genderFilter.Items.Add(g);
        _genderFilter.SelectedIndex = 0;
    }

    private void RefreshList()
    {
        var voices = _svc.AvailableVoices;
        var lang = _languageFilter?.SelectedItem as string;
        var gender = _genderFilter?.SelectedItem as string;

        var filtered = voices.Where(v =>
        {
            if (!string.IsNullOrWhiteSpace(_search))
            {
                var s = _search.Trim();
                if (!(v.Name?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !(v.Id?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false))
                    return false;
            }
            if (lang is { } l && l != AnyLanguage)
            {
                var codes = v.LanguageCodes ?? [];
                if (!codes.Any(c =>
                    (c.Display ?? "").Contains(l, StringComparison.OrdinalIgnoreCase) ||
                    (c.Bcp47 ?? "").Contains(l, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            if (gender is { } g && g != AnyGender)
            {
                if (!string.Equals(v.Gender.ToString(), g, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }).ToList();

        // Reassign ItemsSource wholesale — virtualization means only the
        // visible containers are realized, so this stays cheap per keystroke.
        _voiceList!.ItemsSource = filtered;
        // Mark the current voice only on the initial fill: re-assigning
        // SelectedItem per keystroke scrolls the list back into view and
        // makes it jump while the user types.
        if (!_initialSelectionApplied)
        {
            _voiceList.SelectedItem = filtered.FirstOrDefault(v => v.Id == _svc.SelectedVoiceId);
            _initialSelectionApplied = true;
        }

        _resultCount!.Text = filtered.Count == 1
            ? "1 result"
            : $"{filtered.Count} results";

        _statusText!.IsVisible = voices.Count == 0;
        if (voices.Count == 0)
        {
            _statusText.Text = _svc.IsLoadingVoices
                ? "Loading voices…"
                : _svc.ErrorMessage ?? "No voices found";
        }
    }

    private void Commit(TtsVoice? voice)
    {
        _svc.SelectedVoiceId = voice?.Id;
        _svc.SaveSettings();
        _onSelectionChanged();
        _flyout?.Hide();
    }

    private static string BuildSubtitle(TtsVoice? voice)
    {
        if (voice == null) return "";
        var parts = new List<string>();
        if (voice.LanguageCodes is { Count: > 0 })
            parts.Add(string.Join(", ", voice.LanguageCodes
                .Select(l => l.Display ?? l.Bcp47 ?? "")
                .Where(d => !string.IsNullOrEmpty(d))
                .Take(3)));
        var g = voice.Gender.ToString();
        if (!string.IsNullOrEmpty(g) && g != "Unknown")
            parts.Add(g);
        return string.Join(" · ", parts);
    }
}
