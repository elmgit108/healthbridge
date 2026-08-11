#!/bin/bash
# Download sample DICOM (.dcm) files for testing the /api/dicom/parse endpoint
#
# Sources:
#   - fo-dicom GitHub repo (the same library used by our C# service)
#   - NEMA official DICOM sample datasets
#
# Usage:
#   ./test-data/download_sample_dcm.sh

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DCM_DIR="$SCRIPT_DIR/dcm-samples"
mkdir -p "$DCM_DIR"

bold()  { printf "\033[1m%s\033[0m\n" "$1"; }
green() { printf "\033[32m%s\033[0m\n" "$1"; }
red()   { printf "\033[31m%s\033[0m\n" "$1"; }

bold "Downloading sample DICOM files..."
echo ""

# --- fo-dicom test files (small, known-good, from the library we use) ---
FO_DICOM_BASE="https://raw.githubusercontent.com/fo-dicom/fo-dicom-samples/master/DICOM"

declare -A FILES=(
    ["CT_small.dcm"]="$FO_DICOM_BASE/CT-MONO2-16-ankle"
    ["CT1_J2KI.dcm"]="$FO_DICOM_BASE/CT1_J2KI"
    ["MR_small.dcm"]="$FO_DICOM_BASE/MR-MONO2-12-an2"
)

for filename in "${!FILES[@]}"; do
    url="${FILES[$filename]}"
    echo -n "  Downloading $filename ... "
    if curl -sfL -o "$DCM_DIR/$filename" "$url"; then
        SIZE=$(wc -c < "$DCM_DIR/$filename" | tr -d ' ')
        green "OK (${SIZE} bytes)"
    else
        red "FAILED"
    fi
done

echo ""
bold "Downloaded files:"
ls -lh "$DCM_DIR"/*.dcm 2>/dev/null || echo "  No .dcm files found"

echo ""
bold "Test with:"
echo "  curl -X POST http://localhost:8080/api/dicom/parse \\"
echo "    -F 'file=@$DCM_DIR/CT_small.dcm'"
echo ""
bold "Other free DICOM sample sources (manual download):"
echo "  - fo-dicom samples:  https://github.com/fo-dicom/fo-dicom-samples"
echo "  - NEMA WG04 (compression): ftp://medical.nema.org/medical/dicom/DataSets/WG04/"
echo "  - NEMA WG16 (MR):          ftp://medical.nema.org/medical/dicom/DataSets/WG16/"
echo "  - TCIA (research):          https://cancerimagingarchive.net/"
echo "  - Dclunie samples page:     https://www.dclunie.com/medical-image-faq/html/part8.html"
