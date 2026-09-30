#!/usr/bin/env bash
# Shared by server-side scripts. Do not source .env as shell code.
set -euo pipefail
umask 077
DOD_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$DOD_DIR"
[[ -f .env ]] || { echo 'Create /opt/dod/.env first.' >&2; exit 1; }
compose() {
    local args=(--env-file "$DOD_DIR/.env")
    if [[ -f "$DOD_DIR/release.env" ]]; then args+=(--env-file "$DOD_DIR/release.env"); fi
    docker compose "${args[@]}" -f "$DOD_DIR/compose.yaml" "$@"
}
lock_operations() {
    exec 9> "$DOD_DIR/.operations.lock"
    flock -n 9 || { echo 'Another deployment or backup is running.' >&2; exit 1; }
}
cold_backup() (
    # Run in a subshell so its EXIT trap only covers the backup operation.
    docker volume inspect dod-production-data >/dev/null
    docker pull alpine:3 >/dev/null
    mkdir -p "$DOD_DIR/backups"
    local container was_running=false
    container="$(compose ps -a -q app)"
    if [[ -n "$container" ]] && [[ "$(docker inspect -f '{{.State.Running}}' "$container")" == true ]]; then
        was_running=true
    fi
    # Called indirectly by the EXIT trap.
    # shellcheck disable=SC2329
    resume() { if [[ "$was_running" == true ]]; then compose start app >/dev/null; fi; }
    trap resume EXIT
    compose stop app
    local archive
    archive="journal-$(date -u +%Y%m%dT%H%M%SZ)-$$.tar.gz"
    docker run --rm --network none \
        --mount type=volume,src=dod-production-data,dst=/data,readonly \
        --mount "type=bind,src=$DOD_DIR/backups,dst=/backup" \
        alpine:3 sh -c 'umask 077; tar czf "/backup/$1" -C /data .' sh "$archive"
    echo "Backup: $DOD_DIR/backups/$archive"
)
