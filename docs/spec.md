# RoslynDepGraph — Specification

## 1. Purpose

`RoslynDepGraph` is a .NET 10 console application that loads a `.csproj` or `.sln`
and produces a **code dependency report**: for each type and member in the
codebase, the set of types and members it references.

The report is a directed graph of **outbound** dependency edges ("what does X
depend on?"), not inbound references ("who uses X?").

## 2. Input

- A single positional argument: the path to a `.csproj` or `.sln` file.
- The application loads the project/solution and analyzes all C# source symbols
  defined within it. (VB projects may be present in a solution but are not
  analyzed in v1.)

## 3. Default behavior

With no options beyond the input path, the application:

1. Loads the project/solution.
2. Enumerates every **source-defined** type and member across all loaded
   projects (symbols that have at least one source location).
3. For each such symbol, computes its outgoing dependency edges (the types and
   members it references).
4. Emits a report containing one **node** per analyzed symbol, each with its
   edges grouped into `types` and `members`.
5. **Omits** any node that has no outgoing edges (leaf/empty nodes are not
   reported by default — see `--include-empty`).

## 4. Granularity

`--granularity type|member|both` (default: `both` when omitted).

| Value    | Type nodes | Member nodes | Type-node edge scope |
|----------|------------|--------------|----------------------|
| `type`   | yes (rolled-up) | no | **rolled-up**: declarations + all member bodies; member→member edges roll up to the containing type |
| `member` | no | yes (per-member) | n/a — per-member scope (signature + body, or declared type + initializer for fields) |
| `both`   | yes (rolled-up) | yes (per-member) | same rolled-up scope as `type` |

- **Rolled-up type scope**: a type node's edges are the union of references
  found in the type's own declarations (base types, implemented interfaces,
  type-parameter constraints, attributes, declared types of fields/properties/
  events) **and** all of its members' signatures and bodies. Any edge that
  would point to a member is rolled up to that member's containing type
  (e.g. `B.M → A.DoSomething()` becomes part of `B → A`). A type never has an
  edge to itself.
- **Per-member scope**: a member node's edges come only from that single
  member (its signature and body, or for fields its declared type and
  initializer). No rollup.
- In `both` mode, the rolled-up type view and the per-member view coexist as
  separate nodes in the same report. This is intentional.

## 5. Filtering reported nodes

`--include <glob>` and `--exclude <glob>` (each repeatable).

- Apply to the **reported nodes** only — the symbols whose outgoing edges are
  emitted. They do **not** affect edge targets.
- Default (no flags): all source symbols are included.
- `--include` narrows to only matching symbols; multiple `--include` patterns
  are combined with OR.
- `--exclude` removes matching symbols and is applied after `--include`;
  `--exclude` wins on conflict.
- Patterns are globs (`*` = any sequence, `?` = single character), matched
  case-sensitively against the symbol's fully-qualified name
  (`Namespace.Type` or `Namespace.Type.Member`).
- A diagnostic reports how many nodes each filter removed.

## 6. Filtering edge targets (system / external)

By default, edge **targets** are restricted to source-defined symbols (symbols
with at least one source location; primitive/special types excluded). This
yields a "your code only" report.

`--include-system` disables this restriction, allowing framework types,
primitives, and all external/metadata symbols to appear as edge targets.

> `--include`/`--exclude` never affect targets — only `--include-system`
> controls target visibility.

## 7. Self-reference filtering

- The exact node symbol is always excluded from its own edges (a symbol does
  not depend on itself).
- For **member** nodes, sibling members of the same containing type are
  **included by default**.
- `--exclude-self` (applies to member nodes only) drops sibling members of the
  same containing type **and** the containing type itself from that node's
  edges.
- For **type** nodes, `--exclude-self` has no additional effect (a type
  referencing its own members is normal and kept; only the self-edge is
  dropped).

## 8. Direct vs. transitive dependencies

- By default, only **direct** edges are reported.
- `--transitive` computes the full transitive closure: for each dependency
  target, its own dependencies are also followed and added to the report.
- The closure is cycle-safe.
- Edges that are only reachable transitively (not direct) are flagged as such
  in the output.

## 9. Empty-node omission

- By default, nodes with no outgoing edges (no type deps and no member deps)
  are **omitted** from the report, in both text and JSON output.
- `--include-empty` opts into emitting leaf/empty nodes too (with empty edge
  arrays).
- The count of omitted empty nodes is reported in the summary and diagnostics.

## 10. Output formats

`--format text|json` (default: `text`).

### 10.1 Text

- One section per emitted node, listing its edges grouped under `types:` and
  `members:`.
- Each edge line shows the target name, kind, namespace, and the source
  location(s) where it is referenced.
- `(none)` is shown where a group is empty.
- A summary line gives node/edge counts.

### 10.2 JSON

Schema version `1.1`. Root object:

