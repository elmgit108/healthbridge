// HealthBridge API Gateway — structured JSON logging (ROADMAP A1).
//
// This file owns only logging setup. main() calls initLogging() first, before
// anything else can log.
//
// Every line is one JSON object using the same field names the C# and Python
// services emit — timestamp / level / message / service / logger — so a single
// CloudWatch Logs Insights or Loki query can span all three services.

package main

import (
	"log/slog"
	"os"
	"time"

	"go.opentelemetry.io/otel"
)

// initLogging installs a JSON slog handler as the process-wide default.
func initLogging() {
	serviceName := getEnv("OTEL_SERVICE_NAME", ServiceGateway)

	opts := &slog.HandlerOptions{
		Level: slog.LevelInfo,

		// ReplaceAttr runs for every attribute of every log line. It is where
		// slog's default key names are changed to the shared contract.
		ReplaceAttr: func(groups []string, a slog.Attr) slog.Attr {
			// Only touch top-level attributes, not ones inside a group.
			if len(groups) > 0 {
				return a
			}
			switch a.Key {
			case slog.TimeKey:
				a.Key = "timestamp"
				// slog writes local time with an offset (-04:00). The C# and
				// Python services write UTC ending in "Z". Convert so all
				// three produce the same string.
				if t, ok := a.Value.Any().(time.Time); ok {
					a.Value = slog.StringValue(t.UTC().Format(time.RFC3339Nano))
				}
			case slog.MessageKey:
				a.Key = "message"
			}
			return a
		},
	}

	logger := slog.New(slog.NewJSONHandler(os.Stdout, opts)).
		With("service", serviceName)

	// SetDefault does two jobs:
	//  1. slog.Info(...) and friends now use this handler.
	//  2. The old "log" package is redirected here too, so any library still
	//     calling log.Printf produces JSON as well.
	// This is the Go version of attaching the handler to Python's root logger.
	slog.SetDefault(logger)
	// OpenTelemetry does not use the default log.Logger — it builds its own with
	// log.New(os.Stderr, ...). slog.SetDefault cannot redirect that, so its
	// export failures would print as plain text. Replace its error handler.
	otel.SetErrorHandler(otel.ErrorHandlerFunc(func(err error) {
		slog.Error("OpenTelemetry error", "error", err, "logger", "otel")
	}))
}
