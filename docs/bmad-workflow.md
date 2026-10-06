# BMad Method workflow

Munarium.NET is set up for the [BMad Method](https://docs.bmad-method.org) so that planning, implementation,
review and verification are explicit and versioned in this repository instead of living in a chat history. It is
the same install the sibling repositories in this stack use, done the same way.

- **Installed:** BMad Method `6.13.0-next` from `bmad-code-org/BMAD-METHOD` (skills channel), project scope.
- **Skills:** 33 entries under `.agents/skills` — 30 skills in three modules
  (`core-tools`: 8, `method`: 20, `toolsmith`: 2) plus the three module records that carry them.
- **Runtime:** `_bmad/`, created by `bmad setup` (`status: created`, `current: true`).
- **Tools:** the same skill files serve **Cline** and **GitHub Copilot**, because both read project skills from
  `.agents/skills`.

## What the setup added

| Path | Commit to git | Purpose |
| --- | --- | --- |
| `.agents/skills/bmad*/`, `.agents/skills/bmod*/` | yes | The 33 installed entries: agents, planning, implementation, review, validation and toolsmith skills. |
| `skills-lock.json` | yes | Installed skill versions and their source; what `npx skills update` reads. |
| `_bmad/config.toml` | yes | Team configuration: `project_name = "Munarium.NET"`, output folder `{project-root}/_bmad-output`. |
| `_bmad/scripts/`, `_bmad/core-tools/scripts/`, `_bmad/method/scripts/`, `_bmad/toolsmith/scripts/` | yes | The runtime the skills call (`setup.py`, `knowledge.py`, `tickets.py`, and their tests). Byte-identical copies of the packaged scripts, refreshed by `bmad setup`. |
| `_bmad/custom/` | yes | Team overrides. Personal answers go to `config.user.toml`, which `_bmad/custom/.gitignore` keeps out of the repository. |
| `_bmad-output/` | yes | Planning and implementation artifacts, created on the first artifact. |

The commands, for troubleshooting:

```bash
npx skills list                                                              # the installed skills
uv run --no-cache .agents/skills/bmad/scripts/setup.py --project-root . --skill .agents/skills/bmad --status
uv run --no-cache .agents/skills/bmad/scripts/knowledge.py --content --root .agents/skills
```

## Prerequisites

| Tool | Why |
| --- | --- |
| Node.js 22+ / npm and git | Installing and updating the skills (`npx skills`). |
| [uv](https://docs.astral.sh/uv/) | Running the BMad Python scripts. Required: `bmad setup` stops without it and the runtime is never written another way. |
| The .NET 11 SDK | Building and testing this repository — the toolchain the packages target. |

## Install, update, repair

```bash
# install (already done in this repository)
npx skills add bmad-code-org/BMAD-METHOD --skill '*' --agent cline --agent github-copilot --copy -y

# update the skills, then refresh the project runtime
npx skills update
# then ask the agent to run: bmad setup

# status / doctor (read-only)
# ask the agent to run: bmad status
```

`--copy` rather than the default symlinks, because creating symlinks on Windows needs Developer Mode or
elevated rights. `bmad setup` is an upsert: it repairs stale `_bmad` scripts and asks only the questions that
are still open, so it is also what runs after `npx skills update`.

## Working with it

Invoke a skill by name; a bare change request, issue, or story counts as input for `bmad-build`. `bmad-spec` is
the hub: it condenses any input into a spec folder that every build reads, and the analysis and planning skills
exist to give it better material rather than to form a mandatory sequence.

| Situation | Skill |
| --- | --- |
| Set up or refresh the repository's agent instructions (`AGENTS.md`) | `bmad-project-context` |
| A change that fits one session, described to the agent as work | `bmad-build` |
| The same, unattended or across several stories | `bmad-build-auto` |
| Planning before a larger change | `bmad-product-brief` or `bmad-prfaq`, then `bmad-prd` |
| One document that a build session reads | `bmad-spec` |
| Turn a plan into stories and keep the board | `bmad-ticket` |
| Decisions that keep the kernel, the adapters and the wire consistent | `bmad-architecture` |
| Review a diff, a document or a plan that is not yours | `bmad-review` |
| Review an implementation the way a reviewer of this repository would | `bmad-code-review` |
| End-to-end coverage for a feature that has none | `bmad-qa-generate-e2e-tests` |
| Understand a change well enough to hand it over | `bmad-walkthrough` |
| Close out an epic | `bmad-retrospective` |
| What is installed, and what to do next | `bmad` (help / `bmad status`) |

The core-tools skills stand beside these and belong to no phase: `bmad-brainstorming`, `bmad-forge-idea`,
`bmad-deep-recon`, `bmad-advanced-elicitation`, `bmad-party-mode`, `bmad-customize`. The five agent personas
(`bmad-agent-analyst`, `-pm`, `-ux-designer`, `-architect`, `-dev`) are the same work with a guide who knows one
phase end to end.

## Guardrails this repository already has

BMad does not replace the rules this repository runs on; the skills have to respect them, and the sources below
stay authoritative.

- **The analyzer set is the gate** — the .NET analyzers and `SonarAnalyzer.CSharp` run on every build, warnings
  are errors in shipping code, and the libraries are NativeAOT-compatible so reflection and codegen hazards fail
  the build. `dotnet build Munarium.slnx` and `dotnet test Munarium.slnx` are what a change is held to, and the
  full suite has to be green before a change is called done.
- **The contract is one file, and the surface is held to it** — `openapi/munarium.v1.yaml` is the contract and
  the gRPC service is generated from it. What is served is checked by `WireSurfaceConformanceTests` and
  `UpstreamContractTests`, against the floor that `tools/spec-coverage.ps1` measures, so an operation is either
  served or named as not ported.
- **A claim about the original gets read before it gets written** — [docs/method.md](method.md). The skills are
  held to the same rule the port is.
- **What is not ported is named rather than inferred** — the README's *Known limitations* and *What is not
  ported yet* sections, plus [docs/decisions.md](decisions.md). A new gap goes in those lists, not in a comment.
- **Documentation ships with the change** — one document per area under `docs/`, README rows that link to the
  section they summarise, and an entry in [CHANGELOG.md](../CHANGELOG.md) for a user-visible change.

## Repository tooling and code-analysis scope

Everything the setup added is committed, but none of it is product code: 461 files of Markdown, Python and TOML
against roughly 420 source and documentation files.

| File | Effect |
| --- | --- |
| `.gitattributes` | Marks `.agents/**` and `_bmad/**` as `linguist-vendored` and `_bmad-output/**` as `linguist-documentation`, so GitHub's language bar reports the port rather than the workflow. The files stay in the tree and in the repository. |
| `.github/workflows/ci.yml` | Unchanged. The Sonar analysis runs through the Scanner for .NET, which passes every property on `begin`, and neither tooling folder belongs to a project it builds. A repository that uses SonarCloud Automatic Analysis instead — which reads the whole tree — needs an exclusion for these folders. |

## The next step

`bmad-project-context` is the recommended first skill for an existing codebase like this one: it is
conversational, in that the rules come from the team and everything else is verified against the repository, and
it writes `AGENTS.md`. That file is where the pointer to this document belongs.

