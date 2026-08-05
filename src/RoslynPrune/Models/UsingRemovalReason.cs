// Licensed to the project authors under the MIT license.
// See the LICENSE file in the project root for more information.

namespace RoslynPrune.Models;

/// <summary>
/// The reason a using directive was removed.
/// </summary>
public enum UsingRemovalReason
{
    /// <summary>The alias target type was pruned.</summary>
    AliasTargetPruned,

    /// <summary>The <c>using static</c> target type was pruned.</summary>
    StaticTargetPruned,

    /// <summary>A plain namespace using is no longer needed by remaining code.</summary>
    OrphanedNamespace,

    /// <summary>A global namespace using is no longer needed by any document.</summary>
    OrphanedGlobalNamespace,

    /// <summary>A global alias target type was pruned.</summary>
    OrphanedGlobalAlias,

    /// <summary>A global <c>using static</c> target type was pruned.</summary>
    OrphanedGlobalStatic,
}
