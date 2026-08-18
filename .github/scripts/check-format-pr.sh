#!/usr/bin/env bash
# check-format-pr.sh — runs dotnet format --verify-no-changes for .NET files changed in the PR.
# Expects: caller repo root as cwd, changed_dotnet_files.txt present.
# Applies canonical CI_Toolkit/.editorconfig regardless of any repo-local config.
# Severity: error → job fails (PR blocked); warning → annotation only; suggestion → ignored.
# Multi-solution repos: prefers <RepoName>.sln; falls back to first .sln sorted by name (LC_ALL=C).

set -euo pipefail

# TOOLKIT_DIR can be overridden if CI_Toolkit was checked out at a non-default path.
TOOLKIT_DIR="${TOOLKIT_DIR:-_toolkit}"
CANONICAL_EDITORCONFIG="$TOOLKIT_DIR/.editorconfig"

# Canonical-source enforcement is load-bearing. Falling back to the caller's own
# .editorconfig would silently allow per-repo drift to mask format violations, which
# defeats the purpose of distributing one canonical config from CI_Toolkit.
if [ ! -f "$CANONICAL_EDITORCONFIG" ]; then
  echo "::error::Canonical .editorconfig not found at $CANONICAL_EDITORCONFIG. The CI_Toolkit checkout step must complete successfully before this script runs."
  exit 1
fi
cp "$CANONICAL_EDITORCONFIG" .editorconfig
echo "::notice::Canonical .editorconfig applied from $CANONICAL_EDITORCONFIG."

if [ ! -f changed_dotnet_files.txt ]; then
  echo "::notice::changed_dotnet_files.txt not found. Format check skipped."
  exit 0
fi

# Find owning .csproj for each changed file (walk up from file dir until we hit a .csproj).
# Store newline-separated paths per csproj so paths with spaces are safe to iterate.
declare -A project_files
declare -a unmapped_files=()
while IFS= read -r file; do
  [ -z "$file" ] && continue
  dir=$(dirname "$file")
  csproj=""
  while [ "$dir" != "." ] && [ "$dir" != "/" ]; do

    found=$(find "$dir" -maxdepth 1 -name "*.csproj" 2>/dev/null | head -1)
    if [ -n "$found" ]; then
      csproj="$found"
      break
    fi
    dir=$(dirname "$dir")
  done
  if [ -n "$csproj" ]; then
    if [ -n "${project_files[$csproj]+x}" ]; then
      project_files["$csproj"]="${project_files[$csproj]}"$'\n'"$file"
    else
      project_files["$csproj"]="$file"
    fi
  else
    unmapped_files+=("$file")
  fi
done < changed_dotnet_files.txt

if [ "${#unmapped_files[@]}" -gt 0 ]; then
  echo "::warning::${#unmapped_files[@]} changed file(s) could not be mapped to a .csproj and were skipped by format check: $(IFS=', '; echo "${unmapped_files[*]}")"
fi

if [ ${#project_files[@]} -eq 0 ]; then
  echo "::notice::No .csproj found for any changed file. Format check skipped."
  exit 0
fi

# One primary .sln (deterministic): sorted basename order, first only. Parse listed .csproj paths
# (normalize to forward slashes for comparison with find output on Linux agents).
primary_sln=""
solution_projects=()
shopt -s nullglob
_slns=( *.sln )
shopt -u nullglob
if [ ${#_slns[@]} -gt 0 ]; then
  repo_sln="${GITHUB_REPOSITORY##*/}.sln"
  if [ -f "$repo_sln" ]; then
    primary_sln="$repo_sln"
  else
    mapfile -t _sln_candidates < <(printf '%s\n' "${_slns[@]}" | LC_ALL=C sort)
    primary_sln="${_sln_candidates[0]}"
  fi
  set +o pipefail
  mapfile -t solution_projects < <(
    grep -oE '"[^"]*\.csproj"' "$primary_sln" 2>/dev/null | tr -d '"' | sed 's|\\|/|g' | sort -u
  )
  set -o pipefail
fi

any_errors=0
any_warnings=0

# run_check TARGET LABEL [--include FILE ...]
#
# Single pass: --severity warn captures all violations in one invocation.
# Errors (e.g. ENDOFLINE) appear as ": error " lines in the diagnostic output;
# everything else is a warning. This avoids a second dotnet format call.
run_check() {
  local target="$1" label="$2"
  shift 2
  local include_args=("$@")

  echo "::group::dotnet format — $label"
  set +e
  local output
  output=$(dotnet format "$target" --verify-no-changes --verbosity diagnostic --severity warn "${include_args[@]}" 2>&1)
  local exit_code=$?
  echo "$output"
  set -e
  echo "::endgroup::"

  if [ "$exit_code" -ne 0 ]; then
    # Errors are MSBuild diagnostics with ": error " in the line (e.g. "error ENDOFLINE:").
    if echo "$output" | grep -q ': error '; then
      any_errors=1
    else
      any_warnings=1
    fi
  fi
}

# Split: files in a solution project vs files in a project not listed in the primary .sln
# (e.g. .ci/tests — run format per .csproj).
solution_include=()
declare -A outside_solution
for csproj in "${!project_files[@]}"; do
  in_sln=""
  for sp in "${solution_projects[@]}"; do
    if [ "$csproj" = "$sp" ]; then in_sln=1; break; fi
  done
  if [ -n "$in_sln" ]; then
    while IFS= read -r f; do
      [ -z "$f" ] && continue
      solution_include+=("$f")
    done <<< "${project_files[$csproj]}"
  else
    outside_solution["$csproj"]="${project_files[$csproj]}"
  fi
done

# Run against the primary solution for changed files that belong to solution projects.
if [ ${#solution_include[@]} -gt 0 ] && [ ${#solution_projects[@]} -gt 0 ] && [ -n "$primary_sln" ]; then
  include_args=()
  for f in "${solution_include[@]}"; do include_args+=(--include "$f"); done
  run_check "$primary_sln" "$primary_sln (solution; changed files in solution projects)" "${include_args[@]}"
fi

# Run per project for changed files whose project is NOT in the primary solution.
for csproj in "${!outside_solution[@]}"; do
  include_args=()
  while IFS= read -r f; do
    [ -z "$f" ] && continue
    include_args+=(--include "$f")
  done <<< "${outside_solution[$csproj]}"
  [ ${#include_args[@]} -eq 0 ] && continue
  run_check "$csproj" "$csproj (outside primary solution)" "${include_args[@]}"
done

if [ "$any_errors" -ne 0 ]; then
  echo "::error title=Format check::Format errors found. Open the log for details, then run \`dotnet format\` locally to fix before merging."
  exit 1
elif [ "$any_warnings" -ne 0 ]; then
  echo "::warning title=Format check::Format warnings found. Open the log for details, then run \`dotnet format\` locally to resolve."
fi
exit 0
