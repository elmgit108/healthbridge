// HealthBridge API Gateway — Go
//
// Single entry point for all external traffic. Routes requests to the
// appropriate backend microservice via reverse proxy for Network management,
// traffic routing, security boundaries.
//
//   /api/hl7/*    → C# HL7/DICOM Service (port 5001)
//   /api/dicom/*  → C# HL7/DICOM Service (port 5001)
//   /metrics/*    → Python Monitoring Service (port 5002)
//   /dashboard    → Python Monitoring Service (port 5002)
//   /health       → Aggregated health check (this gateway)
//
// Go was chosen for the gateway because it's the language behind Docker,
// Kubernetes, and Terraform.

package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"sync"
	"time"

	"github.com/gorilla/mux"
	"go.opentelemetry.io/contrib/instrumentation/net/http/otelhttp"
)

// Service URLs are configurable via environment variables for Docker/K8s deployments.
// Defaults use Docker Compose service names (Docker DNS resolves these).
var (
	hl7ServiceURL        = getEnv("HL7_SERVICE_URL", "http://hl7-service:5001")
	monitoringServiceURL = getEnv("MONITORING_SERVICE_URL", "http://monitoring-service:5002")
	port                 = getEnv("PORT", "8080")
)

type HealthStatus string

const (
	StatusHealthy   HealthStatus = "healthy"
	StatusUnhealthy HealthStatus = "unhealthy"
)

// Service names. These appear in three places that must agree: the keys of the
// /health response, the Docker Compose and Kubernetes service names, and the
// OpenTelemetry service.name attribute that identifies spans in Jaeger.
const (
	ServiceGateway           = "gateway"
	ServiceHL7Service        = "hl7-service"
	ServiceMonitoringService = "monitoring-service"
)

// getEnv reads an environment variable with a fallback default.
func getEnv(key, fallback string) string {
	if value, exists := os.LookupEnv(key); exists {
		return value
	}
	return fallback
}

// loggingResponseWriter: wraps http.ResponseWriter to capture the status code
// written by downstream handlers, so the logging middleware can report it.
type loggingResponseWriter struct {
	http.ResponseWriter
	statusCode int
}

func (lrw *loggingResponseWriter) WriteHeader(code int) {
	lrw.statusCode = code
	lrw.ResponseWriter.WriteHeader(code)
}

// requestLogger: is a middleware that logs every request with method, path,
// HTTP status code, and response time are essential for debugging and monitoring.
func requestLogger(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		lrw := &loggingResponseWriter{w, http.StatusOK}

		next.ServeHTTP(lrw, r)

		elapsedMs := float64(time.Since(start).Nanoseconds()) / 1e6
		slog.Info(
			fmt.Sprintf("HTTP %s %s responded %d in %.4f ms",
				r.Method, r.URL.Path, lrw.statusCode, elapsedMs),
			"RequestMethod", r.Method,
			"RequestPath", r.URL.Path,
			"StatusCode", lrw.statusCode,
			"Elapsed", elapsedMs,
			"logger", "gateway.request",
		)
	})
}

// createProxy: builds a reverse proxy that forwards requests to a backend service.
// If the backend is unreachable, returns 503 Service Unavailable.
//
// The proxy uses an otelhttp-instrumented Transport so outbound calls to
// downstream services propagate the OpenTelemetry trace context (W3C tracecontext
// headers). This is what stitches the gateway → backend spans into one trace.
func createProxy(target string) *httputil.ReverseProxy {
	tgt, err := url.Parse(target)
	if err != nil {
		slog.Error("Invalid target URL", "target", target, "error", err)
		os.Exit(1)
	}
	// Rewrite mode, not NewSingleHostReverseProxy, so the X-Forwarded-* headers are
	// built from the real connection instead of trusted from the caller.
	//
	// The older Director mode keeps an X-Forwarded-For header sent by the client and
	// appends the real address after it, so a caller can inject a fake IP ahead of the
	// true one. Rewrite mode deletes any client-supplied X-Forwarded-* first; then
	// SetXForwarded re-adds all three from the actual connection:
	//
	//	X-Forwarded-For    client IP address (from the TCP connection — cannot be faked)
	//	X-Forwarded-Host   host name the client asked for
	//	X-Forwarded-Proto  "http" or "https"
	//
	// This matters because the PHI audit trail in hl7-service records who accessed
	// patient data; that record is only as trustworthy as the client address behind it.
	//
	// SetURL also sets the outbound Host header to the backend (e.g. hl7-service:5001).
	// What the client originally asked for is not lost — it travels in X-Forwarded-Host.
	//
	// Rewrite mode also drops query parameters it cannot parse. That's a safety
	// feature, and the HealthBridge gateway endpoints don't use query strings.
	proxy := &httputil.ReverseProxy{
		Rewrite: func(r *httputil.ProxyRequest) {
			r.SetURL(tgt)
			r.SetXForwarded()
		},
	}

	// Wrap the default transport so outgoing HTTP calls are traced
	proxy.Transport = otelhttp.NewTransport(http.DefaultTransport)

	// Custom error handler — returns a JSON error instead of the default HTML
	proxy.ErrorHandler = func(w http.ResponseWriter, r *http.Request, e error) {
		slog.Error("Proxy error", "target", target, "error", e)
		http.Error(w, `{"error": "Downstream service unavailable"}`, http.StatusServiceUnavailable)
	}
	return proxy
}

