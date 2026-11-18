using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RustTtsWrapper;

namespace Dasher.Windows.Speech;

/// <summary>
/// Sherpa-ONNX model browser (parity with Dasher-Apple's
/// SherpaModelBrowser): searchable registry with install progress, model
/// selection and removal. Rows gate on Windows synthesis capability
/// (SherpaSynthesisCapability) so users can't download a voice that
/// cannot speak in this build.
/// </summary>
public sealed class SherpaModelBrowserWindow : Window
{
    private readonly SherpaModelService _models;
    private readonly SpeechService _svc;
    private readonly Action _onSelectionChanged;

    private TextBox? _searchBox;
    private ComboBox? _familyFilter;
    private CheckBox? _installedOnly;
    private ListBox? _list;
    private TextBlock? _status;
    private TextBlock? _footer;

    private List<TtsLocalModel> _catalogue = [];
    private HashSet<string> _installedIds = [];
    private readonly Dictionary<string, CancellationTokenSource> _downloads = [];
    private string _search = "";

    private const string AllFamilies = "All";

    public SherpaModelBrowserWindow(SpeechService svc, Action onSelectionChanged)
    {
        _svc = svc;
        _models = new SherpaModelService(svc);
        _onSelectionChanged = onSelectionChanged;

        Title = "Sherpa-ONNX voices";
        Width = 660;
        Height = 560;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildUi();
        RefreshCatalogue();
    }

    private Control BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        // Footer: models dir + capability note
        _footer = new TextBlock
        {
            FontSize = 10,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        DockPanel.SetDock(_footer, Dock.Bottom);
        root.Children.Add(_footer);

        // Header row: search + filters
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);

        _searchBox = new TextBox { PlaceholderText = "Search voices", FontSize = 12 };
        _searchBox.TextChanged += (s, e) => { _search = _searchBox.Text ?? ""; RefreshList(); };
        header.Children.Add(_searchBox);

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _familyFilter = new ComboBox { MinWidth = 120, FontSize = 11 };
        _familyFilter.SelectionChanged += (s, e) => RefreshList();
        var famWrap = new StackPanel { Spacing = 2 };
        famWrap.Children.Add(new TextBlock { Text = "Family", FontSize = 9, Foreground = Brushes.Gray });
        famWrap.Children.Add(_familyFilter);
        filters.Children.Add(famWrap);

        _installedOnly = new CheckBox { Content = "Installed only", FontSize = 11, VerticalAlignment = VerticalAlignment.Bottom };
        _installedOnly.IsChecked = false;
        _installedOnly.IsCheckedChanged += (s, e) => RefreshList();
        filters.Children.Add(_installedOnly);

        _status = new TextBlock { FontSize = 10, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Bottom };
        filters.Children.Add(_status);
        header.Children.Add(filters);
        root.Children.Add(header);

