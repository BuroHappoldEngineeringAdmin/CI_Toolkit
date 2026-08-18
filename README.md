# CI_Toolkit

Continuous integration for the BHoM ecosystem. This repository holds every CI check the
BHoM repositories run, as composite GitHub Actions plus the workflow files that call them.

BHoM spans around a hundred repositories that build against each other. A check therefore
cannot just compile the repository it runs in: it has to resolve that repository's
dependencies, build them in order, and then test the result. That work is the same
everywhere, so it lives here once instead of in every repository.

## How it works

Three layers, top to bottom:

1. **A workflow file in each repository.** One file, copied from [`templates/`](templates/),
   naming the checks that repository runs. It contains no logic.
2. **Composite actions in [`.github/actions/`](.github/actions/).** One per check. These do
   the work: resolve dependencies, build, run a check, report results.
3. **.NET command line runners in [`tools/`](tools/).** The checks that need real analysis
   shell out to a compiled runner rather than a script.

There is no reusable-workflow layer and no central orchestrator. A repository's workflow
calls the composite actions directly, and each check is an independent job.

## The checks

| Check | What it does |
|---|---|
| `ci-build` | Builds the repository and its dependency graph in Release |
| `ci-compliance` | Five rule sets over source and datasets. Pick one with `check_type: code \| copyright \| documentation \| project \| dataset` |
| `ci-dataset-tests` | Runs the fixtures in the repository's `.ci/Datasets/` directory |
| `ci-serialisation` | Round-trips objects through the BHoM serialiser and reports failures the branch introduced |
| `ci-versioning` | Deserialises historical data against the current assemblies to catch breaking type changes |
| `ci-unit-tests` | Runs the test solution under `.ci/unit-tests/` |
| `ci-format` | Verifies formatting against `.editorconfig` |

Two of those look alike and are not. `ci-dataset-tests` executes the fixtures in
`.ci/Datasets/`. `ci-compliance` with `check_type: dataset` runs nothing: it checks that
changed dataset JSON deserialises and carries its source and author metadata.

Six further actions in the same directory are shared plumbing rather than checks:
`resolve-dependencies`, `prepare-runner`, `compute-changed-files`, `discover-solution`,
`infer-verification-config` and `mint-dep-token`.

Most checks skip themselves when a pull request changes nothing they care about, so a
documentation-only change does not trigger a full dependency build.

## Adding CI to a repository

Copy one file from [`templates/BHoM/`](templates/BHoM/) into `.github/workflows/`. Which one
depends on how settled the repository is:

| Template | Checks it runs |
|---|---|
| `ci-prototype.yml` | copyright and project compliance |
| `ci-alpha.yml` | the above, plus build and serialisation |
| `ci-beta.yml` | the above, plus code, documentation and dataset compliance, dataset tests, and versioning |

Nothing else is required. The workflow runs on pull requests against `develop` and reports
one status check per job.

**The checks report but do not block.** Making any of them a merge requirement is a separate
decision, configured per organisation in branch protection or a repository ruleset, not
here. Copying the template is safe on a repository that is not ready to be gated.

Two more templates sit at the root of [`templates/`](templates/) for checks that are not
part of a tier: `ci-format.yml` and `ci-unit-tests.yml`.

### What the checks expect to find

The actions read a small number of conventional files.

| Path | Used for | |
|---|---|---|
| `<RepoName>.sln` | The solution to build, at the repository root | Required by `ci-build` |
| `dependencies.txt` | One `owner/repo` per line. The repositories to clone and build first | Optional |
| `altConfigs.txt` | One `owner/repo/Configuration` per line, for repositories that build under more than one configuration | Optional |
| `.ci/unit-tests/` | The unit test solution | Optional |
| `.ci/Datasets/` | Dataset test fixtures | Optional |

The optional ones skip with a notice when absent, so a repository with no datasets can still
run the beta template. The solution file is the exception: `ci-build` reports an error and
fails if it cannot find one, and it rejects legacy non-SDK project files.

### Credentials

Checks work with no configuration on public repositories, using the token GitHub Actions
provides. Two optional secrets improve on that:

| Secret | Purpose |
|---|---|
| `BHOM_APP_ID` | GitHub App ID |
| `BHOM_APP_PRIVATE_KEY` | The App's private key |

When both are present the actions mint a short-lived App token, which raises API rate limits
and is required if a repository depends on a private repository. When they are absent the
actions fall back to `GH_TOKEN`, then to the built-in `github.token`.

If you are adopting this toolkit outside BuroHappold, register your own GitHub App rather
than asking for these credentials. The secret names are the only convention that matters.

## Repository layout

```
.github/actions/     the checks and their shared plumbing
.github/workflows/   this repository's own CI, and maintenance utilities
.github/scripts/     scripts the actions and utilities call
templates/           the workflow files consumers copy
tools/               .NET runners
.editorconfig        the canonical formatting rules, distributed to consumer repositories
```

`tools/` holds three solutions. `ComplianceRunner` targets `net10.0` and builds three
executables, one for source compliance, one for dataset compliance and one for dataset
tests. `SerialiserRunner` and `VersioningRunner` target `net8.0-windows`, because they load
BHoM assemblies and have to match them.

Checks run on `windows-2025-vs2026`, since BHoM builds against Windows-only dependencies.
`ci-format` is the exception and runs on `ubuntu-latest`.

## Contributing

Fork or branch, then open a pull request against `develop`.

**Read this first: changes here are live immediately.** Consumer workflows reference these
actions at `@develop`, so anything merged reaches every repository on the next pull request
they open. There is no release, no version pin and no staged rollout. A broken action breaks
CI everywhere at once.

Two consequences worth taking seriously:

- **Test on a real repository before merging, not only here.** Point a scratch repository's
  workflow at your branch and open a pull request on it. Passing this repository's own CI
  says the YAML parses, not that the check still works.
- **A dependency build is often skipped from cache.** If your change touches
  `resolve-dependencies`, `prepare-runner`, or anything more than one check calls, confirm
  your test run actually built rather than restoring a cache. A green run that skipped your
  code has told you nothing.

Two workflows run on every pull request here:

- **Lint Workflows** runs `actionlint` over `.github/workflows/` and `templates/`, asserts
  the changed-file pathspecs behave as intended, and runs the Pester tests for the
  PowerShell scripts.
- **Test Tools** runs the `SerialiserRunner` and `VersioningRunner` unit tests.

To run those locally you need `actionlint`, PowerShell 7 with Pester 5, and the .NET SDK.
The runner tests are `dotnet test tools/SerialiserRunner/src/SerialiserRunner.Tests` and
`dotnet test tools/VersioningRunner/src/VersioningRunner.Tests`.

Keep check output plain. These messages appear on other people's pull requests, usually when
something is already going wrong, so they should say what failed and where without
decoration.
