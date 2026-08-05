// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace RoslynPrune.Tests;

/// <summary>
/// Resolves on-disk paths for test fixtures.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// The directory containing the copied fixture projects (under the test
    /// bin output, populated by the csproj's CopyToOutputDirectory entries).
    /// </summary>
    public static string FixturesDir => _fixturesDir.Value;

    /// <summary>
    /// The path to a fixture's .csproj by name.
    /// </summary>
    public static string FixtureProject(string fixtureName)
    {
        var path = Path.Combine(FixturesDir, fixtureName, fixtureName + ".csproj");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture project not found: {path}");
        }
        return path;
    }

    private static readonly Lazy<string> _fixturesDir = new(FindFixturesDir);

    private static string FindFixturesDir()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Fixtures directory not found: {path}");
        }
        return path;
    }
}
