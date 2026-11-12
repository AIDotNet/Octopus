using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Shell;
using Xunit;

namespace Octopus.App.Tests;

public class ShellTests
{
    [AvaloniaFact]
    public void Main_window_opens()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
    }
}
