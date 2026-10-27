#!/usr/bin/env python3
"""tools/ci/deploy-agents.sh against a fake az and docker: every path, including the rollbacks. And tools/ci/agents-tip.sh,
which decides whether a run deploys at all, against a fake gh, and that it agrees with tools/ci/plan.sh on which files
change the images.

Runs in CI (no Azure, no Docker daemon needed). Each scenario writes an environment file and a fake Azure state, runs the
script, and checks what it did to the engine and the jobs, and that it pushed only after loading the images.
"""
import json
import os
import subprocess
import sys
import tempfile
import textwrap

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SCRIPT = os.path.join(ROOT, "tools", "ci", "deploy-agents.sh")
TIP_SCRIPT = os.path.join(ROOT, "tools", "ci", "agents-tip.sh")
PLAN_SCRIPT = os.path.join(ROOT, "tools", "ci", "plan.sh")
SUB = "00000000-0000-0000-0000-000000000001"
REGISTRY = "crcixdevabc123"
SERVER = f"{REGISTRY}.azurecr.io"
OLD, NEW = "1111111", "2222222222222222222222222222222222222222"

# The fake az: answers from state.json next to it, and records every call (one per line) in calls.log. Resources are
# found by their management API path, so a wrong subscription, group or name is a 404.
FAKE_AZ = textwrap.dedent(r'''
    #!/usr/bin/env python3
    import json, os, sys
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, "state.json")
    s = json.load(open(path))
    args = sys.argv[1:]
    with open(os.path.join(here, "calls.log"), "a") as log: log.write("az " + " ".join(args) + "\n")
    def opt(name): return args[args.index(name) + 1] if name in args else None
    def save(): json.dump(s, open(path, "w"))
    def out(value): print(json.dumps(value))
    if args[:2] == ["acr", "show"]:
        if opt("--name") != s["registry"]["name"]: sys.exit("ERROR: (ResourceNotFound)")
        out(s["registry"]); sys.exit()
    if args[:2] == ["acr", "login"]: sys.exit()
    if args[:1] != ["rest"]: sys.exit("fake az: unexpected call " + " ".join(args))
    method, url = opt("--method"), opt("--url")
    prefix = "https://management.azure.com" + s["base"]
    resource, _, query = url.partition("?")
    if query != "api-version=2025-01-01" or not resource.startswith(prefix): sys.exit("ERROR: (NotFound) " + url)
    parts = resource[len(prefix):].split("/")
    kind, name, rest = parts[0], parts[1], parts[2:]
    if method == "get":
        s["gets"] += 1; save()
        if s["flaky_every"] and s["gets"] % s["flaky_every"] == 0: sys.exit("ERROR: (TooManyRequests)")
    if kind == "jobs":
        job = s["jobs"].get(name) or sys.exit("ERROR: (ResourceNotFound) " + name)
        if method == "get":
            out({"properties": {"provisioningState": "Succeeded", "template": {"containers": [{"name": "agent", "image": job}]}}})
        else:
            body = json.loads(opt("--body"))
            image = body["properties"]["template"]["containers"][0]["image"]
            if body != {"properties": {"template": {"containers": [{"name": "agent", "image": image}]}}}: sys.exit("ERROR: sent more than the containers")
            if image in s["fail_patch"].get(name, []): sys.exit("ERROR: (BadRequest) job update failed")
            s["jobs"][name] = image; save()
        sys.exit()
    e = s["engine"]
    if kind != "containerApps" or name != e["name"]: sys.exit("ERROR: (ResourceNotFound) " + name)
    # Azure names revisions <app>--<suffix>; the state keeps the suffix.
    def full(r): return s["revision_names"].get(r, e["name"] + "--" + r)
    def short(r): return next((k for k, v in s["revision_names"].items() if v == r), r[len(e["name"]) + 2:] if r.startswith(e["name"] + "--") else "?")
    if method == "patch":
        body = json.loads(opt("--body"))
        image = body["properties"]["template"]["containers"][0]["image"]
        if body != {"properties": {"template": {"containers": [{"name": "engine", "image": image}]}}}: sys.exit("ERROR: sent more than the containers")
        if image in s["fail_patch"].get(name, []): sys.exit("ERROR: (BadRequest) update failed")
        if s["conflict_once"]: s["conflict_once"] = False; save(); sys.exit("ERROR: (Conflict) an operation is in progress")
        # Single revision mode: the revisions running stay active until the new one is ready (below).
        rev = {"name": "rev-%d" % (len(e["revisions"]) + 1), "image": image, "active": True, "polls": 0}
        e["revisions"].append(rev)
        if s["stale"]: e["pending"] = image  # applied only after a few reads: until then Azure still shows the old image
        else: e["image"] = image
        e["previous_latest"] = e["latest"]; e["latest"] = rev["name"]
        e["lag_left"] = s["rev_lag"]; e["prov_left"] = s["prov_lag"]; e["stale_left"] = s["stale"]; save()
    elif not rest:
        latest = e["latest"]
        if e.get("lag_left", 0) > 0: e["lag_left"] -= 1; latest = e["previous_latest"]
        provisioning = "Succeeded"
        if e.get("stale_left", 0) > 0:
            e["stale_left"] -= 1
            if e["stale_left"] == 0: e["image"] = e.pop("pending")
        elif e.get("prov_left", 0) > 0: e["prov_left"] -= 1; provisioning = "InProgress"
        save()
        props = {"template": {"containers": [{"name": "engine", "image": e["image"]}], "scale": {"minReplicas": e["min"], "maxReplicas": 1}},
                 "configuration": {"activeRevisionsMode": s["revisions_mode"]},
                 "latestRevisionName": full(latest), "provisioningState": provisioning}
        if e.get("runningStatus"): props["runningStatus"] = e["runningStatus"]
        out({"properties": props})
    elif rest == ["revisions"]:
        out({"value": [{"name": full(r["name"]), "properties": {"active": r["active"]}} for r in e["revisions"]]})
    elif method == "post":
        rev = next((r for r in e["revisions"] if r["name"] == short(rest[1])), None) or sys.exit("ERROR: (NotFound) revision")
        if rest[2:] != ["deactivate"] or s["fail_deactivate"]: sys.exit("ERROR: (AuthorizationFailed) " + "/".join(rest))
        rev["active"] = False; save()
    else:
        rev = next((r for r in e["revisions"] if r["name"] == short(rest[1])), None) or sys.exit("ERROR: (NotFound) revision")
        if rev["image"] == s["new_engine"]:
            rev["polls"] += 1
            # The new engine starts only once it holds the engine lease, which a running older revision keeps.
            waiting = any(r["active"] and r is not rev for r in e["revisions"])
            if s["new_fails"]: health, running, provisioning = "Unhealthy", "Failed", "Succeeded"
            elif waiting and rev["polls"] >= s["lease_fails_after"]: health, running, provisioning = "Unhealthy", "ActivationFailed", "Provisioned"
            elif not waiting and s["healthy_after"] is not None and rev["polls"] >= s["healthy_after"]:
                health, running, provisioning = "Healthy", s["running_value"], "Succeeded"
                for r in e["revisions"]:
                    if r is not rev: r["active"] = False
            else: health, running, provisioning = "None", "Activating", "Provisioning"
            save()
        else: health, running, provisioning = "Healthy", "Running", "Succeeded"
        out({"properties": {"template": {"containers": [{"image": rev["image"]}]}, "active": rev["active"],
                            "healthState": health, "runningState": running, "provisioningState": provisioning}})
''').lstrip()

