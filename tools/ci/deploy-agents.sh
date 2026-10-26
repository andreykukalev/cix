#!/usr/bin/env bash
# Deploys the paper engine and the agent jobs to Azure Container Apps (ADR-0013, docs/azure.md), after a merge to main:
#   1. pushes the two images CI built to the environment's registry, tagged with the commit (agents.deploy platform stops
#      here: the apps don't exist yet, and provision.ps1 -ImageTag creates them from these images);
#   2. points the engine at its new image and, when the engine runs (agents.startEngine), waits for that revision to be
#      healthy (its startup and readiness probes passed);
#   3. points the three jobs at the new agent image.
# If anything fails once the engine has changed, the engine and the jobs go back to the images they ran before, and the
# run fails. Nothing here changes infrastructure: provision.ps1 (the owner) creates the apps, their settings and roles.
#
# Runs in CI signed in (az) as cix-agents-deploy-<env> (tools/azure/setup-ci-deploy.ps1 -For agents).
# Environment: SETTINGS (infra/environments/<env>.json), TAG (the commit SHA), ENGINE_ARCHIVE and TRADER_ARCHIVE (the
# images as docker save wrote them, tagged cix-engine:ci and cix-trader:ci), and optionally HEALTH_TIMEOUT_SECONDS (720:
# the engine's startup probe allows 10 minutes) and POLL_SECONDS (15).
set -euo pipefail

say() { echo "$*"; }
fail() { echo "::error::$*"; exit 1; }

[[ "${TAG:-}" =~ ^[0-9a-f]{7,40}$ ]] || fail "TAG must be a commit SHA."
[[ -f "${SETTINGS:-}" ]] || fail "SETTINGS must name an environment file."
timeout_seconds="${HEALTH_TIMEOUT_SECONDS:-720}"
poll="${POLL_SECONDS:-15}"

deploy="$(jq -r '.agents.deploy // "none"' "$SETTINGS")"
if [[ "$deploy" != apps && "$deploy" != platform ]]; then
  echo "::notice::agents.deploy is '$deploy' in $SETTINGS: there is no registry in Azure to deploy to."
  exit 0
fi
if [[ "$(jq -r '.protected // false' "$SETTINGS")" == true ]]; then
  fail "$SETTINGS is a protected environment: CI never deploys there."
fi
group="$(jq -r .resourceGroup "$SETTINGS")"
prefix="$(jq -r .namePrefix "$SETTINGS")"
env_name="$(jq -r .environmentName "$SETTINGS")"
start_engine="$(jq -r '.agents.startEngine // false' "$SETTINGS")"
# The names infra/modules/agents-names.bicep gives them.
engine="ca-${prefix}-${env_name}-engine"; engine="${engine:0:32}"
jobs=()
for key in trader requests analyst; do name="caj-${prefix}-${env_name}-${key}"; jobs+=("${name:0:32}"); done

# The registry: the one in the group tagged with this environment.
registry="$(az acr list --resource-group "$group" -o json \
  | jq -r --arg e "$env_name" '[.[] | select((.tags // {})["cix-environment"] == $e)] | if length == 1 then .[0].name else empty end')"
[[ -n "$registry" ]] || fail "No single container registry in $group is tagged cix-environment=$env_name; run provision.ps1 with agents.deploy platform."
server="$(az acr show --name "$registry" --query loginServer -o tsv)"
engine_image="$server/cix-engine:$TAG"
agent_image="$server/cix-trader:$TAG"

