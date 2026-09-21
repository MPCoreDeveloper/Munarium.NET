# Releasing

A release here is a set of NuGet packages and the promises that travel with them. This document says what
a release publishes, how one is cut, and what has to be true before the tag is pushed - each of the three
measured against the actual output rather than described from memory.

## What a release publishes

Five libraries, each with a symbol package beside it:

| Package | What it carries |
|---|---|
| `Munarium.Core` | The kernel - ledger, governance, claims, shapes, facts and pins, chronology, digest ladder, context, retrieval seams, budgets and the provider contracts |
| `Munarium.Wire` | The JSON models and operations generated from the contract, the protobuf message shapes, and `contract/munarium.v1.yaml` - the same specification this repository generates its own surface from |
| `Munarium.Providers` | The provider adapters |
| `Munarium.Runbooks` | Applying and validating runbook YAML |
| `Munarium.Store.SharpCoreDb` | The SharpCoreDB storage, search, index and provider stores |

Inside every package, measured by reading the `.nupkg` back:

| Entry | Why it is there |
|---|---|
| `lib/net11.0/<assembly>.dll` and `.xml` | The library, and the XML documentation its doc comments produce |
| `LICENSE`, `NOTICE` | Apache-2.0 requires both to travel with the artifact; `NOTICE` carries the original's attribution |
| `CREDITS.md` | This is a derived work, so the credit ships with the artifact and not only with the repository |
| `README.md`, `icon.png` | What a reader of the package page sees |
| `lib/net11.0/<assembly>.pdb` (in the `.snupkg`) | Symbols, with a source link to the commit they were built from |

The source link is what makes a package debuggable without a local build: the PDB's document map points at
`https://raw.githubusercontent.com/MPCoreDeveloper/Munarium.NET/<commit>/*`, written by SourceLink from the
repository and the commit the build ran in.

**Not published:** `Munarium.Server` and `tools/Munarium.AotSmoke`. Both are marked not packable on
purpose - the first is the host, which a deployment builds from source so that its transport, storage and
provider wiring are the deployment's decision rather than a package's, and the second is a test tool. The
contract they serve is published: it is `openapi/munarium.v1.yaml`, and it travels in `Munarium.Wire`.

## How a release is cut

`.github/workflows/release.yml` packs and pushes. It reads the API key from the `NUGET_API_KEY` repository
secret, never from a file, and it pushes both the `.nupkg` and the `.snupkg` files.

| How it is started | Where the version comes from | What gets packed |
|---|---|---|
| Push a tag `v0.1.0-preview.2` | The tag, with the leading `v` stripped | `0.1.0-preview.2` |
| `workflow_dispatch` with a version suffix, e.g. `rc.1` | `Directory.Build.props`' prefix, with the suffix | `0.1.0-rc.1` |
| `workflow_dispatch` with no suffix | `Directory.Build.props` | `0.1.0-preview.1` |

The version is deliberately split into a prefix and a suffix in `Directory.Build.props`, and that is not
cosmetic. With a single `Version` property, `-p:VersionSuffix=rc.1` is silently ignored - `Version` wins
over `VersionSuffix` - so the dispatched path above would have packed and pushed the version already
published, and the skip-duplicate switch would have turned that into a release that reported success and
published nothing. All three rows were measured by packing `Munarium.Core` and reading the file name back.

Tags are what cut a release; a dispatch is for a preview out of a branch, which is why it can only move the
suffix. A published NuGet version can never be replaced or deleted, so the version is what has to be right
*before* the tag is pushed, not after.

## What has to be true before the tag

The order the workflow runs is the checklist; most of it is already done by CI on every push.

| Step | Where it runs | What it proves |
|---|---|---|
| `dotnet build Munarium.slnx -c Release` | CI, every push | Compiles with analyzers and warnings as errors |
| `dotnet test Munarium.slnx -c Release` | CI, every push | 956 tests across five suites |
| SonarScanner begin/end | CI, when `SONAR_TOKEN` is set | The analysis the repository is held to |
| `pwsh tools/release-packages.ps1` | CI, every push; the release workflow, before pushing | The packages, read back |
| NativeAOT smoke, three RIDs | CI, every push | The kernel publishes and runs as a native binary on linux-x64, win-x64 and osx-arm64 |
| `tools/spec-coverage.ps1` | By hand, when the wire changes | How much of the original's surface is served, and that the figure is still the floor in `UpstreamContractTests` |
| The documents | By hand, with the change | README, CHANGELOG and `docs/` describe what the tagged commit actually does |

### The packaging gate

`tools/release-packages.ps1` is what stops a release from publishing something nobody decided to publish.
It packs the way the workflow packs and then reads each package back:

```powershell
pwsh tools/release-packages.ps1                                # pack (building if needed), then check
pwsh tools/release-packages.ps1 -NoBuild                       # pack without rebuilding, then check
pwsh tools/release-packages.ps1 -CheckOnly                     # check packages that are already packed
pwsh tools/release-packages.ps1 -CheckOnly -Version 0.1.0      # check what a tag would publish
```

CI runs the second form, because it has already built; the release workflow runs the third, because it has
already packed. When the script packs, it removes the five packages it is about to produce from the output
folder first: a package left there by an earlier pack could otherwise answer for a pack that just failed,
and the check would pass on an artifact nobody built.

It fails, naming each reason, when

- a package outside the five above is produced - which is how a test or tool project that became packable
  by accident is caught before it reaches NuGet, where it could not be removed;
- a package is missing its symbol package, its library, its licence, notice, credits, readme or icon;
- a package's version is not the one the release asked for, which is the check that would have caught the
  ignored `VersionSuffix` above.

Reading the version out of the package's own manifest is deliberate: `Munarium.Core.0.1.0-preview.1.nupkg`
cannot be split back into an id and a version by its file name, because the id contains dots.

## Known constraints on a release

These are measured, not inherited from a template:

- **The version has to be a prerelease while the stack is a prerelease.** Packing a stable version fails by
  design: `NU5104: A stable release of a package should not have a prerelease dependency`, raised for
  `Posseth.UlidFactory 2.1.0-RC.1` and `SharpDispatch 1.1.0-RC.1` (and `SharpCoreDB 2.1.0-RC.3` for
  `Munarium.Store.SharpCoreDb`) and escalated to an error by warnings-as-errors. A `v0.2.0` tag today
  therefore fails at the tag rather than publishing a package that promises more stability than its
  dependencies have. When the stack ships stable versions, the constraint goes with them.
- **A consumer needs the .NET 11 SDK.** The packages target `net11.0`, and the toolchain is not optional:
  the port's unions need BCL surface that only .NET 11 provides.
- **The packages are preview, and the wire contract can move between previews.** What is served, and what is
  deliberately absent, is listed item by item in the README and in [decisions.md](decisions.md); behavior
  that is not in those lists is not implied by a version number.

## What a release does not change

A release is a publication, not a change of state: no code path, no contract and no document is allowed to
differ in the tagged commit from the commit CI checked. If a release needs a fix, the fix is a commit, CI
covers it, and the tag follows - a tag is never the place a change first sees a build.

## After a push

The packages appear on NuGet with their symbols, and a consumer can debug into them. `NUGET_API_KEY` should
be a key scoped to pushing new packages: the repository never needs a key that can unlist or delete, because
a release deletes nothing.

## The documentation a release carries

- [README.md](../README.md) - what this port is, what is in, and what is not ported yet
- [CHANGELOG.md](../CHANGELOG.md) - what a version contains, and what a reader should not expect in it
- [docs/decisions.md](decisions.md) - every decision with what was measured for it
- [docs/method.md](method.md) - how a claim about the original gets checked
