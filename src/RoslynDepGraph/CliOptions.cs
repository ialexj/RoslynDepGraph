// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.CommandLine;
using RoslynDepGraph.Models;

namespace RoslynDepGraph;

/// <summary>
/// Defines the System.CommandLine root command, options, and argument for RoslynDepGraph.
/// </summary>
public static class CliOptions
{
    public static readonly Argument<string> PathArgument = new("path")
    {
        Description = "Path to a .csproj, .sln, or .slnx file.",
    };

    public static readonly Option<Granularity> GranularityOption = new("--granularity")
    {
        Description = "Granularity of the report: type (rolled-up), member (per-member), or both (default).",
        DefaultValueFactory = _ => Granularity.Both,
        CustomParser = ParseGranularity,
    };

    public static readonly Option<string[]> IncludeOption = new("--include")
    {
        Description = "Glob pattern to include reported nodes by fully-qualified name (repeatable).",
        AllowMultipleArgumentsPerToken = true,
    };

    public static readonly Option<string[]> ExcludeOption = new("--exclude")
    {
        Description = "Glob pattern to exclude symbols from the graph and dependency analysis by fully-qualified name (repeatable).",
        AllowMultipleArgumentsPerToken = true,
    };

    public static readonly Option<bool> TransitiveOption = new("--transitive")
    {
        Description = "Compute the full transitive closure of dependencies.",
    };

    public static readonly Option<bool> IncludeSystemOption = new("--include-system")
    {
        Description = "Allow framework/external (non-source) symbols as edge targets.",
    };

    public static readonly Option<bool> ExcludeSelfOption = new("--exclude-self")
    {
        Description = "Drop sibling members and the containing type from member-node edges.",
    };

    public static readonly Option<bool> IncludeEmptyOption = new("--include-empty")
    {
        Description = "Emit nodes that have no outgoing edges.",
    };

    public static readonly Option<OutputFormat> FormatOption = new("--format")
    {
        Description = "Output format: text (default), json, mermaid, or body.",
        DefaultValueFactory = _ => OutputFormat.Text,
        CustomParser = ParseOutputFormat,
    };

    public static RootCommand CreateRootCommand()
    {
        var root = new RootCommand(
            "RoslynDepGraph — produces a code dependency report from a .csproj or .sln.")
        {
            PathArgument,
            GranularityOption,
            IncludeOption,
            ExcludeOption,
            TransitiveOption,
            IncludeSystemOption,
            ExcludeSelfOption,
            IncludeEmptyOption,
            FormatOption,
        };
        return root;
    }

    private static Granularity ParseGranularity(System.CommandLine.Parsing.ArgumentResult result)
    {
        var value = result.Tokens.Count > 0 ? result.Tokens[0].Value : "both";
        return value.ToLowerInvariant() switch
        {
            "type" => Granularity.Type,
            "member" => Granularity.Member,
            "both" => Granularity.Both,
            _ => Granularity.Both,
        };
    }

    private static OutputFormat ParseOutputFormat(System.CommandLine.Parsing.ArgumentResult result)
    {
        var value = result.Tokens.Count > 0 ? result.Tokens[0].Value : "text";
        return value.ToLowerInvariant() switch
        {
            "json" => OutputFormat.Json,
            "mermaid" => OutputFormat.Mermaid,
            "body" => OutputFormat.Body,
            "text" => OutputFormat.Text,
            _ => OutputFormat.Text,
        };
    }
}
