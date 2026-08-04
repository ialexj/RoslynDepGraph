// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace RoslynSourceMap.Tests;

/// <summary>
/// Resolves on-disk paths to fixture files relative to the running test
/// assembly. Walks up from <see cref="AppContext.BaseDirectory"/> to the repo
/// root so tests work regardless of the current working directory.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Absolute path to the <c>tests/RoslynSourceMap.Tests/Fixtures</c> directory.
    /// Fixture files are copied here at build time (see the test .csproj
    /// <c>CopyToOutputDirectory</c> entries), so this resolves under the bin output.
    /// </summary>
    public static string FixturesDir => _fixturesDir.Value;

    /// <summary>
    /// Absolute path to a fixture file under <see cref="FixturesDir"/>.
    /// </summary>
    public static string FixtureFile(string fileName)
    {
        var path = Path.Combine(FixturesDir, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Could not find fixture file '{fileName}' at '{path}'.", path);
        }
        return path;
    }

    private static readonly Lazy<string> _fixturesDir = new(FindFixturesDir);

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
                "Ensure the fixture .cs files are included with " +
                "CopyToOutputDirectory in the test project.");
        }
        return path;
    }
}
