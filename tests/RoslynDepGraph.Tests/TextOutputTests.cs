// Licensed to the project Authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynDepGraph.Models;
using Xunit;

namespace RoslynDepGraph.Tests;

public class TextOutputTests
{
    [Fact]
    public async Task Text_output_uses_full_member_names_and_separates_member_kinds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var solution = await ReportTestHarness.LoadSampleAsync(cts.Token);
        var report = await ReportTestHarness.BuildAsync(
            solution, ReportTestHarness.DefaultOptions(), cts.Token);

        var b = report.Nodes.First(node => node.Name == "B");
        var field = b.Edges.Members.First(edge => edge.Kind == SymbolKind.Field);
        var property = field with
        {
            Id = "B.Name",
            Name = "Name",
            Kind = SymbolKind.Property,
        };
        var nodeWithProperty = b with
        {
            Edges = b.Edges with { Members = b.Edges.Members.Append(property).ToArray() },
        };
        var reportWithProperty = report with
        {
            Nodes = report.Nodes.Select(node => node.Id == b.Id ? nodeWithProperty : node).ToArray(),
        };

        var text = OutputFormatter.ToText(reportWithProperty);

        Assert.Contains("methods:", text);
        Assert.Contains("    A.DoSomething()  (method,", text);
        Assert.Contains("properties:", text);
        Assert.Contains("    B.Name  (property,", text);
        Assert.Contains("fields:", text);
        Assert.Contains("    B.member  (field,", text);
        Assert.Contains("events: (none)", text);
        Assert.DoesNotContain("  members:", text);
    }
}
