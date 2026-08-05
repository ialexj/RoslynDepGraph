// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.CommandLine;

namespace RoslynPrune;

/// <summary>
/// Defines the System.CommandLine root command, options, and argument for RoslynPrune.
/// </summary>
public static class CliOptions
{
    public static readonly Argument<string> PathArgument = new("path")
    {
        Description = "Path to a .csproj, .sln, or .slnx file.",
    };

    public static readonly Option<string[]> FilesOption = new("--files")
    {
        Description = "Glob pattern restricting which files' types/members are prune candidates (repeatable).",
        AllowMultipleArgumentsPerToken = true,
    };

    public static readonly Option<string[]> TypesOption = new("--types")
    {
        Description = "Glob pattern restricting which types are prune candidates by fully-qualified name (repeatable).",
        AllowMultipleArgumentsPerToken = true,
    };

    public static readonly Option<bool> ApplyOption = new("--apply")
    {
        Description = "Write the pruned files to disk. Without this, a dry-run report is printed.",
    };

    public static RootCommand CreateRootCommand()
    {
        var root = new RootCommand(
            "RoslynPrune — removes unused types, members, and using directives from a .csproj or .sln.")
        {
            PathArgument,
            FilesOption,
            TypesOption,
            ApplyOption,
        };
        return root;
    }
}