# The apps run images by digest. Those running before this commit, and the ones its push gives (from before digests, the
# apps ran tags: old_engine_tag and old_agent_tag).
old_engine, new_engine = f"{SERVER}/cix-engine@sha256:{'c' * 64}", f"{SERVER}/cix-engine@sha256:{'e' * 64}"
old_agent, new_agent = f"{SERVER}/cix-trader@sha256:{'d' * 64}", f"{SERVER}/cix-trader@sha256:{'a' * 64}"
old_engine_tag, old_agent_tag = f"{SERVER}/cix-engine:{OLD}", f"{SERVER}/cix-trader:{OLD}"
new_engine_tag, new_agent_tag = f"{SERVER}/cix-engine:{NEW}", f"{SERVER}/cix-trader:{NEW}"

# The fake docker: records every call, and after a push knows the image by its digest in the registry (and, to be ignored,
# in another one).
FAKE_DOCKER = textwrap.dedent(r'''
    #!/bin/sh
    echo "docker $*" >> "$(dirname "$0")/calls.log"
    [ "$1" = push ] && [ -n "$FAIL_PUSH" ] && exit 1
    if [ "$1 $2 $3" = "image inspect --format" ]; then
      [ -n "$NO_DIGEST" ] && { echo '[]'; exit 0; }
      case "$5" in
        NEW_ENGINE_TAG) echo '["elsewhere.example/cix-engine@sha256:OTHER","NEW_ENGINE"]' ;;
        NEW_AGENT_TAG) echo '["elsewhere.example/cix-trader@sha256:OTHER","NEW_AGENT"]' ;;
        *) echo '[]' ;;
      esac
    fi
    exit 0
''').lstrip().replace("NEW_ENGINE_TAG", new_engine_tag).replace("NEW_AGENT_TAG", new_agent_tag) \
    .replace("NEW_ENGINE", new_engine).replace("NEW_AGENT", new_agent).replace("OTHER", "f" * 64)


