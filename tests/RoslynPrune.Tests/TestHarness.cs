// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynPrune.Models;

namespace RoslynPrune.Tests;

/// <summary>
/// Drives the public prune pipeline from test code. Copies a fixture to a temp
/// directory (so <c>--apply</c> never mutates the checked-in fixture), loads it
/// via <see cref="WorkspaceLoader"/>, and runs <see cref="PruneOrchestrator"/>.
/// </summary>
internal static class TestHarness
{
    /// <summary>
    /// Runs a prune pass over a fixture and returns the report plus the
    /// rewritten file contents (keyed by file name) when <paramref name="apply"/>
    /// is true.
    /// </summary>
    public static async Task<(PruneReport Report, IReadOnlyDictionary<string, string> Rewritten)> RunAsync(
        string fixtureName,
        IReadOnlyList<string> fileGlobs,
        IReadOnlyList<string> typeGlobs,
        bool apply,
        CancellationToken ct = default)
    {
        var tempDir = CopyFixtureToTemp(fixtureName);
        try
        {
            var projectPath = Path.Combine(tempDir, fixtureName + ".csproj");
            var (solution, _) = await WorkspaceLoader.LoadAsync(projectPath, null, ct).ConfigureAwait(false);

            var orchestrator = new PruneOrchestrator();
            var report = await orchestrator.RunAsync(solution, fileGlobs, typeGlobs, apply, null, ct).ConfigureAwait(false);

            var rewritten = new Dictionary<string, string>();
            if (apply)
            {
                foreach (var file in report.WrittenFiles)
                {
                    // Deleted files (emptied by pruning) no longer exist on disk;
                    // skip them — their absence is asserted by the test.
                    if (!File.Exists(file))
                    {
                        continue;
                    }
                    rewritten[Path.GetFileName(file)] = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                }
            }
            return (report, rewritten);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// Copies a fixture directory (from the bin output) into a fresh temp
    /// directory and returns the temp path.
    /// </summary>
    private static string CopyFixtureToTemp(string fixtureName)
    {
        var source = Path.Combine(TestPaths.FixturesDir, fixtureName);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Fixture not found: {source}");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "RoslynPrune.Tests", fixtureName + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var dest = Path.Combine(tempDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return tempDir;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; ignore failures (e.g. locked files).
        }
    }
}
