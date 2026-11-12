using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Settings;
using Octopus.App.Shell;
using Xunit;

namespace Octopus.App.Tests.Settings;

/// <summary>
/// The settings page is hosted the way the shell hosts it: under a parent whose DataContext is the
/// <see cref="ShellViewModel"/>. The page's compiled bindings are typed to <see cref="SettingsViewModel"/>, so any
/// moment where it sees the inherited shell model instead throws an InvalidCastException inside the binding.
/// Avalonia swallows that, so it shows up only as a first-chance exception (a break in the debugger).
/// </summary>
public class SettingsHostingTests
{
    [AvaloniaFact]
    public void Opening_and_closing_the_settings_never_binds_a_page_to_the_shell_model()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var casts = new List<string>();
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is InvalidCastException)
            {
                casts.Add(e.Exception.Message);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            AppHost.Get<ShellViewModel>().OpenSettings("agents");
            Dispatcher.UIThread.RunJobs();
            var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
            Assert.IsType<SettingsViewModel>(view.DataContext);

            // Escape closes it: the shell removes the view and the view lets go of its model.
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.GetVisualDescendants().OfType<SettingsView>());
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        Assert.True(casts.Count == 0, "A settings binding received the wrong DataContext: " + string.Join(" | ", casts));
        window.Close();
    }
}
