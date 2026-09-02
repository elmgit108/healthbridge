# HealthBridge — Setup Guide

Build, run, test and debug the HealthBridge microservices locally, with Docker
Compose.

---

## Prerequisites

Only Docker is strictly required. Everything else is for working on the code
outside containers.

| Tool | Version | Install | Needed for |
|------|---------|---------|-----------|
| Docker | 24+ | [docker.com](https://docs.docker.com/get-docker/) | Running the stack |
| Docker Compose | v2+ | Included with Docker Desktop | Running the stack |
| Nix | 2.18+ | [nixos.org](https://nixos.org/download) | Pinned toolchain (recommended) |
| Go | 1.25 | `brew install go` | Gateway dev, if not using Nix |
| .NET SDK | 8.0 | `brew install dotnet-sdk` | HL7 service dev, if not using Nix |
| Python | 3.11+ | `brew install python@3.11` | Monitoring dev, if not using Nix |

The demo console under `web/` needs **nothing extra** — it is plain HTML, CSS and
JavaScript served by nginx. No Node.js, no npm, no build step.

### The Nix shell

`flake.nix` pins the exact versions the Dockerfiles use — Go 1.25, .NET SDK 8,
Python 3.11 — so local builds match container builds:

```bash
nix develop
```

With [direnv](https://direnv.net/), `direnv allow` loads it on `cd` and unloads
it when you leave. `.envrc` is committed for that purpose.

---

## Phase 1 — Run the stack

### 1.1 Clone

```bash
git clone git@github.com:elmgit108/healthbridge.git
cd healthbridge
direnv allow            # optional — loads the Nix toolchain, needs direnv installed
```

`direnv allow` is only needed if you want the pinned Go/.NET/Python toolchain for
working on the code. Running the stack in Docker does not require it. Without
direnv installed the command reports `command not found` — harmless, skip it.

### 1.2 Check the Docker daemon

Compose talks to a running daemon over a socket. If Docker Desktop is not
started, the socket file does not exist and every `docker` command fails.

```bash
docker info >/dev/null 2>&1 && echo "daemon ready" || open -a Docker
```

If it opened Docker Desktop, wait 20–30 seconds and re-run until it prints
`daemon ready`. To see which socket your CLI is targeting:

```bash
docker context show     # e.g. desktop-linux
docker context ls       # maps each context to its DOCKER ENDPOINT
```

### 1.3 Start everything

```bash
docker compose up --build
```

This builds and starts:

| Container | Port | Purpose |
|-----------|------|---------|
| `hl7-service` | 5001 | C# — HL7 v2 parsing, DICOM metadata, FHIR R4 translation |
| `monitoring-service` | 5002 | Python — health metrics, cloud publishers, dashboard |
| `web` | 8081 | nginx — the demo console, reached through the gateway |
| `gateway` | 8080 | Go — reverse proxy and health aggregation |
| `jaeger` | 16686 | Trace UI (OTLP receivers on 4317 / 4318) |

### 1.4 Verify

```bash
curl http://localhost:8080/health
```

Expected:

```json
{
  "status": "healthy",
  "service": "gateway",
  "timestamp": "2026-08-11T...",
  "components": {
    "hl7-service": { "status": "healthy", "service": "hl7-service" },
    "monitoring-service": { "status": "healthy", "service": "monitoring-service" }
  }
}
```

If a component reports unhealthy, the gateway still responds — that is the point
of the fan-out. Check which one with `docker compose ps`.

### 1.5 Open the demo console

```bash
open http://localhost:8080/
```

Three pages, served by the `web` container and reached **through the gateway**,
so the browser stays on one origin and the pages can call `/api/...` with no
cross-origin setup:

| Page | What it does |
|---|---|
| HL7 | build a message from fields, send it, see the parsed patient and the ACK |
| FHIR | translate the same message to a FHIR R4 Bundle |
| DICOM | drag in a `.dcm` file, read the extracted tags |

Useful for demonstrating the API without `curl`, and for generating traffic by
clicking when you are watching traces or metrics.

#### Editing the pages

The pages are baked into the image at build time, so an edit needs a rebuild of
that one container:

```bash
docker compose up -d --build web
```

Takes a couple of seconds — it is nginx plus four files. Then reload the browser.

`nginx.conf` sets `Cache-Control: no-store`, so you never have to force-refresh
or wonder whether you are looking at an old page during a demo.

To iterate faster while writing CSS or JavaScript, mount the folder instead of
rebuilding, by adding this to the `web` service in `docker-compose.yml`:

```yaml
    volumes:
      - ./web:/usr/share/nginx/html:ro
```

Then edits appear on reload with no rebuild at all. Remove it before committing —
the image must contain the files, or the Kubernetes deployment ships an empty
page.

### 1.6 Smoke tests

`test-data/` holds sample HL7 messages, DICOM metadata and test scripts.

```bash
# Pass/fail across all endpoints
./test-data/run_all_tests.sh

# Verbose — full JSON responses, good for demos and screenshots
./test-data/test_verbose.sh
```

### 1.7 (Optional) Real DICOM files

```bash
# Downloads sample .dcm files from the fo-dicom open-source repo
./test-data/download_sample_dcm.sh

# Re-run — now includes .dcm upload tests
./test-data/run_all_tests.sh
```

### 1.8 Manual endpoint checks

```bash
# Parse an HL7 ADT A01
curl -X POST http://localhost:8080/api/hl7/parse \
  -H "Content-Type: text/plain" \
  --data-binary $'MSH|^~\\&|HospitalEMR|MainHospital|HealthBridge|CLOUD|20240115120000||ADT^A01|MSG001|P|2.5\rEVN|A01|20240115120000\rPID|1||PAT001^^^MRN||Smith^John^A||19800315|M\rPV1|1|I|ICU^101^A|E'
```

```bash
# Translate the same message to FHIR R4
curl -X POST http://localhost:8080/api/fhir/translate \
  -H "Content-Type: text/plain" \
  --data-binary $'MSH|^~\\&|HospitalEMR|MainHospital|HealthBridge|CLOUD|20240115120000||ADT^A01|MSG001|P|2.5\rEVN|A01|20240115120000\rPID|1||PAT001^^^MRN||Smith^John^A||19800315|M\rPV1|1|I|ICU^101^A|E'
```

```bash
# DICOM metadata as JSON
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

```
curl http://localhost:8080/metrics       # service metrics
open  http://localhost:8080/dashboard    # visual status page
open  http://localhost:5001/swagger      # interactive API docs
open  http://localhost:16686             # Jaeger traces
```

### 1.9 Stop

```bash
docker compose down          # stop containers
docker compose down -v       # also remove volumes
```

---

## Phase 2 — Run the unit tests

300 tests total, no Docker and no network required.

The `web` service has no unit tests. It is static HTML with no build step and no
server-side logic; what could break is the **contract with the API**, and that is
already covered — the C# and Go suites pin the response shapes the pages read.

### 2.1 C# — 199 tests

```bash
dotnet test HealthBridge.sln
```

Covers HL7 v2 parsing, DICOM extraction, FHIR translation, and PHI security
behaviour. `HealthBridge.sln` includes both `hl7-service` and `hl7-service.Tests`.

### 2.2 Go — 32 tests

```bash
cd gateway
go test ./...
go test -v ./...          # per-test output
```

Covers routing, reverse-proxy behaviour and the concurrent health fan-out.

### 2.3 Python — 69 tests

First run needs a virtualenv:

```bash
cd monitoring-service
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt -r requirements-dev.txt
```

Then:

```bash
.venv/bin/python -m pytest
.venv/bin/python -m pytest -v          # per-test output
```

`.venv/` is gitignored, so it stays local to your machine.

---

## Phase 3 — Tracing

Every service is instrumented with OpenTelemetry and exports OTLP spans to
Jaeger. One request through the gateway produces one trace covering the proxy
hop and the downstream handler.

```bash
docker compose up --build
curl http://localhost:8080/api/hl7/parse -H "Content-Type: text/plain" --data-binary 'MSH|...'
open http://localhost:16686
```

In the Jaeger UI, pick service **gateway** and press *Find Traces*. Each trace
shows the gateway span with the `hl7-service` span nested beneath it.

| Language | Setup lives in |
|----------|----------------|
| Go | [gateway/tracing.go](gateway/tracing.go) — `otelhttp` wraps both the router and the proxy Transport |
| Python | [monitoring-service/infrastructure/tracing.py](monitoring-service/infrastructure/tracing.py) |
| C# | OpenTelemetry ASP.NET Core + HTTP instrumentation, wired in `hl7-service/Program.cs` |

The collector endpoint is passed to each container as `OTEL_EXPORTER_OTLP_ENDPOINT`
in [docker-compose.yml](docker-compose.yml).

---

## Project Structure

```
healthbridge/
├── hl7-service/                 C# — HL7 v2 parser, DICOM extractor, FHIR translator
│   ├── Controllers/             HL7Controller, DicomController, FhirController, health
│   ├── Services/                nHapi parsing, fo-dicom extraction
│   ├── Strategies/              Strategy Pattern per HL7 message type
│   ├── Builders/                HL7 ACK/NACK builder
│   ├── Models/                  HL7ParseResult, DicomMetadata
│   ├── Program.cs               DI wiring, OpenTelemetry, startup
│   └── Dockerfile               Multi-stage build, port 5001
├── hl7-service.Tests/           xUnit suite (199 tests)
├── gateway/                     Go — API gateway / reverse proxy
│   ├── main.go                  Routing, health fan-out, logging middleware
│   ├── tracing.go               OpenTelemetry setup
│   ├── main_test.go             Gateway tests (32)
│   └── Dockerfile               Multi-stage build, port 8080
├── monitoring-service/          Python — health metrics + cloud telemetry
├── web/                         Static demo console — nginx, no build step
│   ├── index.html               HL7 v2 message builder
│   ├── fhir.html                HL7 → FHIR R4 translation
│   ├── dicom.html               .dcm upload + metadata
│   └── nginx.conf               port 8081, /healthz
│   ├── api/routes.py            Flask endpoints
│   ├── services/                MonitoringManager (business logic)
│   ├── infrastructure/          CloudWatch + Azure publishers, HTTP checker, tracing
│   ├── core/                    Interfaces, models, constants
│   ├── background/              APScheduler health sweep
│   ├── templates/dashboard.html Visual status dashboard
│   ├── tests/                   pytest suite (69 tests)
│   └── Dockerfile               Python 3.11 slim, port 5002
├── test-data/                   Sample HL7/DICOM data + smoke-test scripts
├── HealthBridge.sln             .NET solution (service + tests)
├── docker-compose.yml           Local dev — all services + Jaeger
├── flake.nix / flake.lock       Nix-pinned toolchain
├── .envrc                       direnv hook for the Nix shell
└── SETUP.md                     ← you are here
```

---

## Quick Reference

### Service URLs (local)

| Service | URL |
|---------|-----|
| **Demo console** | http://localhost:8080/ |
| Gateway (entry point) | http://localhost:8080 |
| HL7/DICOM/FHIR service | http://localhost:5001 |
| Monitoring service | http://localhost:5002 |
| Swagger UI | http://localhost:5001/swagger |
| Dashboard | http://localhost:8080/dashboard |
| Jaeger UI | http://localhost:16686 |

### API endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/health` | Aggregated health check |
| POST | `/api/hl7/parse` | Parse raw HL7 message (`text/plain`) |
| POST | `/api/hl7/parse/json` | Parse HL7 in JSON wrapper |
| POST | `/api/hl7/ack` | Generate HL7 ACK |
| POST | `/api/dicom/parse` | Upload `.dcm` file |
| POST | `/api/dicom/metadata` | DICOM metadata as JSON |
| POST | `/api/fhir/translate` | HL7 v2 → FHIR R4 (`text/plain`) |
| POST | `/api/fhir/translate/json` | HL7 v2 → FHIR R4 (JSON wrapper) |
| GET | `/metrics` | Service health metrics |
| POST | `/metrics/push/aws` | Push metric to AWS CloudWatch |
| POST | `/metrics/push/azure` | Azure publisher is not wired up — returns 502 |
| GET | `/dashboard` | Visual status dashboard |

The gateway forwards `/api/hl7/`, `/api/dicom/` and `/api/fhir/` to the C#
service, and `/metrics` and `/dashboard` to the Python service. `/health` is
handled by the gateway itself.

### Test data (in `test-data/`)

| File | Type | Description |
|------|------|-------------|
| `hl7_adt_a01.txt` | HL7 ADT^A01 | Patient admission — Smith, John (ICU) |
| `hl7_adt_a08.txt` | HL7 ADT^A08 | Patient update — Garcia, Maria (Cardiology) |
| `hl7_oru_r01.txt` | HL7 ORU^R01 | CBC lab result — Doe, Jane |
| `hl7_oru_bloodwork.txt` | HL7 ORU^R01 | Basic Metabolic Panel — Chen, David (5 OBX results) |
| `dicom_ct_chest.json` | DICOM JSON | CT scan — Toronto General Hospital |
| `dicom_mri_brain.json` | DICOM JSON | MRI brain — Sunnybrook |
| `dicom_xray_hand.json` | DICOM JSON | X-ray hand — Mount Sinai |
| `dicom_ultrasound.json` | DICOM JSON | Ultrasound abdomen — St. Michael's |
| `run_all_tests.sh` | Script | Automated pass/fail smoke test |
| `test_verbose.sh` | Script | Full JSON output for demos |
| `download_sample_dcm.sh` | Script | Downloads real `.dcm` files from the fo-dicom repo |

All patient data is **fictional** — no real PHI.

---

## Troubleshooting

**`failed to connect to the docker API at unix:///…/docker.sock … no such file or directory`**

```
unable to get image 'jaegertracing/all-in-one:1.55': failed to connect to the
docker API at unix:///Users/you/.docker/run/docker.sock; check if the path is
correct and if the daemon is running
```

The Docker daemon is not running. It creates that socket file at startup and
removes it on quit, so a missing file means it was never started this session —
the image name in the message is incidental, it just happens to be the first one
Compose tried to pull.

```bash
open -a Docker                     # start Docker Desktop
docker info >/dev/null && echo ok  # re-run until this prints ok
```

If Docker *is* running and you still see this, your CLI is pointed at the wrong
socket. Compare the endpoint against the engine you actually use:

```bash
docker context ls        # which endpoint is starred
docker context use desktop-linux
echo "${DOCKER_HOST:-unset}"       # an old DOCKER_HOST export overrides the context
```

**Docker Compose build fails**
- Check disk space: `docker system df`, then `docker system prune` if tight
- Stale layers: `docker compose build --no-cache`

**Health check returns unhealthy**
- Which service? `docker compose ps` and `docker compose logs <service>`
- The gateway reports `unhealthy` for a component it cannot reach — that is
  working as designed, not a gateway fault

**`dotnet test` cannot find a project**
- Run it from the repo root; `HealthBridge.sln` references both
  `hl7-service/` and `hl7-service.Tests/` by relative path

**`zsh: no such file or directory: .venv/bin/python`**
- Expected in a fresh clone — `.venv/` is gitignored and never checked in.
  Create it with the two setup lines in step 2.3 before running pytest.

**Python tests fail on import**
- The virtualenv is stale or was built against a different Python. Recreate it:
  `rm -rf monitoring-service/.venv` then repeat step 2.3

**No traces in Jaeger**
- Traces only appear after a request — `curl http://localhost:8080/health` first
- Confirm Jaeger is up: `docker compose ps jaeger`
- Check `OTEL_EXPORTER_OTLP_ENDPOINT` reaches `jaeger:4317` from inside the
  container network, not `localhost`

**Demo console shows an old version after editing**
- The pages live inside the image. Rebuild that container:
  `docker compose up -d --build web`
- If it still looks stale, the browser is not at fault — `nginx.conf` sends
  `Cache-Control: no-store`. Check the rebuild actually ran: `docker compose ps web`

**Demo console returns 404 for every page**
- The `web` container is down; the gateway then has nothing to proxy to.
  `docker compose ps web` and `docker compose logs web`

**A page loads but every request from it fails**
- Open the browser console. If you see CORS errors, you have opened the page
  directly (`file://` or `localhost:8081`) instead of **through the gateway**.
  Use `http://localhost:8080/` — that is what keeps everything on one origin.

**Ports already in use**
- 8080, 5001, 5002, 8081 and 16686 must be free: `lsof -i :8080`

---

## Scope

This guide covers running HealthBridge **locally, with Docker Compose**. Every
command here has been run and works.
