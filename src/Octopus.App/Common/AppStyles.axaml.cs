using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Octopus.App.Common;

/// <summary>The app's shared styles (AppStyles.axaml).</summary>
public partial class AppStyles : Styles
{
    public AppStyles() => AvaloniaXamlLoader.Load(this);
}
