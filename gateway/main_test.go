// Unit tests for the HealthBridge API gateway.
//
// Everything here runs against httptest servers — no network, no Docker, no
// downstream services. Run with:
//
//	cd gateway && go test ./... -v
package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/mux"
)

// withServiceURLs points the package-level service URLs at test servers for the
// duration of one test, restoring them afterwards. healthHandler reads these
// globals directly, so they must be swapped rather than injected.
func withServiceURLs(t *testing.T, hl7, monitoring string) {
	t.Helper()
	originalHL7, originalMonitoring := hl7ServiceURL, monitoringServiceURL
	hl7ServiceURL, monitoringServiceURL = hl7, monitoring
	t.Cleanup(func() {
		hl7ServiceURL, monitoringServiceURL = originalHL7, originalMonitoring
	})
}

// healthyBackend is a stub service that answers GET /health with 200.
func healthyBackend() *httptest.Server {
	return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/health" {
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write([]byte(`{"status":"healthy","service":"stub"}`))
			return
		}
		w.WriteHeader(http.StatusNotFound)
	}))
}

// unhealthyBackend answers /health with 500.
func unhealthyBackend() *httptest.Server {
	return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusInternalServerError)
	}))
}

// --- getEnv ---------------------------------------------------------------

func TestGetEnvReturnsFallbackWhenUnset(t *testing.T) {
	if got := getEnv("HEALTHBRIDGE_DEFINITELY_UNSET", "fallback"); got != "fallback" {
		t.Errorf("getEnv() = %q, want %q", got, "fallback")
	}
}

func TestGetEnvReturnsTheEnvironmentValueWhenSet(t *testing.T) {
	t.Setenv("HEALTHBRIDGE_TEST_VAR", "from-env")

	if got := getEnv("HEALTHBRIDGE_TEST_VAR", "fallback"); got != "from-env" {
		t.Errorf("getEnv() = %q, want %q", got, "from-env")
	}
}

func TestGetEnvPrefersAnEmptyEnvironmentValueOverTheFallback(t *testing.T) {
	// LookupEnv distinguishes "set to empty" from "unset". A deployment that sets
	// HL7_SERVICE_URL="" should get the empty string, not silently fall back to the
	// Docker Compose default and appear to work.
	t.Setenv("HEALTHBRIDGE_EMPTY_VAR", "")

	if got := getEnv("HEALTHBRIDGE_EMPTY_VAR", "fallback"); got != "" {
		t.Errorf("getEnv() = %q, want empty string", got)
	}
}

// --- loggingResponseWriter ------------------------------------------------

func TestLoggingResponseWriterCapturesTheStatusCode(t *testing.T) {
	recorder := httptest.NewRecorder()
	lrw := &loggingResponseWriter{recorder, http.StatusOK}

	lrw.WriteHeader(http.StatusTeapot)

	if lrw.statusCode != http.StatusTeapot {
		t.Errorf("captured status = %d, want %d", lrw.statusCode, http.StatusTeapot)
	}
	if recorder.Code != http.StatusTeapot {
		t.Errorf("underlying writer status = %d, want %d", recorder.Code, http.StatusTeapot)
	}
}

func TestLoggingResponseWriterDefaultsTo200WhenHandlerNeverWritesAHeader(t *testing.T) {
	// A handler that only calls Write() implicitly returns 200 — the wrapper must
	// report that rather than a zero value.
	recorder := httptest.NewRecorder()
	lrw := &loggingResponseWriter{recorder, http.StatusOK}

	_, _ = lrw.Write([]byte("body"))

	if lrw.statusCode != http.StatusOK {
		t.Errorf("captured status = %d, want 200", lrw.statusCode)
	}
}

// --- requestLogger middleware ---------------------------------------------

func TestRequestLoggerPassesTheRequestThrough(t *testing.T) {
	var called bool
	handler := requestLogger(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		called = true
		w.WriteHeader(http.StatusCreated)
		_, _ = w.Write([]byte("created"))
	}))

	recorder := httptest.NewRecorder()
	handler.ServeHTTP(recorder, httptest.NewRequest(http.MethodPost, "/api/hl7/parse", nil))

	if !called {
		t.Fatal("middleware did not invoke the next handler")
	}
	if recorder.Code != http.StatusCreated {
		t.Errorf("status = %d, want 201", recorder.Code)
	}
	if body := recorder.Body.String(); body != "created" {
		t.Errorf("body = %q, want %q", body, "created")
	}
}

func TestRequestLoggerPreservesResponseHeaders(t *testing.T) {
	// The X-HL7-ACK header from the C# service travels back through this middleware.
	handler := requestLogger(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-HL7-ACK", "TVNBfEFB")
		w.WriteHeader(http.StatusOK)
	}))

	recorder := httptest.NewRecorder()
	handler.ServeHTTP(recorder, httptest.NewRequest(http.MethodGet, "/", nil))

	if got := recorder.Header().Get("X-HL7-ACK"); got != "TVNBfEFB" {
		t.Errorf("X-HL7-ACK = %q, want it preserved", got)
	}
}

