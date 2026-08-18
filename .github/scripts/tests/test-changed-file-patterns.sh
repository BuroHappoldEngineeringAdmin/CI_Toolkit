#!/usr/bin/env bash
# test-changed-file-patterns.sh — asserts the changed-file patterns this repo
# ships behave correctly when handed to `git diff -- <pathspec>`.
#
# compute-changed-files does `read -ra pathspecs <<< "$PATTERNS"` and passes the
# tokens straight to git, so these are git pathspecs, not globs. Three rules bite:
#
#   1. '*' matches '/' (wildmatch without WM_PATHNAME), so '*.cs' matches nested
#      paths. This is why the source patterns work.
#   2. A token with no wildcard is matched as a path prefix from the repo root,
#      NOT by basename. A bare 'AssemblyInfo.cs' therefore matches only a
#      repo-root file and misses every Properties/AssemblyInfo.cs.
#   3. '**' is not special to git. '**/' degrades to '*' plus a mandatory '/',
#      so it requires at least one leading directory and skips root-level dirs.
#
# Both 2 and 3 shipped as live defects and silently under-selected files, which
# means the affected checks passed without examining anything. Patterns are read
# out of the shipped YAML rather than restated here, so drift fails the test.
#
# The fixture is built with `git update-index --cacheinfo`, not real files, so
# mixed-case sibling directories survive on case-insensitive filesystems
# (Windows collapses DataSets/ into Datasets/ on disk). Verified that
# core.ignorecase does not affect pathspec matching either way.
#
# Usage: .github/scripts/tests/test-changed-file-patterns.sh   (needs git only)

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
FIXTURE="$(mktemp -d)"
trap 'rm -rf "$FIXTURE"' EXIT

failures=0
checks=0

pass() { checks=$((checks + 1)); printf '  ok   %s\n' "$1"; }
fail() { checks=$((checks + 1)); failures=$((failures + 1)); printf '  FAIL %s\n' "$1"; }

# ── Fixture ───────────────────────────────────────────────────────────────────
# Paths mirror the layouts the 2026-07-28 fleet audit found, so each assertion
# maps to a real repo rather than a hypothetical.
FIXTURE_PATHS=(
  # Dataset layouts. '.ci/Datasets' is the canonical one (ci-dataset-tests
  # hardcodes it); the rest are real layouts in live repos.
  ".ci/Datasets/Area/deep.json"          # canonical, already matched
  ".ci/DataSets/Area/capsdeep.json"      # nested but capital S: isolates casing
  "src/datasets/lower/low.json"          # lowercase, already matched
  "a/mydatasets.json"                    # substring in filename, already matched
  "Datasets/root-direct.json"            # root-level, e.g. CFD_Toolkit
  "Datasets/Area/root-nested.json"       # root-level nested, e.g. BuroHappold_Datasets
  "DataSets/Caps/caps.json"              # root + capital S, e.g. LifeCycleAssessment_Toolkit
  "DATASETS/shouty/shout.json"           # upper case, defensive
  "datasets.json"                        # root-level file
  "Other/config.json"                    # NEGATIVE: json unrelated to datasets
  # Project-compliance inputs.
  "AssemblyInfo.cs"                      # repo root: the only thing the old token matched
  "Properties/AssemblyInfo.cs"           # the real layout, was missed
  "Proj/Properties/AssemblyInfo.cs"      # nested project, was missed
  "Proj/Proj.csproj"                     # already matched
  "Engine/AssemblyInfoHelper.cs"         # NEGATIVE for the exclusions: real source, name only contains AssemblyInfo
  # Source / misc.
  "Engine/Query/Thing.cs"
  "Engine/Compute/Thing.sln"
  "altConfigs.txt"
)

build_fixture() {
  git init -q "$FIXTURE"
  # Pathspec matching reads the index, so ignorecase is irrelevant, but pin it
  # so a developer's global config cannot change the result.
  git -C "$FIXTURE" config core.ignorecase false
  local blob
  blob=$(printf 'x' | git -C "$FIXTURE" hash-object -w --stdin)
  git -C "$FIXTURE" -c user.email=ci@example.invalid -c user.name=ci \
    commit -q --allow-empty -m base
  BASE_SHA=$(git -C "$FIXTURE" rev-parse HEAD)
  local p
  for p in "${FIXTURE_PATHS[@]}"; do
    git -C "$FIXTURE" update-index --add --cacheinfo "100644,$blob,$p"
  done
  git -C "$FIXTURE" -c user.email=ci@example.invalid -c user.name=ci \
    commit -q -m changes
}

