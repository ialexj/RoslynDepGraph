// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynSourceMap.Models;

namespace RoslynSourceMap;

/// <summary>
/// Formats a <see cref="SourceMapReport"/> as JSON.
/// </summary>
public static class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(SourceMapReport report)
    {
        return JsonSerializer.Serialize(report, JsonOptions);
    }
}