// --- createProxy ----------------------------------------------------------

func TestProxyForwardsRequestsToTheBackend(t *testing.T) {
	var gotPath, gotMethod, gotBody string
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotPath, gotMethod = r.URL.Path, r.Method
		buf := make([]byte, r.ContentLength)
		_, _ = r.Body.Read(buf)
		gotBody = string(buf)
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte(`{"success":true}`))
	}))
	defer backend.Close()

	proxy := createProxy(backend.URL)
	recorder := httptest.NewRecorder()
	proxy.ServeHTTP(recorder, httptest.NewRequest(
		http.MethodPost, "/api/hl7/parse", strings.NewReader("MSH|^~\\&|")))

	if gotPath != "/api/hl7/parse" {
		t.Errorf("backend path = %q, want /api/hl7/parse", gotPath)
	}
	if gotMethod != http.MethodPost {
		t.Errorf("backend method = %q, want POST", gotMethod)
	}
	if gotBody != "MSH|^~\\&|" {
		t.Errorf("backend body = %q, want the HL7 message forwarded intact", gotBody)
	}
	if recorder.Code != http.StatusOK {
		t.Errorf("status = %d, want 200", recorder.Code)
	}
}

func TestProxyReturns503JsonWhenTheBackendIsDown(t *testing.T) {
	// A dead backend must produce JSON, not Go's default HTML error page — clients
	// of this gateway parse every response as JSON.
	backend := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	deadURL := backend.URL
	backend.Close() // nothing is listening now

	proxy := createProxy(deadURL)
	recorder := httptest.NewRecorder()
	proxy.ServeHTTP(recorder, httptest.NewRequest(http.MethodGet, "/api/hl7/parse", nil))

	if recorder.Code != http.StatusServiceUnavailable {
		t.Errorf("status = %d, want 503", recorder.Code)
	}

	var payload map[string]string
	if err := json.Unmarshal(recorder.Body.Bytes(), &payload); err != nil {
		t.Fatalf("response body is not JSON: %v (body: %q)", err, recorder.Body.String())
	}
	if payload["error"] == "" {
		t.Errorf("expected an 'error' field, got %v", payload)
	}
}

func TestProxyPreservesBackendStatusCodes(t *testing.T) {
	// The C# service answers 422 for an unparseable message; the gateway must not
	// flatten that into a 200 or a 500.
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusUnprocessableEntity)
		_, _ = w.Write([]byte(`{"error":"bad HL7"}`))
	}))
	defer backend.Close()

	recorder := httptest.NewRecorder()
	createProxy(backend.URL).ServeHTTP(recorder, httptest.NewRequest(http.MethodPost, "/api/hl7/parse", nil))

	if recorder.Code != http.StatusUnprocessableEntity {
		t.Errorf("status = %d, want 422", recorder.Code)
	}
}

func TestProxyPreservesBackendResponseHeaders(t *testing.T) {
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-HL7-ACK", "TVNBfEFBfE1TRzAwMQ==")
		w.WriteHeader(http.StatusOK)
	}))
	defer backend.Close()

	recorder := httptest.NewRecorder()
	createProxy(backend.URL).ServeHTTP(recorder, httptest.NewRequest(http.MethodPost, "/api/hl7/parse", nil))

	if got := recorder.Header().Get("X-HL7-ACK"); got != "TVNBfEFBfE1TRzAwMQ==" {
		t.Errorf("X-HL7-ACK = %q, want the backend value passed through", got)
	}
}

func TestProxySetsForwardingHeadersFromTheRealConnection(t *testing.T) {
	// The gateway must not trust X-Forwarded-* sent by the caller. The PHI audit
	// trail in hl7-service records who accessed patient data, and that record is
	// only as trustworthy as the client address behind it.
	//
	// This test fails if createProxy is ever switched back to
	// NewSingleHostReverseProxy, which keeps the caller's value and appends to it.
	var gotFor, gotHost, gotProto string
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotFor = r.Header.Get("X-Forwarded-For")
		gotHost = r.Header.Get("X-Forwarded-Host")
		gotProto = r.Header.Get("X-Forwarded-Proto")
	}))
	defer backend.Close()

	req := httptest.NewRequest(http.MethodGet, "/api/hl7/parse", nil)
	req.Host = "healthbridge.example"
	req.RemoteAddr = "1.2.3.4:5678"
	req.Header.Set("X-Forwarded-For", "9.9.9.9") // a caller pretending to be someone else

	createProxy(backend.URL).ServeHTTP(httptest.NewRecorder(), req)

	if gotFor != "1.2.3.4" {
		t.Errorf("X-Forwarded-For = %q, want 1.2.3.4 (spoofed 9.9.9.9 must be discarded)", gotFor)
	}
	if gotHost != "healthbridge.example" {
		t.Errorf("X-Forwarded-Host = %q, want healthbridge.example", gotHost)
	}
	if gotProto != "http" {
		t.Errorf("X-Forwarded-Proto = %q, want http", gotProto)
	}
}

