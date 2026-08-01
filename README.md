# RoslynDepGraph

`RoslynDepGraph` is a command-line tool that loads a C# `.csproj`, `.sln`, or
`.slnx` file with Roslyn and produces a **code dependency report**: for every
type and member defined in your source, the set of types and members it
references.

The report is a directed graph of **outbound** dependency edges — "what does X
depend on?" — not inbound references ("who uses X?"). It's useful for answering
questions like:

- What does `MyService.PlaceOrder` actually touch?
- Which types pull in `System.Net.Http`?
- What's the blast radius if I change `Order`?
- What's the transitive dependency set of a single entry-point method?

## Quick start

```powershell
dotnet run --project src/RoslynDepGraph -- <path-to-csproj-or-sln> [options]
```

The first positional argument is the path to the project or solution to
analyze. Everything else is optional.

## Usage

```
RoslynDepGraph <path> [options]

Arguments:
  <path>                 Path to a .csproj, .sln, or .slnx file.

Options:
  --granularity <value>  Granularity of the report. (default: both)
                         type    - one rolled-up node per type
                         member  - one node per member (no type rollup)
                         both    - both views in the same report
  --include <glob>       Glob pattern to include reported nodes by fully-qualified
                         name. Repeatable; multiple patterns combine with OR.
  --exclude <glob>       Glob pattern to exclude reported nodes by fully-qualified
                         name. Repeatable; applied after --include and wins on
                         conflict.
  --transitive           Compute the full transitive closure of dependencies
                         (cycle-safe). Edges only reachable transitively are
                         flagged as such in the output.
  --include-system       Allow framework/external (non-source) symbols as edge
                         targets. By default only source-defined symbols appear as
                         targets, giving a "your code only" report.
  --exclude-self         For member nodes, drop sibling members of the same
                         containing type and the containing type itself from that
                         node's edges. (No effect on type nodes beyond the
                         always-dropped self-edge.)
  --include-empty        Emit nodes that have no outgoing edges. By default leaf
                         nodes are omitted; the count of omitted nodes is reported
                         in the summary.
  --format <value>       Output format. (default: text)
                         text - human-readable, one section per node
                         json - machine-readable, schema version 1.1
  -?, -h, --help         Show help and usage information.
  --version              Show version information.
```

### Glob patterns

`--include` and `--exclude` use simple globs matched **case-sensitively**
against the symbol's fully-qualified name (`Namespace.Type` or
`Namespace.Type.Member`):

- `*` matches any sequence of characters
- `?` matches a single character

Filters apply to **reported nodes only** — the symbols whose outgoing edges are
emitted. They never affect edge targets; target visibility is controlled solely
by `--include-system`.

## Examples

Analyze a project with the defaults (both type and member granularity, source
symbols only as targets, leaf nodes omitted):

```powershell
dotnet run --project src/RoslynDepGraph -- samples/DepSample/DepSample.csproj
```

Report only the dependencies of a single namespace, as JSON:

```powershell
dotnet run --project src/RoslynDepGraph -- samples/DepSample/DepSample.csproj `
    --include "MyApp.Services.*" --format json
```

Get the full transitive closure for a solution, including framework types:

```powershell
dotnet run --project src/RoslynDepGraph -- MySolution.sln `
    --transitive --include-system
```

Focus on a single method's outgoing edges, excluding its own type's members:

```powershell
dotnet run --project src/RoslynDepGraph -- MySolution.sln `
    --include "MyApp.OrderService.PlaceOrder" --exclude-self --granularity member
```

## Output

### Text (default)

One section per emitted node, with edges grouped under `types:` and `members:`.
Each edge line shows the target name, kind, namespace, and the source location(s)
where it's referenced. A summary line gives node and edge counts, plus any
filter/omission diagnostics.

### JSON (`--format json`)

A structured report (schema version `1.1`) suitable for tooling. The root object
echoes the effective options, the analyzed source, the list of emitted nodes
(each with its `types`/`members` edge arrays), a summary, and diagnostics. See
[`docs/spec.md`](docs/spec.md) for the full schema.
