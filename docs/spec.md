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

- `--include` applies to the **reported nodes** only — the symbols whose outgoing
  edges are emitted. It does **not** affect edge targets or transitive traversal.
- Default (no flags): all source symbols are included.
- `--include` narrows to only matching symbols; multiple `--include` patterns
  are combined with OR.
- `--exclude` removes matching symbols from reported nodes and all edge targets,
  prevents their transitive analysis, and skips matching members in rolled-up
  type extraction. It is applied after `--include` and wins on conflict.
  Exclusions apply even without `--transitive` or with `--include-system`.
  Excluding a property or event also excludes its associated accessors and backing field.
  An exact type pattern does not exclude separately named members; use
  `Namespace.Type*` to match both.
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

> `--include` never affects targets. `--exclude` removes matching targets even
> when `--include-system` is enabled.

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
- With `--granularity member`, only member targets are expanded. Type targets
  remain in the report but are not expanded into their members' dependencies.
  Referenced methods, constructors, properties, fields, and events are followed
  transitively through their own signatures and bodies/initializers.
  The `type` and `both` modes retain rolled-up type expansion.
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

`--format text|json|mermaid|body` (default: `text`).

### 10.1 Text

- One section per emitted node, listing its edges grouped under `types:`,
  `methods:`, `properties:`, `fields:`, and `events:`.
- Member edge lines show the fully qualified member name; each edge line also
  shows the target kind, namespace, and reference count.
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

### 10.3 Mermaid

A [Mermaid](https://mermaid.js.org/) `flowchart LR` diagram suitable for
pasting into Markdown renderers that support fenced `mermaid` code blocks
(GitHub, GitLab, VS Code, etc.).

- The first non-comment line is `flowchart LR` (left-to-right).
- Each emitted node becomes a node:
  - **Type** nodes use a stadium shape: `n0(["B (type, global)"])`.
  - **Member** nodes use a subroutine shape: `n1[["M (method, global)"])`.
  - Labels are `Name (kind, namespace)` for types and `ContainingType.Name (kind, namespace)`
    for members (the containing type disambiguates same-named members across types).
    An empty namespace renders as `global`.
- **Subgraphs**: when the report contains member nodes, each containing type that
  has at least one emitted member is rendered as a `subgraph` holding those members
  (and the type node itself, if it is also emitted). Type nodes with no emitted
  members stay as standalone stadium-shaped nodes. When there are no member nodes
  at all (e.g. `--granularity type`), every node is emitted standalone (no subgraphs).
  Subgraphs are opened and closed lazily as the containing type changes across the
  (deterministic) node order — they are never nested. Member labels inside a
  subgraph drop the `ContainingType.` prefix since the subgraph title already names
  the type.
- Each outgoing edge (types + members) becomes an arrow:
  - Direct edges: `n0 --> n1`.
  - Transitive edges (`direct: false`, only with `--transitive`): `n0 -.->|transitive| n1`.
- Edge targets that are themselves emitted nodes link to that node. Edge targets
  that are **not** emitted (filtered out by `--include`, omitted as
  empty, or external when `--include-system` is set) are rendered as standalone
  plain-rectangle nodes (`t0["..."]`) so no edge is silently dropped.
- Edges are deduplicated on `(source, target, direct)`: if a node references the
  same target via both a type edge and a member edge, only one arrow is drawn.
- Node ids are positional (`n0`, `n1`, … for emitted nodes; `t0`, `t1`, … for
  non-emitted targets; `sg0`, `sg1`, … for subgraphs) and are assigned in report
  order, which is deterministic (see §11). Symbol ids are not used directly
  because they contain characters that are unsafe in Mermaid node ids.
- The diagram is preceded by `%%` comment lines echoing the tool, source, and
  summary counts.

### 10.4 Body

- Each emitted source declaration is rendered as
  `// <id>, <source-file>, <start-line>-<end-line>`, immediately followed on the next
  line by the full declaration (signature and body), then a blank line.
  Headers include the full symbol id and no blank line precedes the declaration.
- File names omit the directory; line ranges are inclusive and 1-based and cover
  the declaration, excluding leading/trailing trivia.
- Member granularity emits only member declarations, never enclosing classes.
  Type nodes emit complete type declarations.
- Partial declarations, including partial member implementations, produce
  separate sections with their own file/line headers.
- Fields and field-like events include the enclosing declaration, preserving
  modifiers, declared type, and initializers (including other variables in a
  shared declaration).
- Symbols without an explicit declaration body, including implicit/primary
  constructors whose syntax refers to an enclosing type, produce no section.
- Node filters and empty-node omission apply to roots as in other formats.
  With `--transitive`, also emit reachable source dependencies (direct and
  transitive), including leaf dependencies and targets outside the root filters.
  Member mode includes only member dependencies; type mode includes only type
  dependencies; both mode includes both. Roots appear first, followed by
  dependencies ordered by symbol id (ordinal), with each symbol emitted once
  apart from its separate partial declarations. External targets have no body.
  Without `--transitive`, only emitted roots are printed.
- There is no report preamble, edge list, or summary in this format. Other formats
  retain their existing root-node and edge representations.

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
  --format text|json|mermaid|body  Default: text.
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
