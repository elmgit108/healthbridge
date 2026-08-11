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

# Create a non-root user for better security context
RUN adduser -D -g '' gatekeeper
USER gatekeeper

# Copy binary from builder
COPY --from=builder /app/gateway .

EXPOSE 8080

CMD ["./gateway"]
