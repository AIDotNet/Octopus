using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Octopus.Git;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Octopus.App.Explorer.Git;

/// <summary>The one-letter git status (M, A, D, R, U, ?) in the color of its meaning; empty when <see cref="Kind"/> is null.</summary>
public sealed class GitStatusLetter : TextBlock
{
    public static readonly StyledProperty<GitChangeKind?> KindProperty =
        AvaloniaProperty.Register<GitStatusLetter, GitChangeKind?>(nameof(Kind));

    private IDisposable? _binding;

    static GitStatusLetter()
    {
        KindProperty.Changed.AddClassHandler<GitStatusLetter>((letter, _) => letter.Update());
    }

    public GitStatusLetter()
    {
        FontWeight = FontWeight.SemiBold;
        ArcTokens.Bind(this, FontSizeProperty, ArcDesignTokenKind.TypeXS);
        Update();
    }

    public GitChangeKind? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private void Update()
    {
        _binding?.Dispose();
        _binding = null;
        if (Kind is not { } kind)
        {
            Text = "";
            IsVisible = false;
            return;
        }
        Text = GitKindInfo.Letter(kind);
        IsVisible = true;
        var token = kind switch
        {
            GitChangeKind.Modified => ArcDesignTokenKind.Warning,
            GitChangeKind.Added or GitChangeKind.Untracked => ArcDesignTokenKind.Success,
            GitChangeKind.Deleted or GitChangeKind.Conflicted => ArcDesignTokenKind.Danger,
            _ => ArcDesignTokenKind.Accent,
        };
        _binding = ArcTokens.Bind(this, ForegroundProperty, token);
    }
}
