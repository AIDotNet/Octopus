using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Arc;

namespace Octopus.App.Files.Code;

/// <summary>The read-only <see cref="CodeView"/> inside its scroll area, driven by plain properties so any viewer can embed it.</summary>
public partial class CodeSurface : UserControl
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<CodeSurface, string>(nameof(Text), "");
    public static readonly StyledProperty<string> LanguageProperty = AvaloniaProperty.Register<CodeSurface, string>(nameof(Language), "txt");
    public static readonly StyledProperty<bool> WrapLinesProperty = AvaloniaProperty.Register<CodeSurface, bool>(nameof(WrapLines));
    public static readonly StyledProperty<double> CodeFontSizeProperty = AvaloniaProperty.Register<CodeSurface, double>(nameof(CodeFontSize), 13);
    public static readonly StyledProperty<string?> MonoFontFamilyProperty = AvaloniaProperty.Register<CodeSurface, string?>(nameof(MonoFontFamily));

    public CodeSurface() => InitializeComponent();

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Language { get => GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }
    public bool WrapLines { get => GetValue(WrapLinesProperty); set => SetValue(WrapLinesProperty, value); }
    public double CodeFontSize { get => GetValue(CodeFontSizeProperty); set => SetValue(CodeFontSizeProperty, value); }

    /// <summary>The terminal's font family name; null is the theme's monospace stack.</summary>
    public string? MonoFontFamily { get => GetValue(MonoFontFamilyProperty); set => SetValue(MonoFontFamilyProperty, value); }

    public CodeView CodeView => Code;
    public ScrollArea ScrollArea => Scroller;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) Code.Text = Text;
        else if (change.Property == LanguageProperty) Code.Language = Language;
        else if (change.Property == WrapLinesProperty) Code.WrapLines = WrapLines;
        else if (change.Property == CodeFontSizeProperty) Code.FontSize = CodeFontSize;
        else if (change.Property == MonoFontFamilyProperty)
        {
            if (MonoFontFamily is { } family) Code.FontFamily = new FontFamily(family);
            else Code.ClearValue(CodeView.FontFamilyProperty);
        }
    }
}
