#!/usr/bin/env bash
# Drive the whole Authorization Code + PKCE flow from the shell, printing each
# step. This is the scripted version of what your browser does at
# http://localhost:5002 - useful for seeing the sequence without devtools.
set -euo pipefail
cd "$(dirname "$0")" && source ./_common.sh
require_keycloak
curl -fsS -o /dev/null "$WEB_URL/" || die "WebApp not running. Run: dotnet run --project src/WebApp"
exec python3 ./walk_the_flow.py
