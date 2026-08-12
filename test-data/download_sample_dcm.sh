#!/usr/bin/env bash
# Download sample DICOM (.dcm) files for testing the /api/dicom/parse endpoint
#
# Source: the fo-dicom test suite — the same library our C# service uses, so
# these files are known-good and exercise real transfer syntaxes.
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

# fo-dicom keeps its test corpus in the main repo, under a directory with a
# space in the name — hence the %20 in the URL.
FO_DICOM_BASE="https://raw.githubusercontent.com/fo-dicom/fo-dicom/development/Tests/FO-DICOM.Tests/Test%20Data"

# "local name|remote name" pairs. A plain indexed array is used deliberately:
# associative arrays (declare -A) need bash 4, and macOS ships bash 3.2.
FILES=(
    "CT_small.dcm|D_CLUNIE_CT1_RLE_FRAGS.dcm"
    "MR_small.dcm|mr_brucker.dcm"
    "CR_small.dcm|CR-ModalitySequenceLUT.dcm"
)

FAILED=0

for entry in "${FILES[@]}"; do
    filename="${entry%%|*}"
    remote="${entry#*|}"
    target="$DCM_DIR/$filename"

    printf "  Downloading %-14s ... " "$filename"

    if ! curl -sfL -o "$target" "$FO_DICOM_BASE/$remote"; then
        red "FAILED (download)"
        rm -f "$target"
        FAILED=$((FAILED + 1))
        continue
    fi

    # A DICOM Part 10 file carries the magic string "DICM" at byte offset 128.
    # Checking it catches an error page saved under a .dcm name.
    if [ "$(dd if="$target" bs=1 skip=128 count=4 2>/dev/null)" != "DICM" ]; then
        red "FAILED (not a DICOM file)"
        rm -f "$target"
        FAILED=$((FAILED + 1))
        continue
    fi

    SIZE=$(wc -c < "$target" | tr -d ' ')
    green "OK (${SIZE} bytes)"
done

echo ""
bold "Downloaded files:"
ls -lh "$DCM_DIR"/*.dcm 2>/dev/null || echo "  No .dcm files found"

if [ "$FAILED" -gt 0 ]; then
    echo ""
    red "$FAILED file(s) failed. The upstream paths may have moved — check:"
    echo "  https://github.com/fo-dicom/fo-dicom/tree/development/Tests/FO-DICOM.Tests/Test%20Data"
fi

echo ""
bold "Test with:"
echo "  curl -X POST http://localhost:8080/api/dicom/parse \\"
echo "    -F 'file=@$DCM_DIR/CT_small.dcm'"
echo ""
bold "Other free DICOM sample sources (manual download):"
echo "  - fo-dicom test data:      https://github.com/fo-dicom/fo-dicom/tree/development/Tests/FO-DICOM.Tests"
echo "  - NEMA WG04 (compression): ftp://medical.nema.org/medical/dicom/DataSets/WG04/"
echo "  - NEMA WG16 (MR):          ftp://medical.nema.org/medical/dicom/DataSets/WG16/"
echo "  - TCIA (research):          https://cancerimagingarchive.net/"
echo "  - Dclunie samples page:     https://www.dclunie.com/medical-image-faq/html/part8.html"

exit 0
