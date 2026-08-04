// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynDepGraph.Models;
using Xunit;

namespace RoslynDepGraph.Tests;

/// <summary>
/// Regression guard for the parallelization in <see cref="ReportBuilder.BuildAsync"/>
/// (Parallel.ForEachAsync with pre-allocated indexed slots) and the per-project
/// Compilation cache (ConcurrentDictionary). The extracted edge set must be
/// deterministic and correct regardless of parallelism: running the build N times
/// against the same inputs must yield byte-identical output (after normalizing the
/// non-deterministic generatedAt timestamp).
/// </summary>
/// <remarks>
/// This test would have caught ordering bugs had the implementation collected
/// results into a ConcurrentBag or otherwise failed to preserve the candidate
/// order. The comparison is on the full normalized JSON document plus the
/// in-memory node-id and per-node edge-id sequences, so any reordering of nodes
/// or edges would surface as a diff.
/// </remarks>
public class DeterminismTests
{
    private const int DefaultRuns = 5;
    private const int IncludeEmptyRuns = 3;

    [Fact]
    public async Task Default_options_is_deterministic_across_runs()
    {
        var first = await BuildOnceAsync(ReportTestHarness.DefaultOptions());
        var firstJson = ReportTestHarness.NormalizedJson(first);
        var firstNodeIds = NodeIdSequence(first);

        for (var i = 1; i < DefaultRuns; i++)
        {
            var run = await BuildOnceAsync(ReportTestHarness.DefaultOptions());
            var runJson = ReportTestHarness.NormalizedJson(run);

            Assert.Equal(firstJson, runJson);
            Assert.Equal(firstNodeIds, NodeIdSequence(run));
        }
    }

    [Fact]
    public async Task IncludeEmpty_is_deterministic_across_runs()
    {
        // The empty-node path has an early-return under parallelism (slot left null
        // when !IncludeEmpty); exercise the IncludeEmpty=true branch too.
        var options = ReportTestHarness.DefaultOptions() with { IncludeEmpty = true };
        var first = await BuildOnceAsync(options);
        var firstJson = ReportTestHarness.NormalizedJson(first);
        var firstNodeIds = NodeIdSequence(first);

        for (var i = 1; i < IncludeEmptyRuns; i++)
        {
            var run = await BuildOnceAsync(options);
            Assert.Equal(firstJson, ReportTestHarness.NormalizedJson(run));
            Assert.Equal(firstNodeIds, NodeIdSequence(run));
        }
    }

    private static async Task<DependencyReport> BuildOnceAsync(ReportOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadSampleAsync(cts.Token);
        return await ReportTestHarness.BuildAsync(solution, options, cts.Token);
    }

    /// <summary>
    /// A structural projection of the report: for each node, its id followed by
    /// the ids of its type and member edges (in reported order). Any reordering of
    /// nodes or edges changes this sequence even if the JSON happens to re-serialize
    /// identically.
    /// </summary>
    private static IReadOnlyList<string> NodeIdSequence(DependencyReport report)
    {
        var seq = new List<string>();
        foreach (var node in report.Nodes)
        {
            seq.Add(node.Id);
            seq.AddRange(ReportTestHarness.EdgeIds(node));
        }
        return seq;
    }
}