def names(prefix, env):
    return (f"ca-{prefix}-{env}-engine"[:32], [f"caj-{prefix}-{env}-{k}"[:32] for k in ("trader", "requests", "analyst")])


def run(name, *, deploy="apps", protected=False, prefix="cix", env="dev", registry_env=None, min_replicas=1,
        running_status=None, revisions=None, engine_image=None, job_images=None, missing_engine=False, healthy_after=2,
        running_value="Running", new_fails=False, fail_patch=None, rev_lag=0, prov_lag=1, stale=0, conflict_once=False,
        flaky_every=0, fail_push=False, no_digest=False, fail_deactivate=False, lease_fails_after=6, revisions_mode="Single",
        revision_names=None,
        timeout=5, update_timeout=20, args=(), rollback_state=None, expect_rc=0, expect_engine=None, expect_jobs=None, expect_in_output=(),
        expect_calls=(), never_calls=(), expect_rollback_file=False):
    engine, jobs = names(prefix, env)
    with tempfile.TemporaryDirectory() as d:
        settings = os.path.join(d, f"{env}.json")
        json.dump({"environmentName": env, "resourceGroup": f"rg-{prefix}-{env}", "namePrefix": prefix, "protected": protected,
                   "agents": {"deploy": deploy}}, open(settings, "w"))
        current = engine_image or old_engine
        state = {
            "base": f"/subscriptions/{SUB}/resourceGroups/rg-{prefix}-{env}/providers/Microsoft.App/",
            "registry": {"name": REGISTRY, "loginServer": SERVER, "tags": {"cix-environment": registry_env or env}},
            "engine": {"name": "missing" if missing_engine else engine, "image": current, "min": min_replicas,
                       "runningStatus": running_status, "latest": "rev-1",
                       "revisions": revisions if revisions is not None else [{"name": "rev-1", "image": current, "active": True, "polls": 0}]},
            "jobs": job_images or {j: old_agent for j in jobs},
            "healthy_after": healthy_after, "running_value": running_value, "new_fails": new_fails, "new_engine": new_engine,
            "fail_patch": fail_patch or {}, "rev_lag": rev_lag, "prov_lag": prov_lag, "stale": stale, "conflict_once": conflict_once,
            "flaky_every": flaky_every, "gets": 0, "fail_deactivate": fail_deactivate, "lease_fails_after": lease_fails_after,
            "revisions_mode": revisions_mode, "revision_names": revision_names or {},
        }
        json.dump(state, open(os.path.join(d, "state.json"), "w"))
        for tool, body in (("az", FAKE_AZ), ("docker", FAKE_DOCKER)):
            path = os.path.join(d, tool)
            open(path, "w").write(body)
            os.chmod(path, 0o755)
        for archive in ("engine.tar.gz", "trader.tar.gz"):
            open(os.path.join(d, archive), "w").close()
        rollback_file = os.path.join(d, "rollback.json")
        if rollback_state:
            json.dump(rollback_state(jobs), open(rollback_file, "w"))
        env_vars = dict(os.environ, PATH=d + os.pathsep + os.environ["PATH"], SETTINGS=settings, TAG=NEW, REGISTRY=REGISTRY,
                        SUBSCRIPTION_ID=SUB, ENGINE_ARCHIVE=os.path.join(d, "engine.tar.gz"),
                        TRADER_ARCHIVE=os.path.join(d, "trader.tar.gz"), ROLLBACK_FILE=rollback_file,
                        HEALTH_TIMEOUT_SECONDS=str(timeout), POLL_SECONDS="0",
                        # Updates get their own, longer timeout: a slow runner must not time out the update in a health test.
                        UPDATE_TIMEOUT_SECONDS=str(update_timeout))
        if fail_push:
            env_vars["FAIL_PUSH"] = "1"
        if no_digest:
            env_vars["NO_DIGEST"] = "1"
        p = subprocess.run(["bash", SCRIPT, *args], env=env_vars, capture_output=True, text=True, timeout=60)
        out = p.stdout + p.stderr
        final = json.load(open(os.path.join(d, "state.json")))
        log_path = os.path.join(d, "calls.log")
        calls = open(log_path).read() if os.path.exists(log_path) else ""
        rollback_left = os.path.exists(rollback_file)
    problems = []
    if p.returncode != expect_rc:
        problems.append(f"exit {p.returncode}, wanted {expect_rc}")
    if expect_engine and final["engine"]["image"] != expect_engine:
        problems.append(f"engine runs {final['engine']['image']}, wanted {expect_engine}")
    if expect_jobs:
        for j in jobs:
            if final["jobs"][j] != expect_jobs:
                problems.append(f"{j} runs {final['jobs'][j]}, wanted {expect_jobs}")
    for text in expect_in_output:
        if text not in out:
            problems.append(f"output lacks {text!r}")
    for call in expect_calls:
        if call not in calls:
            problems.append(f"no call {call!r}")
    for call in never_calls:
        if call in calls or call in out:
            problems.append(f"unexpected {call!r}")
    if rollback_left != expect_rollback_file:
        problems.append("the rollback file was " + ("left behind" if rollback_left else "removed"))
    return report("deploy-agents", name, problems, out + "\n--- calls\n" + calls)


