#!/bin/bash
# HealthBridge — Real DICOM file test script
# Walks the downloaded patient studies and uploads each .dcm file to the parser.
#
# Usage:
#   ./test-data/test_real_dicom.sh                    # uses gateway on localhost
#   ./test-data/test_real_dicom.sh http://x.x.x.x:8080
#
# Expects real DICOM studies in test-data/<patient_id>_<study_date>*/

BASE_URL="${1:-http://localhost:8080}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

bold()  { printf "\033[1m%s\033[0m\n" "$1"; }
green() { printf "\033[32m%s\033[0m\n" "$1"; }
red()   { printf "\033[31m%s\033[0m\n" "$1"; }
cyan()  { printf "\033[36m%s\033[0m\n" "$1"; }

PASS=0
FAIL=0
TOTAL=0

# Find every directory that looks like a patient study
STUDIES=$(find "$SCRIPT_DIR" -maxdepth 1 -type d -name "*_2*" 2>/dev/null)

if [ -z "$STUDIES" ]; then
    red "No patient studies found in $SCRIPT_DIR"
    echo "Expected directories like: 24759123_20010101/"
    exit 1
fi

bold "============================================"
bold "  Real DICOM Parser Test"
bold "  Target: $BASE_URL"
bold "============================================"
echo ""

for study in $STUDIES; do
    name=$(basename "$study")
    cyan "▸ Study: $name"

    # Take a sample of files (first instance from each series — DICOMDIR excluded)
    SAMPLES=$(find "$study" -type f ! -name "DICOMDIR" ! -name ".DS_Store" 2>/dev/null | head -3)
    count=$(echo "$SAMPLES" | wc -l | tr -d ' ')

    for f in $SAMPLES; do
        TOTAL=$((TOTAL + 1))
        relative="${f#$study/}"

        RESPONSE=$(curl -s -X POST "$BASE_URL/api/dicom/parse" \
            -F "file=@$f;filename=test.dcm" 2>/dev/null)

        # Extract key fields with python (avoid jq dependency)
        RESULT=$(echo "$RESPONSE" | python3 -c "
import sys, json
try:
    d = json.loads(sys.stdin.read())
    if 'patientName' in d:
        print(f\"  {d.get('modality','?')} | {d.get('patientName','?')} | {d.get('studyDescription','')} | {d.get('rows',0)}x{d.get('columns',0)}\")
        sys.exit(0)
    else:
        print(f\"  ERROR: {d.get('error','unknown')}\")
        sys.exit(1)
except Exception as e:
    print(f\"  PARSE ERROR: {e}\")
    sys.exit(1)
" 2>/dev/null)

        if [ $? -eq 0 ]; then
            green "$RESULT"
            PASS=$((PASS + 1))
        else
            red "$RESULT"
            FAIL=$((FAIL + 1))
        fi
    done
    echo ""
done

bold "============================================"
if [ "$FAIL" -eq 0 ]; then
    green "  All $TOTAL real DICOM files parsed successfully"
else
    red "  $FAIL/$TOTAL failed"
fi
bold "============================================"

exit $FAIL
