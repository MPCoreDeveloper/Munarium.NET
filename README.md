# Munarium.NET

**Governed memory and traceable evidence for AI applications — the C# port.**

Munarium.NET is a from-scratch C# implementation of Munarium (originally written in Rust).
It keeps the same promise — an append-only fact ledger with governance in the write path,
hybrid retrieval that carries a provenance envelope on every answer, and bring-your-own-key
model providers — and re-homes it on the .NET 11 / C# 15 toolchain.

This is an independent port. It does not track the upstream Rust repository and shares no
build tooling with it.

## Toolchain

| Concern | Choice |
|---|---|
| Runtime | .NET 11 (RC), pinned in `global.json` |
| Language | C# 15 (`<LangVersion>preview</LangVersion>`) |
| Storage & retrieval | SharpCoreDB (embedded, encrypted, vector + GraphRAG) |
| Command path | SharpDispatch (zero-allocation CQRS dispatch) |
| Wire / codegen | SharpPortico (OpenAPI → gRPC) plus protobuf-first MMP stubs |
| Identifiers | Posseth.UlidFactory (spec-compliant ULIDs) |

## Repository layout

```
Munarium.NET/
├── src/
│   └── Munarium.Core/        the pure kernel (ledger, gates, composition)
├── tests/
│   └── Munarium.Core.Tests/  unit tests (xunit.v3)
├── Directory.Build.props     shared build settings
├── Directory.Packages.props  central package management
└── global.json               pinned .NET SDK
```

## Build and test

```powershell
dotnet tool restore
dotnet build Munarium.slnx -c Release
dotnet test  Munarium.slnx -c Release
```

## Engineering standards

- Every project targets `net11.0`; `Nullable` and `ImplicitUsings` are enabled.
- The .NET analyzers and `SonarAnalyzer.CSharp` run on every build; **warnings are errors** in `src/`.
- Code style is enforced in the build (`EnforceCodeStyleInBuild`) using the rules in `.editorconfig`.
- Package versions live in one place (`Directory.Packages.props`, Central Package Management).
- SonarCloud analysis runs in CI through the .NET SonarScanner. Its properties live in
  `.github/workflows/ci.yml` — the SonarScanner for .NET does **not** read `sonar-project.properties`,
  so the project key, organization and settings are passed as `/d:` arguments on `begin`.

## Consuming the MPCoreDeveloper net11 RCs

The dogfood packages ship as net11 RC builds. They resolve from nuget.org once published, or
from the local `mpcore-rc` feed declared in `NuGet.config`:

```powershell
Copy-Item ..\SharpCoreDB\artifacts\*.nupkg         artifacts\packages\
Copy-Item ..\SharpDispatch\artifacts\*.nupkg       artifacts\packages\
Copy-Item ..\SharpPortico\artifacts\*.nupkg        artifacts\packages\
Copy-Item ..\posseth.global.ulid\artifacts\*.nupkg artifacts\packages\
```

## CI and secrets

Two repository secrets drive automation (Settings → Secrets and variables → Actions):

| Secret | Used by | Purpose |
|---|---|---|
| `NUGET_API_KEY` | `.github/workflows/release.yml` | pushes packages to NuGet.org |
| `SONAR_TOKEN` | `.github/workflows/ci.yml` | SonarCloud analysis |

Set them from a terminal with the GitHub CLI (the value is read from a prompt, never logged):

```powershell
gh secret set NUGET_API_KEY
gh secret set SONAR_TOKEN
```

Neither secret is ever written to the repository. To publish manually:

```powershell
$env:NUGET_API_KEY = '<your key>'
dotnet pack Munarium.slnx -c Release -o artifacts/nugetout
Get-ChildItem artifacts/nugetout/*.nupkg | ForEach-Object {
  dotnet nuget push $_.FullName --api-key $env:NUGET_API_KEY --source https://api.nuget.org/v3/index.json
}
```

## License

Apache-2.0, the same license as the original Munarium.
