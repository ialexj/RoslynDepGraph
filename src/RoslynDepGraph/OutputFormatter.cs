// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynDepGraph.Models;

namespace RoslynDepGraph;

/// <summary>
/// Formats a <see cref="DependencyReport"/> as text or JSON.
/// </summary>
public static class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ToText(DependencyReport report)
    {
        using var writer = new StringWriter();
        var opts = report.Options;

        writer.WriteLine($"RoslynDepGraph — {report.Source.Path} ({report.Source.Kind})");
        writer.WriteLine($"Granularity: {opts.Granularity.ToString().ToLowerInvariant()} | "
            + $"transitive: {(opts.Transitive ? "yes" : "no")} | "
            + $"include-system: {(opts.IncludeSystem ? "yes" : "no")} | "
            + $"exclude-self: {(opts.ExcludeSelf ? "yes" : "no")}");
        writer.WriteLine();

        if (report.Nodes.Count == 0)
        {
            writer.WriteLine("No nodes to report (all filtered or empty).");
        }
        else
        {
            writer.WriteLine($"Nodes ({report.Nodes.Count}):");
            writer.WriteLine();
            foreach (var node in report.Nodes)
            {
                WriteNode(writer, node);
            }
        }

        var s = report.Summary;
        writer.WriteLine(
            $"Summary: {s.NodeCount} nodes, {s.EdgeCount} edges "
            + $"({s.DirectEdges} direct, {s.TransitiveEdges} transitive)");
        if (s.NodesExcludedByFilter > 0)
        {
            writer.WriteLine($"  Nodes excluded by filter: {s.NodesExcludedByFilter}");
        }
        if (s.NodesOmittedEmpty > 0)
        {
            writer.WriteLine($"  Nodes omitted (empty): {s.NodesOmittedEmpty}");
        }
        foreach (var d in report.Diagnostics)
        {
            writer.WriteLine($"  diag: {d}");
        }

        return writer.ToString();
    }

    private static void WriteNode(TextWriter writer, DependencyNode node)
    {
        var kind = node.Kind.ToString().ToLowerInvariant();
        var containing = node.ContainingType is null ? "" : $", {node.ContainingType}";
        var ns = string.IsNullOrEmpty(node.Namespace) ? "global namespace" : node.Namespace;
        writer.WriteLine(
            $"[{node.Id}] ({kind}{containing}, {ns}, {node.Location.File}:{node.Location.Line})");

        WriteEdgeGroup(writer, "types", node.Edges.Types);
        WriteEdgeGroup(writer, "members", node.Edges.Members);
        writer.WriteLine();
    }

    private static void WriteEdgeGroup(TextWriter writer, string label, System.Collections.Generic.IReadOnlyList<Dependency> edges)
    {
        if (edges.Count == 0)
        {
            writer.WriteLine($"  {label}: (none)");
            return;
        }

        writer.WriteLine($"  {label}:");
        foreach (var dep in edges)
        {
            var kind = dep.Kind.ToString().ToLowerInvariant();
            var ns = string.IsNullOrEmpty(dep.Namespace) ? "global namespace" : dep.Namespace;
            var refs = string.Join(", ",
                dep.References.Select(r => $"{r.File}:{r.Line}"));
            var directTag = dep.Direct ? "" : " [transitive]";
            writer.WriteLine(
                $"    {dep.Name}  ({kind}, {ns}, {refs}){directTag}");
        }
    }

    public static string ToJson(DependencyReport report)
    {
        return JsonSerializer.Serialize(report, JsonOptions);
    }
}
