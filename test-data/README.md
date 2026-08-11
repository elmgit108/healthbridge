# Test Data

Sample HL7 v2 messages and DICOM metadata for testing HealthBridge endpoints.

## HL7 Messages

| File | Type | Patient | Scenario |
|------|------|---------|----------|
| `hl7_adt_a01.txt` | ADT^A01 | Smith, John | Patient admission to ICU |
| `hl7_adt_a08.txt` | ADT^A08 | Garcia, Maria | Patient info update (Cardiology) |
| `hl7_oru_r01.txt` | ORU^R01 | Doe, Jane | CBC lab result |
| `hl7_oru_bloodwork.txt` | ORU^R01 | Chen, David | Basic Metabolic Panel (5 OBX segments) |

## DICOM Metadata (JSON)

| File | Modality | Institution | Scenario |
|------|----------|-------------|----------|
| `dicom_ct_chest.json` | CT | Toronto General Hospital | Chest CT without contrast |
| `dicom_mri_brain.json` | MR | Sunnybrook Health Sciences | Brain MRI with/without contrast |
| `dicom_xray_hand.json` | DX | Mount Sinai Hospital | Hand X-ray, rule out fracture |
| `dicom_ultrasound.json` | US | St. Michael's Hospital | Abdominal ultrasound |

## DICOM Binary Files (.dcm)

Real .dcm files for testing the file upload endpoint (`/api/dicom/parse`). These are **not** committed to the repo — download them on demand:

```bash
./test-data/download_sample_dcm.sh
```

This pulls small test files from the [fo-dicom samples repo](https://github.com/fo-dicom/fo-dicom-samples) (the same DICOM library our C# service uses).

**Other free sources** (from [Dclunie's FAQ](https://www.dclunie.com/medical-image-faq/html/part8.html)):
- [NEMA official datasets](ftp://medical.nema.org/medical/dicom/DataSets/) — WG04 (compression), WG12 (ultrasound), WG16 (MR)
- [Cancer Imaging Archive (TCIA)](https://cancerimagingarchive.net/) — large public research datasets
- [fo-dicom samples](https://github.com/fo-dicom/fo-dicom-samples) — small test files, various modalities

## Test Scripts

```bash
# Make scripts executable
chmod +x test-data/*.sh

# Quick pass/fail smoke test (all endpoints)
./test-data/run_all_tests.sh

# Verbose output with full JSON responses (good for demos)
./test-data/test_verbose.sh

# Test against a remote server
./test-data/run_all_tests.sh http://<server-ip>:8080
./test-data/test_verbose.sh http://<server-ip>:8080
```

## Notes

- All patient data is **fictional** — no real PHI
- HL7 messages use `\r` (carriage return) as segment separators per the HL7 v2 spec
- DICOM JSON files use the `/api/dicom/metadata` endpoint (no .dcm binary needed)
- Hospital names reference real Toronto institutions for realism but data is entirely fabricated
