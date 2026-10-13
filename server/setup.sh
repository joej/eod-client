#!/bin/sh
# One-time setup, run on the server host from this directory:
#   REGION_IP=<address clients use to reach this host> ./setup.sh
#   - writes .env with a random DB password and REGION_IP (if missing)
#   - extracts the DB seed (combined.sql) from the pinned server image into ./initdb
# Then: ./up.sh
# REGION_IP is sent to clients in the region list, so it must be reachable from the client;
# 127.0.0.1 only works when the client runs on the same machine.
set -eu
cd "$(dirname "$0")"

IMAGE=$(sed -n 's/^ *image: *\(ghcr.io\/opendaoc\/.*\)$/\1/p' compose.yml)

if [ ! -f .env ]; then
    # umask in a subshell: initdb/ must stay readable by the container's mysql user
    (umask 077; printf 'DB_PASSWORD=%s\n' "$(head -c 24 /dev/urandom | base64 | tr -d '/+=')" > .env)
    echo "wrote .env"
fi

if ! grep -q '^REGION_IP=' .env; then
    if [ -z "${REGION_IP:-}" ]; then
        echo "REGION_IP is not set. Run: REGION_IP=<address clients use to reach this host> $0" >&2
        exit 1
    fi
    printf 'REGION_IP=%s\n' "$REGION_IP" >> .env
    echo "added REGION_IP=$REGION_IP to .env"
fi

if [ ! -f initdb/combined.sql ]; then
    mkdir -p -m 755 initdb
    cid=$(podman create "$IMAGE")
    podman cp "$cid:/tmp/opendaoc-db/combined.sql" initdb/combined.sql
    podman rm "$cid" >/dev/null
    echo "extracted initdb/combined.sql ($(wc -c < initdb/combined.sql) bytes)"
fi
