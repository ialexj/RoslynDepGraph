// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynDepGraph.Models;

namespace RoslynDepGraph;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var root = CliOptions.CreateRootCommand();

        root.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(CliOptions.PathArgument);
            var granularity = parseResult.GetValue(CliOptions.GranularityOption);
            var includes = parseResult.GetValue(CliOptions.IncludeOption) ?? Array.Empty<string>();
            var excludes = parseResult.GetValue(CliOptions.ExcludeOption) ?? Array.Empty<string>();
            var transitive = parseResult.GetValue(CliOptions.TransitiveOption);
            var includeSystem = parseResult.GetValue(CliOptions.IncludeSystemOption);
            var excludeSelf = parseResult.GetValue(CliOptions.ExcludeSelfOption);
            var includeEmpty = parseResult.GetValue(CliOptions.IncludeEmptyOption);
            var format = parseResult.GetValue(CliOptions.FormatOption);

            return await RunAsync(
                path!, granularity, includes, excludes,
                transitive, includeSystem, excludeSelf, includeEmpty, format,
                cancellationToken);
        });

        var parseConfig = new InvocationConfiguration
        {
            Error = Console.Error,
        };
        return await root.Parse(args).InvokeAsync(parseConfig);
    }

    private static async Task<int> RunAsync(
        string path,
        Granularity granularity,
        string[] includes,
        string[] excludes,
        bool transitive,
        bool includeSystem,
        bool excludeSelf,
        bool includeEmpty,
        OutputFormat format,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Console.Error.WriteLine($"Error: file not found: '{path}'");
                return 2;
            }

            Console.Error.WriteLine($"Loading {path}...");
            var (solution, kind) = await WorkspaceLoader.LoadAsync(path, Console.Error, ct).ConfigureAwait(false);
            Console.Error.WriteLine($"Loaded {kind} with {solution.Projects.Count()} project(s).");

            var options = new ReportOptions
            {
                Granularity = granularity,
                Transitive = transitive,
                IncludeSystem = includeSystem,
                ExcludeSelf = excludeSelf,
                IncludeEmpty = includeEmpty,
                Include = includes,
                Exclude = excludes,
            };

            var builder = new ReportBuilder();
            var report = await builder.BuildAsync(solution, path, kind, options, ct).ConfigureAwait(false);

            var output = format switch
            {
                OutputFormat.Json => OutputFormatter.ToJson(report),
                OutputFormat.Mermaid => OutputFormatter.ToMermaid(report),
                OutputFormat.Body => await OutputFormatter.ToBodyAsync(report, ct).ConfigureAwait(false),
                _ => OutputFormatter.ToText(report),
            };

            Console.Out.WriteLine(output);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