# matched <pattern-string> -> newline-separated paths, exactly as
# compute-changed-files computes them (same split, same diff invocation).
matched() {
  local patterns="$1"
  local -a pathspecs
  read -ra pathspecs <<< "$patterns"
  git -C "$FIXTURE" diff --name-only --diff-filter=ACMRT \
    "$BASE_SHA" HEAD -- "${pathspecs[@]}" | LC_ALL=C sort
}

assert_matches() {
  local label="$1" patterns="$2" path="$3"
  if matched "$patterns" | grep -qxF "$path"; then
    pass "$label: matches $path"
  else
    fail "$label: does NOT match $path   [pattern: $patterns]"
  fi
}

assert_not_matches() {
  local label="$1" patterns="$2" path="$3"
  if matched "$patterns" | grep -qxF "$path"; then
    fail "$label: unexpectedly matches $path   [pattern: $patterns]"
  else
    pass "$label: correctly ignores $path"
  fi
}

# ── Pattern extraction from the shipped YAML ──────────────────────────────────
# Tier templates pair 'check_type:' with a following 'patterns:'. Emit
# "<check_type>\t<patterns>" so assertions test what actually ships.
extract_check_patterns() {
  local check="$1"
  awk -v want="$check" '
    /^[[:space:]]*check_type:[[:space:]]*/ {
      ct = $0; sub(/^[[:space:]]*check_type:[[:space:]]*/, "", ct); gsub(/[[:space:]]+$/, "", ct)
    }
    /^[[:space:]]*patterns:[[:space:]]*/ {
      p = $0; sub(/^[[:space:]]*patterns:[[:space:]]*/, "", p)
      gsub(/^['"'"'"]|['"'"'"][[:space:]]*$/, "", p)
      if (ct == want) print p
    }
  ' "$REPO_ROOT"/templates/*/ci-*.yml | LC_ALL=C sort -u
}

# Every 'patterns:' value anywhere in the repo, for the structural invariant.
all_pattern_values() {
  grep -rh "^[[:space:]]*patterns:[[:space:]]*['\"]" \
    "$REPO_ROOT/templates" "$REPO_ROOT/.github/actions" 2>/dev/null \
    | sed -E "s/^[[:space:]]*patterns:[[:space:]]*//; s/^['\"]//; s/['\"][[:space:]]*$//" \
    | LC_ALL=C sort -u
}

# ── Diff-range fixture ────────────────────────────────────────────────────────
# The pathspec fixture above is linear, which is all that pattern matching needs.
# The diff RANGE needs the shape production actually presents: actions/checkout
# takes its default ref, so on a pull_request HEAD is refs/pull/<n>/merge, a
# merge of the PR head into a commit on the base branch.
#
# This fixture reproduces that, plus the condition that made the old range wrong:
# the base branch advancing after the pull request was opened, so that
# `pull_request.base.sha` (pinned at creation) is behind the merge's base parent.
#
#   base0 ── B(bar.cs) ────────── M   <- HEAD, the merge ref analogue
#      \                         /
#       F(foo.cs) ──────────────/
#
#   base.sha  = base0   (pinned at PR creation)
#   HEAD^1    = B       (the base commit the merge was computed against)
#   HEAD^2    = F       (the PR head)
#
# The pull request changed foo.cs only. bar.cs landed on the base branch
# independently and must not be attributed to it.
build_range_fixture() {
  RF="$(mktemp -d)"
  git init -q "$RF"
  local g=(git -C "$RF" -c user.email=ci@example.invalid -c user.name=ci)
  "${g[@]}" commit -q --allow-empty -m base0
  RANGE_BASE_SHA=$("${g[@]}" rev-parse HEAD)          # base.sha, pinned at creation
  "${g[@]}" checkout -q -b feature
  echo x > "$RF/foo.cs";  "${g[@]}" add foo.cs;  "${g[@]}" commit -q -m "PR: foo"
  "${g[@]}" checkout -q -
  echo x > "$RF/bar.cs";  "${g[@]}" add bar.cs;  "${g[@]}" commit -q -m "base advances: bar"
  RANGE_BASE_TIP=$("${g[@]}" rev-parse HEAD)          # the base commit the merge is computed against
  # Merge the PR head INTO base, so first parent is base. This is the order
  # GitHub uses when it computes refs/pull/<n>/merge.
  "${g[@]}" merge -q --no-ff -m "Merge feature into base" feature
}

