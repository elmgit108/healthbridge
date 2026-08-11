#!/bin/bash
# HealthBridge — Smoke Test Script
# Runs all test data against the API endpoints
#
# Usage:
#   ./test-data/run_all_tests.sh              # default: localhost via gateway
#   ./test-data/run_all_tests.sh http://x.x.x.x:8080   # remote server

BASE_URL="${1:-http://localhost:8080}"
PASS=0
FAIL=0
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

green() { printf "\033[32m%s\033[0m\n" "$1"; }
red()   { printf "\033[31m%s\033[0m\n" "$1"; }
bold()  { printf "\033[1m%s\033[0m\n" "$1"; }

run_test() {
    local name="$1"
    local status="$2"
    if [ "$status" -eq 0 ]; then
        green "  ✓ $name"
        PASS=$((PASS + 1))
    else
        red "  ✗ $name"
        FAIL=$((FAIL + 1))
    fi
}

bold "============================================"
bold "  HealthBridge Smoke Tests"
bold "  Target: $BASE_URL"
bold "============================================"
echo ""

# ---------------------------------------------------
bold "1. Health Checks"
# ---------------------------------------------------

curl -sf "$BASE_URL/health" > /dev/null 2>&1
run_test "Gateway /health" $?

curl -sf "http://localhost:5001/health" > /dev/null 2>&1
run_test "HL7 Service /health (direct)" $?

curl -sf "http://localhost:5002/health" > /dev/null 2>&1
run_test "Monitoring Service /health (direct)" $?

echo ""

# ---------------------------------------------------
bold "2. HL7 Parsing — Raw Messages (text/plain)"
# ---------------------------------------------------

# ADT A01 — Patient Admission
RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/hl7/parse" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/hl7_adt_a01.txt"))
[ "$RESPONSE" = "200" ]
run_test "ADT A01 — Patient Admission (Smith^John)" $?

# ORU R01 — Lab Result (CBC)
RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/hl7/parse" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/hl7_oru_r01.txt"))
[ "$RESPONSE" = "200" ]
run_test "ORU R01 — CBC Lab Result (Doe^Jane)" $?

# ADT A08 — Patient Update
RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/hl7/parse" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/hl7_adt_a08.txt"))
[ "$RESPONSE" = "200" ]
run_test "ADT A08 — Patient Update (Garcia^Maria)" $?

# ORU R01 — Bloodwork Panel
RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/hl7/parse" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/hl7_oru_bloodwork.txt"))
[ "$RESPONSE" = "200" ]
run_test "ORU R01 — Basic Metabolic Panel (Chen^David)" $?

echo ""

# ---------------------------------------------------
bold "3. HL7 Parsing — JSON Wrapper"
# ---------------------------------------------------

RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/hl7/parse/json" \
    -H "Content-Type: application/json" \
    -d "{\"message\": \"MSH|^~\\\\&|HospitalEMR|MainHospital|HealthBridge|CLOUD|20240115120000||ADT^A01|MSG005|P|2.5\\rPID|1||PAT005^^^MRN||Wilson^Robert||19701225|M\"}")
[ "$RESPONSE" = "200" ]
run_test "ADT A01 via JSON wrapper (Wilson^Robert)" $?

echo ""

# ---------------------------------------------------
bold "4. HL7 ACK Generation"
# ---------------------------------------------------

RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/hl7/ack" \
    -H "Content-Type: application/json" \
    -d '{"messageId": "MSG001", "success": true}')
[ "$RESPONSE" = "200" ]
run_test "ACK — success for MSG001" $?

RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/hl7/ack" \
    -H "Content-Type: application/json" \
    -d '{"messageId": "MSG999", "success": false, "errorDetail": "Unknown patient ID"}')
[ "$RESPONSE" = "200" ]
run_test "NACK — error for MSG999" $?

echo ""

# ---------------------------------------------------
bold "5. DICOM Metadata (JSON mode)"
# ---------------------------------------------------

RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/dicom/metadata" \
    -H "Content-Type: application/json" \
    -d @"$SCRIPT_DIR/dicom_ct_chest.json")
[ "$RESPONSE" = "200" ]
run_test "CT Chest — Toronto General Hospital" $?

RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/dicom/metadata" \
    -H "Content-Type: application/json" \
    -d @"$SCRIPT_DIR/dicom_mri_brain.json")
[ "$RESPONSE" = "200" ]
run_test "MRI Brain — Sunnybrook" $?

RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/dicom/metadata" \
    -H "Content-Type: application/json" \
    -d @"$SCRIPT_DIR/dicom_xray_hand.json")
[ "$RESPONSE" = "200" ]
run_test "X-Ray Hand — Mount Sinai" $?

RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/dicom/metadata" \
    -H "Content-Type: application/json" \
    -d @"$SCRIPT_DIR/dicom_ultrasound.json")
[ "$RESPONSE" = "200" ]
run_test "Ultrasound Abdomen — St. Michael's" $?

echo ""

# ---------------------------------------------------
bold "6. FHIR Translation (HL7 v2 → FHIR R4)"
# ---------------------------------------------------

# ADT A01 → FHIR Patient + Encounter
RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/fhir/translate" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/fhir_translate_adt.txt"))
[ "$RESPONSE" = "200" ]
run_test "ADT A01 → FHIR Bundle (Patient + Encounter)" $?

# ORU R01 → FHIR Patient + DiagnosticReport + Observations
RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/fhir/translate" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/fhir_translate_oru.txt"))
[ "$RESPONSE" = "200" ]
run_test "ORU R01 → FHIR Bundle (Patient + DiagnosticReport + 5 Observations)" $?

echo ""

# ---------------------------------------------------
bold "7. DICOM File Upload (.dcm)"
# ---------------------------------------------------

DCM_DIR="$SCRIPT_DIR/dcm-samples"
if [ -d "$DCM_DIR" ] && ls "$DCM_DIR"/*.dcm 1>/dev/null 2>&1; then
    for dcmfile in "$DCM_DIR"/*.dcm; do
        fname=$(basename "$dcmfile")
        RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/api/dicom/parse" \
            -F "file=@$dcmfile")
        [ "$RESPONSE" = "200" ]
        run_test "Upload $fname" $?
    done
else
    echo "  (skipped — no .dcm files found. Run ./test-data/download_sample_dcm.sh first)"
fi

echo ""

# ---------------------------------------------------
bold "8. Monitoring & Dashboard"
# ---------------------------------------------------

curl -sf "$BASE_URL/metrics" > /dev/null 2>&1
run_test "Metrics endpoint" $?

curl -sf "$BASE_URL/dashboard" > /dev/null 2>&1
run_test "Dashboard HTML page" $?

echo ""

# ---------------------------------------------------
bold "============================================"
TOTAL=$((PASS + FAIL))
if [ "$FAIL" -eq 0 ]; then
    green "  All $TOTAL tests passed!"
else
    red "  $FAIL/$TOTAL tests failed"
fi
bold "============================================"

exit $FAIL
