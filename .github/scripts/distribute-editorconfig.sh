#!/usr/bin/env bash
# Distributes .editorconfig to all target BHoM repos.
#
# Usage: distribute-editorconfig.sh <repo-list-file>
#
# Environment variables (set by the calling workflow):
#   GH_TOKEN  — installation token for the DevOps GitHub App
#   ORG       — GitHub organisation login
#   DRY_RUN   — 'true' to log intended changes without opening PRs
#
# Behaviour per repo:
#   - Skips if the repo's .editorconfig already matches the canonical file.
#   - Creates branch governance/update-editorconfig-YYYY-MM-DD (datestamped to
#     prevent stale approvals from carrying over if the branch is re-pushed).
#   - Closes any open PRs opened from older governance/update-editorconfig-* branches
#     before opening a fresh PR for the new branch.
#   - Failures are collected and reported at the end without stopping the loop.
set -euo pipefail

# Remove cloned repos on any exit (normal or error). Safe on ephemeral runners;
# prevents disk accumulation on self-hosted runners.
trap 'rm -rf targets/' EXIT

REPO_FILE="${1:?Usage: distribute-editorconfig.sh <repo-list-file>}"
CANONICAL_ABS="$(pwd)/.editorconfig"
BRANCH="governance/update-editorconfig-$(date +%Y-%m-%d)"
BRANCH_PREFIX="governance/update-editorconfig-"
DRY_RUN="${DRY_RUN:-false}"
ORG="${ORG:?ORG must be set}"

SKIPPED=0
UPDATED=0
DRY_RUN_COUNT=0
FAILURES=()

if [ ! -f "$CANONICAL_ABS" ]; then
  echo "::error::Canonical .editorconfig not found at $CANONICAL_ABS"
  exit 1
fi

if [ ! -f "$REPO_FILE" ]; then
  echo "::error::Repo list file not found: $REPO_FILE"
  exit 1
fi

git config --global user.name  "bhom-devops[bot]"
git config --global user.email "bhom-devops[bot]@users.noreply.github.com"
# gh handles auth automatically; credential helper needed for git push over HTTPS.
git config --global credential.helper \
  '!f() { echo "username=x-access-token"; echo "password=${GH_TOKEN}"; }; f'

mkdir -p targets

while IFS= read -r repo; do
  [ -z "$repo" ] && continue
  echo "::group::$ORG/$repo"

  TARGET_DIR="targets/$repo"
  rm -rf "$TARGET_DIR"

  BASE_BRANCH=$(gh repo view "$ORG/$repo" --json defaultBranchRef --jq '.defaultBranchRef.name' 2>/dev/null || true)
  if [ -z "$BASE_BRANCH" ]; then
    echo "::error::Failed to determine default branch for $ORG/$repo"
    FAILURES+=("$repo")
    echo "::endgroup::"
    continue
  fi
  if ! gh repo clone "$ORG/$repo" "$TARGET_DIR" -- --depth=1 --branch "$BASE_BRANCH" --quiet; then
    echo "::error::Failed to clone $ORG/$repo (branch: $BASE_BRANCH)"
    FAILURES+=("$repo")
    echo "::endgroup::"
    continue
  fi

  if diff -q "$CANONICAL_ABS" "$TARGET_DIR/.editorconfig" >/dev/null 2>&1; then
    echo "::notice::$repo already up to date. Skipped."
    SKIPPED=$((SKIPPED + 1))
    echo "::endgroup::"
    continue
  fi

  if [ "$DRY_RUN" = "true" ]; then
    echo "::notice::[dry run] $repo would be updated."
    DRY_RUN_COUNT=$((DRY_RUN_COUNT + 1))
    echo "::endgroup::"
    continue
  fi

  # Run in a subshell so a failure can be caught without exiting the loop.
  (
    set -euo pipefail
    cd "$TARGET_DIR"

    git checkout -b "$BRANCH"
    cp "$CANONICAL_ABS" .editorconfig
    git add .editorconfig
    git commit -m "chore: update .editorconfig from CI_Toolkit"

    # --force is safe: this branch is exclusively owned by this workflow.
    git push origin "$BRANCH" --force

    # Close PRs opened from earlier governance branches so reviewers see only one open PR per repo.
    gh pr list \
      --repo "$ORG/$repo" \
      --state open \
      --json number,headRefName \
      --jq ".[] | select(.headRefName | startswith(\"${BRANCH_PREFIX}\")) | select(.headRefName != \"${BRANCH}\") | .number" \
    | xargs -r -I{} gh pr close {} --repo "$ORG/$repo" --comment "Superseded by a newer sync run."

    # A new commit on an already-open PR branch updates it automatically; only create if none exists.
    if gh pr list \
        --repo "$ORG/$repo" \
        --state open \
        --head "$BRANCH" \
        --json number \
        --jq '.[].number' | grep -q .; then
      echo "::notice::Commit pushed to existing PR."
    else
      PR_URL=$(gh pr create \
        --repo "$ORG/$repo" \
        --head "$BRANCH" \
        --base "$BASE_BRANCH" \
        --title "chore: update .editorconfig" \
        --body "Automated update of \`.editorconfig\` from [CI_Toolkit](https://github.com/BHoM/CI_Toolkit/blob/main/.editorconfig).

Formatting rules are managed centrally in CI_Toolkit. This PR was opened automatically.

To fix formatting issues locally before opening a PR:
\`\`\`bash
dotnet format
\`\`\`")
      echo "::notice::PR opened: $PR_URL"
    fi
  ) || {
    echo "::error::Failed to update $repo"
    FAILURES+=("$repo")
    echo "::endgroup::"
    continue
  }

  UPDATED=$((UPDATED + 1))
  echo "::endgroup::"
done < "$REPO_FILE"

# ── Step summary ─────────────────────────────────────────────────────────────
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### .editorconfig distribution for ${ORG}"
    echo ""
    echo "| | Count |"
    echo "|---|---|"
    if [ "$DRY_RUN" = "true" ]; then
      echo "| Would be updated (dry run) | $DRY_RUN_COUNT |"
      echo "| Already up to date | $SKIPPED |"
    else
      echo "| Updated | $UPDATED |"
      echo "| Already up to date | $SKIPPED |"
      echo "| Failed | ${#FAILURES[@]} |"
    fi

    if [ "${#FAILURES[@]}" -gt 0 ]; then
      echo ""
      echo "**Failed repos:**"
      for r in "${FAILURES[@]}"; do
        echo "- \`$r\`"
      done
    fi
  } >> "$GITHUB_STEP_SUMMARY"
fi

if [ "${#FAILURES[@]}" -gt 0 ]; then
  echo "::error::Distribution failed for ${#FAILURES[@]} repo(s): ${FAILURES[*]}"
  exit 1
fi

if [ "$DRY_RUN" = "true" ]; then
  echo "::notice::Distribution complete (dry run). Would update: $DRY_RUN_COUNT  Already up to date: $SKIPPED"
else
  echo "::notice::Distribution complete. Updated: $UPDATED  Skipped: $SKIPPED"
fi
# targets/ cleanup is handled by the EXIT trap.
