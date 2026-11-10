// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynDepGraph.Models;

namespace RoslynDepGraph;

/// <summary>
/// Formats a <see cref="DependencyReport"/> as text, JSON, Mermaid, or source bodies.
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
            $"[{node.Id}] ({kind}{containing}, {ns})");

        WriteEdgeGroup(writer, "types", node.Edges.Types);
        WriteMemberEdgeGroup(writer, "methods", node.Edges.Members, SymbolKind.Method);
        WriteMemberEdgeGroup(writer, "properties", node.Edges.Members, SymbolKind.Property);
        WriteMemberEdgeGroup(writer, "fields", node.Edges.Members, SymbolKind.Field);
        WriteMemberEdgeGroup(writer, "events", node.Edges.Members, SymbolKind.Event);
        writer.WriteLine();
    }

    private static void WriteEdgeGroup(TextWriter writer, string label, System.Collections.Generic.IReadOnlyList<Dependency> edges)
    {
        WriteEdgeGroup(writer, label, edges, fullyQualifiedNames: false);
    }

    private static void WriteMemberEdgeGroup(
        TextWriter writer,
        string label,
        System.Collections.Generic.IReadOnlyList<Dependency> edges,
        SymbolKind kind)
    {
        WriteEdgeGroup(
            writer,
            label,
            edges.Where(edge => edge.Kind == kind).ToArray(),
            fullyQualifiedNames: true);
    }

    private static void WriteEdgeGroup(
        TextWriter writer,
        string label,
        System.Collections.Generic.IReadOnlyList<Dependency> edges,
        bool fullyQualifiedNames)
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
            var refCount = dep.References.Count;
            var refTag = refCount switch
            {
                0 => "",
                1 => ", 1 ref",
                _ => $", {refCount} refs",
            };
            var directTag = dep.Direct ? "" : " [transitive]";
            writer.WriteLine(
                $"    {(fullyQualifiedNames ? dep.Id : dep.Name)}  ({kind}, {ns}{refTag}){directTag}");
        }
    }

    public static string ToJson(DependencyReport report)
    {
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static async Task<string> ToBodyAsync(
        DependencyReport report, CancellationToken ct = default)
    {
        using var writer = new StringWriter();
        foreach (var node in report.Nodes.Concat(report.BodyDependencies))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var declaration in node.Declarations)
            {
                var syntax = await declaration.GetSyntaxAsync(ct).ConfigureAwait(false);
                // Primary constructors can point at their enclosing type declaration.
                if (node.Kind != SymbolKind.Type &&
                    syntax is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                {
                    continue;
                }

                if (syntax is VariableDeclaratorSyntax)
                {
                    syntax = syntax.FirstAncestorOrSelf<FieldDeclarationSyntax>()
                        ?? syntax.FirstAncestorOrSelf<EventFieldDeclarationSyntax>()
                        ?? syntax;
                }

                var lines = syntax.GetLocation().GetLineSpan();
                writer.WriteLine(
                    $"// {node.Id}, {Path.GetFileName(lines.Path)}, "
                    + $"{lines.StartLinePosition.Line + 1}-{lines.EndLinePosition.Line + 1}");
                writer.WriteLine(syntax.ToString());
                writer.WriteLine();
            }
        }

        return writer.ToString();
    }

    /// <summary>
    /// Renders the report as a Mermaid <c>flowchart LR</c> diagram. Each emitted
    /// node becomes a node; each outgoing edge (types + members) becomes an arrow.
    /// Edge targets that are themselves emitted nodes link to that node; targets
    /// that are not emitted (filtered out, omitted as empty, or external) are
    /// rendered as standalone nodes so no edge is lost. Transitive edges are drawn
    /// dashed and labelled <c>transitive</c>.
    /// <para>
    /// When the report contains member nodes, each containing type that has at
    /// least one emitted member is rendered as a <c>subgraph</c> holding those
    /// members (and the type node itself, if it is also emitted). Type nodes with
    /// no emitted members stay as standalone stadium-shaped nodes. When there are
    /// no member nodes at all, every node is emitted standalone (no subgraphs).
    /// </para>
    /// </summary>
    public static string ToMermaid(DependencyReport report)
    {
        using var writer = new StringWriter();

        // Stable, mermaid-safe ids: "n" + index for every emitted node, plus a
        // synthetic id for any edge target that is not itself an emitted node.
        var emittedIds = new HashSet<string>(report.Nodes.Select(n => n.Id));
        var nodeIdBySymbolId = new Dictionary<string, string>();
        for (var i = 0; i < report.Nodes.Count; i++)
        {
            nodeIdBySymbolId[report.Nodes[i].Id] = $"n{i}";
        }

        // First pass: assign ids to non-emitted edge targets, in encounter order,
        // so the output is deterministic (the report's node/edge order is already
        // deterministic — see ReportBuilder).
        var nextExternalIndex = 0;
        var targetIdBySymbolId = new Dictionary<string, string>();
        foreach (var node in report.Nodes)
        {
            foreach (var dep in node.Edges.Types.Concat(node.Edges.Members))
            {
                if (emittedIds.Contains(dep.Id))
                {
                    continue;
                }
                if (!targetIdBySymbolId.ContainsKey(dep.Id))
                {
                    targetIdBySymbolId[dep.Id] = $"t{nextExternalIndex++}";
                }
            }
        }

        writer.WriteLine("%% RoslynDepGraph mermaid output");
        writer.WriteLine("%% {0} — {1} ({2})", report.Tool, report.Source.Path, report.Source.Kind);
        writer.WriteLine("%% nodes: {0}, edges: {1} ({2} direct, {3} transitive)",
            report.Summary.NodeCount,
            report.Summary.EdgeCount,
            report.Summary.DirectEdges,
            report.Summary.TransitiveEdges);
        writer.WriteLine("flowchart LR");

        // Decide whether to group members under their containing type as subgraphs.
        // We only do this when there is at least one emitted member node; otherwise
        // (e.g. --granularity type) every node is a type and subgraphs would just
        // wrap single nodes with no benefit.
        var hasMemberNodes = report.Nodes.Any(n => n.Kind != Models.SymbolKind.Type);

        // Map of containing-type name -> subgraph id, for types that will host a
        // subgraph (those with at least one emitted member). Built in report order
        // so subgraph ids are deterministic.
        var subgraphIdByType = new Dictionary<string, string>();
        if (hasMemberNodes)
        {
            var nextSubgraphIndex = 0;
            foreach (var node in report.Nodes)
            {
                if (node.Kind == Models.SymbolKind.Type || node.ContainingType is null)
                {
                    continue;
                }
                if (subgraphIdByType.TryAdd(node.ContainingType, $"sg{nextSubgraphIndex}"))
                {
                    nextSubgraphIndex++;
                }
            }
        }

        // Emitted nodes: shape reflects kind. Types get a stadium/rounded box `([..])`,
        // members get a subroutine box `[[..]]`. The label is the simple name plus a
        // kind/namespace hint.
        //
        // When subgraphs are in use, member nodes are emitted inside their
        // containing type's subgraph (and the type node itself, if emitted, is also
        // placed inside its own subgraph so the type and its members visually group).
        // Member labels drop the ContainingType. prefix inside a subgraph since the
        // subgraph title already names the type.
        //
        // Subgraphs are opened/closed lazily as the containing type changes across
        // the (already-deterministic) node order. We do NOT support interleaved
        // members of different types within a single subgraph: when the containing
        // type changes, the previous subgraph is closed first. This keeps the
        // output flat (no nesting) and matches the mental model of one box per type.
        string? currentSubgraph = null;
        for (var i = 0; i < report.Nodes.Count; i++)
        {
            var node = report.Nodes[i];
            var id = $"n{i}";
            var containingType = node.ContainingType;
            string? sgId = null;
            if (hasMemberNodes && containingType is not null)
            {
                subgraphIdByType.TryGetValue(containingType, out sgId);
            }
            var inSubgraph = sgId is not null;

            // If we're entering a different subgraph (or leaving subgraphs entirely),
            // close the currently-open one first.
            if (currentSubgraph is not null && currentSubgraph != sgId)
            {
                writer.WriteLine("    end");
                currentSubgraph = null;
            }

            if (inSubgraph && currentSubgraph is null)
            {
                writer.WriteLine("    subgraph {0} [\"{1}\"]", sgId, containingType);
                currentSubgraph = sgId;
            }

            var indent = inSubgraph ? "        " : "    ";
            var label = inSubgraph
                ? NodeLabel(node.Name, node.Kind, node.Namespace, containingType: null)
                : NodeLabel(node.Name, node.Kind, node.Namespace, node.ContainingType);
            var shape = node.Kind == Models.SymbolKind.Type
                ? $"{indent}{id}([\"{label}\"])"
                : $"{indent}{id}[[\"{label}\"]]";
            writer.WriteLine(shape);
        }

        // Close any subgraph still open after the last node.
        if (currentSubgraph is not null)
        {
            writer.WriteLine("    end");
        }

        // Non-emitted edge targets: rendered as plain rectangles so they are
        // visually distinct from reported nodes.
        foreach (var kv in targetIdBySymbolId)
        {
            // Find a representative Dependency for this target id (first encounter).
            var dep = FirstDependency(report, kv.Key);
            var label = dep is null
                ? kv.Key
                : NodeLabel(dep.Name, dep.Kind, dep.Namespace, dep.ContainingType);
            writer.WriteLine("    {0}[\"{1}\"]", kv.Value, label);
        }

        // Edges. Dedup identical (source, target, direct) triples so a node that
        // references the same target via both a type edge and a member edge only
        // draws one arrow (the report itself does not dedup across the two groups).
        var seenEdges = new HashSet<(string From, string To, bool Direct)>();
        foreach (var node in report.Nodes)
        {
            if (!nodeIdBySymbolId.TryGetValue(node.Id, out var fromId))
            {
                continue;
            }
            foreach (var dep in node.Edges.Types.Concat(node.Edges.Members))
            {
                var toId = nodeIdBySymbolId.TryGetValue(dep.Id, out var emittedTo)
                    ? emittedTo
                    : targetIdBySymbolId[dep.Id];
                if (!seenEdges.Add((fromId, toId, dep.Direct)))
                {
                    continue;
                }
                if (dep.Direct)
                {
                    writer.WriteLine("    {0} --> {1}", fromId, toId);
                }
                else
                {
                    writer.WriteLine("    {0} -.->|transitive| {1}", fromId, toId);
                }
            }
        }

        return writer.ToString();
    }

    private static Dependency? FirstDependency(DependencyReport report, string id)
    {
        foreach (var node in report.Nodes)
        {
            foreach (var dep in node.Edges.Types.Concat(node.Edges.Members))
            {
                if (dep.Id == id)
                {
                    return dep;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Builds a compact, single-line label for a mermaid node. Includes the simple
    /// name, kind, and namespace (or "global" when empty). Containing type is
    /// included for member nodes to disambiguate same-named members across types.
    /// </summary>
    private static string NodeLabel(
        string name, Models.SymbolKind kind, string ns, string? containingType)
    {
        var kindText = kind.ToString().ToLowerInvariant();
        var nsText = string.IsNullOrEmpty(ns) ? "global" : ns;
        var prefix = containingType is null ? "" : $"{containingType}.";
        return $"{prefix}{name} ({kindText}, {nsText})";
    }
}
