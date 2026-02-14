#!/usr/bin/env bash
set -euo pipefail

COMPOSE_FILE=".devcontainer/deps.compose.yml"

if ! command -v docker >/dev/null 2>&1; then
  echo "Warning: docker not found; skipping dependency startup"
  exit 0
fi

if ! docker info >/dev/null 2>&1; then
  echo "Warning: docker daemon not available; skipping dependency startup"
  exit 0
fi

echo "Starting dependency containers (postgres, redis)..."
docker compose -f "${COMPOSE_FILE}" up -d

echo "Waiting for dependencies to become healthy..."

wait_for_health() {
  local serviceName="$1"
  local timeoutSeconds="$2"
  local startEpoch
  startEpoch="$(date +%s)"

  while true; do
    local containerId
    containerId="$(docker compose -f "${COMPOSE_FILE}" ps -q "${serviceName}" 2>/dev/null || true)"

    if [ -n "${containerId}" ]; then
      local status
      status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "${containerId}" 2>/dev/null || true)"

      if [ "${status}" = "healthy" ]; then
        echo "- ${serviceName}: healthy"
        return 0
      fi

      if [ "${status}" = "running" ]; then
        # Service is running but has no healthcheck yet (or not reported)
        echo "- ${serviceName}: running (no health yet)"
      else
        echo "- ${serviceName}: ${status:-unknown}"
      fi
    else
      echo "- ${serviceName}: container not created yet"
    fi

    local nowEpoch
    nowEpoch="$(date +%s)"
    if [ $((nowEpoch - startEpoch)) -ge "${timeoutSeconds}" ]; then
      echo "Warning: Timed out waiting for ${serviceName} to become healthy after ${timeoutSeconds}s"
      docker compose -f "${COMPOSE_FILE}" ps || true
      return 1
    fi

    sleep 2
  done
}

# Keep startup snappy but reliable
wait_for_health "postgres" 90
wait_for_health "redis" 60

echo "Dependency containers are ready."
