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

    [AvaloniaFact]
    public void A_chinese_alias_and_the_protocol_are_in_the_draft()
    {
        var form = new SshHostFormView(new SshHostDraft("生产服务器", "10.0.0.5", Protocol: SshHostProtocol.Mosh));

        Assert.Equal(new SshHostDraft("生产服务器", "10.0.0.5", Protocol: SshHostProtocol.Mosh), form.Draft);
        Assert.Equal(SshHostProtocol.Ssh, new SshHostFormView(null).Protocol);
    }

    [AvaloniaFact]
    public void The_alias_of_a_host_being_edited_cannot_be_typed_over_and_telnet_hides_the_fields_it_does_not_use()
    {
        var edit = new SshHostFormView(new SshHostDraft("box"), editing: true);
        var telnet = new SshHostFormView(new SshHostDraft("路由器", Protocol: SshHostProtocol.Telnet));

        Assert.False(edit.FindControl<Input>("AliasInput")!.IsEnabled);
        Assert.True(new SshHostFormView(null).FindControl<Input>("AliasInput")!.IsEnabled);
        Assert.False(telnet.FindControl<Input>("UserInput")!.IsVisible);
        Assert.False(telnet.FindControl<Input>("IdentityFileInput")!.IsVisible);
        Assert.False(telnet.FindControl<Input>("ProxyJumpInput")!.IsVisible);
        Assert.True(telnet.FindControl<Input>("PortInput")!.IsVisible);
    }

    private static void Click(SshHostFormView form) =>
        form.FindControl<TKCode.Arc.Button>("TestButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

    [AvaloniaFact]
    public async Task The_test_button_tries_what_is_typed_and_shows_the_answer()
    {
        SshHostDraft? tried = null;
        var form = new SshHostFormView(new SshHostDraft("盒子", "10.0.0.5", Protocol: SshHostProtocol.Mosh), tester: (draft, _) =>
        {
            tried = draft;
            return Task.FromResult(new SshTestResult(SshTestOutcome.Connected));
        });
        Assert.False(form.FindControl<Alert>("TestResult")!.IsVisible);

        Click(form);
        await Task.Delay(50);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(new SshHostDraft("盒子", "10.0.0.5", Protocol: SshHostProtocol.Mosh), tried);
        var result = form.FindControl<Alert>("TestResult")!;
        Assert.True(result.IsVisible);
        Assert.Equal(AlertTone.Success, result.Tone);
        Assert.True(form.FindControl<TKCode.Arc.Button>("TestButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task A_failed_test_says_what_went_wrong_in_danger_colors()
    {
        var form = new SshHostFormView(new SshHostDraft("box"), tester: (_, _) =>
            Task.FromResult(new SshTestResult(SshTestOutcome.Failed, "Connection refused")));

        Click(form);
        await Task.Delay(50);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var result = form.FindControl<Alert>("TestResult")!;
        Assert.Equal(AlertTone.Danger, result.Tone);
        Assert.Contains("Connection refused", result.Content?.ToString());
    }

    [AvaloniaFact]
    public async Task A_draft_that_is_not_valid_is_not_tested_and_its_problems_are_shown()
    {
        var tests = 0;
        var form = new SshHostFormView(new SshHostDraft("box", Port: "70000"), tester: (_, _) =>
        {
            tests++;
            return Task.FromResult(new SshTestResult(SshTestOutcome.Connected));
        });

        Click(form);
        await Task.Delay(50);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, tests);
        Assert.False(string.IsNullOrEmpty(form.FindControl<Input>("PortInput")!.ErrorMessage));
        Assert.False(form.FindControl<Alert>("TestResult")!.IsVisible);
    }

    [AvaloniaFact]
    public void Without_a_tester_there_is_no_test_button()
    {
        Assert.False(new SshHostFormView(null).FindControl<StackPanel>("TestRow")!.IsVisible);
    }
}
