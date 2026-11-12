using Avalonia.Controls;

namespace Octopus.App.Services;

public sealed class WindowAccessor : IWindowAccessor
{
    public TopLevel? Window { get; set; }
}