def report(kind, name, problems, detail):
    if problems:
        print(f"FAIL {name}: " + "; ".join(problems))
        print(textwrap.indent(detail.strip(), "    "))
        print(f"::error title={kind}: {name}::" + "; ".join(problems))
        return False
    print(f"ok   {name}")
    return True


def interrupted(jobs):
    return {"server": SERVER, "engine": old_engine, "jobs": {j: old_agent for j in jobs}}


def tampered(jobs):
    return {"server": SERVER, "engine": "null", "jobs": {j: old_agent for j in jobs}}


engine_dev, jobs_dev = names("cix", "dev")
results = [
    run("deploys the engine, waits for it to be healthy, then the jobs",
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=[f"Engine revision {engine_dev}--rev-2 is healthy", f"Deployed {NEW}"],
        expect_calls=[f"docker push --quiet {new_engine_tag}", f"docker push --quiet {new_agent_tag}", f"az acr login --name {REGISTRY}"]),
    run("the old revision is stopped, so the new engine can take the engine lease",
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=[f"Stopping the engine's revision {engine_dev}--rev-1, so that {engine_dev}--rev-2 can take"],
        expect_calls=["rev-1/deactivate"], never_calls=["rev-2/deactivate"]),
    run("every older revision still running is stopped",
        revisions=[{"name": "rev-0", "image": old_engine, "active": True, "polls": 0}, {"name": "rev-1", "image": old_engine, "active": True, "polls": 0}],
        expect_engine=new_engine, expect_jobs=new_agent, expect_calls=["rev-0/deactivate", "rev-1/deactivate"]),
    run("an old revision that can't be stopped rolls back, rather than wait out the startup probe", fail_deactivate=True, expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=[f"refused to stop the engine's revision {engine_dev}--rev-1", "Rolled back"],
        never_calls=["rev-2 is healthy"]),
    run("an engine set by hand to run several revisions is rolled back, none stopped", revisions_mode="Multiple", expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["runs several revisions at once", "Rolled back"], never_calls=["deactivate"]),
    run("a revision name Azure wouldn't give is never put in a URL", revision_names={"rev-0": "ca-cix-dev-engine--x/../y"}, expect_rc=1,
        revisions=[{"name": "rev-0", "image": old_engine, "active": True, "polls": 0}, {"name": "rev-1", "image": old_engine, "active": True, "polls": 0}],
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["Unexpected revision name", "Rolled back"], never_calls=["deactivate"]),
    run("the rollback stops the failed revision too", new_fails=True, expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_calls=["rev-1/deactivate", "rev-2/deactivate"], expect_in_output=["Rolled back"]),
    run("apps that ran images by tag, from before digests, get digests",
        engine_image=old_engine_tag, job_images={j: old_agent_tag for j in jobs_dev}, revisions=[{"name": "rev-1", "image": old_engine_tag, "active": True, "polls": 0}],
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=[f"Engine: {old_engine_tag} -> {new_engine}", f"Deployed {NEW}"]),
    run("... and the tags back if it fails", new_fails=True, expect_rc=1,
        engine_image=old_engine_tag, job_images={j: old_agent_tag for j in jobs_dev}, revisions=[{"name": "rev-1", "image": old_engine_tag, "active": True, "polls": 0}],
        expect_engine=old_engine_tag, expect_jobs=old_agent_tag, expect_in_output=["Rolled back"]),
    run("a push docker has no digest for changes nothing", no_digest=True, expect_rc=1, expect_engine=old_engine, expect_jobs=old_agent,
        expect_in_output=["docker has no digest for it"], never_calls=["--method patch"]),
    run("a single replica at its maximum (RunningAtMaxScale) is healthy too", running_value="RunningAtMaxScale",
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=["rev-2 is healthy"]),
    run("the old revision being healthy doesn't count while Azure still reports it as the latest", rev_lag=3,
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=["rev-2 is healthy"], never_calls=["rev-1 is healthy"]),
    run("an update Azure hasn't started yet isn't taken for done", stale=2, min_replicas=0,
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=[f"Deployed {NEW}"]),
    run("an update refused while another is in progress is tried again", conflict_once=True,
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=[f"Deployed {NEW}"]),
    run("one failed read along the way is retried, not rolled back", flaky_every=3,
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=[f"Deployed {NEW}"]),
    run("long names are cut to 32 characters, as the templates cut them", prefix="cixlongprefix", env="abcdefghijkl",
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=[f"Deployed {NEW}"]),
    run("an engine with no replica gets its new image without waiting", min_replicas=0,
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=["The engine runs no replica"], never_calls=["/revisions/rev-2", "deactivate"]),
    run("an engine that fails comes back to the old image, and the jobs stay", new_fails=True, expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["failed (provisioning Succeeded, running Failed)", "Rolled back"]),
    run("an engine that never becomes healthy is rolled back after the timeout", healthy_after=None, timeout=1, expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["wasn't healthy after 1 seconds", "Rolled back"]),
    run("a job that can't be updated rolls everything back", fail_patch={jobs_dev[1]: [new_agent]}, expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["Rolled back"]),
    run("an engine update refused by Azure is rolled back too", fail_patch={engine_dev: [new_engine]}, expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["Rolled back"]),
    run("a failed rollback says so, and leaves the rollback file for the workflow's next step", new_fails=True,
        fail_patch={engine_dev: [old_engine]}, expect_rc=1, expect_in_output=["The rollback itself failed"],
        expect_rollback_file=True),
    run("--roll-back puts back what an interrupted run left in the rollback file", args=["--roll-back"],
        engine_image=new_engine, job_images={j: new_agent for j in jobs_dev}, rollback_state=interrupted,
        expect_rc=1, expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["was interrupted", "Rolled back"]),
    run("--roll-back refuses images that aren't from the registry", args=["--roll-back"], engine_image=new_engine,
        job_images={j: new_agent for j in jobs_dev}, rollback_state=tampered, expect_rc=1, expect_engine=new_engine,
        expect_jobs=old_agent, expect_in_output=["no usable image for " + engine_dev, "The rollback itself failed"],
        expect_rollback_file=True),
    run("--roll-back with no rollback file does nothing", args=["--roll-back"], expect_in_output=["Nothing to roll back"],
        never_calls=["az "]),
    run("a failed push changes nothing", fail_push=True, expect_rc=1, expect_engine=old_engine, expect_jobs=old_agent,
        never_calls=["--method patch"]),
    run("nothing to do when the commit already runs", engine_image=new_engine, job_images={j: new_agent for j in jobs_dev},
        expect_in_output=["Already running"], expect_calls=[f"docker push --quiet {new_engine_tag}"], never_calls=["--method patch"]),
    run("an engine whose latest revision was deactivated by hand is left alone",
        revisions=[{"name": "rev-1", "image": old_engine, "active": False, "polls": 0}],
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["CI leaves it alone"], never_calls=["docker", "--method patch"]),
    run("so is one stopped with az containerapp stop", running_status="Stopped",
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["CI leaves it alone"], never_calls=["docker", "--method patch"]),
    run("and one whose older revision was activated by hand", running_status="Running",
        revisions=[{"name": "rev-0", "image": old_engine, "active": True, "polls": 0}, {"name": "rev-1", "image": old_engine, "active": False, "polls": 0}],
        expect_engine=old_engine, expect_in_output=["CI leaves it alone"], never_calls=["docker", "--method patch"]),
    run("a job not running from the registry is left to provision.ps1",
        job_images={**{j: old_agent for j in jobs_dev}, jobs_dev[2]: "mcr.microsoft.com/k8se/quickstart-jobs:latest"},
        expect_rc=1, expect_in_output=["not an image from"], never_calls=["docker", "--method patch"]),
    run("agents.deploy platform only pushes the images, for provision.ps1 to create the apps from", deploy="platform",
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["provision.ps1 -ImageTag " + NEW],
        expect_calls=[f"docker push --quiet {new_engine_tag}", f"docker push --quiet {new_agent_tag}"], never_calls=["az rest"]),
    run("agents.deploy none deploys nothing", deploy="none", expect_in_output=["no registry in Azure"], never_calls=["az ", "docker"]),
    run("never a protected environment", protected=True, expect_rc=1, expect_in_output=["protected environment"],
        never_calls=["az ", "docker"]),
    run("a registry of another environment is refused", registry_env="prod", expect_rc=1,
        expect_in_output=["isn't tagged cix-environment=dev"], never_calls=["docker", "az rest"]),
    run("no engine yet is left to provision.ps1", missing_engine=True, expect_rc=1,
        expect_in_output=["run provision.ps1 with agents.deploy apps first"], never_calls=["docker", "--method patch"]),
    run("an engine not running from the registry is left to provision.ps1", engine_image="mcr.microsoft.com/k8se/quickstart:latest",
        expect_rc=1, expect_in_output=["not an image from"], never_calls=["docker", "--method patch"]),
]

