using System.Runtime.ExceptionServices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// A view whose bindings are compiled for one view model must never see another one, even for a moment: the binding
/// then throws an InvalidCastException that Avalonia swallows, which stops a debugger that breaks on thrown exceptions.
/// </summary>
public class BindingCastTests
{
    [AvaloniaFact]
    public void Opening_the_window_and_its_panels_throws_no_cast_exception()
    {
        var thrown = new List<string>();
        void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is InvalidCastException) thrown.Add(e.Exception.Message);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            var dir = Directory.CreateTempSubdirectory("octopus-cast-").FullName;
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
            var window = new MainWindow();
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var shell = AppHost.Get<ShellViewModel>();
            AppHost.Get<IProjectService>().AddProject(dir);
            Dispatcher.UIThread.RunJobs();
            shell.ToggleRightPanelCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            shell.ToggleSidebarCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            shell.ToggleSidebarCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        Assert.True(thrown.Count == 0, string.Join("\n", thrown.Distinct()));
    }
}
