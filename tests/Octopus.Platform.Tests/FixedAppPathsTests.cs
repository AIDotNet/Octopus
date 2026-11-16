using Octopus.Platform;
using Xunit;

namespace Octopus.Platform.Tests;

/// <summary>OCTOPUS_DATA_DIR: a second instance keeps its files in a folder of its own.</summary>
public class FixedAppPathsTests
{
    [Fact]
    public void Data_and_cache_live_under_the_given_folder_and_are_created_on_first_use()
    {
        var root = Path.Combine(Directory.CreateTempSubdirectory("octopus-paths-").FullName, "second", "instance");
        var paths = new FixedAppPaths(root);
        Assert.False(Directory.Exists(root));

        Assert.Equal(Path.GetFullPath(root), paths.DataDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "cache"), paths.CacheDirectory);
        Assert.True(Directory.Exists(paths.CacheDirectory));
    }

    [Fact]
    public void A_relative_folder_is_taken_from_the_current_directory()
    {
        var paths = new FixedAppPaths(Path.Combine("octopus-relative-test", "x"));
        try
        {
            Assert.True(Path.IsPathRooted(paths.DataDirectory));
            Assert.StartsWith(Directory.GetCurrentDirectory(), paths.DataDirectory);
        }
        finally
        {
            Directory.Delete(Path.Combine(Directory.GetCurrentDirectory(), "octopus-relative-test"), recursive: true);
        }
    }

    [Fact]
    public void Without_the_variable_the_platforms_own_places_are_used()
    {
        // The test process is not started with it.
        Assert.Null(Environment.GetEnvironmentVariable(FixedAppPaths.Variable));
        Assert.Null(FixedAppPaths.FromEnvironment());
    }
}
