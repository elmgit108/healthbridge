# HealthBridge

Polyglot microservices project for healthcare data integration. Parses HL7 v2
clinical messages and DICOM medical imaging metadata, translates HL7 to FHIR R4,
and reports service health — with distributed tracing across all three services.

```
  ┌──────────────────────────────────────────────────┐
  │              Go API Gateway :8080                │
  │        routes all traffic, health aggregation    │
  └──────────┬────────────────────────┬──────────────┘
             │                        │
  ┌──────────▼──────────┐  ┌──────────▼──────────────┐
  │  C# HL7/DICOM :5001 │  │  Python Monitoring :5002│
  │  nHapi + fo-dicom   │  │  Flask + boto3 + Azure  │
  │  + Firely (FHIR R4) │  │  Metrics + Dashboard    │
  └──────────┬──────────┘  └──────────┬──────────────┘
             │                        │
             └────────┬───────────────┘
                      ▼
             ┌──────────────────┐
             │  Jaeger  :16686  │
             │  OTLP traces     │
             └──────────────────┘
```

**Stack:** C# / ASP.NET Core 8 · Go 1.25 · Python 3.11 / Flask · OpenTelemetry ·
Docker Compose · Nix

---

## Quick Start (local)

```bash
# 1. Clone
git clone git@github.com:elmgit108/healthbridge.git
cd healthbridge
direnv allow                                    # optional — needs direnv installed

# 2. Make sure the Docker daemon is running, then build and start
docker info >/dev/null 2>&1 || open -a Docker   # starts Docker Desktop if needed
docker compose up --build

# 3. Verify — returns aggregated health of all services
curl http://localhost:8080/health
```

| Service | URL | What it does |
|---------|-----|--------------|
| Go Gateway | http://localhost:8080 | Entry point, routes to backend services |
| C# HL7/DICOM/FHIR | http://localhost:5001 | Parses HL7 v2, DICOM metadata, HL7→FHIR |
| Python Monitoring | http://localhost:5002 | Health metrics, CloudWatch/Azure push |
| Swagger UI | http://localhost:5001/swagger | Interactive API docs |
| Dashboard | http://localhost:8080/dashboard | Visual service status page |
| Jaeger UI | http://localhost:16686 | Distributed traces across all services |

### Development shell (optional)

The toolchain is pinned with Nix, so every machine gets the same Go, .NET and
Python versions the Dockerfiles use:

```bash
nix develop          # Go 1.25 + .NET SDK 8 + Python 3.11
```