image_of_app() { az containerapp show --name "$1" --resource-group "$group" --query 'properties.template.containers[0].image' -o tsv; }
image_of_job() { az containerapp job show --name "$1" --resource-group "$group" --query 'properties.template.containers[0].image' -o tsv; }
declare -A previous_job
if [[ "$deploy" == apps ]]; then
  previous_engine="$(image_of_app "$engine")"
  for job in "${jobs[@]}"; do previous_job[$job]="$(image_of_job "$job")"; done
  [[ "$previous_engine" == "$server/"* ]] || fail "$engine runs $previous_engine, not an image from $server; provision.ps1 sets it up."
  if [[ "$previous_engine" == "$engine_image" ]]; then
    all_current=true
    for job in "${jobs[@]}"; do [[ "${previous_job[$job]}" == "$agent_image" ]] || all_current=false; done
    if [[ "$all_current" == true ]]; then say "Already running $TAG: nothing to deploy."; exit 0; fi
  fi
  say "Engine: $previous_engine -> $engine_image"
  for job in "${jobs[@]}"; do say "Job $job: ${previous_job[$job]} -> $agent_image"; done
fi

# 1. The images, pushed under this commit's tag.
docker load --input "$ENGINE_ARCHIVE" >/dev/null
docker load --input "$TRADER_ARCHIVE" >/dev/null
docker tag cix-engine:ci "$engine_image"
docker tag cix-trader:ci "$agent_image"
az acr login --name "$registry" >/dev/null
docker push --quiet "$engine_image" >/dev/null
docker push --quiet "$agent_image" >/dev/null
say "Pushed both images to $registry."
if [[ "$deploy" == platform ]]; then
  echo "::notice::agents.deploy is platform: the images are in $registry as $TAG. To run them, set agents.deploy to apps and run provision.ps1 -ImageTag $TAG."
  exit 0
fi

# From here on, a failure puts back what ran before.
changed=false
roll_back() {
  local status=$? ok=true
  [[ "$changed" == true ]] || exit "$status"
  echo "::error::The deployment of $TAG failed; going back to the images that ran before."
  az containerapp update --name "$engine" --resource-group "$group" --image "$previous_engine" -o none || ok=false
  for job in "${jobs[@]}"; do
    az containerapp job update --name "$job" --resource-group "$group" --image "${previous_job[$job]}" -o none || ok=false
  done
  if [[ "$ok" == true ]]; then
    echo "::error::Rolled back to $previous_engine and the jobs' previous images."
  else
    echo "::error::The rollback itself failed: check the engine and the jobs in the Azure portal (docs/azure.md)."
  fi
  exit 1
}
trap roll_back EXIT

# 2. The engine, and its health when it runs.
changed=true
az containerapp update --name "$engine" --resource-group "$group" --image "$engine_image" -o none
if [[ "$start_engine" != true ]]; then
  echo "::notice::The engine is stopped (agents.startEngine false): its new image starts with it, so there is no health to wait for."
else
  deadline=$(( $(date +%s) + timeout_seconds ))
  while :; do
    revision="$(az containerapp show --name "$engine" --resource-group "$group" --query properties.latestRevisionName -o tsv)"
    state="$(az containerapp revision show --name "$engine" --resource-group "$group" --revision "$revision" -o json \
      | jq -c '{image: .properties.template.containers[0].image, health: .properties.healthState, running: .properties.runningState, provisioning: .properties.provisioningState}')"
    image="$(jq -r .image <<<"$state")"; health="$(jq -r .health <<<"$state")"
    running="$(jq -r .running <<<"$state")"; provisioning="$(jq -r .provisioning <<<"$state")"
    if [[ "$image" == "$engine_image" ]]; then
      if [[ "$health" == Healthy && ( "$running" == Running || "$running" == RunningAtMaxScale ) ]]; then
        say "Engine revision $revision is healthy."
        break
      fi
      if [[ "$provisioning" == Failed || "$running" == Failed ]]; then
        echo "::error::Engine revision $revision failed (provisioning $provisioning, running $running)."
        exit 1
      fi
    fi
    if (( $(date +%s) >= deadline )); then
      echo "::error::Engine revision $revision wasn't healthy after $timeout_seconds seconds (health $health, running $running)."
      exit 1
    fi
    sleep "$poll"
  done
fi

# 3. The jobs: their next executions use the new image.
for job in "${jobs[@]}"; do
  az containerapp job update --name "$job" --resource-group "$group" --image "$agent_image" -o none
done
trap - EXIT
say "Deployed $TAG: the engine and the jobs ${jobs[*]}."
