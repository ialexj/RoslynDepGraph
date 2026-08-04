# RoslynSourceMap

`RoslynSourceMap` is a command-line tool that parses one or more C# files with
Roslyn's **syntax-only** API (no semantic model, no MSBuild) and emits a JSON
map of every type and member declaration, including each declaration's leading
comments.

It's useful for answering questions like:

- What classes and members exist in a generated file?
- Which members carry a `// Generated from: ...` comment?
- Where is each type/member declared (file, line, column)?

## Quick start

```powershell
dotnet run --project src/RoslynSourceMap -- <path-to-cs-file-or-dir-or-glob>
```

The first positional argument is a path to a `.cs` file, a directory (searched
recursively for `*.cs`), or a glob pattern (e.g. `**/*.cs`).

## Usage

```
RoslynSourceMap <path> [options]

Arguments:
  <path>                 Path to a .cs file, a directory, or a glob pattern.

Options:
  -?, -h, --help         Show help and usage information.
  --version              Show version information.
```

## Examples

Parse a single file:

```powershell
dotnet run --project src/RoslynSourceMap -- samples/SourceMapSample/Sample.cs
```

Parse every `.cs` file under a directory:

```powershell
dotnet run --project src/RoslynSourceMap -- src
```

Parse a glob:

```powershell
dotnet run --project src/RoslynSourceMap -- "**/*.cs"
```

## JSON schema (version 1.0)

```jsonc
{
  "tool": "RoslynSourceMap",
  "schemaVersion": "1.0",
  "files": [
    {
      "path": "samples/SourceMapSample/Sample.cs",
      "types": [
        {
          "kind": "class",
          "name": "Auto",
          "namespace": "Optiware",
          "isRecord": false,
          "modifiers": ["public"],
          "parameters": [],
          "leadingComments": null,
          "location": { "file": "...", "line": 6, "column": 18 },
          "members": [
            {
              "kind": "method",
              "name": "Command1_Click",
              "containingType": "Auto",
              "modifiers": ["private"],
              "returnType": "void",
              "parameters": [],
              "leadingComments": "// Generated from: Optiware/Auto.ctl:61",
              "location": { "file": "...", "line": 8, "column": 9 },
              "members": []
            }
          ]
        }
      ],
      "diagnostics": []
    }
  ]
}
```

### Fields

- `kind` — a type kind (`class|struct|interface|enum|delegate|record`) or a
  member kind (`method|constructor|destructor|property|indexer|field|
  event|event-field|operator|conversion-operator`).
- `name` — the declared identifier (indexers are named `this`).
- `fullName` — the name plus its comma-separated parameter types, e.g.
  `WriteNode(TextWriter, DependencyNode)` for a method. Members without
  parameters (properties, fields, events) have `fullName` equal to `name`.
- `containingType` — the nearest enclosing type's name, for nested declarations.
- `namespace` — the namespace, when declared inside one.
- `isRecord` — true for record class/struct declarations.
- `modifiers` — the declared modifiers, verbatim (e.g. `public`, `static`).
- `returnType` — the declared return type, where applicable.
- `parameters` — name/type/modifiers/default for methods, constructors,
  indexers, operators, delegates, and record primary constructors.
- `leadingComments` — the comment trivia immediately preceding the declaration
  (verbatim, including `//`, `/* */`, `///` markers), or `null` when none.
- `location` — file path and 1-based line/column of the declaration.
- `members` — nested members; populated only for type declarations (recursive).
- `diagnostics` — Roslyn parse diagnostics for the file (extraction still
  proceeds best-effort when diagnostics are present).

### Notes

- Extraction is **syntax-only**: effective accessibility, resolved types, base
  types, and inherited members are not computed. Modifiers are reported verbatim.
- Fields with multiple declarators (`int a, b;`) produce one entry per name,
  sharing the type, modifiers, and leading comments.
- Nested types appear in their parent's `members` array (a tree, not flattened).