func TestProxySendsTheBackendHostNotTheClientHost(t *testing.T) {
	// SetURL points the outbound Host at the backend. This is deliberate — see
	// docs/DECISIONS.md section 6. What the client asked for is not lost; it
	// travels in X-Forwarded-Host, which the test above covers.
	var gotHost string
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotHost = r.Host
	}))
	defer backend.Close()

	backendHost := strings.TrimPrefix(backend.URL, "http://")

	req := httptest.NewRequest(http.MethodGet, "/api/hl7/parse", nil)
	req.Host = "healthbridge.example"

	createProxy(backend.URL).ServeHTTP(httptest.NewRecorder(), req)

	if gotHost != backendHost {
		t.Errorf("backend saw Host = %q, want %q (the backend's own address)", gotHost, backendHost)
	}
}

// --- healthHandler --------------------------------------------------------

func decodeHealth(t *testing.T, recorder *httptest.ResponseRecorder) AggregatedHealth {
	t.Helper()
	var health AggregatedHealth
	if err := json.Unmarshal(recorder.Body.Bytes(), &health); err != nil {
		t.Fatalf("health response is not valid JSON: %v (body: %q)", err, recorder.Body.String())
	}
	return health
}

func TestHealthHandlerReports200WhenEveryServiceIsUp(t *testing.T) {
	hl7, monitoring := healthyBackend(), healthyBackend()
	defer hl7.Close()
	defer monitoring.Close()
	withServiceURLs(t, hl7.URL, monitoring.URL)

	recorder := httptest.NewRecorder()
	healthHandler(recorder, httptest.NewRequest(http.MethodGet, "/health", nil))

	if recorder.Code != http.StatusOK {
		t.Errorf("status = %d, want 200", recorder.Code)
	}

	health := decodeHealth(t, recorder)
	if health.Status != StatusHealthy {
		t.Errorf("overall status = %q, want healthy", health.Status)
	}
	if health.Service != "gateway" {
		t.Errorf("service = %q, want gateway", health.Service)
	}
	if len(health.Components) != 2 {
		t.Errorf("components = %d, want 2", len(health.Components))
	}
}

func TestHealthHandlerReports503WhenAnyServiceIsDown(t *testing.T) {
	// Any single unhealthy component degrades the whole gateway — this is what a
	// load balancer or Kubernetes readiness probe acts on.
	hl7, monitoring := healthyBackend(), unhealthyBackend()
	defer hl7.Close()
	defer monitoring.Close()
	withServiceURLs(t, hl7.URL, monitoring.URL)

	recorder := httptest.NewRecorder()
	healthHandler(recorder, httptest.NewRequest(http.MethodGet, "/health", nil))

	if recorder.Code != http.StatusServiceUnavailable {
		t.Errorf("status = %d, want 503", recorder.Code)
	}

	health := decodeHealth(t, recorder)
	if health.Status != StatusUnhealthy {
		t.Errorf("overall status = %q, want unhealthy", health.Status)
	}
	if health.Components["hl7-service"].Status != StatusHealthy {
		t.Errorf("hl7-service should still report healthy individually")
	}
	if health.Components["monitoring-service"].Status != StatusUnhealthy {
		t.Errorf("monitoring-service should report unhealthy")
	}
}

func TestHealthHandlerTreatsAnUnreachableServiceAsUnhealthy(t *testing.T) {
	dead := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	deadURL := dead.URL
	dead.Close()

	healthy := healthyBackend()
	defer healthy.Close()
	withServiceURLs(t, deadURL, healthy.URL)

	recorder := httptest.NewRecorder()
	healthHandler(recorder, httptest.NewRequest(http.MethodGet, "/health", nil))

	if recorder.Code != http.StatusServiceUnavailable {
		t.Errorf("status = %d, want 503", recorder.Code)
	}
	if decodeHealth(t, recorder).Components["hl7-service"].Status != StatusUnhealthy {
		t.Error("a connection-refused service must be reported unhealthy, not omitted")
	}
}

