<#
.SYNOPSIS
  Creates a local kind cluster, builds and loads the SeatFlow images, deploys the manifests and waits for rollout.

.EXAMPLE
  ./scripts/kind-up.ps1                 # create (if needed), build, load, deploy
  ./scripts/kind-up.ps1 -SkipBuild      # reuse the images already built by docker compose
  ./scripts/kind-up.ps1 -Recreate       # delete and recreate the cluster first
  ./scripts/demo.ps1 -BaseUrl http://localhost:8081 -MailpitUrl http://localhost:8026 -Scenario all
#>
param(
    [switch]$Recreate,
    [switch]$SkipBuild
)

# kind and kubectl report progress on stderr; Windows PowerShell 5.1 would turn that into terminating errors under
# "Stop". Failures are detected through exit codes instead (Invoke-Native).
$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot -Parent
$cluster = "seatflow"
$context = "kind-$cluster"
$services = "catalog", "booking", "payment", "notification", "gateway"

function Invoke-Native([string]$What, [scriptblock]$Command) {
    Write-Host "==> $What" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

if ($Recreate) {
    Invoke-Native "Deleting cluster $cluster" { kind delete cluster --name $cluster }
}

$existing = @(kind get clusters 2>$null)
if ($existing -notcontains $cluster) {
    Invoke-Native "Creating kind cluster $cluster" { kind create cluster --config "$root/deploy/kind/cluster.yaml" --wait 120s }
}

if (-not $SkipBuild) {
    Invoke-Native "Building images" { docker compose -f "$root/docker-compose.yml" build @services }
}

foreach ($service in $services) {
    Invoke-Native "Loading seatflow/${service}:dev into the cluster" { kind load docker-image "seatflow/${service}:dev" --name $cluster }
}

kubectl --context $context -n seatflow get deployment booking 2>$null | Out-Null
$redeploy = $LASTEXITCODE -eq 0

Invoke-Native "Applying manifests" { kubectl --context $context apply -k "$root/deploy/k8s" }

if ($redeploy) {
    # Same :dev tag, new image content: pods must be restarted to pick it up.
    Invoke-Native "Restarting app deployments to pick up the freshly loaded images" {
        kubectl --context $context -n seatflow rollout restart deployment $services
    }
}

foreach ($statefulSet in "postgres", "rabbitmq") {
    Invoke-Native "Waiting for $statefulSet" { kubectl --context $context -n seatflow rollout status "statefulset/$statefulSet" --timeout=300s }
}
foreach ($deployment in @("redis", "mailpit", "dashboard") + $services) {
    Invoke-Native "Waiting for $deployment" { kubectl --context $context -n seatflow rollout status "deployment/$deployment" --timeout=300s }
}

kubectl --context $context -n seatflow get pods -o wide
Write-Host ""
Write-Host "Gateway:   http://localhost:8081" -ForegroundColor Green
Write-Host "Dashboard: http://localhost:18889   Mailpit: http://localhost:8026" -ForegroundColor Green
Write-Host "Demo:      ./scripts/demo.ps1 -BaseUrl http://localhost:8081 -MailpitUrl http://localhost:8026 -Scenario all" -ForegroundColor Green
