// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynDepGraph.Models;
using Xunit;

namespace RoslynDepGraph.Tests;

/// <summary>
/// Tests for the --transitive closure (TransitiveClosure.ComputeAsync): a per-node
/// BFS with a visited set keyed by Dependency.Id, cycle-safe, flagging newly-
/// discovered edges with Direct=false. Direct edges win on dedup.
/// </summary>
public class TransitiveClosureTests
{
    private static async Task<DependencyReport> BuildChainAsync(ReportOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadFixtureAsync("ChainFixture", cts.Token);
        return await ReportTestHarness.BuildForFixtureAsync(
            "ChainFixture", solution, options, cts.Token);
    }

    private static async Task<DependencyReport> BuildCycleAsync(ReportOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadFixtureAsync("CycleFixture", cts.Token);
        return await ReportTestHarness.BuildForFixtureAsync(
            "CycleFixture", solution, options, cts.Token);
    }

    [Fact]
    public async Task Without_transitive_C_has_only_direct_edges()
    {
        // ChainFixture: C -> B -> A. Without --transitive, C's edges are direct only
        // (B and b); A is NOT reachable in the direct view.
        var report = await BuildChainAsync(ReportTestHarness.DefaultOptions());

        Assert.Equal(0, report.Summary.TransitiveEdges);
        Assert.Equal(report.Summary.EdgeCount, report.Summary.DirectEdges);

        var c = ReportTestHarness.Node(report, "C");
        // Direct edges only: type B, member b. No A.
        Assert.True(ReportTestHarness.HasEdgeTo(c, "B"));
        Assert.True(ReportTestHarness.HasEdgeTo(c, "b"));
        Assert.False(ReportTestHarness.HasEdgeTo(c, "A"));
        // Without --transitive, every edge on C is direct.
        Assert.All(c.Edges.Types.Concat(c.Edges.Members), d => Assert.True(d.Direct));
    }

    [Fact]
    public async Task With_transitive_C_includes_indirect_edges_flagged_not_direct()
    {
        // With --transitive, C's closure expands B's edges, so A/a/DoA appear as
        // transitive (Direct=false) edges on C. Direct edges (B, b) stay Direct=true.
        var options = ReportTestHarness.DefaultOptions() with { Transitive = true };
        var report = await BuildChainAsync(options);

        Assert.True(report.Summary.TransitiveEdges > 0);
        Assert.True(report.Summary.DirectEdges > 0);

        var c = ReportTestHarness.Node(report, "C");

        // Direct edges remain direct.
        var directB = c.Edges.Types.FirstOrDefault(d => d.Name == "B");
        Assert.NotNull(directB);
        Assert.True(directB!.Direct);

        // Transitive edges appear and are flagged Direct=false.
        var transitiveA = c.Edges.Types.FirstOrDefault(d => d.Name == "A");
        Assert.NotNull(transitiveA);
        Assert.False(transitiveA!.Direct);

        Assert.True(ReportTestHarness.HasEdgeTo(c, "a"));
        Assert.True(ReportTestHarness.HasEdgeTo(c, "DoA"));
        Assert.All(c.Edges.Types.Concat(c.Edges.Members).Where(d => d.Name is "A" or "a" or "DoA"),
            d => Assert.False(d.Direct));
    }

    [Fact]
    public async Task Cycle_does_not_hang_or_duplicate_edges()
    {
        // CycleFixture: A <-> B. The closure must terminate (visited set) and must
        // not emit duplicate edge ids per node.
        var options = ReportTestHarness.DefaultOptions() with { Transitive = true };
        var report = await BuildCycleAsync(options);

        // If the closure failed to terminate, the 30s token would have cancelled
        // and this assertion (or the build) would have thrown. Reaching here means
        // the cycle was handled safely.
        Assert.NotEmpty(report.Nodes);

        foreach (var node in report.Nodes)
        {
            var allIds = ReportTestHarness.EdgeIds(node);
            Assert.Equal(allIds.Count, allIds.Distinct().Count());
        }
    }

    [Fact]
    public async Task Cycle_produces_transitive_self_edge()
    {
        // Spec/implementation divergence (spec section 7 says "a symbol does not
        // depend on itself", but that rule is only enforced for DIRECT edges).
        // In a cycle A -> B -> A, node A's closure re-expands A (reached via B)
        // and adds A -> A with Direct=false. This test pins the current behavior;
        // if the implementation is changed to suppress transitive self-edges, it
        // should be updated to assert the absence instead.
        var options = ReportTestHarness.DefaultOptions() with { Transitive = true };
        var report = await BuildCycleAsync(options);

        var a = ReportTestHarness.Node(report, "A");
        var selfTransitive = a.Edges.Types.FirstOrDefault(d => d.Name == "A");
        Assert.NotNull(selfTransitive);
        Assert.False(selfTransitive!.Direct);
    }
}
