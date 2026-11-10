// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynDepGraph.Models;
using Xunit;

namespace RoslynDepGraph.Tests;

/// <summary>
/// Tests for the --include/--exclude node filters (NodeFilter.GlobToRegex +
/// NodeFilter.Select). Globs are anchored (^...$), case-sensitive, with
/// '*' -> '.*' (matches any sequence including dots) and '?' -> '.'. A literal
/// '.' in the glob is regex-escaped to '\.', so "B.*" matches "B.M" but not "B".
/// Include applies only to reported nodes; exclude also removes dependency targets
/// and prevents transitive analysis (spec sections 5 and 6).
/// </summary>
public class FilterTests
{
    private static async Task<DependencyReport> BuildAsync(ReportOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadSampleAsync(cts.Token);
        return await ReportTestHarness.BuildAsync(solution, options, cts.Token);
    }

    [Fact]
    public async Task Include_B_matches_exactly_B_not_its_members()
    {
        // Pattern "B" is anchored (^B$), so it matches the type node B but NOT
        // B.member or B.M (whose FQNs are "B.member" and "B.M").
        var options = ReportTestHarness.DefaultOptions() with { Include = new[] { "B" } };
        var report = await BuildAsync(options);

        var names = report.Nodes.Select(n => n.Name).ToArray();
        Assert.Equal(new[] { "B" }, names);

        // Edge targets are NOT narrowed by --include: B still references A/member/DoSomething.
        var b = ReportTestHarness.Node(report, "B");
        Assert.True(ReportTestHarness.HasEdgeTo(b, "A"));
        Assert.True(ReportTestHarness.HasEdgeTo(b, "member"));
        Assert.True(ReportTestHarness.HasEdgeTo(b, "DoSomething"));
    }

    [Fact]
    public async Task Include_B_star_matches_B_and_its_members()
    {
        // "B*" -> ^B.*$ : '*' -> '.*' (matches any sequence, including dots), so it
        // matches B, B.member, and B.M. (B.M and B.member have outgoing edges;
        // B.V is empty and omitted.)
        var options = ReportTestHarness.DefaultOptions() with { Include = new[] { "B*" } };
        var report = await BuildAsync(options);

        var names = report.Nodes.Select(n => n.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "B", "M", "member" }, names);
    }

    [Fact]
    public async Task Include_B_dot_star_matches_only_dotted_children()
    {
        // "B.*" -> ^B\..*$ : a literal '.' in the glob is regex-escaped to '\.', so
        // this matches B.member and B.M (which have a dot) but NOT B itself.
        var options = ReportTestHarness.DefaultOptions() with { Include = new[] { "B.*" } };
        var report = await BuildAsync(options);

        var names = report.Nodes.Select(n => n.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "M", "member" }, names);
    }

    [Fact]
    public async Task Exclude_B_removes_type_but_not_its_separately_named_members()
    {
        // Excluding B removes it from reported nodes, but B.member and B.M still
        // reference A (and each other) as non-excluded edge targets.
        var options = ReportTestHarness.DefaultOptions() with { Exclude = new[] { "B" } };
        var report = await BuildAsync(options);

        Assert.DoesNotContain(report.Nodes, n => n.Name == "B");
        Assert.True(report.Summary.NodesExcludedByFilter >= 1);

        // B.member and B.M survive (they are "B.member" and "B.M", not "B").
        var member = report.Nodes.First(n => n.Name == "member");
        var m = report.Nodes.First(n => n.Name == "M");

        // Their edges still point to A (a non-excluded source symbol).
        Assert.True(ReportTestHarness.HasEdgeTo(member, "A"));
        Assert.True(ReportTestHarness.HasEdgeTo(m, "A"));
    }

    [Fact]
    public async Task Exclude_is_case_sensitive()
    {
        // GlobToRegex uses only RegexOptions.CultureInvariant (no IgnoreCase),
        // so a lowercase "b" does NOT match the type node "B".
        var options = ReportTestHarness.DefaultOptions() with { Exclude = new[] { "b" } };
        var report = await BuildAsync(options);

        // "b" matches nothing, so B is still present and nothing was excluded.
        Assert.Contains(report.Nodes, n => n.Name == "B");
        Assert.Equal(0, report.Summary.NodesExcludedByFilter);
    }
}
