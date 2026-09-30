#!/usr/bin/env bash
set -euo pipefail
image="${1:-}"
if [[ ! "$image" =~ ^ghcr\.io/[a-z0-9_.-]+/[a-z0-9_.-]+:[a-f0-9]{40}$ ]]; then
    echo 'Usage: bash deploy.sh ghcr.io/owner/repository:<full-40-character-commit-sha>' >&2
    exit 1
fi
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
lock_operations
export APP_IMAGE="$image"
compose config --quiet
compose pull
if docker volume inspect dod-production-data >/dev/null 2>&1; then cold_backup; fi
compose up -d --remove-orphans
healthy=false
for _ in {1..30}; do
    if curl --fail --silent --show-error --max-time 2 http://127.0.0.1:8080/health >/dev/null 2>&1; then
        healthy=true
        break
    fi
    sleep 2
done
if [[ "$healthy" != true ]]; then
    echo 'App failed its local health check. Inspect compose logs; release.env still records the previous successful release.' >&2
    echo 'Do not blindly roll back across schema changes. A pre-deployment backup is in backups/ if this was an existing installation.' >&2
    exit 1
fi
if [[ -f release.env ]]; then cp release.env previous-release.env; fi
printf 'APP_IMAGE=%s\n' "$image" > release.env.tmp
mv release.env.tmp release.env
echo "Deployed $image. Now check your public HTTPS URL and sign in."