func TestHealthHandlerNamesEveryComponentItChecked(t *testing.T) {
	hl7, monitoring := healthyBackend(), healthyBackend()
	defer hl7.Close()
	defer monitoring.Close()
	withServiceURLs(t, hl7.URL, monitoring.URL)

	recorder := httptest.NewRecorder()
	healthHandler(recorder, httptest.NewRequest(http.MethodGet, "/health", nil))

	health := decodeHealth(t, recorder)
	for _, name := range []string{"hl7-service", "monitoring-service"} {
		component, present := health.Components[name]
		if !present {
			t.Errorf("component %q missing from the response", name)
			continue
		}
		if component.Service != name {
			t.Errorf("component %q reports service = %q", name, component.Service)
		}
	}
}

func TestHealthHandlerSetsJsonContentTypeAndRfc3339Timestamp(t *testing.T) {
	hl7, monitoring := healthyBackend(), healthyBackend()
	defer hl7.Close()
	defer monitoring.Close()
	withServiceURLs(t, hl7.URL, monitoring.URL)

	recorder := httptest.NewRecorder()
	healthHandler(recorder, httptest.NewRequest(http.MethodGet, "/health", nil))

	if got := recorder.Header().Get("Content-Type"); got != "application/json" {
		t.Errorf("Content-Type = %q, want application/json", got)
	}
	if _, err := time.Parse(time.RFC3339, decodeHealth(t, recorder).Timestamp); err != nil {
		t.Errorf("timestamp is not RFC3339: %v", err)
	}
}

func TestHealthHandlerChecksServicesConcurrently(t *testing.T) {
	// The fan-out exists so total latency is the slowest check, not the sum. With two
	// backends sleeping 300ms each, a sequential implementation would take ~600ms.
	slow := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		time.Sleep(300 * time.Millisecond)
		w.WriteHeader(http.StatusOK)
	}))
	defer slow.Close()

	slowToo := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		time.Sleep(300 * time.Millisecond)
		w.WriteHeader(http.StatusOK)
	}))
	defer slowToo.Close()

	withServiceURLs(t, slow.URL, slowToo.URL)

	start := time.Now()
	healthHandler(httptest.NewRecorder(), httptest.NewRequest(http.MethodGet, "/health", nil))
	elapsed := time.Since(start)

	if elapsed > 550*time.Millisecond {
		t.Errorf("health checks took %v — they appear to be running sequentially", elapsed)
	}
}

// --- routing --------------------------------------------------------------

// buildTestRouter mirrors the route table wired up in main(), pointing every
// backend at a single stub that echoes which path it received.
func buildTestRouter(backendURL string) *mux.Router {
	router := mux.NewRouter()
	router.Use(requestLogger)

	proxy := createProxy(backendURL)
	for _, prefix := range []string{"/api/hl7/", "/api/dicom/", "/api/fhir/", "/hl7/", "/dicom/", "/fhir/"} {
		router.PathPrefix(prefix).Handler(proxy)
	}
	router.PathPrefix("/metrics").Handler(proxy)
	router.PathPrefix("/dashboard").Handler(proxy)
	router.HandleFunc("/health", healthHandler).Methods("GET")

	return router
}

func TestRouterSendsKnownPrefixesToABackend(t *testing.T) {
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte(r.URL.Path))
	}))
	defer backend.Close()

	router := buildTestRouter(backend.URL)

	paths := []string{
		"/api/hl7/parse",
		"/api/dicom/metadata",
		"/api/fhir/translate",
		"/hl7/parse",
		"/dicom/parse",
		"/fhir/translate",
		"/metrics",  // no trailing slash — Flask routes omit it
		"/metrics/", // with trailing slash
		"/metrics/push/aws",
		"/dashboard",
	}

	for _, path := range paths {
		t.Run(path, func(t *testing.T) {
			recorder := httptest.NewRecorder()
			router.ServeHTTP(recorder, httptest.NewRequest(http.MethodGet, path, nil))

			if recorder.Code != http.StatusOK {
				t.Errorf("status = %d, want 200 (path not routed to a backend)", recorder.Code)
			}
			if got := recorder.Body.String(); got != path {
				t.Errorf("backend received %q, want %q", got, path)
			}
		})
	}
}

func TestRouterReturns404ForUnknownPaths(t *testing.T) {
	backend := healthyBackend()
	defer backend.Close()

	recorder := httptest.NewRecorder()
	buildTestRouter(backend.URL).ServeHTTP(
		recorder, httptest.NewRequest(http.MethodGet, "/not-a-route", nil))

	if recorder.Code != http.StatusNotFound {
		t.Errorf("status = %d, want 404", recorder.Code)
	}
}

func TestRouterRejectsNonGetHealthRequests(t *testing.T) {
	backend := healthyBackend()
	defer backend.Close()

	recorder := httptest.NewRecorder()
	buildTestRouter(backend.URL).ServeHTTP(
		recorder, httptest.NewRequest(http.MethodPost, "/health", nil))

	if recorder.Code == http.StatusOK {
		t.Error("POST /health should not be served by the health handler")
	}
}
