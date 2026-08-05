// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynPrune.Models;

namespace RoslynPrune;

/// <summary>
/// Formats a <see cref="PruneReport"/> as JSON.
/// </summary>
public static class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(PruneReport report)
    {
        return JsonSerializer.Serialize(report, JsonOptions);
    }
}
