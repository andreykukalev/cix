#!/usr/bin/env bash
# Deploys the paper engine and the agent jobs to Azure Container Apps (ADR-0013, docs/azure.md), after a merge to main:
#   1. pushes the two images CI built to the environment's registry, tagged with the commit (agents.deploy platform stops
#      here: the apps don't exist yet, and provision.ps1 -ImageTag creates them from these images);
#   2. points the engine at its new image and, when it runs a replica (minimum replicas 1, set by agents.startEngine),
#      waits for that revision to be healthy (its startup and readiness probes passed);
#   3. points the three jobs at the new agent image.
# If anything fails once the engine has changed, the engine and the jobs go back to the images they ran before, and the
# run fails. An engine stopped by hand (the emergency stop, or the cutover) is left alone. Nothing here changes
# infrastructure or settings: provision.ps1 (the owner) creates the apps, their settings and roles.
#
# Changes go through the management API (az rest), as a JSON merge patch of the containers only, so no CLI extension is
# needed and nothing else in the apps is sent or changed.
#
# Runs in CI signed in (az) as cix-agents-deploy-<env> (tools/azure/setup-ci-deploy.ps1 -For agents).
# Environment: SETTINGS (infra/environments/<env>.json), SUBSCRIPTION_ID, REGISTRY (the registry's name), TAG (the commit
# SHA), ENGINE_ARCHIVE and TRADER_ARCHIVE (the images as docker save wrote them, tagged cix-engine:ci and cix-trader:ci),
# and optionally ROLLBACK_FILE (where the previous images are kept while the apps are changing), HEALTH_TIMEOUT_SECONDS
# (720: the engine's startup probe allows 10 minutes), UPDATE_TIMEOUT_SECONDS (600) and POLL_SECONDS (15).
#
#   deploy-agents.sh --roll-back   puts back the images in ROLLBACK_FILE, if an interrupted run left it behind.
set -euo pipefail

say() { echo "$*"; }
fail() { echo "::error::$*"; exit 1; }

mode=deploy
[[ "${1:-}" == --roll-back ]] && mode=rollback
[[ -f "${SETTINGS:-}" ]] || fail "SETTINGS must name an environment file."
[[ "${SUBSCRIPTION_ID:-}" =~ ^[0-9a-fA-F-]{36}$ ]] || fail "SUBSCRIPTION_ID must be a subscription ID."
if [[ "$mode" == deploy ]]; then
  [[ "${TAG:-}" =~ ^[0-9a-f]{7,40}$ ]] || fail "TAG must be a commit SHA."
  [[ "${REGISTRY:-}" =~ ^[A-Za-z0-9]{5,50}$ ]] || fail "REGISTRY must name the environment's container registry."
fi
health_timeout="${HEALTH_TIMEOUT_SECONDS:-720}"
update_timeout="${UPDATE_TIMEOUT_SECONDS:-600}"
poll="${POLL_SECONDS:-15}"
rollback_file="${ROLLBACK_FILE:-}"

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
# The names infra/modules/agents-names.bicep gives them.
engine="ca-${prefix}-${env_name}-engine"; engine="${engine:0:32}"
jobs=()
for key in trader requests analyst; do name="caj-${prefix}-${env_name}-${key}"; jobs+=("${name:0:32}"); done
api=2024-03-01
apps="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$group/providers/Microsoft.App"
engine_id="$apps/containerApps/$engine"
job_id() { echo "$apps/jobs/$1"; }

# A read of the management API, tried three times: one failed request (throttling, the network) isn't a failed deployment.
get() {
  local attempt out
  for attempt in 1 2 3; do
    if out="$(az rest --method get --url "https://management.azure.com$1?api-version=$api" -o json)"; then
      printf '%s\n' "$out"
      return 0
    fi
    if (( attempt < 3 )); then sleep "$poll"; fi
  done
  return 1
}
image_of() { get "$1" | jq -er '.properties.template.containers[0].image'; }

# Points an app or a job at an image (its first container; the other containers and everything else stay as they are)
# and waits until Azure has applied it. Each step checks its own result, since this also runs where set -e is off.
set_image() {
  local id="$1" image="$2" body state deadline
  body="$(get "$id" | jq -ec --arg image "$image" '{properties: {template: {containers: (.properties.template.containers | .[0].image = $image)}}}')" || return 1
  az rest --method patch --url "https://management.azure.com$id?api-version=$api" --body "$body" -o none || return 1
  deadline=$(( $(date +%s) + update_timeout ))
  while :; do
    state="$(get "$id" | jq -r '.properties.provisioningState')" || return 1
    case "$state" in
      Succeeded) return 0 ;;
      Failed|Canceled) echo "::error::Azure couldn't update ${id##*/} ($state)."; return 1 ;;
    esac
    if (( $(date +%s) >= deadline )); then echo "::error::${id##*/} was still $state after $update_timeout seconds."; return 1; fi
    sleep "$poll"
  done
}

# Puts back the images in a rollback file: the engine first, then every job, trying them all even if one fails.
roll_back_from() {
  local ok=true previous job
  previous="$(jq -r .engine "$1")"
  set_image "$engine_id" "$previous" || ok=false
  for job in "${jobs[@]}"; do
    previous="$(jq -r --arg j "$job" '.jobs[$j]' "$1")"
    set_image "$(job_id "$job")" "$previous" || ok=false
  done
  if [[ "$ok" == true ]]; then
    echo "::error::Rolled back to the images that ran before ($(jq -r .engine "$1") and the jobs' own)."
    rm -f "$1"
    return 0
  fi
  echo "::error::The rollback itself failed: check the engine's revisions and the jobs in the Azure portal (docs/azure.md)."
  return 1
}

