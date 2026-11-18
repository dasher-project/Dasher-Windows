using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotNetTtsWrapper.Models;

namespace Dasher.Windows.Speech;

/// <summary>
/// Searchable, filterable voice picker (parity with Dasher-Apple's
/// SpeechVoiceBrowser). Opens as a Flyout from the speech settings and
/// lets the user search by name, filter by language/gender, and preview
/// voices before selecting.
/// </summary>
public sealed class VoicePickerFlyout
{
    private readonly SpeechService _svc;
    private readonly Action _onSelectionChanged;
    private TextBox? _searchBox;
    private ComboBox? _languageFilter;
    private ComboBox? _genderFilter;
    private TextBlock? _resultCount;
    private ItemsControl? _voiceList;
    private TextBlock? _statusText;
    private string _search = "";

    private const string AnyLanguage = "Any";
    private const string AnyGender = "Any";

    public VoicePickerFlyout(SpeechService svc, Action onSelectionChanged)
    {
        _svc = svc;
        _onSelectionChanged = onSelectionChanged;
    }

    public void Show(Control placementTarget)
    {
        var flyout = new Flyout();
        var panel = BuildPanel();
        flyout.Content = panel;
        flyout.ShowAt(placementTarget);
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
            Watermark = "Search voices",
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

        _voiceList = new ItemsControl { MaxHeight = 280 };
        _voiceList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TtsVoice>((voice, _) =>
        {
            var isSelected = voice?.Id == _svc.SelectedVoiceId;
            var border = new Border
            {
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(4),
                Background = isSelected ? Brushes.LightBlue : null,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock { Text = voice?.Name ?? "", FontSize = 12, FontWeight = FontWeight.Medium });
            var subtitle = BuildSubtitle(voice);
            if (!string.IsNullOrEmpty(subtitle))
                sp.Children.Add(new TextBlock { Text = subtitle, FontSize = 10, Foreground = Brushes.Gray });
            border.Child = sp;
            border.PointerPressed += (_, _) => Select(voice, border);
            return border;
        });
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

        // LanguageCodes is List<LanguageInfo> with Bcp47/Display properties
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

        _voiceList!.Items.Clear();
        foreach (var v in filtered)
            _voiceList.Items.Add(v);

        _resultCount!.Text = filtered.Count == 1
            ? "1 result"
            : $"{filtered.Count} results";

        _statusText!.IsVisible = voices.Count == 0;
        if (voices.Count == 0)
            _statusText.Text = _svc.ErrorMessage ?? "No voices found";
    }

    private void Select(TtsVoice? voice, Border? clickedBorder)
    {
        _svc.SelectedVoiceId = voice?.Id;
        _onSelectionChanged();
        // Highlight the selection and close the flyout
        if (clickedBorder?.Parent is Panel p)
        {
            // Walk up to find the FlyoutPresenter
            var current = (Visual?)p;
            while (current != null)
            {
                if (current is FlyoutPresenter fp)
                {
                    fp.IsVisible = false;
                    return;
                }
                current = current.GetVisualParent();
            }
        }
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
