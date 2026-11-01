using System;
using Avalonia.Markup.Xaml;

namespace Dasher.Windows.Services;

/// <summary>
/// AXAML form of Loc.Tr (RFC 0003): Text="{loc:Tr Key=keyboard}". Keys are
/// shared-catalogue keys; the neutral (English) resource is the fallback, so
/// no English literal is needed at the call site. Evaluated when the XAML
/// loads (InitializeComponent) — safe because the UI culture is OS-set from
/// process start and there is no runtime language switch (RFC 0003 "one
/// locale" principle: the system language is the single source of truth).
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public string? Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Loc.Tr(Key ?? string.Empty);
}
