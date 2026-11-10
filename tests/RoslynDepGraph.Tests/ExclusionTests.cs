// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynDepGraph.Models;
using Xunit;

namespace RoslynDepGraph.Tests;

[Collection("Console output")]
public sealed class ExclusionTests
{
    [Theory]
    [InlineData(Granularity.Member, false)]
    [InlineData(Granularity.Member, true)]
    [InlineData(Granularity.Both, false)]
    [InlineData(Granularity.Both, true)]
    [InlineData(Granularity.Type, false)]
    [InlineData(Granularity.Type, true)]
    public async Task Excluded_member_is_not_a_target_or_analyzed_through_type_rollup(
        Granularity granularity, bool transitive)
    {
        var (report, progress) = await BuildAsync(
            granularity, transitive, "Service.Used", "Service.Value");
        var node = Assert.Single(report.Nodes);

        Assert.DoesNotContain(node.Edges.Members, d => d.Name is "Used" or "Finish");
        Assert.DoesNotContain(node.Edges.Types, d => d.Name == "Tail");
        Assert.DoesNotContain("] Service.Used()", progress);
        Assert.DoesNotContain("] Tail.Finish()", progress);
        if (granularity == Granularity.Member && transitive)
        {
            Assert.Contains("Analyzed 1 node task(s); queued 0 additional node(s).", progress);
        }
    }

    [Theory]
    [InlineData(Granularity.Member)]
    [InlineData(Granularity.Both)]
    [InlineData(Granularity.Type)]
    public async Task Exclusions_apply_to_dependencies_discovered_transitively(Granularity granularity)
    {
        var (report, progress) = await BuildAsync(granularity, true, "Tail.Finish");
        var node = Assert.Single(report.Nodes);

        Assert.DoesNotContain(node.Edges.Members, d => d.Name == "Finish");
        Assert.DoesNotContain("] Tail.Finish()", progress);
        Assert.Contains(node.Edges.Types, d => d.Name == "Tail");
    }

    [Theory]
    [InlineData(Granularity.Member)]
    [InlineData(Granularity.Both)]
    [InlineData(Granularity.Type)]
    public async Task Excluded_type_is_not_reintroduced_by_member_rollup(Granularity granularity)
    {
        var (report, progress) = await BuildAsync(granularity, true, "Service");
        var node = Assert.Single(report.Nodes);

        Assert.DoesNotContain(node.Edges.Types, d => d.Name == "Service");
        Assert.DoesNotContain("] Service" + Environment.NewLine, progress);
        if (granularity != Granularity.Type)
        {
            // An exact type glob does not exclude its separately named members.
            Assert.Contains(node.Edges.Members, d => d.Name == "Used");
        }
    }

    [Fact]
    public async Task Exclusion_globs_block_all_matching_dependencies()
    {
        var (report, progress) = await BuildAsync(Granularity.Member, true, "Service.*", "Tail.*");
        var node = Assert.Single(report.Nodes);

        Assert.Empty(node.Edges.Members);
        Assert.Contains(node.Edges.Types, d => d.Name == "Service");
        Assert.Contains("Analyzed 1 node task(s); queued 0 additional node(s).", progress);
    }

    [Theory]
    [InlineData(Granularity.Member)]
    [InlineData(Granularity.Both)]
    public async Task Excluded_property_also_removes_its_accessor_nodes(Granularity granularity)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadFixtureAsync("MemberClosureFixture", cts.Token);
        var options = ReportTestHarness.DefaultOptions() with
        {
            Granularity = granularity,
            Transitive = true,
            Include = new[] { "Service*" },
            Exclude = new[] { "Service.Value" },
            IncludeEmpty = true,
        };
        var report = await ReportTestHarness.BuildForFixtureAsync(
            "MemberClosureFixture", solution, options, cts.Token);

        Assert.NotEmpty(report.Nodes);
        Assert.DoesNotContain(report.Nodes, n => n.Name is "Value" or "get_Value");
    }

    [Fact]
    public async Task Exclusions_still_apply_when_system_targets_are_enabled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadFixtureAsync("SystemRefFixture", cts.Token);
        var options = ReportTestHarness.DefaultOptions() with
        {
            IncludeSystem = true,
            Exclude = new[] { "System.String" },
            IncludeEmpty = true,
        };
        var baseline = await ReportTestHarness.BuildForFixtureAsync(
            "SystemRefFixture", solution, options with { Exclude = Array.Empty<string>() }, cts.Token);
        Assert.Contains(baseline.Nodes.SelectMany(n => n.Edges.Types), d => d.Name == "String");

        var report = await ReportTestHarness.BuildForFixtureAsync(
            "SystemRefFixture", solution, options, cts.Token);
        Assert.DoesNotContain(report.Nodes.SelectMany(n => n.Edges.Types), d => d.Name == "String");
    }

    [Theory]
    [InlineData(Granularity.Member)]
    [InlineData(Granularity.Both)]
    [InlineData(Granularity.Type)]
    public async Task Excluded_declarations_do_not_leak_dependencies_through_shared_fields(
        Granularity granularity)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadFixtureAsync("MemberClosureFixture", cts.Token);
        solution = solution.AddDocument(
            DocumentId.CreateNewId(solution.ProjectIds.Single()),
            "Shared.cs",
            SourceText.From("""
                class Shared
                {
                    public Leaf Kept = new Leaf(), Skipped = Factory.Make();
                    public Factory Hidden;
                    public void Ignored() => Factory.Make();
                }
                class Factory
                {
                    public static Leaf Make() => new Leaf();
                }
                """));
        var options = ReportTestHarness.DefaultOptions() with
        {
            Granularity = granularity,
            Transitive = true,
            Include = new[] { "Shared*" },
            Exclude = new[] { "Shared.Skipped", "Shared.Hidden", "Shared.Ignored" },
        };
        var report = await ReportTestHarness.BuildForFixtureAsync(
            "MemberClosureFixture", solution, options, cts.Token);

        Assert.NotEmpty(report.Nodes);
        Assert.All(report.Nodes, node =>
        {
            Assert.DoesNotContain(node.Edges.Types, d => d.Name == "Factory");
            Assert.DoesNotContain(node.Edges.Members, d => d.Name == "Make");
        });
        Assert.DoesNotContain(report.Nodes, n => n.Name is "Skipped" or "Hidden" or "Ignored");
    }

    private static async Task<(DependencyReport Report, string Progress)> BuildAsync(
        Granularity granularity, bool transitive, params string[] excludes)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadFixtureAsync("MemberClosureFixture", cts.Token);
        var options = ReportTestHarness.DefaultOptions() with
        {
            Granularity = granularity,
            Transitive = transitive,
            Include = new[] { granularity == Granularity.Type ? "Entry" : "Entry.Call" },
            Exclude = excludes,
            IncludeEmpty = true,
        };
        var originalError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            var report = await ReportTestHarness.BuildForFixtureAsync(
                "MemberClosureFixture", solution, options, cts.Token);
            return (report, error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
