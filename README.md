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

Every progress update is written on its own line to stderr, preserving the full
history in both the terminal and redirected logs. The report is written to stdout.

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
  --exclude <glob>       Glob pattern to exclude symbols from the graph and
                         dependency analysis by fully-qualified name. Repeatable;
                         applied after --include and wins on conflict.
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
                         text    - human-readable, one section per node
                         json    - machine-readable, schema version 1.1
                         mermaid - Mermaid flowchart diagram (renderable in
                                   GitHub/VS Code Markdown)
                         body    - full source declarations with symbol/file/line headers
  -?, -h, --help         Show help and usage information.
  --version              Show version information.
```

### Glob patterns

`--include` and `--exclude` use simple globs matched **case-sensitively**
against the symbol's fully-qualified name (`Namespace.Type` or
`Namespace.Type.Member`):

- `*` matches any sequence of characters
- `?` matches a single character

`--include` selects **reported nodes only**; dependencies outside those patterns
can still be analyzed and appear as targets. `--exclude` removes matching symbols
from both reported nodes and edge targets, prevents them from being queued for
transitive analysis, and skips matching members when analyzing a rolled-up type.
Exclusions apply with or without `--transitive`, including with `--include-system`.
Excluding a property or event also excludes its associated accessors and backing field.
An exact type pattern does not match its members; use `Namespace.Type*` to exclude
both the type and its members.

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

With `--granularity member --transitive`, only referenced members are followed.
Referenced types remain edge targets, but do not cause all their members to be
analyzed. The `type` and `both` modes retain rolled-up type expansion.

## Output

### Text (default)

One section per emitted node, with edges grouped under `types:`, `methods:`,
`properties:`, `fields:`, and `events:`. Member edges show the fully qualified
member name; each edge line also shows its kind, namespace, and reference count.
A summary line gives node and edge counts, plus any filter/omission diagnostics.

### JSON (`--format json`)

A structured report (schema version `1.1`) suitable for tooling. The root object
echoes the effective options, the analyzed source, the list of emitted nodes
(each with its `types`/`members` edge arrays), a summary, and diagnostics. See
[`docs/spec.md`](docs/spec.md) for the full schema.

### Body (`--format body`)

Outputs each emitted symbol's full source declaration (signature and body),
preceded by a comment with the full symbol id, file name, and inclusive 1-based line range.
The declaration starts on the next line, with no intervening blank line:

```text
// MyApp.Service.Run(), Service.cs, 10-13
public void Run()
{
    Execute();
}

```

With `--granularity member`, only member declarations are printed, never their
enclosing classes. Type nodes print full type declarations. Partial declarations
are printed separately with their own file/line headers. Field/event variables include
their enclosing field/event declaration. Leading comments outside a declaration
are not included; symbols without an explicit declaration body (such as implicit
constructors) produce no section.

The usual node filters and empty-node omission apply to roots; use `--include-empty`
to include leaf roots. With `--transitive`, output also includes each reachable
source dependency once, including leaf dependencies and those outside the root
filters. Member mode includes only member dependencies, never whole types; type
mode includes only type dependencies. External symbols have no source body.
Without `--transitive`, only emitted roots are printed.

```powershell
dotnet run --project src/RoslynDepGraph -- samples/DepSample/DepSample.csproj `
    --format body --granularity member --include-empty
```

### Mermaid (`--format mermaid`)

A [Mermaid](https://mermaid.js.org/) `flowchart LR` diagram, suitable for
pasting into a fenced ` ```mermaid ` code block in GitHub, GitLab, or VS Code
Markdown. Each emitted node becomes a node (types use a stadium shape, members a
subroutine shape), and each outgoing edge becomes an arrow — direct edges are
solid, transitive edges (from `--transitive`) are dashed and labelled
`transitive`. Edge targets that were filtered out or omitted as empty still
appear as standalone nodes so no edge is silently dropped. See
[`docs/spec.md`](docs/spec.md) §10.3 for the full rules.

```powershell
dotnet run --project src/RoslynDepGraph -- samples/DepSample/DepSample.csproj `
    --format mermaid
```
