#!/bin/bash
# Run every unit test suite in the repo: C#, Go, and Python.
#
# These are unit tests only — nothing here starts Docker, hits the network, or
# touches AWS/Azure. For the end-to-end smoke tests against a running stack, use
# test-data/run_all_tests.sh instead.
#
# Usage:
#   ./scripts/test-all.sh            # run everything
#   ./scripts/test-all.sh csharp     # run one suite
#   ./scripts/test-all.sh go python
#
# Exits non-zero if any suite fails, so it works as a pre-deploy gate.

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VENV="$ROOT/monitoring-service/.venv"

bold()  { printf "\033[1m%s\033[0m\n" "$1"; }
green() { printf "\033[32m%s\033[0m\n" "$1"; }
red()   { printf "\033[31m%s\033[0m\n" "$1"; }
dim()   { printf "\033[2m%s\033[0m\n" "$1"; }

FAILED=()
RAN=()

run_csharp() {
    bold "── C# — hl7-service ────────────────────────────────────────"
    if ! command -v dotnet >/dev/null 2>&1; then
        red "  dotnet not found — install the .NET SDK"
        FAILED+=("csharp")
        return
    fi

    if dotnet test "$ROOT/HealthBridge.sln" --nologo -v q; then
        green "  C# tests passed"
        RAN+=("csharp")
    else
        red "  C# tests FAILED"
        FAILED+=("csharp")
    fi
    echo ""
}

run_go() {
    bold "── Go — gateway ────────────────────────────────────────────"
    if ! command -v go >/dev/null 2>&1; then
        red "  go not found — install Go"
        FAILED+=("go")
        return
    fi

    if (cd "$ROOT/gateway" && go test ./...); then
        green "  Go tests passed"
        RAN+=("go")
    else
        red "  Go tests FAILED"
        FAILED+=("go")
    fi
    echo ""
}

run_python() {
    bold "── Python — monitoring-service ─────────────────────────────"
    if [ ! -x "$VENV/bin/python" ]; then
        red "  No virtualenv at monitoring-service/.venv"
        dim  "  Create it with:"
        dim  "    cd monitoring-service"
        dim  "    python3 -m venv .venv"
        dim  "    .venv/bin/pip install -r requirements.txt -r requirements-dev.txt"
        FAILED+=("python")
        return
    fi

    if (cd "$ROOT/monitoring-service" && "$VENV/bin/python" -m pytest -q); then
        green "  Python tests passed"
        RAN+=("python")
    else
        red "  Python tests FAILED"
        FAILED+=("python")
    fi
    echo ""
}

# No arguments = run everything.
SUITES=("$@")
if [ ${#SUITES[@]} -eq 0 ]; then
    SUITES=(csharp go python)
fi

for suite in "${SUITES[@]}"; do
    case "$suite" in
        csharp|c#|dotnet) run_csharp ;;
        go|gateway)       run_go ;;
        python|py)        run_python ;;
        *) red "Unknown suite: $suite (expected csharp, go, or python)"; exit 2 ;;
    esac
done

bold "── Summary ─────────────────────────────────────────────────"
if [ ${#FAILED[@]} -eq 0 ]; then
    green "All suites passed: ${RAN[*]}"
    exit 0
else
    red "Failed: ${FAILED[*]}"
    [ ${#RAN[@]} -gt 0 ] && dim "Passed: ${RAN[*]}"
    exit 1
fi
