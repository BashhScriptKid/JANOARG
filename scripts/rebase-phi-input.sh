#!/usr/bin/env bash
# Rebase the phi-input patch series onto a newer master and verify it still builds.
#
# Usage: scripts/rebase-phi-input.sh [new-base]
#   new-base defaults to the local `master` branch (which tracks upstream/master).
#
# The series is expected to be linear: `phi-base` marks the upstream commit the
# patches currently sit on, and `phi-input` is the patch series itself.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

branch=phi-input
base_tag=phi-base
new_base="${1:-master}"

git rev-parse --verify "$branch" >/dev/null
git rev-parse --verify "$base_tag" >/dev/null
git rev-parse --verify "$new_base" >/dev/null

old_base="$(git rev-parse "$base_tag^{commit}")"
old_tip="$(git rev-parse "$branch^{commit}")"

echo "Rebasing $branch: $old_base..$old_tip onto $new_base"
git switch "$branch" >/dev/null
git rebase --autostash --onto "$new_base" "$old_base"

# Show what actually changed in the series after the move (git >= 2.24).
git range-diff "$old_base".."$old_tip" "$new_base".."$branch" || true

git tag -f "$base_tag" "$new_base"
echo "Updated $base_tag -> $(git rev-parse --short "$new_base")"

dotnet build Assembly-CSharp.csproj -nologo -v q
