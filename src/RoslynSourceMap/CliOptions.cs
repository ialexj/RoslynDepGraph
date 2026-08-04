// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.CommandLine;

namespace RoslynSourceMap;

/// <summary>
/// Defines the System.CommandLine root command and argument for RoslynSourceMap.
/// </summary>
public static class CliOptions
{
    public static readonly Argument<string> PathArgument = new("path")
    {
        Description = "Path to a .cs file, a directory, or a glob pattern (e.g. **/*.cs).",
    };

    public static RootCommand CreateRootCommand()
    {
        var root = new RootCommand(
            "RoslynSourceMap — parses one or more C# files (no semantic model) and emits a JSON map of every type and member with its leading comments.")
        {
            PathArgument,
        };
        return root;
    }
}
