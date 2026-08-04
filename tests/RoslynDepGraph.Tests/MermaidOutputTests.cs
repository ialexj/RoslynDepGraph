// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynDepGraph.Models;
using Xunit;

namespace RoslynDepGraph.Tests;

/// <summary>
/// Tests for <see cref="OutputFormatter.ToMermaid"/>: renders a
/// <see cref="DependencyReport"/> as a Mermaid <c>flowchart LR</c> diagram.
/// Node ids are positional (n0.. for emitted nodes, t0.. for non-emitted edge
/// targets) and assigned in report order, which is deterministic, so the tests
/// derive the expected id for each symbol from <see cref="DependencyReport.Nodes"/>
/// rather than hardcoding positions.
/// </summary>
public class MermaidOutputTests
{
    private static async Task<DependencyReport> BuildAsync(ReportOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadSampleAsync(cts.Token);
        return await ReportTestHarness.BuildAsync(solution, options, cts.Token);
    }

    private static async Task<DependencyReport> BuildChainAsync(ReportOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadFixtureAsync("ChainFixture", cts.Token);
        return await ReportTestHarness.BuildForFixtureAsync(
            "ChainFixture", solution, options, cts.Token);
    }

    /// <summary>
    /// Splits mermaid output into trimmed, non-empty lines.
    /// </summary>
    private static string[] Lines(string mermaid)
        => mermaid.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .ToArray();

    /// <summary>
    /// The mermaid id assigned to an emitted node, by name. Emitted nodes get
    /// ids n0, n1, ... in <see cref="DependencyReport.Nodes"/> order.
    /// </summary>
    private static string EmittedId(DependencyReport report, string name)
    {
        for (var i = 0; i < report.Nodes.Count; i++)
        {
            if (report.Nodes[i].Name == name)
            {
                return $"n{i}";
            }
        }
        throw new InvalidOperationException(
            $"No emitted node named '{name}'. Available: " +
            string.Join(", ", report.Nodes.Select(n => n.Name)));
    }

    [Fact]
    public async Task Header_and_flowchart_directive_are_present()
    {
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var mermaid = OutputFormatter.ToMermaid(report);
        var lines = Lines(mermaid);

        Assert.StartsWith("%% RoslynDepGraph mermaid output", lines[0]);
        Assert.Contains(lines, l => l == "flowchart LR");
        // The flowchart directive comes before any node/edge declarations.
        var flowchartIndex = System.Array.IndexOf(lines, "flowchart LR");
        Assert.True(flowchartIndex >= 0);
        Assert.All(lines.Skip(flowchartIndex + 1), l => Assert.StartsWith("    ", l));
    }

    [Fact]
    public async Task Summary_comment_echoes_report_counts()
    {
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var mermaid = OutputFormatter.ToMermaid(report);

        Assert.Contains(
            $"%% nodes: {report.Summary.NodeCount}, edges: {report.Summary.EdgeCount} " +
            $"({report.Summary.DirectEdges} direct, {report.Summary.TransitiveEdges} transitive)",
            mermaid);
    }

    [Fact]
    public async Task Type_nodes_use_stadium_shape_and_members_use_subroutine_shape()
    {
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var mermaid = OutputFormatter.ToMermaid(report);

        // B is a type node -> stadium shape ([...]).
        Assert.Contains("([\"B (type, global)\"])", mermaid);
        // M and member are member nodes -> subroutine shape ([[...]]). When grouped
        // under a subgraph, member labels drop the ContainingType. prefix since the
        // subgraph title already names the type (see NodeLabel / spec §10.3).
        Assert.Contains("[[\"M (method, global)\"]", mermaid);
        Assert.Contains("[[\"member (field, global)\"]", mermaid);
    }

    [Fact]
    public async Task Non_emitted_targets_appear_as_plain_rectangles()
    {
        // A and DoSomething are omitted as empty (no outgoing edges), so they are
        // not emitted nodes — but they are edge targets, so they must still appear
        // as plain-rectangle t-nodes so no edge is silently dropped. Non-emitted
        // member targets keep the ContainingType. prefix (they are not inside a
        // subgraph).
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var mermaid = OutputFormatter.ToMermaid(report);

        Assert.Contains("[\"A (type, global)\"]", mermaid);
        Assert.Contains("[\"A.DoSomething (method, global)\"]", mermaid);
        // Plain rectangles use a single [..], not the stadium ([..]) or subroutine ([[..]]).
        Assert.DoesNotContain("([[\"A (type, global)\"]])", mermaid);
    }