```jsonc
{
  "schemaVersion": "1.1",
  "tool": "RoslynDepGraph",
  "generatedAt": "2026-08-01T14:32:00Z",   // ISO-8601 UTC
  "options": {
    "granularity": "both",                  // type|member|both
    "transitive": false,
    "includeSystem": false,
    "excludeSelf": false,
    "includeEmpty": false,                  // false = omit empty nodes (default)
    "include": [],                          // echoed include globs
    "exclude": []                           // echoed exclude globs
  },
  "source": { "path": "...", "kind": "project" },  // "project" | "solution"
  "nodes": [
    {
      "id": "B.M",                          // stable, dedup, graph-link key
      "name": "M",
      "kind": "method",                     // type|method|property|field|event
      "containingType": "B",                // null for type nodes
      "namespace": "",
      "location": { "file": "Samples.cs", "line": 7, "column": 8 },
      "edges": {
        "types":   [ /* Dependency[] */ ],
        "members": [ /* Dependency[] */ ]
      }
    }
  ],
  "summary": {
    "nodeCount": 3,                         // emitted nodes (post empty-omission)
    "edgeCount": 4,                         // total outgoing edges
    "directEdges": 4,
    "transitiveEdges": 0,
    "nodesExcludedByFilter": 2,
    "nodesOmittedEmpty": 3                 // 0 when includeEmpty=true
  },
  "diagnostics": []                        // warnings, filter/exclusion notes
}
```

Each `Dependency` (edge target):

```jsonc
{
  "id": "A",                               // stable, dedup, graph-link key
  "name": "A",
  "kind": "type",                          // type|method|property|field|event
  "containingType": null,                  // null for types; "B" for a B.M member dep
  "namespace": "",
  "assembly": "DepSample",                 // source assembly or metadata assembly name
  "isExternal": false,                     // false = defined in loaded solution
  "direct": true,                          // false = only reached via --transitive
  "references": [                          // all source locations where this node references the target
    { "file": "Samples.cs", "line": 5, "column": 3 }
  ]
}
```

#### JSON schema rules

- `id` is a stable, run-to-run consistent identifier for the symbol, used as
  the dedup key and for graph consumers to link nodes. Always present.
- `nodes[]` is the set of reported symbols after filtering and empty-omission.
  It is an empty array (never omitted as a key) when all nodes are filtered or
  omitted. With `includeEmpty=true`, leaf nodes appear with empty
  `edges.types`/`edges.members` arrays.
- `summary.nodeCount` counts emitted nodes only; `nodesOmittedEmpty` tracks
  dropped leaves separately.
- `references[]` per edge lists the locations **within this node's source**
  where the target is referenced (not global). It is non-empty for every edge.
- `isExternal` is `false` for symbols defined in the loaded solution. With
  `includeSystem=false`, only `isExternal:false` targets appear; `diagnostics`
  notes the count excluded.
- `direct: false` edges appear only when `options.transitive=true`.
- `summary.edgeCount` is the sum of all `nodes[].edges.types` and
  `edges.members` lengths.
- `generatedAt` is the UTC timestamp in ISO-8601 round-trip format.
- JSON is emitted indented with camelCase property naming; default values are
  serialized (not omitted) so the schema shape is stable.

## 11. CLI summary

```
RoslynDepGraph <path> [options]

Options:
  --granularity type|member|both   Default: both
  --include <glob>                 Repeatable; OR'd. Filters reported nodes.
  --exclude <glob>                 Repeatable; wins over --include.
  --transitive                     Compute full transitive closure.
  --include-system                 Allow framework/external edge targets.
  --exclude-self                   Drop sibling members + containing type (member nodes).
  --include-empty                  Emit nodes with no edges.
  --format text|json               Default: text.
  --help
```

## 12. Worked example

Given this input (`Samples.cs`):

```csharp
class A {
    public void DoSomething() { }
}

class B {
    A member;

    void M() {
        member.DoSomething();
    }

    void V() { }
}
```

### Default run (`--granularity both`)

Emitted nodes (empty nodes `A`, `A.DoSomething()`, `B.V` omitted):

| Node | types | members |
|---|---|---|
| `B` | A | (none) |
| `B.member` | A | (none) |
| `B.M` | A | A.DoSomething(), B.member |

- `B → A` is rolled up from the field `member`'s declared type and `M`'s body.
- `B.M → B.member` is included by default (sibling member).
- `summary.nodesOmittedEmpty == 3`.

### `--granularity type`

| Node | types | members |
|---|---|---|
| `B` | A | (none) |

(`A` omitted as empty.)

### `--granularity member`

| Node | types | members |
|---|---|---|
| `B.member` | A | (none) |
| `B.M` | A | A.DoSomething(), B.member |

(`A.DoSomething()` and `B.V` omitted as empty; `A` is an edge target, not a node.)

### `--granularity both --exclude-self`

Same nodes as the default, but `B.M` edges become:

| Node | types | members |
|---|---|---|
| `B.M` | A | A.DoSomething() |

(`B.member` dropped as a sibling.)

### `--granularity type --transitive`

| Node | types | members |
|---|---|---|
| `B` | A (direct) | (none) |

Since `A` has no edges, the closure adds nothing beyond the direct edge.

## 13. Scope and non-goals (v1)

- **C# only.** VB projects in a solution load but are not analyzed.
- **Source-only by default.** Edge targets are restricted to symbols defined
  in the loaded solution; `--include-system` is the single escape hatch.
- **Outbound dependencies only.** The report answers "what does X depend on?",
  not "who depends on X?".
- **Glob patterns** (`*`/`?`) on fully-qualified names; no regex.
- **No persistence.** The report is printed to stdout; no database or cache.
