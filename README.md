# RoslynDepGraph

A .NET 10 console application that loads a `.csproj` or `.sln` into Roslyn
(`MSBuildWorkspace`) and produces a **code dependency report**: which types and
members each symbol in the codebase references.

## Status

🚧 Scaffolded — implementation in progress. See [`/docs`](docs/) for the design plan.

## Repository layout

```
RoslynDepGraph/
├── docs/                       # Design notes and the project plan
├── src/                        # Main application projects
│   └── RoslynDepGraph/         # The console app
├── tests/                      # Test projects
│   └── RoslynDepGraph.Tests/   # xUnit tests
├── RoslynDepGraph.slnx
└── README.md
```

## Build

```powershell
dotnet build RoslynDepGraph.slnx
```

## Run

```powershell
dotnet run --project src/RoslynDepGraph -- <path-to-csproj-or-sln> [options]
```

> CLI options (`--granularity`, `--include`, `--exclude`, `--transitive`,
> `--include-system`, `--exclude-self`, `--include-empty`, `--format`) are
> documented in the [plan](docs/plan.md).

## Target framework

- .NET 10 (`net10.0`) with `<RollForward>LatestMajor</RollForward>` for SDK resilience.

## License

MIT — see [`LICENSE`](LICENSE).