With [direnv](https://direnv.net/) installed, `direnv allow` loads it automatically
on `cd`.

---

## Run the Tests

**302 unit tests** across the three services. No Docker, no network:

| Suite | Tests | Command |
|-------|-------|---------|
| C# — HL7/DICOM/FHIR parsing, security | 199 | `dotnet test HealthBridge.sln` |
| Go — gateway routing, health aggregation | 32 | `cd gateway && go test ./...` |
| Python — monitoring, publishers, routes | 71 | needs a virtualenv first — see below |

C# and Go run straight from a clone. The Python suite needs a virtualenv, which
is gitignored and therefore absent after cloning:

```bash
cd monitoring-service
python3 -m venv .venv                                        # once per clone
.venv/bin/pip install -r requirements.txt -r requirements-dev.txt
.venv/bin/python -m pytest                                   # every run after that
```

Skipping the first two lines gives `zsh: no such file or directory:
.venv/bin/python` — the venv simply is not there yet.

**End-to-end smoke tests** — require the stack to be running:

```bash
# Automated smoke test across all endpoints
./test-data/run_all_tests.sh

# Verbose mode — full JSON responses (good for demos)
./test-data/test_verbose.sh

# (Optional) Download real .dcm files, then re-run to include upload tests
./test-data/download_sample_dcm.sh
./test-data/run_all_tests.sh
```

---

## Try the API

### Parse an HL7 ADT A01 (Patient Admission)

```bash
curl -X POST http://localhost:8080/api/hl7/parse \
  -H "Content-Type: text/plain" \
  --data-binary $'MSH|^~\\&|HospitalEMR|MainHospital|HealthBridge|CLOUD|20240115120000||ADT^A01|MSG001|P|2.5\rEVN|A01|20240115120000\rPID|1||PAT001^^^MRN||Smith^John^A||19800315|M\rPV1|1|I|ICU^101^A|E'
```

### Parse an HL7 ORU R01 (Lab Result)

```bash
curl -X POST http://localhost:8080/api/hl7/parse \
  -H "Content-Type: text/plain" \
  --data-binary $'MSH|^~\\&|LabSystem|MainLab|HealthBridge|CLOUD|20240115130000||ORU^R01|MSG002|P|2.5\rPID|1||PAT002^^^MRN||Doe^Jane||19920720|F\rOBR|1|||CBC^Complete Blood Count\rOBX|1|NM|WBC^White Blood Cell Count||7.5|10*3/uL|4.5-11.0|N|||F'
```

### Translate HL7 to FHIR R4

```bash
curl -X POST http://localhost:8080/api/fhir/translate \
  -H "Content-Type: text/plain" \
  --data-binary $'MSH|^~\\&|HospitalEMR|MainHospital|HealthBridge|CLOUD|20240115120000||ADT^A01|MSG001|P|2.5\rEVN|A01|20240115120000\rPID|1||PAT001^^^MRN||Smith^John^A||19800315|M\rPV1|1|I|ICU^101^A|E'
```

### Submit DICOM Metadata (JSON — no .dcm file needed)

```bash
curl -X POST http://localhost:8080/api/dicom/metadata \
  -H "Content-Type: application/json" \
  -d '{
    "patientName": "Smith^John",
    "patientId": "PAT001",
    "studyDate": "20240115",
    "modality": "CT",
    "studyDescription": "Chest CT without contrast",
    "institutionName": "Toronto General Hospital"
  }'
```

### Upload a Real DICOM File

```bash
curl -X POST http://localhost:8080/api/dicom/parse \
  -F "file=@test-data/dcm-samples/CT_small.dcm"
```

### Generate an HL7 ACK

```bash
curl -X POST http://localhost:8080/api/hl7/ack \
  -H "Content-Type: application/json" \
  -d '{"messageId": "MSG001", "success": true}'
```

---

## API Endpoints

All requests go through the gateway on port 8080.

| Method | Path | Backend | Description |
|--------|------|---------|-------------|
| GET | `/health` | gateway | Aggregated health check (fans out to both services) |
| POST | `/api/hl7/parse` | hl7-service | Parse raw HL7 v2 message (`text/plain` body) |
| POST | `/api/hl7/parse/json` | hl7-service | Parse HL7 message in JSON wrapper |
| POST | `/api/hl7/ack` | hl7-service | Generate HL7 ACK/NACK response |
| POST | `/api/dicom/parse` | hl7-service | Upload `.dcm` file, extract metadata |
| POST | `/api/dicom/metadata` | hl7-service | Submit DICOM metadata as JSON |
| POST | `/api/fhir/translate` | hl7-service | Translate raw HL7 v2 to FHIR R4 (`text/plain`) |
| POST | `/api/fhir/translate/json` | hl7-service | Translate HL7 to FHIR from a JSON wrapper |
| GET | `/metrics` | monitoring | Service health metrics (JSON) |
| POST | `/metrics/push` | monitoring | Push custom metric to AWS CloudWatch |
| POST | `/metrics/push/azure` | monitoring | Push custom metric to Azure Monitor |
| GET | `/dashboard` | monitoring | Visual status dashboard (HTML) |

---

## Observability

All three services are instrumented with **OpenTelemetry** and export traces over
OTLP to Jaeger. A single request through the gateway produces one trace spanning
the gateway proxy hop and the downstream service.

```bash
docker compose up --build
curl http://localhost:8080/health
open http://localhost:16686        # search for service "gateway"
```

| Piece | Where |
|-------|-------|
| Go tracing setup | [gateway/tracing.go](gateway/tracing.go) — `otelhttp` on the handler and the proxy Transport |
| Python tracing setup | [monitoring-service/infrastructure/tracing.py](monitoring-service/infrastructure/tracing.py) |
| C# tracing setup | OpenTelemetry ASP.NET Core + HTTP instrumentation, wired in `Program.cs` |
| Collector | `jaeger` in [docker-compose.yml](docker-compose.yml) — OTLP gRPC 4317, HTTP 4318 |

---

## Standards & Sources

Every field mapping, code value and message structure traces back to a published
standard. The same URLs are repeated inline in the code next to the mapping they
justify.

| Area | Primary source | Status |
|------|---------------|--------|
| HL7 v2.5 messages (ADT, ORU, ACK, PID/PV1/OBR/OBX, tables) | [Caristix v2.5 reference](https://hl7-definition.caristix.com/v2/HL7v2.5) — free rendering of the [HL7 International standard](https://www.hl7.org/implement/standards/) | Reference (spec is paid) |
| DICOM tags, modules, VRs, file format | [DICOM PS3 (NEMA)](https://dicom.nema.org/medical/dicom/current/output/chtml/) | Normative, free |
| FHIR R4 resources & terminology | [FHIR R4 4.0.1](https://www.hl7.org/fhir/R4/) · [terminology.hl7.org](https://terminology.hl7.org/) | Normative, free |
| HL7 v2 → FHIR mappings | [HL7 v2-to-FHIR IG](https://build.fhir.org/ig/HL7/v2-to-fhir/) | Normative (ballot) |
| PHI encryption & audit logging | [45 CFR §164.312](https://www.ecfr.gov/current/title-45/subtitle-A/subchapter-C/part-164/subpart-C/section-164.312) · [NIST SP 800-38D](https://csrc.nist.gov/pubs/sp/800/38/d/final) · [NIST SP 800-66r2](https://csrc.nist.gov/pubs/sp/800/66/r2/final) | Normative |
| Parsing libraries | [nHapi](https://github.com/nHapiNET/nHapi) · [fo-dicom](https://github.com/fo-dicom/fo-dicom) · [Firely .NET SDK](https://github.com/FirelyTeam/firely-net-sdk) | Implementation |

---

## Project Structure

```
healthbridge/
├── hl7-service/                 C# — HL7 v2 + DICOM parser + FHIR translation
│   ├── Controllers/             HL7, DICOM, FHIR, health endpoints
│   ├── Services/                nHapi parsing, fo-dicom extraction
│   ├── Strategies/              Strategy Pattern for HL7 message types
│   ├── Builders/                HL7 ACK/NACK message builder
│   ├── Models/                  HL7ParseResult, DicomMetadata
│   └── Dockerfile               Multi-stage build, port 5001
├── hl7-service.Tests/           C# — xUnit unit tests (199)
├── gateway/                     Go — API gateway / reverse proxy
│   ├── main.go                  Routing, health fan-out, logging middleware
│   ├── tracing.go               OpenTelemetry setup
│   ├── main_test.go             Gateway tests (32)
│   └── Dockerfile               Multi-stage build, port 8080
├── monitoring-service/          Python — health metrics + cloud telemetry
│   ├── api/routes.py            Flask endpoints
│   ├── services/                MonitoringManager (business logic)
│   ├── infrastructure/          CloudWatch + Azure Monitor publishers, tracing
│   ├── core/                    Interfaces, data models, constants
│   ├── background/              APScheduler health sweep
│   ├── tests/                   pytest suite (71)
│   └── Dockerfile               Python 3.11 slim, port 5002
├── test-data/                   Sample HL7/DICOM data + smoke-test scripts
├── HealthBridge.sln             .NET solution (service + tests)
├── docker-compose.yml           Local dev — all services + Jaeger
├── flake.nix / flake.lock       Nix-pinned toolchain (Go, .NET, Python)
└── README.md                    ← you are here
```

---

## Test Data

All test data is in `test-data/` — **fictional patients, no real PHI**.

| File | Type | Scenario |
|------|------|----------|
| `hl7_adt_a01.txt` | ADT^A01 | Patient admission — Smith, John (ICU) |
| `hl7_adt_a08.txt` | ADT^A08 | Patient update — Garcia, Maria (Cardiology) |
| `hl7_oru_r01.txt` | ORU^R01 | CBC lab result — Doe, Jane |
| `hl7_oru_bloodwork.txt` | ORU^R01 | Basic Metabolic Panel — Chen, David |
| `dicom_ct_chest.json` | DICOM | CT chest — Toronto General Hospital |
| `dicom_mri_brain.json` | DICOM | MRI brain — Sunnybrook |
| `dicom_xray_hand.json` | DICOM | X-ray hand — Mount Sinai |
| `dicom_ultrasound.json` | DICOM | Ultrasound — St. Michael's |

---


## Stop & Teardown

```bash
docker compose down          # stop containers
docker compose down -v       # also remove volumes
```