// --- Health check models ---

// ServiceHealth represents the health of a single downstream service.
type ServiceHealth struct {
	Status    HealthStatus `json:"status"`
	Service   string       `json:"service"`
	Timestamp string       `json:"timestamp,omitempty"`
}

// AggregatedHealth is the gateway's response to GET /health — it polls all
// downstream services concurrently and reports their individual + overall status.
type AggregatedHealth struct {
	Status     HealthStatus             `json:"status"`
	Service    string                   `json:"service"`
	Timestamp  string                   `json:"timestamp"`
	Components map[string]ServiceHealth `json:"components"`
}

// checkServiceHealth pings a single service's /health endpoint.
// Runs as a goroutine — results are written to a shared map under a mutex.
func checkServiceHealth(name string, targetURL string, wg *sync.WaitGroup, mu *sync.Mutex, results map[string]ServiceHealth) {
	defer wg.Done()

	// Short timeout so health checks don't block the gateway response
	client := http.Client{Timeout: 3 * time.Second}
	resp, err := client.Get(targetURL + "/health")

	health := ServiceHealth{
		Service: name,
	}

	if err != nil || resp.StatusCode != http.StatusOK {
		health.Status = StatusUnhealthy
	} else {
		health.Status = StatusHealthy
	}

	mu.Lock()
	results[name] = health
	mu.Unlock()
}

// healthHandler fans out health checks to all downstream services using goroutines,
// waits for all to respond, then returns an aggregated health status.
// Returns 200 if all healthy, 503 if any service is down.
func healthHandler(w http.ResponseWriter, r *http.Request) {
	var wg sync.WaitGroup
	var mu sync.Mutex
	results := make(map[string]ServiceHealth)

	// All services we depend on
	downstream := map[string]string{
		ServiceHL7Service:        hl7ServiceURL,
		ServiceMonitoringService: monitoringServiceURL,
	}

	// Fan out — check all services concurrently
	for name, svcURL := range downstream {
		wg.Add(1)
		go checkServiceHealth(name, svcURL, &wg, &mu, results)
	}

	// Wait for all goroutines to complete
	wg.Wait()

	// Determine overall status — degraded if any component is unhealthy
	overallStatus := StatusHealthy
	statusCode := http.StatusOK
	for _, res := range results {
		if res.Status != StatusHealthy {
			overallStatus = StatusUnhealthy
			statusCode = http.StatusServiceUnavailable
		}
	}

	resp := AggregatedHealth{
		Status:     overallStatus,
		Service:    ServiceGateway,
		Timestamp:  time.Now().UTC().Format(time.RFC3339),
		Components: results,
	}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(statusCode)
	json.NewEncoder(w).Encode(resp)
}

func main() {

	// Structured JSON logging must be first — initTracer() below logs.
	initLogging()

	// --- Initialize OpenTelemetry tracing ---
	// Tracer is set as the global provider; otelhttp will use it automatically.
	// Shutdown is deferred so any buffered spans are flushed before exit.
	shutdownTracer := initTracer()
	defer func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		if err := shutdownTracer(ctx); err != nil {
			slog.Error("Error shutting down tracer", "error", err)
		}
	}()

	router := mux.NewRouter()

	// Apply request logging to every route
	router.Use(requestLogger)

	// --- Reverse proxies to backend services ---
	hl7Proxy := createProxy(hl7ServiceURL)
	monitoringProxy := createProxy(monitoringServiceURL)

	// Route HL7, DICOM, and FHIR requests to the C# service
	router.PathPrefix("/api/hl7/").Handler(hl7Proxy)
	router.PathPrefix("/api/dicom/").Handler(hl7Proxy)
	router.PathPrefix("/api/fhir/").Handler(hl7Proxy)
	router.PathPrefix("/hl7/").Handler(hl7Proxy)
	router.PathPrefix("/dicom/").Handler(hl7Proxy)
	router.PathPrefix("/fhir/").Handler(hl7Proxy)

	// Route monitoring and dashboard requests to the Python service.
	// Match both /metrics and /metrics/ — Flask routes don't include trailing slashes.
	router.PathPrefix("/metrics").Handler(monitoringProxy)
	router.PathPrefix("/dashboard").Handler(monitoringProxy)

	// Gateway's own health endpoint — aggregates downstream health
	router.HandleFunc("/health", healthHandler).Methods("GET")

	// Wrap the entire router with otelhttp — every incoming request becomes
	// a span automatically, and trace context is propagated to downstream services.
	tracedRouter := otelhttp.NewHandler(router, "gateway.request")

	slog.Info("HealthBridge Gateway starting", "port", port)
	err := http.ListenAndServe(fmt.Sprintf(":%s", port), tracedRouter)
	if err != nil {
		slog.Error("Server failed to start", "error", err)
		os.Exit(1)
	}
}
