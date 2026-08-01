// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynDepGraph;

/// <summary>
/// Loads a .csproj or .sln into a Roslyn <see cref="Solution"/>.
/// </summary>
public static class WorkspaceLoader
{
    private static int _registered;

    /// <summary>
    /// Ensures <see cref="MSBuildLocator.RegisterDefaults"/> has run exactly once.
    /// Must be called before any MSBuild/Roslyn workspace type is touched.
    /// </summary>
    public static void EnsureMsBuildRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    public static async Task<(Solution Solution, string Kind)> LoadAsync(string path, TextWriter? errorWriter = null, CancellationToken ct = default)
    {
        EnsureMsBuildRegistered();

        var ext = Path.GetExtension(path);
        var properties = new System.Collections.Generic.Dictionary<string, string>
        {
            // Avoid trying to load the project in the context of a build target.
            ["BuildingInsideVisualStudio"] = "false",
        };

        using var workspace = MSBuildWorkspace.Create(properties);
        workspace.WorkspaceFailed += (_, e) =>
        {
            errorWriter?.WriteLine($"[workspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");
        };

        Solution solution;
        string kind;

        if (string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            kind = "solution";
            solution = await workspace.OpenSolutionAsync(path, cancellationToken: ct).ConfigureAwait(false);
        }
        else if (string.Equals(ext, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            kind = "project";
            var project = await workspace.OpenProjectAsync(path, cancellationToken: ct).ConfigureAwait(false);
            solution = project.Solution;
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported file extension '{ext}'. Expected .sln, .slnx, or .csproj.",
                nameof(path));
        }

        if (workspace.Diagnostics.Count > 0)
        {
            foreach (var d in workspace.Diagnostics)
            {
                errorWriter?.WriteLine($"[workspace-diag] {d.Kind}: {d.Message}");
            }
        }

        return (solution, kind);
    }
}
