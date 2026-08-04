// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RoslynSourceMap.Models;

namespace RoslynSourceMap;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var root = CliOptions.CreateRootCommand();

        root.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(CliOptions.PathArgument);
            return await RunAsync(path!, cancellationToken);
        });

        var parseConfig = new InvocationConfiguration
        {
            Error = Console.Error,
        };
        return await root.Parse(args).InvokeAsync(parseConfig);
    }

    private static async Task<int> RunAsync(string path, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Console.Error.WriteLine("Error: no path provided.");
                return 2;
            }

            var files = FileResolver.Resolve(path);
            if (files.Count == 0)
            {
                Console.Error.WriteLine($"Error: no .cs files matched: '{path}'");
                return 2;
            }

            var fileEntries = new System.Collections.Generic.List<FileEntry>(files.Count);
            foreach (var file in files)
            {
                Console.Error.WriteLine($"Parsing {file}...");
                fileEntries.Add(await MemberExtractor.ExtractAsync(file, ct).ConfigureAwait(false));
            }

            var report = new SourceMapReport
            {
                Tool = "RoslynSourceMap",
                SchemaVersion = "1.0",
                Files = fileEntries,
            };

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
