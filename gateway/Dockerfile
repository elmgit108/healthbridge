# Build stage
FROM golang:1.25-alpine AS builder

WORKDIR /app

# Enable CGO_ENABLED=0 early doesnot need a Linux distribution , fully static(like 'FROM scratch' )
#
ENV CGO_ENABLED=0 GOOS=linux

# Copy source first so go mod tidy can resolve all imports
COPY go.mod ./
COPY *.go ./
# Tidy generates go.sum from imports — works whether or not one was checked in
RUN go mod tidy && go build -o gateway .

# Final stage
FROM alpine:3.24
WORKDIR /app

# to connect the package to the github repo.
LABEL org.opencontainers.image.source=https://github.com/elmgit108/healthbridge

# Create a non-root user for better security context
# explicitly enforces UID 10001 across both the creation step and the active runtime user configuration.
RUN adduser -D -u 10001 -g '' gatekeeper
USER 10001

# Copy binary from builder
COPY --from=builder /app/gateway .

EXPOSE 8080

CMD ["./gateway"]
