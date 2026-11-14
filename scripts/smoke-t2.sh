#!/usr/bin/env bash
# Live smoke test for T2 (Docker core): boots the real app against the local Docker socket in a READ-ONLY posture
# (QM_READ_ONLY=true, QM_MANAGED_SCOPE=*) and curls the read endpoints. Issues no Docker mutation.
# Usage: scripts/smoke-t2.sh
set -uo pipefail
cd "$(dirname "$0")/.."

PORT="${SMOKE_PORT:-18789}"
DATA="$(mktemp -d)"
DLL=src/Quaymaster.Api/bin/Release/net10.0/Quaymaster.Api.dll
dotnet build src/Quaymaster.Api -c Release -v q --nologo >/dev/null || exit 1

export QM_SECRET_KEY="$(dotnet "$DLL" gen-secret | cut -d= -f2-)" QM_DATA_DIR="$DATA" QM_PORT="$PORT" QM_SCRYPT_LOG2_N=14
export QM_READ_ONLY=true QM_MANAGED_SCOPE='*' QM_STATS_INTERVAL_SECONDS=2 QM_LOG_LEVEL=warn
dotnet "$DLL" >"$DATA/app.log" 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null || true; wait $PID 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do curl -fs "localhost:$PORT/healthz" >/dev/null 2>&1 && break; sleep 0.2; done

BASE="http://127.0.0.1:$PORT"; O="Origin: $BASE"; J="Content-Type: application/json"; JAR="$DATA/jar"
TOKEN="$(tr -d '\n' <"$DATA/setup-token")"
curl -s -c "$JAR" -H "$O" -H "$J" -d "{\"token\":\"$TOKEN\",\"username\":\"admin\",\"password\":\"correct horse battery\"}" "$BASE/api/v1/auth/setup" >/dev/null
CSRF="$(curl -s -b "$JAR" "$BASE/api/v1/auth/me" | sed 's/.*"csrfToken":"\([0-9a-f]*\)".*/\1/')"
get() { curl -s -b "$JAR" "$BASE/api/v1$1"; }
show() { echo "== $1"; get "$1" | head -c "${2:-400}"; echo; }

for p in /system/mode /system/health /system/host "/system/df" /containers /images /volumes /networks /compose/projects /events /audit /operations /monitors/settings; do show "$p" 500; done
NAME="$(get /containers | sed 's/.*"name":"\([^"]*\)".*/\1/')"
show "/containers/$NAME" 600
show "/containers/$NAME/logs?tail=3" 400
sleep 5
show "/containers/$NAME/stats" 300
echo "== mutation attempt (expect read_only)"
curl -s -b "$JAR" -H "$O" -H "X-QM-CSRF: $CSRF" -H "$J" -d '{}' "$BASE/api/v1/containers/$NAME/actions/restart"; echo
echo "== SSE /stream/stats (3 s)"
curl -s -N --max-time 3 -b "$JAR" "$BASE/api/v1/stream/stats" | head -c 500; echo
echo "---- app.log (tail)"; tail -n 15 "$DATA/app.log"
