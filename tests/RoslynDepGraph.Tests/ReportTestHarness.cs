// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynDepGraph.Models;

namespace RoslynDepGraph.Tests;

/// <summary>
/// Shared helpers that drive the public RoslynDepGraph pipeline
/// (<see cref="WorkspaceLoader.LoadAsync"/> + <see cref="ReportBuilder.BuildAsync"/>)
/// from test code, plus normalization helpers for deterministic comparison.
/// </summary>
internal static class ReportTestHarness
{
    /// <summary>
    /// Loads the DepSample sample project into a Roslyn <see cref="Solution"/>.
    /// </summary>
    public static async Task<Solution> LoadSampleAsync(CancellationToken ct = default)
    {
        var path = TestPaths.SampleProject;
        var (solution, _) = await WorkspaceLoader.LoadAsync(path, errorWriter: null, ct)
            .ConfigureAwait(false);
        return solution;
    }

    /// <summary>
    /// Loads an arbitrary fixture project (under <see cref="TestPaths.FixturesDir"/>)
    /// into a Roslyn <see cref="Solution"/>.
    /// </summary>
    public static async Task<Solution> LoadFixtureAsync(
        string fixtureName, CancellationToken ct = default)
    {
        var path = TestPaths.FixtureProject(fixtureName);
        var (solution, _) = await WorkspaceLoader.LoadAsync(path, errorWriter: null, ct)
            .ConfigureAwait(false);
        return solution;
    }

    /// <summary>
    /// Builds a <see cref="DependencyReport"/> for the given solution using the
    /// DepSample path/kind. Use <see cref="BuildForFixtureAsync"/> for fixture projects.
    /// </summary>
    public static async Task<DependencyReport> BuildAsync(
        Solution solution, ReportOptions options, CancellationToken ct = default)
    {
        var builder = new ReportBuilder();
        return await builder.BuildAsync(
            solution,
            sourcePath: TestPaths.SampleProject,
            sourceKind: "project",
            options: options,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a <see cref="DependencyReport"/> for a fixture project, echoing the
    /// fixture path/kind into <see cref="DependencyReport.Source"/>.
    /// </summary>
    public static async Task<DependencyReport> BuildForFixtureAsync(
        string fixtureName, Solution solution, ReportOptions options,
        CancellationToken ct = default)
    {
        var builder = new ReportBuilder();
        return await builder.BuildAsync(
            solution,
            sourcePath: TestPaths.FixtureProject(fixtureName),
            sourceKind: "project",
            options: options,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Default options matching the CLI defaults: <see cref="Granularity.Both"/>,
    /// no filters, no transitive, no system, no exclude-self, no empty nodes.
    /// </summary>
    public static ReportOptions DefaultOptions() => new()
    {
        Granularity = Granularity.Both,
        Include = Array.Empty<string>(),
        Exclude = Array.Empty<string>(),
    };

    /// <summary>
    /// Serializes <paramref name="report"/> via <see cref="OutputFormatter.ToJson"/>
    /// and strips the non-deterministic <c>generatedAt</c> field so two reports
    /// built from the same inputs can be compared byte-for-byte.
    /// </summary>
    public static string NormalizedJson(DependencyReport report)
    {
        var json = OutputFormatter.ToJson(report);
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("Failed to parse report JSON.");
        // generatedAt is the only non-deterministic field (DateTimeOffset.UtcNow).
        // Replace it with a fixed sentinel so the rest of the document is comparable.
        node["generatedAt"] = "<normalized>";
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Finds the first node whose <see cref="DependencyNode.Name"/> equals
    /// <paramref name="name"/> (case-sensitive). Throws if not found.
    /// </summary>
    public static DependencyNode Node(DependencyReport report, string name)
    {
        return report.Nodes.FirstOrDefault(n => n.Name == name)
            ?? throw new InvalidOperationException(
                $"No node named '{name}' in report. " +
                $"Available: {string.Join(", ", report.Nodes.Select(n => n.Name))}");
    }

    /// <summary>
    /// All edge target names (types + members) for a node, in reported order.
    /// </summary>
    public static IReadOnlyList<string> EdgeNames(DependencyNode node)
    {
        var names = new List<string>();
        names.AddRange(node.Edges.Types.Select(d => d.Name));
        names.AddRange(node.Edges.Members.Select(d => d.Name));
        return names;
    }

    /// <summary>
    /// All edge target ids (types + members) for a node, in reported order.
    /// </summary>
    public static IReadOnlyList<string> EdgeIds(DependencyNode node)
    {
        var ids = new List<string>();
        ids.AddRange(node.Edges.Types.Select(d => d.Id));
        ids.AddRange(node.Edges.Members.Select(d => d.Id));
        return ids;
    }

    /// <summary>
    /// True if the node has any edge (type or member) whose <see cref="Dependency.Name"/>
    /// equals <paramref name="targetName"/>.
    /// </summary>
    public static bool HasEdgeTo(DependencyNode node, string targetName)
    {
        return node.Edges.Types.Any(d => d.Name == targetName)
            || node.Edges.Members.Any(d => d.Name == targetName);
    }
}
