#!/usr/bin/env bash
# Creates a kind cluster (unless it exists), builds and loads the images, deploys and waits for rollout.
#   scripts/kind-up.sh                # SKIP_BUILD=1 to reuse images built by docker compose
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CLUSTER=seatflow
CTX="kind-$CLUSTER"
SERVICES=(catalog booking payment notification gateway)

if ! kind get clusters 2>/dev/null | grep -qx "$CLUSTER"; then
  kind create cluster --config "$ROOT/deploy/kind/cluster.yaml" --wait 120s
fi

if [[ "${SKIP_BUILD:-0}" != 1 ]]; then
  docker compose -f "$ROOT/docker-compose.yml" build "${SERVICES[@]}"
fi

for service in "${SERVICES[@]}"; do
  kind load docker-image "seatflow/$service:dev" --name "$CLUSTER"
done

redeploy=0
kubectl --context "$CTX" -n seatflow get deployment booking >/dev/null 2>&1 && redeploy=1

kubectl --context "$CTX" apply -k "$ROOT/deploy/k8s"
if [[ $redeploy == 1 ]]; then
  kubectl --context "$CTX" -n seatflow rollout restart deployment "${SERVICES[@]}"
fi

for statefulset in postgres rabbitmq; do
  kubectl --context "$CTX" -n seatflow rollout status "statefulset/$statefulset" --timeout=300s
done
for deployment in redis mailpit dashboard "${SERVICES[@]}"; do
  kubectl --context "$CTX" -n seatflow rollout status "deployment/$deployment" --timeout=300s
done

kubectl --context "$CTX" -n seatflow get pods -o wide
echo
echo "Gateway http://localhost:8081 · Dashboard http://localhost:18889 · Mailpit http://localhost:8026"
echo "BASE_URL=http://localhost:8081 MAILPIT_URL=http://localhost:8026 scripts/demo.sh all"