range_matched() {  # <range-args...> -> newline-separated paths
  git -C "$RF" diff --name-only --diff-filter=ACMRT "$@" -- '*.cs' | LC_ALL=C sort
}

assert_range_selects() {
  local label="$1"; shift
  local expected="$1"; shift
  local got; got=$(range_matched "$@" | tr '\n' ' ' | sed 's/ $//')
  if [ "$got" = "$expected" ]; then
    pass "$label: selects [$got]"
  else
    fail "$label: selected [$got], expected [$expected]"
  fi
}

# ── Tests ─────────────────────────────────────────────────────────────────────
build_fixture
build_range_fixture

echo "diff range: HEAD^1..HEAD is the pull request's own contribution"
# The shipped range. Must see only what the PR changed.
assert_range_selects "range HEAD^1..HEAD" "foo.cs" 'HEAD^1' HEAD

# The superseded range, kept as a characterisation test. It over-selects, which
# is the defect: bar.cs landed on the base branch and the PR never touched it.
assert_range_selects "range base.sha..HEAD (superseded)" "bar.cs foo.cs" "$RANGE_BASE_SHA" HEAD

# Three-dot against base.sha was the originally proposed fix. base.sha is an
# ancestor of HEAD, so the merge base IS base.sha and it collapses to two-dot:
# identical output, no improvement. Asserted so nobody re-proposes it.
assert_range_selects "range base.sha...HEAD (proposed, identical to two-dot)" "bar.cs foo.cs" "$RANGE_BASE_SHA...HEAD"

# HEAD is a merge commit with the base as its first parent. If this ever stops
# holding, the shipped range is wrong and every consumer silently changes scope.
if [ "$(git -C "$RF" rev-list --parents -n1 HEAD | wc -w)" -eq 3 ]; then
  pass "range: HEAD is a merge commit with two parents"
else
  fail "range: HEAD is not a two-parent merge commit; the shipped range assumes it is"
fi
# HEAD^1 must be the base commit the merge was computed against, and HEAD^2 the
# PR head. Asserted explicitly because the whole range depends on that ordering,
# and `git merge` puts the branch you were on first: merging the PR head INTO
# base is what makes the base the first parent.
if [ "$(git -C "$RF" rev-parse 'HEAD^1')" = "$RANGE_BASE_TIP" ]; then
  pass "range: HEAD^1 is the base commit the merge was computed against"
else
  fail "range: HEAD^1 is not the base commit; parent ordering is not as assumed"
fi
if [ "$(git -C "$RF" rev-parse 'HEAD^2')" = "$(git -C "$RF" rev-parse feature)" ]; then
  pass "range: HEAD^2 is the PR head"
else
  fail "range: HEAD^2 is not the PR head"
fi
# base.sha is an ancestor of HEAD, which is why three-dot collapsed to two-dot.
if git -C "$RF" merge-base --is-ancestor "$RANGE_BASE_SHA" HEAD; then
  pass "range: base.sha is an ancestor of HEAD (so three-dot == two-dot)"
else
  fail "range: base.sha is not an ancestor of HEAD"
fi

rm -rf "$RF"


echo "dataset patterns (as shipped in the tier templates)"
dataset_patterns=$(extract_check_patterns dataset)
if [ -z "$dataset_patterns" ]; then
  fail "dataset: no 'patterns:' found for check_type dataset"
else
  while IFS= read -r pat; do
    # Regression: everything the old pattern already caught must still match.
    assert_matches     "dataset" "$pat" ".ci/Datasets/Area/deep.json"
    assert_matches     "dataset" "$pat" "src/datasets/lower/low.json"
    assert_matches     "dataset" "$pat" "a/mydatasets.json"
    # Defect A1, root-anchoring.
    assert_matches     "dataset" "$pat" "Datasets/root-direct.json"
    assert_matches     "dataset" "$pat" "Datasets/Area/root-nested.json"
    assert_matches     "dataset" "$pat" "datasets.json"
    # Defect A2, casing.
    assert_matches     "dataset" "$pat" ".ci/DataSets/Area/capsdeep.json"
    assert_matches     "dataset" "$pat" "DataSets/Caps/caps.json"
    assert_matches     "dataset" "$pat" "DATASETS/shouty/shout.json"
    # Must stay narrower than '*.json'.
    assert_not_matches "dataset" "$pat" "Other/config.json"
    assert_not_matches "dataset" "$pat" "Engine/Query/Thing.cs"
  done <<< "$dataset_patterns"
