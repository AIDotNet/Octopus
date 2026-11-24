using Octopus.Acp.Process;

namespace Octopus.Acp.Tests;

public class BatchShimResolverTests
{
    private const string NpmJsShim = """
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0

        IF EXIST "%dp0%\node.exe" (
          SET "_prog=%dp0%\node.exe"
        ) ELSE (
          SET "_prog=node"
          SET PATHEXT=%PATHEXT:;.JS;=;%
        )

        endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\node_modules\@anthropic-ai\claude-code\cli.js" %*
        """;

    private const string NpmExeShim = """
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0
        "%dp0%\node_modules\@anthropic-ai\claude-code\bin\claude.exe"   %*
        """;

    [Fact]
    public void Parses_js_target_of_npm_shim_skipping_node_exe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "npm");
        var target = BatchShimResolver.ParseTarget(NpmJsShim, dir);
        Assert.Equal(Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js"), target);
    }

    [Fact]
    public void Parses_native_exe_target_of_npm_shim()
    {
        var dir = Path.Combine(Path.GetTempPath(), "npm");
        var target = BatchShimResolver.ParseTarget(NpmExeShim, dir);
        Assert.Equal(Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"), target);
    }

    [Fact]
    public void Returns_null_for_unrecognised_batch()
    {
        Assert.Null(BatchShimResolver.ParseTarget("@echo off\r\nclaude.exe %*\r\n", "C:\\x"));
    }

    [Fact]
    public void Resolve_returns_existing_target_and_leaves_non_batch_untouched()
    {
        var dir = Directory.CreateTempSubdirectory("octopus-shim-").FullName;
        try
        {
            var js = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(js)!);
            File.WriteAllText(js, "");
            var shim = Path.Combine(dir, "claude.cmd");
            File.WriteAllText(shim, NpmJsShim);

            Assert.Equal(js, BatchShimResolver.Resolve(shim));
            Assert.Equal("/usr/bin/claude", BatchShimResolver.Resolve("/usr/bin/claude"));
            Assert.Equal(Path.Combine(dir, "missing.cmd"), BatchShimResolver.Resolve(Path.Combine(dir, "missing.cmd")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
