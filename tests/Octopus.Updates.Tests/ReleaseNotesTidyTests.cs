using Octopus.Updates;
using Xunit;

namespace Octopus.Updates.Tests;

public sealed class ReleaseNotesTidyTests
{
    [Fact]
    public void GeneratedNotesLoseTheNoiseAndKeepTheLinks()
    {
        var tidy = ReleaseNotesTidy.Apply("""
            ## What's Changed
            * Release: pass Version to the Android job's dotnet calls by @239573049 in https://github.com/AIDotNet/Octopus/pull/4
            * Usage history: export a bill (#5) by @239573049 in https://github.com/AIDotNet/Octopus/pull/6

            **Full Changelog**: https://github.com/AIDotNet/Octopus/compare/v1.0.1122...v1.0.1123
            """);
        Assert.Equal(
            "* Release: pass Version to the Android job's dotnet calls ([#4](https://github.com/AIDotNet/Octopus/pull/4))\n" +
            "* Usage history: export a bill (#5) ([#6](https://github.com/AIDotNet/Octopus/pull/6))\n\n" +
            "[Full changelog](https://github.com/AIDotNet/Octopus/compare/v1.0.1122...v1.0.1123)",
            tidy);
    }

    [Fact]
    public void NotesWithOnlyTheChangelogLinkComeOutEmpty()
    {
        Assert.Equal("", ReleaseNotesTidy.Apply("""
            ## What's Changed

            **Full Changelog**: https://github.com/AIDotNet/Octopus/compare/v1.0.1131...v1.0.1132
            """));
    }

    [Fact]
    public void CommitNotesKeepTheirSectionsAndLinks()
    {
        var notes = """
            ### New

            - Add immediate send ([#20](https://github.com/AIDotNet/Octopus/pull/20))

            **Full Changelog**: https://github.com/AIDotNet/Octopus/compare/v1.0.1132...v1.0.1133
            """;
        Assert.Equal(
            "### New\n\n- Add immediate send ([#20](https://github.com/AIDotNet/Octopus/pull/20))\n\n" +
            "[Full changelog](https://github.com/AIDotNet/Octopus/compare/v1.0.1132...v1.0.1133)",
            ReleaseNotesTidy.Apply(notes));
    }

    [Fact]
    public void NotesWrittenByHandPassThrough()
    {
        Assert.Equal("### New\n\n- a thing by @someone in prose", ReleaseNotesTidy.Apply("### New\r\n\r\n- a thing by @someone in prose\r\n"));
    }
}