fi

# ci-dataset-tests deliberately does NOT share the compliance dataset pattern.
# Compliance validates any dataset JSON anywhere in the repo; dataset tests
# execute fixtures, and fixtures live in .ci/Datasets/. The all-fixtures branch
# of that action enumerates that directory directly, so the changed-file
# pathspec has to agree with it or the two branches select different files on
# the same PR.
echo "dataset-tests pattern is anchored to .ci/Datasets (not the compliance pattern)"
dt_pattern=$(grep -h "^[[:space:]]*patterns:" \
  "$REPO_ROOT/.github/actions/ci-dataset-tests/action.yml" 2>/dev/null \
  | sed -E "s/^[[:space:]]*patterns:[[:space:]]*//; s/^['\"]//; s/['\"][[:space:]]*$//" \
  | grep -i 'datasets' | head -1)
if [ -z "$dt_pattern" ]; then
  fail "ci-dataset-tests: no dataset pattern found"
else
  # In scope: fixtures under the canonical directory, at any depth, any casing.
  assert_matches     "dataset-tests" "$dt_pattern" ".ci/Datasets/Area/deep.json"
  assert_matches     "dataset-tests" "$dt_pattern" ".ci/DataSets/Area/capsdeep.json"

  # Out of scope: dataset JSON that is not a fixture. Each of these was selected
  # by the previous unanchored pattern and handed to CheckTest, while the
  # all-fixtures branch would never have found it.
  assert_not_matches "dataset-tests" "$dt_pattern" "Datasets/root-direct.json"
  assert_not_matches "dataset-tests" "$dt_pattern" "Datasets/Area/root-nested.json"
  assert_not_matches "dataset-tests" "$dt_pattern" "DataSets/Caps/caps.json"
  assert_not_matches "dataset-tests" "$dt_pattern" "DATASETS/shouty/shout.json"
  assert_not_matches "dataset-tests" "$dt_pattern" "src/datasets/lower/low.json"
  assert_not_matches "dataset-tests" "$dt_pattern" "a/mydatasets.json"
  assert_not_matches "dataset-tests" "$dt_pattern" "datasets.json"
  assert_not_matches "dataset-tests" "$dt_pattern" "Other/config.json"

  # The two patterns must stay distinct. If someone re-unifies them this fails.
  if [ "$dt_pattern" = "$(printf '%s' "$dataset_patterns" | head -1)" ]; then
    fail "dataset-tests: pattern is identical to the compliance dataset pattern; they encode different intents"
  else
    pass "dataset-tests: pattern is distinct from the compliance dataset pattern"
  fi
fi

echo "project patterns (as shipped in the tier templates)"
project_patterns=$(extract_check_patterns project)
if [ -z "$project_patterns" ]; then
  fail "project: no 'patterns:' found for check_type project"
else
  while IFS= read -r pat; do
    # Defect B: AssemblyInfo.cs lives under Properties/, never at the repo root.
    assert_matches     "project" "$pat" "Properties/AssemblyInfo.cs"
    assert_matches     "project" "$pat" "Proj/Properties/AssemblyInfo.cs"
    # Regression: the root-level file and .csproj must still match.
    assert_matches     "project" "$pat" "AssemblyInfo.cs"
    assert_matches     "project" "$pat" "Proj/Proj.csproj"
    assert_not_matches "project" "$pat" "Engine/Query/Thing.cs"
  done <<< "$project_patterns"
fi

echo "serialisation pattern (excludes AssemblyInfo.cs, keeps real source)"
ser_pattern=$(grep -h "^[[:space:]]*patterns:" \
  "$REPO_ROOT/.github/actions/ci-serialisation/action.yml" 2>/dev/null \
  | sed -E "s/^[[:space:]]*patterns:[[:space:]]*//; s/^['\"]//; s/['\"][[:space:]]*$//" \
  | head -1)
if [ -z "$ser_pattern" ]; then
  fail "ci-serialisation: no pattern found"
