#!/bin/sh
# Start the stack in order. podman-compose 1.0.6 ignores depends_on conditions, and the
# game server exits (code 0) if the DB isn't ready, so wait for it first.
# The first start imports the ~72 MB seed (about 10 s on a fast machine). During that import the
# entrypoint runs a temporary server with networking off ("port: 0"); only the real server
# listens on 3306, so its "Version: ... port: 3306" banner means the seed is fully loaded.
set -eu
cd "$(dirname "$0")"

podman-compose up -d db
printf 'waiting for DB'
until podman logs opendaoc-db 2>&1 | grep -q 'port: 3306 '; do
    if ! podman ps --filter name=opendaoc-db --filter status=running -q | grep -q .; then
        echo; echo "opendaoc-db is not running:"; podman logs --tail 20 opendaoc-db; exit 1
    fi
    printf '.'; sleep 5
done
echo " ok"
podman-compose up -d gameserver
