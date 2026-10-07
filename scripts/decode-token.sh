#!/usr/bin/env bash
# Decode a JWT's header and payload, and explain the key claims.
#
#   ./decode-token.sh "$TOKEN"
#   ./get-token.sh alice raw | ./decode-token.sh
#
# DECODING IS NOT VERIFYING - see the note at the top of decode_token.py
set -euo pipefail
cd "$(dirname "$0")"
if [ $# -ge 1 ]; then exec python3 ./decode_token.py "$1"; else exec python3 ./decode_token.py; fi
