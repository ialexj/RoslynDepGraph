// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace RoslynDepGraph.Tests;

/// <summary>
/// Resolves on-disk paths to fixture projects (the repo-owned DepSample sample
/// and the test-owned Fixtures/* projects) relative to the running test assembly.
/// Walks up from <see cref="AppContext.BaseDirectory"/> to the repo root so tests
/// work regardless of the current working directory.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Absolute path to <c>samples/DepSample/DepSample.csproj</c>.
    /// </summary>
    public static string SampleProject => _sampleProject.Value;

    /// <summary>
    /// Absolute path to the <c>tests/RoslynDepGraph.Tests/Fixtures</c> directory.
    /// Fixture projects are copied here at build time (see the test .csproj
    /// <c>CopyToOutputDirectory</c> entries), so this resolves under the bin output.
    /// </summary>
    public static string FixturesDir => _fixturesDir.Value;

    private static readonly Lazy<string> _sampleProject = new(FindSampleProject);
    private static readonly Lazy<string> _fixturesDir = new(FindFixturesDir);

    private static string FindSampleProject()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "samples", "DepSample", "DepSample.csproj");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Could not find DepSample.csproj at '{path}'. " +
                "Ensure the test is run from a built checkout of the repo.",
                path);
        }
        return path;
    }

    private static string FindFixturesDir()
    {
        // Fixtures are copied into the bin output via CopyToOutputDirectory, so
        // resolve relative to the test assembly location.
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Fixtures");
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"Could not find Fixtures directory at '{path}'. " +
                "Ensure the fixture .csproj/.cs files are included with " +
                "CopyToOutputDirectory in the test project.");
        }
        return path;
    }

    /// <summary>
    /// Absolute path to a fixture project under <see cref="FixturesDir"/>.
    /// </summary>
    public static string FixtureProject(string fixtureName)
    {
        var path = Path.Combine(FixturesDir, fixtureName, $"{fixtureName}.csproj");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Could not find fixture project '{fixtureName}' at '{path}'.", path);
        }
        return path;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RoslynDepGraph.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate repo root (RoslynDepGraph.slnx or .git) " +
            $"starting from '{AppContext.BaseDirectory}'.");
    }
}