    [Fact]
    public async Task All_direct_edges_are_solid_arrows()
    {
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var mermaid = OutputFormatter.ToMermaid(report);
        var lines = Lines(mermaid);

        // DepSample default: 7 edges, all direct, all distinct (source, target) pairs.
        var solidEdges = lines.Count(l => l.Contains(" --> "));
        var dashedEdges = lines.Count(l => l.Contains(" -.->|transitive| "));

        Assert.Equal(7, solidEdges);
        Assert.Equal(0, dashedEdges);
    }

    [Fact]
    public async Task Edges_link_emitted_targets_to_their_node_and_others_to_t_nodes()
    {
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var mermaid = OutputFormatter.ToMermaid(report);

        var bId = EmittedId(report, "B");
        var memberId = EmittedId(report, "member");
        var mId = EmittedId(report, "M");

        // B -> member (emitted) and B -> A (non-emitted t-node).
        Assert.Contains($"{bId} --> {memberId}", mermaid);
        Assert.Contains($"{bId} --> t0", mermaid); // A is the first-encountered non-emitted target.
        // M -> member (emitted).
        Assert.Contains($"{mId} --> {memberId}", mermaid);
    }

    [Fact]
    public async Task Duplicate_edges_across_types_and_members_are_deduplicated()
    {
        // B references A via its field's declared type (a type edge) AND via M's body
        // (which rolls up to A as a type edge). The report may list A once in types,
        // but if a node references the same target id via both a type edge and a member
        // edge, only one arrow should be drawn. Construct a case: M references A (type)
        // and member (member) — distinct targets, so no dedup. Instead, verify the total
        // edge-line count equals the distinct (source, target, direct) triples.
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var mermaid = OutputFormatter.ToMermaid(report);
        var lines = Lines(mermaid);

        var edgeLines = lines
            .Where(l => l.Contains(" --> ") || l.Contains(" -.->|transitive| "))
            .ToArray();

        // Compute expected distinct triples directly from the report.
        var expected = new System.Collections.Generic.HashSet<(string From, string To, bool Direct)>();
        var emittedIds = new System.Collections.Generic.HashSet<string>(
            report.Nodes.Select(n => n.Id));
        var idToMermaidId = new System.Collections.Generic.Dictionary<string, string>();
        for (var i = 0; i < report.Nodes.Count; i++)
        {
            idToMermaidId[report.Nodes[i].Id] = $"n{i}";
        }
        var nextT = 0;
        var targetMermaidId = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var node in report.Nodes)
        {
            foreach (var dep in node.Edges.Types.Concat(node.Edges.Members))
            {
                if (emittedIds.Contains(dep.Id))
                {
                    if (!idToMermaidId.TryGetValue(dep.Id, out var toId))
                    {
                        continue;
                    }
                    expected.Add((idToMermaidId[node.Id], toId, dep.Direct));
                }
                else
                {
                    if (!targetMermaidId.ContainsKey(dep.Id))
                    {
                        targetMermaidId[dep.Id] = $"t{nextT++}";
                    }
                    expected.Add((idToMermaidId[node.Id], targetMermaidId[dep.Id], dep.Direct));
                }
            }
        }

