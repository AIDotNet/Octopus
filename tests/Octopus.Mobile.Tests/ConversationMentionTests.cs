using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Octopus.App.Common.Mentions;
using Octopus.Mobile.Conversations;
using Octopus.Mobile.Demo;
using TKCode.Arc.Mobile;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>Inline @session / @agent mentions in the phone's composer (the desktop composer's rules, shared).</summary>
public class ConversationMentionTests
{
    private static (ConversationPage Page, ConversationViewModel ViewModel) OpenDemoConversation(Phone phone)
    {
        phone.Workspace.ConversationStep = TimeSpan.FromMilliseconds(40);
        var station = phone.Workspace.Hosts.Single(host => host.Id == "station");
        var gui = station.Projects[0].Sessions.Single(session => session.IsGui);
        phone.Shell.ShowHost(station);
        phone.Shell.ShowSession(gui);
        Assert.True(Phone.Until(() => phone.Top<ConversationPage>().DataContext is ConversationViewModel { IsLoaded: true }));
        var page = phone.Top<ConversationPage>();
        return (page, (ConversationViewModel)page.DataContext!);
    }

    [AvaloniaFact]
    public void At_button_and_typed_queries_write_several_inline_tokens_that_travel_with_the_prompt()
    {
        using var phone = new Phone();
        var (page, vm) = OpenDemoConversation(phone);
        var field = page.FindControl<TextField>("PromptField")!;

        Assert.True(vm.SupportsMentions);
        Assert.True(page.FindControl<Control>("MentionButton")!.IsVisible);

        // The @ button types the @ (a phone keyboard needs a layer switch for it) and the strip offers targets.
        vm.PromptText = "请";
        vm.PromptCaretIndex = 1;
        vm.StartMentionCommand.Execute(null);
        Assert.Equal("请 @", vm.PromptText);
        Assert.True(Phone.Until(() => vm.IsMentionStripOpen));
        vm.InsertMentionCommand.Execute(vm.MentionSuggestions.First(t => t.TargetId == "claude"));
        Assert.Equal("请 @Claude Code ", vm.PromptText);
        Assert.False(vm.IsMentionStripOpen);

        // A second one typed by hand later in the sentence: several targets in one prompt.
        vm.PromptText += "和 @co";
        vm.PromptCaretIndex = vm.PromptText.Length;
        Assert.True(Phone.Until(() => vm.IsMentionStripOpen));
        vm.InsertMentionCommand.Execute(vm.MentionSuggestions.First(t => t.TargetId == "codex"));
        vm.PromptText += "一起看看";
        Phone.Pump();

        Assert.Equal(vm.PromptText, field.Text);
        Assert.Equal(2, MentionEditing.GetSpans(field).Count);
        Assert.Contains(TopLevel.GetTopLevel(page)!.GetVisualDescendants(), v => v is MentionHighlightLayer);

        vm.SendCommand.Execute(null);
        var demo = (DemoWorkspace.DemoConversation)vm.Conversation!;
        Assert.True(Phone.Until(() => demo.Sent.Count > 0));
        Assert.Equal("请 @Claude Code 和 @Codex 一起看看", demo.Sent[^1]);
        Assert.Equal(["claude", "codex"], demo.SentMentions[^1].Select(m => m.TargetId).ToArray());
        Assert.Empty(vm.MentionTokens);
    }
}
