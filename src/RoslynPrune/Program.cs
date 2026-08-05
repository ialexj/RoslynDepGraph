// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RoslynPrune.Models;

namespace RoslynPrune;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var root = CliOptions.CreateRootCommand();

        root.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(CliOptions.PathArgument);
            var files = parseResult.GetValue(CliOptions.FilesOption) ?? Array.Empty<string>();
            var types = parseResult.GetValue(CliOptions.TypesOption) ?? Array.Empty<string>();
            var apply = parseResult.GetValue(CliOptions.ApplyOption);

            return await RunAsync(path!, files, types, apply, cancellationToken);
        });

        var parseConfig = new InvocationConfiguration
        {
            Error = Console.Error,
        };
        return await root.Parse(args).InvokeAsync(parseConfig);
    }

    private static async Task<int> RunAsync(
        string path,
        string[] files,
        string[] types,
        bool apply,
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
            Console.Error.WriteLine($"Loaded {solution.Projects.Count()} project(s).");

            var orchestrator = new PruneOrchestrator();
            var report = await orchestrator.RunAsync(solution, files, types, apply, Console.Error, ct).ConfigureAwait(false);

            Console.Out.WriteLine(OutputFormatter.ToJson(report));
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
