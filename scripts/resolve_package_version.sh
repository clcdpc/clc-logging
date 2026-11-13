#!/usr/bin/env bash
set -euo pipefail

resolve_package_version() {
  local ref="${1:-}"
  local run_number="${2:-}"
  local run_attempt="${3:-}"

  if [[ "$ref" == refs/tags/* ]]; then
    local tag="${ref#refs/tags/}"
    if [[ "$tag" != v* ]]; then
      echo "Release tag '$tag' must start with 'v'." >&2
      return 1
    fi

    local version="${tag#v}"
    local numeric='(0|[1-9][0-9]*)'
    local nonnumeric='[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*'
    local identifier="($numeric|$nonnumeric)"
    local semver="^${numeric}\\.${numeric}\\.${numeric}(-${identifier}(\\.${identifier})*)?$"
    if [[ ! "$version" =~ $semver ]]; then
      echo "Release tag '$tag' is invalid. Expected v<major>.<minor>.<patch> with an optional SemVer prerelease suffix, for example v1.2.3 or v1.2.3-beta.2." >&2
      return 1
    fi

    printf '%s\n' "$version"
    return 0
  fi

  if [[ -n "$run_number" || -n "$run_attempt" ]]; then
    if [[ ! "$run_number" =~ ^[1-9][0-9]*$ || ! "$run_attempt" =~ ^[1-9][0-9]*$ ]]; then
      echo "CI run number and attempt must both be positive integers; got '$run_number' and '$run_attempt'." >&2
      return 1
    fi

    printf '0.0.0-ci.%s.%s\n' "$run_number" "$run_attempt"
    return 0
  fi

  printf '0.0.0-local\n'
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  resolve_package_version "${GITHUB_REF:-}" "${GITHUB_RUN_NUMBER:-}" "${GITHUB_RUN_ATTEMPT:-}"
fi
