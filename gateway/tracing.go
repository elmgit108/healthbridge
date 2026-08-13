// OpenTelemetry tracing setup for the HealthBridge gateway.
//
// Initializes a tracer that exports spans to an OTLP collector
// (Jaeger, Tempo, or AWS X-Ray via the OTel Collector). The endpoint
// is configurable via OTEL_EXPORTER_OTLP_ENDPOINT, defaulting to the
// Jaeger service name in docker-compose.
//
// Single Responsibility — this file owns *only* tracing setup.
// main.go just calls initTracer() at startup.

package main

import (
	"context"
	"log/slog"
	"os"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	semconv "go.opentelemetry.io/otel/semconv/v1.24.0"
)

// serviceName is what this process calls itself in traces. It is the same value
// the /health response reports, so a span in Jaeger and a health entry line up.
const serviceName = ServiceGateway

// initTracer configures the global OpenTelemetry tracer provider with an
// OTLP gRPC exporter. Returns a shutdown function that should be deferred
// from main() so spans are flushed before the process exits.
func initTracer() func(context.Context) error {
	endpoint := os.Getenv("OTEL_EXPORTER_OTLP_ENDPOINT")
	if endpoint == "" {
		endpoint = "jaeger:4317"
	}

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	// OTLP gRPC exporter — sends spans over the wire to a collector
	exporter, err := otlptrace.New(ctx,
		otlptracegrpc.NewClient(
			otlptracegrpc.WithEndpoint(endpoint),
			otlptracegrpc.WithInsecure(),
		),
	)
	if err != nil {
		slog.Warn("Failed to create OTLP exporter — continuing without tracing", "error", err)
		return func(context.Context) error { return nil }
	}

	// Resource describes this service to the trace backend
	res, err := resource.New(ctx,
		resource.WithAttributes(
			semconv.ServiceName(serviceName),
			semconv.ServiceVersion("1.0.0"),
		),
	)
	if err != nil {
		slog.Warn("Failed to create resource:", "error", err)
	}

	// Tracer provider — batches spans and forwards them to the exporter
	tp := sdktrace.NewTracerProvider(
		sdktrace.WithBatcher(exporter),
		sdktrace.WithResource(res),
		sdktrace.WithSampler(sdktrace.AlwaysSample()),
	)

	// Register globally so otelhttp middleware picks it up
	otel.SetTracerProvider(tp)

	slog.Info("OpenTelemetry tracing initialized", "endpoint", endpoint)

	return tp.Shutdown
}
