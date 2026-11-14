#!/usr/bin/env bash
# Live smoke test for the T1 foundation: starts the real app (no Docker socket needed), exercises
# /healthz and the auth endpoints over HTTP, then stops it. Usage: scripts/smoke-t1.sh
set -euo pipefail
cd "$(dirname "$0")/.."

PORT="${SMOKE_PORT:-18788}"
DATA="$(mktemp -d)"
DLL=src/Quaymaster.Api/bin/Release/net10.0/Quaymaster.Api.dll
dotnet build src/Quaymaster.Api -c Release -v q --nologo >/dev/null

SECRET="$(dotnet "$DLL" gen-secret | cut -d= -f2-)"
export QM_SECRET_KEY="$SECRET" QM_DATA_DIR="$DATA" QM_PORT="$PORT" QM_SCRYPT_LOG2_N=14
dotnet "$DLL" >"$DATA/app.log" 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null || true; wait $PID 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do curl -fs "localhost:$PORT/healthz" >/dev/null 2>&1 && break; sleep 0.2; done

BASE="http://127.0.0.1:$PORT"; O="Origin: $BASE"; J="Content-Type: application/json"; JAR="$DATA/jar"
check() { # name expected actual
  if [[ "$3" == *"$2"* ]]; then echo "ok   $1"; else echo "FAIL $1: expected '$2' in '$3'"; exit 1; fi
}

check "healthz"            '{"status":"ok"}'                        "$(curl -s $BASE/healthz)"
check "status (fresh)"     '"setupRequired":true'                   "$(curl -s $BASE/api/v1/auth/status)"
check "me without session" '"code":"unauthorized"'                  "$(curl -s $BASE/api/v1/auth/me)"
check "setup without origin" '"code":"origin"'                      "$(curl -s -H "$J" -d '{}' $BASE/api/v1/auth/setup)"
check "setup validation"   '"details":[{"path":"token","message":"Required"}' "$(curl -s -H "$O" -H "$J" -d '{}' $BASE/api/v1/auth/setup)"
TOKEN="$(tr -d '\n' <"$DATA/setup-token")"
check "token on stderr"    "$TOKEN"                                 "$(cat "$DATA/app.log")"
check "setup"              '"role":"admin"'                         "$(curl -s -c $JAR -H "$O" -H "$J" -d "{\"token\":\"$TOKEN\",\"username\":\"admin\",\"password\":\"correct horse battery\"}" $BASE/api/v1/auth/setup)"
CSRF="$(curl -s -b $JAR $BASE/api/v1/auth/me | sed 's/.*"csrfToken":"\([0-9a-f]*\)".*/\1/')"
check "me"                 '"username":"admin"'                     "$(curl -s -b $JAR $BASE/api/v1/auth/me)"
check "csrf required"      '"code":"csrf"'                          "$(curl -s -b $JAR -H "$O" -H "$J" -d '{}' $BASE/api/v1/users)"
check "create viewer"      '"role":"viewer"'                        "$(curl -s -b $JAR -H "$O" -H "X-QM-CSRF: $CSRF" -H "$J" -d '{"username":"v","password":"viewer password 1","role":"viewer"}' $BASE/api/v1/users)"
check "unknown route"      '"code":"not_found"'                     "$(curl -s -b $JAR $BASE/api/v1/nope)"
check "logout"             '{"ok":true}'                            "$(curl -s -b $JAR -H "$O" -H "X-QM-CSRF: $CSRF" -X POST $BASE/api/v1/auth/logout)"
check "me after logout"    '"code":"unauthorized"'                  "$(curl -s -b $JAR $BASE/api/v1/auth/me)"
check "CSP header"         "default-src 'self'"                     "$(curl -sI $BASE/healthz)"
check "db mode 600"        '600'                                    "$(stat -c %a "$DATA/quaymaster.db")"
check "data dir mode 700"  '700'                                    "$(stat -c %a "$DATA")"
echo "T1 live smoke: all checks passed"
