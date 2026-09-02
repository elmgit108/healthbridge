# Build stage using a slim base to reduce size
FROM python:3.11-slim

WORKDIR /app

# Set environment variables for Python standard behaviors
#"ENV key=value" should be used, legacy "ENV key value"
ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

# to connect the package to the github repo.
LABEL org.opencontainers.image.source=https://github.com/elmgit108/healthbridge


# curl is needed for docker-compose HEALTHCHECK; not present in python:3.11-slim
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Install dependencies first for layer caching
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Create a non-root user
RUN adduser --disabled-password --gecos '' --uid 10002 monitoruser
USER 10002
COPY --chown=10002:10002 . .

# Expose the correct port
EXPOSE 5002

# Run via python (in production we'd use gunicorn, but we keep it simple for the portfolio)
CMD ["python", "app.py"]
