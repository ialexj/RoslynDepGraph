// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynDepGraph.Models;
using Xunit;

namespace RoslynDepGraph.Tests;

/// <summary>
/// Happy-path correctness against the repo-owned DepSample fixture
/// (samples/DepSample/Samples.cs: classes A and B, field B.member of type A,
/// method B.M calling member.DoSomething(), empty method B.V).
/// </summary>
public class DepSampleTests
{
    private static async Task<DependencyReport> BuildAsync(ReportOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadSampleAsync(cts.Token);
        return await ReportTestHarness.BuildAsync(solution, options, cts.Token);
    }

    [Fact]
    public async Task Default_options_has_expected_node_and_edge_counts()
    {
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());

        Assert.Equal(3, report.Summary.NodeCount);
        Assert.Equal(5, report.Summary.NodesOmittedEmpty);
        Assert.Equal(7, report.Summary.EdgeCount);
        Assert.Equal(7, report.Summary.DirectEdges);
        Assert.Equal(0, report.Summary.TransitiveEdges);
    }

    [Fact]
    public async Task Default_options_reports_B_B_member_B_M_only()
    {
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());

        // A, A.DoSomething, A.A, B.V, B.B are omitted (empty); only B, B.member, B.M survive.
        var names = report.Nodes.Select(n => n.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "B", "M", "member" }, names);
    }

    [Fact]
    public async Task Default_options_B_type_node_has_expected_edges_and_no_self_edge()
    {
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var b = ReportTestHarness.Node(report, "B");

        // Rolled-up type node: type edge to A, member edges to member + DoSomething.
        Assert.Equal(new[] { "A" }, b.Edges.Types.Select(d => d.Name));
        Assert.Equal(new[] { "member", "DoSomething" }, b.Edges.Members.Select(d => d.Name));

        // A type never has a self-edge to itself.
        Assert.False(ReportTestHarness.HasEdgeTo(b, "B"));
    }

    [Fact]
    public async Task Default_options_B_member_field_edges_to_type_A()
    {
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var member = report.Nodes.First(n => n.Name == "member");

        Assert.Equal(new[] { "A" }, member.Edges.Types.Select(d => d.Name));
        Assert.Empty(member.Edges.Members);
    }

    [Fact]
    public async Task Default_options_B_M_method_edges_to_A_DoSomething_member()
    {
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());
        var m = ReportTestHarness.Node(report, "M");

        Assert.Equal(new[] { "A" }, m.Edges.Types.Select(d => d.Name));
        Assert.Equal(new[] { "DoSomething", "member" }, m.Edges.Members.Select(d => d.Name));
    }

    [Fact]
    public async Task IncludeEmpty_reports_all_eight_source_symbols()
    {
        var options = ReportTestHarness.DefaultOptions() with { IncludeEmpty = true };
        var report = await BuildAsync(options);

        // All source symbols: A, B, A.DoSomething, A.A (implicit ctor), B.member, B.M, B.V, B.B.
        Assert.Equal(8, report.Summary.NodeCount);
        Assert.Equal(0, report.Summary.NodesOmittedEmpty);
    }

    [Fact]
    public async Task Granularity_Type_reports_only_type_nodes()
    {
        var options = ReportTestHarness.DefaultOptions() with { Granularity = Granularity.Type };
        var report = await BuildAsync(options);

        Assert.All(report.Nodes, n => Assert.Equal(SymbolKind.Type, n.Kind));
        // A is empty (no outgoing edges) so it is omitted unless IncludeEmpty.
        Assert.Equal(new[] { "B" }, report.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public async Task Granularity_Member_reports_only_member_nodes()
    {
        var options = ReportTestHarness.DefaultOptions() with { Granularity = Granularity.Member };
        var report = await BuildAsync(options);

        Assert.All(report.Nodes, n => Assert.NotEqual(SymbolKind.Type, n.Kind));
        // Only members with outgoing edges survive: B.member, B.M. (A.DoSomething, B.V, ctors are empty.)
        var names = report.Nodes.Select(n => n.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "M", "member" }, names);
    }

    [Fact]
    public async Task IncludeSystem_false_omits_all_external_edges()
    {
        // Default has IncludeSystem=false; DepSample has no framework refs anyway,
        // but assert the invariant explicitly: no edge is external.
        var report = await BuildAsync(ReportTestHarness.DefaultOptions());

        Assert.All(report.Nodes, node =>
        {
            Assert.All(node.Edges.Types, d => Assert.False(d.IsExternal));
            Assert.All(node.Edges.Members, d => Assert.False(d.IsExternal));
        });
    }

    [Fact]
    public async Task IncludeSystem_true_surfaces_external_edge()
    {
        // SystemRefFixture.Holder has a string field/method; with IncludeSystem=true
        // the String SpecialType should surface as an external edge.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadFixtureAsync("SystemRefFixture", cts.Token);
        var options = ReportTestHarness.DefaultOptions() with { IncludeSystem = true };
        var report = await ReportTestHarness.BuildForFixtureAsync(
            "SystemRefFixture", solution, options, cts.Token);

        var hasExternal = report.Nodes.Any(n =>
            n.Edges.Types.Any(d => d.IsExternal) || n.Edges.Members.Any(d => d.IsExternal));
        Assert.True(hasExternal, "Expected at least one external edge with IncludeSystem=true.");
    }
}
