using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Octopus.Mobile.Shell;

/// <summary>The phone app's shared styles (MobileStyles.axaml).</summary>
public partial class MobileStyles : Styles
{
    public MobileStyles() => AvaloniaXamlLoader.Load(this);
}