FAKE_GH = textwrap.dedent(r'''
    #!/bin/sh
    echo "gh $*" >> "$(dirname "$0")/calls.log"
    case "$2" in
      */commits/main) [ -n "$TIP" ] && echo "$TIP" || exit 1 ;;
      */compare/*) [ -n "$COMPARE" ] && echo "$COMPARE" || exit 1 ;;
      *) exit 1 ;;
    esac
''').lstrip()


def run_tip(d, *, tip_sha, compare, deploy):
    gh = os.path.join(d, "gh")
    open(gh, "w").write(FAKE_GH)
    os.chmod(gh, 0o755)
    settings = os.path.join(d, "dev.json")
    json.dump({"agents": {"deploy": deploy}}, open(settings, "w"))
    out_file = os.path.join(d, "out")
    env = dict(os.environ, PATH=d + os.pathsep + os.environ["PATH"], TIP=tip_sha, SETTINGS=settings,
               COMPARE=json.dumps(compare) if compare is not None else "", GITHUB_REPOSITORY="o/r",
               GITHUB_SHA=OLD, GITHUB_OUTPUT=out_file)
    p = subprocess.run(["bash", TIP_SCRIPT], env=env, capture_output=True, text=True, timeout=30)
    got = open(out_file).read().strip() if os.path.exists(out_file) else ""
    return p, got, os.path.exists(os.path.join(d, "calls.log"))