else
  # A version bump alone must not trigger the check, at either depth.
  assert_not_matches "serialisation" "$ser_pattern" "Properties/AssemblyInfo.cs"
  assert_not_matches "serialisation" "$ser_pattern" "Proj/Properties/AssemblyInfo.cs"
  assert_not_matches "serialisation" "$ser_pattern" "AssemblyInfo.cs"
  # The exclusion must not swallow real source or project files alongside it.
  assert_matches     "serialisation" "$ser_pattern" "Engine/Query/Thing.cs"
  assert_matches     "serialisation" "$ser_pattern" "Proj/Proj.csproj"
fi

echo "versioning pattern (excludes AssemblyInfo.cs, keeps real source)"
ver_pattern=$(grep -h "^[[:space:]]*patterns:" \
  "$REPO_ROOT/.github/actions/ci-versioning/action.yml" 2>/dev/null \
  | sed -E "s/^[[:space:]]*patterns:[[:space:]]*//; s/^['\"]//; s/['\"][[:space:]]*$//" \
  | head -1)
if [ -z "$ver_pattern" ]; then
  fail "ci-versioning: no pattern found"
else
  # Mirrors BHoMBot Versioning.cs:49. One consumer now: the skip decision
  # (count). It previously also fed the deletion-only guard in
  # Assert-VersioningPrerequisites.ps1, but that fast-fail was removed on
  # 2026-08-07 because its expected set was the whole installer payload while
  # ProgramData holds only the calling repo's closure. The exclusion still
  # matters on its own: a fleet-wide milestone AssemblyInfo bump must not
  # trigger a versioning run that cannot change its own verdict.
  assert_not_matches "versioning" "$ver_pattern" "Properties/AssemblyInfo.cs"
  assert_not_matches "versioning" "$ver_pattern" "Proj/Properties/AssemblyInfo.cs"
  assert_not_matches "versioning" "$ver_pattern" "AssemblyInfo.cs"
  # The exclusion is an EndsWith, matching BHoMBot: a file whose name merely
  # contains AssemblyInfo is real source and must still trigger the check.
  assert_matches     "versioning" "$ver_pattern" "Engine/AssemblyInfoHelper.cs"
  assert_matches     "versioning" "$ver_pattern" "Engine/Query/Thing.cs"
  assert_matches     "versioning" "$ver_pattern" "Proj/Proj.csproj"
  assert_matches     "versioning" "$ver_pattern" "Engine/Compute/Thing.sln"
fi

echo "source patterns (regression only; wildcards already behave)"
for pat in '*.cs' '*.csproj *.sln' 'altConfigs.txt'; do
  case "$pat" in
    '*.cs')            assert_matches "source" "$pat" "Engine/Query/Thing.cs" ;;
    '*.csproj *.sln')  assert_matches "source" "$pat" "Engine/Compute/Thing.sln"
                       assert_matches "source" "$pat" "Proj/Proj.csproj" ;;
    'altConfigs.txt')  assert_matches "source" "$pat" "altConfigs.txt" ;;
  esac
done

# Structural invariant: catches any future token that reintroduces defect 2 or 3.
# A wildcard-free token silently means "repo root only"; '**/' silently means
# "at least one leading directory". Neither is ever intended for a basename
# match. altConfigs.txt is the sole legitimate root-anchored file.
echo "structural invariant over every shipped pattern token"
ROOT_ANCHORED_OK="altConfigs.txt"
while IFS= read -r pat; do
  [ -z "$pat" ] && continue
  for tok in $pat; do
    case "$tok" in
      *'**'*)
        fail "invariant: '$tok' uses '**', which git reads as '*' plus a mandatory '/' (skips root-level dirs)" ;;
      *'*'*|*'?'*|*'['*|:\(*\)*)
        pass "invariant: '$tok' is wildcarded or uses pathspec magic" ;;
      "$ROOT_ANCHORED_OK")
        pass "invariant: '$tok' is an allow-listed root-level file" ;;
      *)
        fail "invariant: '$tok' has no wildcard, so git matches it as a repo-root path prefix, not a basename" ;;
    esac
  done
done <<< "$(all_pattern_values)"

echo
if [ "$failures" -ne 0 ]; then
  echo "FAILED: $failures of $checks assertions"
  exit 1
fi
echo "PASSED: all $checks assertions"
