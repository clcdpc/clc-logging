#!/usr/bin/env bash
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/resolve_package_version.sh"

assert_version() {
  local expected="$1"
  shift
  local actual
  actual="$(resolve_package_version "$@")"
  if [[ "$actual" != "$expected" ]]; then
    echo "Expected version '$expected', got '$actual'." >&2
    exit 1
  fi
}

assert_invalid_tag() {
  local ref="$1"
  if resolve_package_version "$ref" >/dev/null 2>&1; then
    echo "Expected invalid release tag '$ref' to fail." >&2
    exit 1
  fi
}

assert_version "1.2.3" "refs/tags/v1.2.3"
assert_version "0.1.0-beta.2" "refs/tags/v0.1.0-beta.2"
assert_version "1.2.3-rc.0" "refs/tags/v1.2.3-rc.0"
assert_version "1.2.3-beta-fix.12" "refs/tags/v1.2.3-beta-fix.12"
assert_version "0.0.0-ci.431.2" "refs/pull/3/merge" "431" "2"
assert_version "0.0.0-ci.431.2" "refs/heads/master" "431" "2"
assert_version "0.0.0-local"

assert_invalid_tag "refs/tags/v1.2"
assert_invalid_tag "refs/tags/v01.2.3"
assert_invalid_tag "refs/tags/v1.2.3-"
assert_invalid_tag "refs/tags/v1.2.3-01"
assert_invalid_tag "refs/tags/v1.2.3+build.7"
assert_invalid_tag "refs/tags/1.2.3"
assert_invalid_tag "refs/tags/v1.2.3.4"
assert_invalid_tag "refs/tags/v1.2.3-beta..2"
assert_invalid_tag "refs/tags/v1.2.3-beta.01"
assert_invalid_tag "refs/tags/v1.2.3/other"
assert_invalid_tag "refs/tags/v1.2.3 trailing"

if resolve_package_version "refs/heads/master" "431" >/dev/null 2>&1; then
  echo "Expected incomplete CI metadata to fail." >&2
  exit 1
fi

if resolve_package_version "refs/heads/master" "0" "1" >/dev/null 2>&1; then
  echo "Expected nonpositive CI metadata to fail." >&2
  exit 1
fi

echo "Package version resolution checks passed."