if [[ "$mode" == rollback ]]; then
  if [[ -z "$rollback_file" || ! -f "$rollback_file" ]]; then say "Nothing to roll back."; exit 0; fi
  echo "::error::A deployment was interrupted; going back to the images that ran before."
  roll_back_from "$rollback_file" || true
  exit 1
fi

# The registry: the one setup-ci-deploy.ps1 named, which must belong to this environment.
registry_json="$(az acr show --name "$REGISTRY" -o json)" || fail "Couldn't read the registry $REGISTRY (AGENTS_REGISTRY)."
[[ "$(jq -r '(.tags // {})["cix-environment"] // ""' <<<"$registry_json")" == "$env_name" ]] \
  || fail "The registry $REGISTRY isn't tagged cix-environment=$env_name; check AGENTS_REGISTRY and AGENTS_ENVIRONMENT."
server="$(jq -er .loginServer <<<"$registry_json")"
engine_image="$server/cix-engine:$TAG"
agent_image="$server/cix-trader:$TAG"

declare -A previous_job
if [[ "$deploy" == apps ]]; then
  app="$(get "$engine_id")" || fail "Couldn't read $engine in $group: run provision.ps1 with agents.deploy apps first."
  previous_engine="$(jq -r '.properties.template.containers[0].image' <<<"$app")"
  min_replicas="$(jq -r '.properties.template.scale.minReplicas // 0' <<<"$app")"
  # Stopped by hand: CI must not start it again, which a new revision would do.
  active="$(get "$engine_id/revisions" | jq '[.value[] | select(.properties.active)] | length')" || fail "Couldn't list $engine's revisions."
  if [[ "$(jq -r '.properties.runningStatus // ""' <<<"$app")" == Stopped || "$active" == 0 ]]; then
    echo "::warning::$engine is stopped (no active revision), so CI leaves it alone: nothing was deployed. Once it runs again, run CI on main by hand to deploy the latest commit."
    exit 0
  fi
  for job in "${jobs[@]}"; do previous_job[$job]="$(image_of "$(job_id "$job")")" || fail "Couldn't read the job $job in $group."; done
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
az acr login --name "$REGISTRY" >/dev/null
docker push --quiet "$engine_image" >/dev/null
docker push --quiet "$agent_image" >/dev/null
say "Pushed both images to $REGISTRY."
if [[ "$deploy" == platform ]]; then
  echo "::notice::agents.deploy is platform: the images are in $REGISTRY as $TAG. To run them, set agents.deploy to apps and run provision.ps1 -ImageTag $TAG."
  exit 0
fi

# From here on, a failure puts back what ran before. The previous images go in a file too, so that a run cancelled in the
# middle, which may not get to finish this script's own rollback, is rolled back by the workflow's next step.
state_file="${rollback_file:-$(mktemp)}"
jobs_json="$(for job in "${jobs[@]}"; do printf '%s\t%s\n' "$job" "${previous_job[$job]}"; done \
  | jq -Rn '[inputs | split("\t") | {(.[0]): .[1]}] | add')"
jq -n --arg engine "$previous_engine" --argjson jobs "$jobs_json" '{engine: $engine, jobs: $jobs}' > "$state_file"
on_exit() {
  local status=$?
  trap - EXIT
  if (( status == 0 )); then exit 0; fi
  echo "::error::The deployment of $TAG failed; going back to the images that ran before."
  roll_back_from "$state_file" || true
  exit 1
}
trap on_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# 2. The engine, and its health when it runs a replica.
set_image "$engine_id" "$engine_image"
if (( min_replicas < 1 )); then
  echo "::notice::The engine runs no replica (minimum replicas 0, agents.startEngine false): its new image starts with it, so there is no health to wait for."
else
  deadline=$(( $(date +%s) + health_timeout ))
  while :; do
    revision="$(get "$engine_id" | jq -r .properties.latestRevisionName)"
    state="$(get "$engine_id/revisions/$revision" \
      | jq -c '{image: .properties.template.containers[0].image, health: .properties.healthState, running: .properties.runningState, provisioning: .properties.provisioningState}')"
    image="$(jq -r .image <<<"$state")"; health="$(jq -r .health <<<"$state")"
    running="$(jq -r .running <<<"$state")"; provisioning="$(jq -r .provisioning <<<"$state")"
    if [[ "$image" == "$engine_image" ]]; then
      if [[ "$health" == Healthy && ( "$running" == Running || "$running" == RunningAtMaxScale ) ]]; then
        say "Engine revision $revision is healthy."
        break
      fi
      if [[ "$provisioning" == Failed || "$running" == Failed || "$running" == ActivationFailed ]]; then
        echo "::error::Engine revision $revision failed (provisioning $provisioning, running $running)."
        exit 1
      fi
    fi
    if (( $(date +%s) >= deadline )); then
      echo "::error::Engine revision $revision wasn't healthy after $health_timeout seconds (health $health, running $running)."
      exit 1
    fi
    sleep "$poll"
  done
fi

# 3. The jobs: their next executions use the new image.
for job in "${jobs[@]}"; do
  set_image "$(job_id "$job")" "$agent_image"
done
trap - EXIT INT TERM
rm -f "$state_file"
say "Deployed $TAG: the engine and the jobs ${jobs[*]}."
