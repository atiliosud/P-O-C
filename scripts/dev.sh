#!/usr/bin/env bash
# Starts the Case API (with the AI backend in the same process), the web app and, with --cosmos, the MCP server.
# Usage: scripts/dev.sh [--shadow] [--seed] [--cosmos]
set -euo pipefail

SHADOW=false; SEED=false; COSMOS=false
for arg in "$@"; do
  case "$arg" in
    --shadow) SHADOW=true ;;
    --seed) SEED=true ;;
    --cosmos) COSMOS=true ;;
    *) echo "Unknown option: $arg (use --shadow, --seed, --cosmos)" >&2; exit 1 ;;
  esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
API_PORT=5080
MCP_PORT=5090

if [ "$COSMOS" = true ] && [ -z "${CASEAGENTS_COSMOS_CONNECTION_STRING:-}" ]; then
  echo "Cosmos mode needs the emulator connection string in CASEAGENTS_COSMOS_CONNECTION_STRING (see the Cosmos DB emulator docs)." >&2
  exit 1
fi

PIDS=()
cleanup() {
  trap - EXIT INT TERM
  echo "Stopping child processes..."
  for pid in "${PIDS[@]:-}"; do
    if [ -n "$pid" ]; then
      pkill -TERM -P "$pid" 2>/dev/null || true
      kill "$pid" 2>/dev/null || true
    fi
  done
  wait 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# Random signing key for this run (48 chars).
KEY="$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 48 || true)"
export ASPNETCORE_ENVIRONMENT=Development
export Auth__Mode=Dev
export Auth__Dev__SigningKey="$KEY"
export AiBackend__ShadowMode="$SHADOW"

if [ "$COSMOS" = true ]; then
  export Storage__Mode=Cosmos
  export Storage__Cosmos__ConnectionString="$CASEAGENTS_COSMOS_CONNECTION_STRING"
  export Storage__Cosmos__EmulatorMode=true
  export Storage__Cosmos__EnsureCreated=true
  echo "Starting Cosmos DB emulator (docker compose up -d)..."
  docker compose up -d
  echo "Waiting for the emulator (up to 180 s)..."
  ready=false
  for _ in $(seq 1 90); do
    if curl -ksf -o /dev/null "https://localhost:${COSMOS_EMULATOR_PORT:-8081}/_explorer/emulator.pem"; then ready=true; break; fi
    sleep 2
  done
  if [ "$ready" != true ]; then echo "Cosmos emulator did not answer on https://localhost:${COSMOS_EMULATOR_PORT:-8081}." >&2; exit 1; fi
else
  export Storage__Mode=InMemory
fi

if [ ! -d web/node_modules ]; then
  echo "Installing web dependencies..."
  npm --prefix web ci
fi

echo "Building solution once..."
dotnet build CaseAgents.sln -nologo

dotnet run --project src/CaseAgents.Api --no-build --no-launch-profile --urls "http://localhost:$API_PORT" &
PIDS+=($!)
if [ "$COSMOS" = true ]; then
  dotnet run --project src/CaseAgents.Mcp --no-build --no-launch-profile --urls "http://localhost:$MCP_PORT" &
  PIDS+=($!)
fi
npm --prefix web run dev &
PIDS+=($!)

if [ "$SEED" = true ]; then
  ok=false
  for _ in $(seq 1 60); do
    if curl -sf -o /dev/null "http://localhost:$API_PORT/health"; then ok=true; break; fi
    sleep 1
  done
  if [ "$ok" != true ]; then echo "API did not become healthy within 60 s." >&2; exit 1; fi
  node scripts/seed.mjs --api "http://localhost:$API_PORT"
fi

echo "API http://localhost:$API_PORT | web http://localhost:5173 | Ctrl+C to stop"
if [ "$COSMOS" = true ]; then echo "MCP server http://localhost:$MCP_PORT/mcp"; fi
echo "Log in as: A. Silva / Reviewer (analyst), alice / Submitter"
wait