def tip(name, *, tip_sha, compare=None, deploy="apps", expect, expect_rc=0, never_gh=False):
    with tempfile.TemporaryDirectory() as d:
        p, got, asked = run_tip(d, tip_sha=tip_sha, compare=compare, deploy=deploy)
    problems = []
    if p.returncode != expect_rc or got != (f"deploy={expect}" if expect else ""):
        problems.append(f"exit {p.returncode} and {got!r}, wanted exit {expect_rc} and deploy={expect}")
    if never_gh and asked:
        problems.append("it asked GitHub")
    return report("agents-tip", name, problems, p.stdout + p.stderr)


def files(*paths, status="ahead"):
    return {"status": status, "files": [{"filename": n} for n in paths]}


results += [
    tip("the tip of main deploys", tip_sha=OLD, expect="true"),
    tip("nothing deploys while agents.deploy is none, and GitHub isn't asked", tip_sha=OLD, deploy="none", expect="false",
        never_gh=True),
    tip("a newer tip with only docs, the dashboard, infra and skills still lets this run deploy", tip_sha=NEW,
        compare=files("docs/azure.md", "dashboard/app/app.js", "README.md", "infra/environments/dev.json", ".claude/skills/x/SKILL.md"),
        expect="true"),
    tip("a newer tip that changed the engine deploys instead", tip_sha=NEW, compare=files("docs/a.md", "src/Cix.Engine/x.cs"),
        expect="false"),
    tip("so does one that changed the agent image or the deployment", tip_sha=NEW, compare=files(".claude/agents/trader.md"),
        expect="false"),
    tip("a tip that isn't simply ahead leaves it to the newer run", tip_sha=NEW, compare=files(status="diverged"),
        expect="false"),
    tip("too many files to list leaves it to the newer run", tip_sha=NEW,
        compare=files(*[f"docs/{i}.md" for i in range(300)]), expect="false"),
    tip("a comparison that fails leaves it to the newer run", tip_sha=NEW, expect="false"),
    tip("an unreadable tip fails the run", tip_sha="", expect=None, expect_rc=1),
]


