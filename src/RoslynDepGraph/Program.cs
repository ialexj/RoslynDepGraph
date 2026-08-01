// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;

namespace RoslynDepGraph;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // CLI parsing + orchestration will be wired up in a later phase.
        // For now, just acknowledge the scaffold.
        await Task.CompletedTask;
        System.Console.WriteLine("RoslynDepGraph scaffold ready.");
        return 0;
    }
}
