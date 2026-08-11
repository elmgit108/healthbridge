#!/bin/bash
# HealthBridge — Verbose Test Script
# Shows full request/response for each endpoint (great for demos & screenshots)
#
# Usage:
#   ./test-data/test_verbose.sh
#   ./test-data/test_verbose.sh http://x.x.x.x:8080

BASE_URL="${1:-http://localhost:8080}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

bold()  { printf "\n\033[1;36m━━━ %s ━━━\033[0m\n\n" "$1"; }

bold "Gateway Health Check"
curl -s "$BASE_URL/health" | python3 -m json.tool
echo ""

bold "HL7 ADT A01 — Patient Admission"
echo "Sending: Smith^John^A, MRN PAT001, ICU admission"
curl -s -X POST "$BASE_URL/api/hl7/parse" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/hl7_adt_a01.txt") | python3 -m json.tool
echo ""

bold "HL7 ORU R01 — Lab Result (CBC)"
echo "Sending: Doe^Jane, WBC count = 7.5"
curl -s -X POST "$BASE_URL/api/hl7/parse" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/hl7_oru_r01.txt") | python3 -m json.tool
echo ""

bold "HL7 ORU R01 — Bloodwork Panel"
echo "Sending: Chen^David, Glucose/BUN/Creatinine/Na/K"
curl -s -X POST "$BASE_URL/api/hl7/parse" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/hl7_oru_bloodwork.txt") | python3 -m json.tool
echo ""

bold "HL7 ACK Generation"
echo "Generating ACK for MSG001"
curl -s -X POST "$BASE_URL/api/hl7/ack" \
    -H "Content-Type: application/json" \
    -d '{"messageId": "MSG001", "success": true}'
echo ""

bold "DICOM — CT Chest (Toronto General)"
curl -s -X POST "$BASE_URL/api/dicom/metadata" \
    -H "Content-Type: application/json" \
    -d @"$SCRIPT_DIR/dicom_ct_chest.json" | python3 -m json.tool
echo ""

bold "DICOM — MRI Brain (Sunnybrook)"
curl -s -X POST "$BASE_URL/api/dicom/metadata" \
    -H "Content-Type: application/json" \
    -d @"$SCRIPT_DIR/dicom_mri_brain.json" | python3 -m json.tool
echo ""

bold "DICOM — X-Ray Hand (Mount Sinai)"
curl -s -X POST "$BASE_URL/api/dicom/metadata" \
    -H "Content-Type: application/json" \
    -d @"$SCRIPT_DIR/dicom_xray_hand.json" | python3 -m json.tool
echo ""

bold "DICOM — Ultrasound Abdomen (St. Michael's)"
curl -s -X POST "$BASE_URL/api/dicom/metadata" \
    -H "Content-Type: application/json" \
    -d @"$SCRIPT_DIR/dicom_ultrasound.json" | python3 -m json.tool
echo ""

bold "FHIR Translation — ADT A01 → Patient + Encounter"
curl -s -X POST "$BASE_URL/api/fhir/translate" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/fhir_translate_adt.txt") | python3 -m json.tool
echo ""

bold "FHIR Translation — ORU R01 → Patient + DiagnosticReport + Observations"
curl -s -X POST "$BASE_URL/api/fhir/translate" \
    -H "Content-Type: text/plain" \
    --data-binary @<(sed 's/$/\r/' "$SCRIPT_DIR/fhir_translate_oru.txt") | python3 -m json.tool
echo ""

bold "DICOM File Upload (.dcm)"
DCM_DIR="$SCRIPT_DIR/dcm-samples"
if [ -d "$DCM_DIR" ] && ls "$DCM_DIR"/*.dcm 1>/dev/null 2>&1; then
    for dcmfile in "$DCM_DIR"/*.dcm; do
        fname=$(basename "$dcmfile")
        echo "Uploading: $fname"
        curl -s -X POST "$BASE_URL/api/dicom/parse" \
            -F "file=@$dcmfile" | python3 -m json.tool
        echo ""
    done
else
    echo "(skipped — no .dcm files found. Run ./test-data/download_sample_dcm.sh first)"
fi
echo ""

bold "Monitoring Metrics"
curl -s "$BASE_URL/metrics" | python3 -m json.tool 2>/dev/null || curl -s "$BASE_URL/metrics"
echo ""

echo ""
echo "Dashboard available at: $BASE_URL/dashboard"