        Assert.Equal(expected.Count, edgeLines.Length);
    }

    [Fact]
    public async Task Transitive_edges_are_dashed_and_labelled()
    {
        // ChainFixture: C -> B -> A. With --transitive, C's closure adds A/a/DoA as
        // transitive (Direct=false) edges, which must render as dashed arrows.
        var options = ReportTestHarness.DefaultOptions() with { Transitive = true };
        var report = await BuildChainAsync(options);
        var mermaid = OutputFormatter.ToMermaid(report);
        var lines = Lines(mermaid);

        var solidEdges = lines.Count(l => l.Contains(" --> "));
        var dashedEdges = lines.Count(l => l.Contains(" -.->|transitive| "));

        Assert.True(solidEdges > 0, "Expected at least one direct (solid) edge.");
        Assert.True(dashedEdges > 0, "Expected at least one transitive (dashed) edge.");
        Assert.Equal(report.Summary.DirectEdges, solidEdges);
        Assert.Equal(report.Summary.TransitiveEdges, dashedEdges);
    }

    [Fact]
    public async Task Empty_report_still_emits_flowchart_directive()
    {
        // When all nodes are filtered out, the diagram should still be valid mermaid
        // (just a flowchart with no nodes/edges).
        var report = await BuildAsync(
            ReportTestHarness.DefaultOptions() with { Include = new[] { "DoesNotExist.*" } });
        var mermaid = OutputFormatter.ToMermaid(report);
        var lines = Lines(mermaid);

        Assert.Contains("flowchart LR", lines);
        // No node or edge declarations.
        Assert.DoesNotContain(lines, l => l.StartsWith("    n") || l.StartsWith("    t"));
    }

    [Fact]
    public async Task Member_nodes_are_grouped_under_containing_type_subgraphs()
    {
        // With the default (both) granularity, member nodes are present, so each
        // containing type with emitted members becomes a subgraph holding them.
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var mermaid = OutputFormatter.ToMermaid(report);
        var lines = Lines(mermaid);

        // B is the only containing type with emitted members (member + M).
        Assert.Contains("subgraph sg0 [\"B\"]", mermaid);
        Assert.Contains("    end", lines);

        // The member nodes are indented inside the subgraph (8 spaces vs 4 outside).
        Assert.Contains("        n1[[\"member (field, global)\"]", mermaid);
        Assert.Contains("        n2[[\"M (method, global)\"]", mermaid);

        // Exactly one subgraph open and one end for DepSample (only type B has members).
        var subgraphOpens = lines.Count(l => l.StartsWith("    subgraph "));
        var ends = lines.Count(l => l == "    end");
        Assert.Equal(1, subgraphOpens);
        Assert.Equal(1, ends);
    }

    [Fact]
    public async Task Type_only_granularity_emits_no_subgraphs()
    {
        // With --granularity type there are no member nodes, so no subgraphs should
        // be emitted — every node is a standalone type node.
        var options = ReportTestHarness.DefaultOptions() with { Granularity = Granularity.Type };
        var report = await BuildAsync(options);
        var mermaid = OutputFormatter.ToMermaid(report);
        var lines = Lines(mermaid);

        Assert.DoesNotContain(lines, l => l.StartsWith("    subgraph "));
        Assert.DoesNotContain(lines, l => l == "    end");
    }

    [Fact]
    public async Task Multiple_types_produce_separate_non_nested_subgraphs()
    {
        // ChainFixture has types B and C, each with emitted members. The members are
        // reported in order B.a, B.UseA, C.b, C.UseB, so the B subgraph must close
        // before the C subgraph opens (no nesting).
        var options = ReportTestHarness.DefaultOptions() with { Transitive = true };
        var report = await BuildChainAsync(options);
        var mermaid = OutputFormatter.ToMermaid(report);
        var lines = Lines(mermaid);

        var subgraphLines = lines.Where(l => l.StartsWith("    subgraph ")).ToArray();
        var endLines = lines.Where(l => l == "    end").ToArray();

        Assert.Equal(2, subgraphLines.Length);
        Assert.Equal(2, endLines.Length);
        Assert.Equal("    subgraph sg0 [\"B\"]", subgraphLines[0]);
        Assert.Equal("    subgraph sg1 [\"C\"]", subgraphLines[1]);

        // The first `end` must come AFTER sg0 opens and BEFORE sg1 opens (no nesting).
        var sg0Index = System.Array.IndexOf(lines, "    subgraph sg0 [\"B\"]");
        var firstEndIndex = System.Array.IndexOf(lines, "    end");
        var sg1Index = System.Array.IndexOf(lines, "    subgraph sg1 [\"C\"]");
        Assert.True(sg0Index < firstEndIndex, "sg0 should open before the first end");
        Assert.True(firstEndIndex < sg1Index, "sg0 should close before sg1 opens (no nesting)");
    }

    [Fact]
    public async Task Output_is_deterministic_across_runs()
    {
        var first = await BuildAsync(ReportTestHarness.DefaultOptions());
        var firstMermaid = OutputFormatter.ToMermaid(first);

        for (var i = 1; i < 3; i++)
        {
            var run = await BuildAsync(ReportTestHarness.DefaultOptions());
            var runMermaid = OutputFormatter.ToMermaid(run);
            Assert.Equal(firstMermaid, runMermaid);
        }
    }
}