        // List
        _list = new ListBox();
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TtsLocalModel>((m, _) => m == null ? null : BuildRow(m));
        root.Children.Add(_list);
        return root;
    }

    private Control BuildRow(TtsLocalModel m)
    {
        var selected = m.Id == _models.CurrentModelId;
        var installed = _installedIds.Contains(m.Id);
        var speakable = SherpaSynthesisCapability.IsSpeakable(m.ModelType);

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

        // Right: ONE action button — Install / Use / Remove — so the label
        // itself tells you the row's state (in use is carried by the row
        // styling, not a disabled button). Downloading swaps in progress.
        Control action;
        if (_downloads.TryGetValue(m.Id, out var cts))
        {
            var wrap = new StackPanel { Spacing = 2 };
            wrap.Children.Add(new ProgressBar { MinWidth = 110, MinHeight = 12, IsIndeterminate = true });
            var cancel = new Button { Content = "Cancel", FontSize = 10, Padding = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Right };
            cancel.Click += (s, e) => cts.Cancel();
            wrap.Children.Add(cancel);
            action = wrap;
        }
        else if (!installed && m.Availability == "blocked")
        {
            action = new TextBlock
            {
                Text = string.IsNullOrEmpty(m.Reason) ? "Unavailable" : $"Unavailable ({m.Reason})",
                FontSize = 9,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 130,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            Button btn;
            if (!installed)
            {
                // Install does double duty: download + select (AAC users pay
                // a high cost per click — browse-then-commit is wrong here).
                btn = new Button
                {
                    Content = speakable ? $"Install {FormatSize(m.FileSizeMb)}" : "Pending audio fix",
                    FontSize = 10,
                    Padding = new Thickness(6, 2),
                    IsEnabled = speakable,
                };
                btn.Click += async (s, e) => await InstallAndUse(m, btn);
            }
            else if (selected)
            {
                btn = new Button { Content = "Remove", FontSize = 10, Padding = new Thickness(6, 2) };
                btn.Click += (s, e) => Remove(m);
            }
            else
            {
                btn = new Button { Content = "Use", FontSize = 10, Padding = new Thickness(6, 2), IsEnabled = speakable };
                btn.Click += (s, e) => Select(m);
            }
            action = btn;
        }
        DockPanel.SetDock(action, Dock.Right);
        action.SetValue(MarginProperty, new Thickness(12, 0, 0, 0));
        action.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        row.Children.Add(action);

        // Left: title + subtitle (in-use carried here, bold + colour)
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = selected ? $"{DisplayName(m)} · In use" : DisplayName(m),
            FontSize = 12,
            FontWeight = selected ? FontWeight.Bold : FontWeight.Medium,
        };
        if (selected) title.Foreground = Brushes.SteelBlue;
        info.Children.Add(title);
        info.Children.Add(new TextBlock
        {
            Text = BuildSubtitle(m),
            FontSize = 10,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        });
        row.Children.Add(info);
        return row;
    }

    private void Select(TtsLocalModel m)
    {
        try
        {
            _models.Select(m.Id);
            _onSelectionChanged();
            RefreshCatalogue();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private void Remove(TtsLocalModel m)
    {
        try
        {
            _models.Uninstall(m.Id);
            if (m.Id == _models.CurrentModelId)
            {
                // Removing the in-use model clears the selection.
                _svc.Credentials.Remove("ModelId");
                _svc.SelectedVoiceId = null;
                _svc.InvalidateClient();
                _svc.SaveSettings();
                _onSelectionChanged();
            }
            RefreshCatalogue();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private async Task InstallAndUse(TtsLocalModel m, Button button)
    {
        button.IsEnabled = false;
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _downloads[m.Id] = cts;
        RefreshList(); // show progress row
        try
        {
            var progress = new Progress<(double F, string S)>(p =>
            {
                if (_downloads.TryGetValue(m.Id, out _))
                    SetStatus($"{DisplayName(m)} — {p.F:P0} {p.S}");
            });
            await _models.InstallAsync(m.Id, progress, cts.Token);
            _models.Select(m.Id);
            _onSelectionChanged();
            SetStatus(null);
            RefreshCatalogue();
        }
        catch (OperationCanceledException)
        {
            SetStatus(null);
            RefreshList();
        }
        catch (Exception ex)
        {
            SetError(ex.InnerException?.Message ?? ex.Message);
            RefreshList();
        }
        finally
        {
            _downloads.Remove(m.Id, out _);
            cts.Dispose();
        }
    }

    private void RefreshCatalogue()
    {
        try
        {
            _catalogue = _models.Search("", 2000);
            _installedIds = _models.InstalledModels().Select(m => m.Id).ToHashSet();

            var families = new List<string> { AllFamilies };
            families.AddRange(_catalogue.Select(m => m.ModelType).Where(t => !string.IsNullOrEmpty(t)).Distinct().OrderBy(t => t));
            _familyFilter!.Items.Clear();
            foreach (var f in families) _familyFilter.Items.Add(f);
            _familyFilter.SelectedIndex = 0;

            var dir = RustTtsWrapper.TtsClient.GetModelsDir(SherpaModelService.EngineId);
            _footer!.Text = $"Models are stored in {dir} · Only families verified to speak on Windows can be downloaded (others pending an upstream audio fix).";
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        RefreshList();
    }

    private void RefreshList()
    {
        if (_list == null) return;
        var family = _familyFilter?.SelectedItem as string;
        var installedOnly = _installedOnly?.IsChecked == true;

        var filtered = _catalogue.Where(m =>
        {
            if (installedOnly && !_installedIds.Contains(m.Id) && !_downloads.ContainsKey(m.Id)) return false;
            if (family is { } f && f != AllFamilies && !string.Equals(m.ModelType, f, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrWhiteSpace(_search))
            {
                var s = _search.Trim();
                var langs = string.Join(" ", (m.Languages ?? []).Select(l => $"{l.Code} {l.Name}"));
                if (!(m.Name?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !(m.Id?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !langs.Contains(s, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }).ToList();

        _list.ItemsSource = filtered;
        var speakable = filtered.Count(m => SherpaSynthesisCapability.IsSpeakable(m.ModelType));
        _status!.Text = filtered.Count == 0 ? "No matching voices"
            : $"{filtered.Count} voices · {speakable} ready to download";
    }

    private void SetStatus(string? text) => _status!.Text = text ?? "";
    private void SetError(string message) => _status!.Text = message;

    internal static string DisplayName(TtsLocalModel m)
    {
        var name = (m.Name ?? "").Replace('_', ' ').Trim();
        if (string.IsNullOrEmpty(name) || name.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return m.ModelType?.ToUpperInvariant() ?? m.Id;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    internal static string BuildSubtitle(TtsLocalModel m)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(m.ModelType)) parts.Add(char.ToUpperInvariant(m.ModelType[0]) + m.ModelType[1..]);
        var langs = (m.Languages ?? [])
            .Where(l => !string.IsNullOrEmpty(l.Name))
            .Select(l => l.Name!)
            .Distinct().Take(4).ToList();
        if (langs.Count > 0) parts.Add(string.Join(", ", langs));
        if (!string.IsNullOrEmpty(m.Quality) && m.Quality != "unknown") parts.Add(m.Quality);
        if (m.FileSizeMb > 0) parts.Add(FormatSize(m.FileSizeMb));
        if (m.NumSpeakers > 1) parts.Add($"{m.NumSpeakers} speakers");
        if (!string.IsNullOrEmpty(m.License)) parts.Add(m.License);
        return string.Join(" · ", parts);
    }

    internal static string FormatSize(double mb) => mb >= 1024 ? $"{mb / 1024:0.#} GB" : $"{mb:0.#} MB";
}