def plan_images(path):
    """What plan.sh says about `images` for a pull request that changes only this file."""
    with tempfile.TemporaryDirectory() as d:
        def git(*a):
            return subprocess.run(["git", "-C", d, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", *a],
                                  check=True, capture_output=True, text=True).stdout.strip()
        git("init", "-q")
        git("commit", "-q", "--allow-empty", "-m", "base")
        base = git("rev-parse", "HEAD")
        os.makedirs(os.path.join(d, os.path.dirname(path)) or d, exist_ok=True)
        open(os.path.join(d, path), "w").write("x")
        git("add", "-A")
        git("commit", "-q", "-m", "change")
        head = git("rev-parse", "HEAD")
        env = dict(os.environ, EVENT="pull_request", BASE_SHA=base, HEAD_SHA=head, GITHUB_OUTPUT=os.path.join(d, "plan.out"))
        env.pop("GITHUB_STEP_SUMMARY", None)
        subprocess.run(["bash", PLAN_SCRIPT], cwd=d, env=env, check=True, capture_output=True, text=True, timeout=30)
        return "images=true" in open(os.path.join(d, "plan.out")).read().split()


def held_back_by_tip(path):
    """Whether agents-tip.sh leaves deploying to a newer commit that changed only this file."""
    with tempfile.TemporaryDirectory() as d:
        _, got, _ = run_tip(d, tip_sha=NEW, compare=files(path), deploy="apps")
        return got == "deploy=false"


# Each of these is either a file plan.sh deploys images for (or that makes it run everything), and then an older run must
# leave deploying to the newer one, or neither. If the two disagreed, a change could deploy out of order, or not at all.
for path in ["src/Cix.Engine/a.cs", "Cix.slnx", "Directory.Build.props", "global.json", "NuGet.config", ".editorconfig",
             "config/a.json", "strategies/s.json", "docker/engine.Dockerfile", "docker-compose.yml", "docker-compose.azure.yml",
             ".dockerignore", "tools/trader/agent-job.sh", ".claude/agents/trader.md", ".claude/hooks/h.sh",
             ".claude/settings.json", ".github/workflows/ci.yml", "tools/ci/plan.sh",
             "infra/main.bicep", "infra/environments/dev.json", "dashboard/app/app.js", "docs/azure.md", "README.md",
             ".claude/skills/x/SKILL.md", "tests/azure/CixAzure.Tests.ps1", "tools/azure/provision.ps1",
             "tests/Cix.Engine.Tests/a.cs", ".gitleaks.toml", "src/README.md", "tools/spike/revx_spike.py", "Makefile",
             ".gitignore", "LICENSE"]:
    images, held = plan_images(path), held_back_by_tip(path)
    results.append(report("agents-tip", f"plan.sh and agents-tip.sh agree on {path}",
                          [] if images == held else [f"plan.sh images={images}, agents-tip.sh holds back={held}"], ""))

failed = results.count(False)
print(f"{len(results) - failed} passed, {failed} failed")
sys.exit(1 if failed else 0)
