using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Dialogs;
using Octopus.Ssh;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>The SSH host form: what was typed becomes the draft, and each problem is shown under its field.</summary>
public class SshHostFormTests
{
    [AvaloniaFact]
    public void The_draft_is_what_was_typed_with_empty_optional_fields_left_out()
    {
        var form = new SshHostFormView(new SshHostDraft(" box ", "10.0.0.5", " ", "2222", "~/.ssh/my key", null));

        Assert.Equal(new SshHostDraft("box", "10.0.0.5", null, "2222", "~/.ssh/my key", null), form.Draft);
    }

    [AvaloniaFact]
    public void Each_problem_is_shown_under_its_own_field_and_the_others_are_cleared()
    {
        var form = new SshHostFormView(null);
        var draft = new SshHostDraft("prod", Port: "70000", User: "two words");

        form.ShowIssues(draft.Validate(["prod"]));

        Assert.False(string.IsNullOrEmpty(form.FindControl<Input>("AliasInput")!.ErrorMessage));
        Assert.False(string.IsNullOrEmpty(form.FindControl<Input>("PortInput")!.ErrorMessage));
        Assert.False(string.IsNullOrEmpty(form.FindControl<Input>("UserInput")!.ErrorMessage));
        Assert.Null(form.FindControl<Input>("HostNameInput")!.ErrorMessage);

        form.ShowIssues([]);
        Assert.Null(form.FindControl<Input>("AliasInput")!.ErrorMessage);
        Assert.Null(form.FindControl<Input>("PortInput")!.ErrorMessage);
    }
}
