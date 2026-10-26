#!/usr/bin/env python3
"""tools/ci/deploy-agents.sh against a fake az and docker: every path, including the rollbacks. And tools/ci/agents-tip.sh,
which decides whether a run deploys at all, against a fake gh.

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
SERVER = "crcixdevabc123.azurecr.io"
OLD, NEW = "1111111", "2222222222222222222222222222222222222222"
ENGINE = "ca-cix-dev-engine"
JOBS = ["caj-cix-dev-trader", "caj-cix-dev-requests", "caj-cix-dev-analyst"]

# The fake az: answers from state.json next to it, and records every call (one per line) in calls.log.
FAKE_AZ = textwrap.dedent(r'''
    #!/usr/bin/env python3
    import json, os, sys
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.join(here, "state.json")
    state = json.load(open(path))
    args = sys.argv[1:]
    with open(os.path.join(here, "calls.log"), "a") as log: log.write("az " + " ".join(args) + "\n")
    def opt(name):
        return args[args.index(name) + 1] if name in args else None
    def save(): json.dump(state, open(path, "w"))
    name, image, query = opt("--name"), opt("--image"), opt("--query")
    words = [a for a in args if not a.startswith("-")][:3]
    if args[:2] == ["acr", "list"]: print(json.dumps(state["registries"]))
    elif args[:2] == ["acr", "show"]: print(state["server"])
    elif args[:2] == ["acr", "login"]: pass
    elif args[:3] == ["containerapp", "job", "show"]: print(state["jobs"][name])
    elif args[:3] == ["containerapp", "job", "update"]:
        if image in state.get("fail_job_images", {}).get(name, []): sys.exit("ERROR: job update failed")
        state["jobs"][name] = image; save()
    elif args[:2] == ["containerapp", "show"]:
        if query == "properties.latestRevisionName": print("rev-%d" % state["revision"])
        else: print(state["engine"])
    elif args[:2] == ["containerapp", "update"]:
        if image in state.get("fail_engine_images", []): sys.exit("ERROR: update failed")
        state["engine"] = image; state["revision"] += 1; state["polls"] = 0; save()
    elif args[:3] == ["containerapp", "revision", "show"]:
        state["polls"] += 1; save()
        healthy_after = state["healthy_after"]
        new = state["engine"].endswith(state["new_tag"])
        if new and state.get("new_fails"): health, running, provisioning = "Unhealthy", "Failed", "Succeeded"
        elif new and healthy_after is not None and state["polls"] >= healthy_after: health, running, provisioning = "Healthy", "Running", "Succeeded"
        else: health, running, provisioning = "None", "Activating", "Provisioning"
        print(json.dumps({"properties": {"template": {"containers": [{"image": state["engine"]}]},
                                          "healthState": health, "runningState": running, "provisioningState": provisioning}}))
    else: sys.exit("fake az: unexpected call " + " ".join(args))
''').lstrip()

FAKE_DOCKER = textwrap.dedent(r'''
    #!/bin/sh
    echo "docker $*" >> "$(dirname "$0")/calls.log"
    [ "$1" = push ] && [ -n "$FAIL_PUSH" ] && exit 1
    exit 0
''').lstrip()


def run(name, *, deploy="apps", start_engine=True, protected=False, registries=None, engine_image=None,
        job_images=None, healthy_after=2, new_fails=False, fail_engine_images=(), fail_job_images=None,
        fail_push=False, timeout=5, expect_rc=0, expect_engine=None, expect_jobs=None, expect_in_output=(),
        expect_calls=(), never_calls=()):
    with tempfile.TemporaryDirectory() as d:
        settings = os.path.join(d, "dev.json")
        json.dump({"environmentName": "dev", "resourceGroup": "rg-cix-dev", "namePrefix": "cix", "protected": protected,
                   "agents": {"deploy": deploy, "startEngine": start_engine}}, open(settings, "w"))
        state = {
            "registries": registries if registries is not None else [
                {"name": "crother", "tags": {"cix-environment": "prod"}},
                {"name": "crcixdevabc123", "tags": {"cix-environment": "dev"}}],
            "server": SERVER,
            "engine": engine_image or f"{SERVER}/cix-engine:{OLD}",
            "jobs": job_images or {j: f"{SERVER}/cix-trader:{OLD}" for j in JOBS},
            "revision": 1, "polls": 0, "healthy_after": healthy_after, "new_fails": new_fails, "new_tag": NEW,
            "fail_engine_images": list(fail_engine_images), "fail_job_images": fail_job_images or {},
        }
        json.dump(state, open(os.path.join(d, "state.json"), "w"))
        for tool, body in (("az", FAKE_AZ), ("docker", FAKE_DOCKER)):
            path = os.path.join(d, tool)
            open(path, "w").write(body)
            os.chmod(path, 0o755)
        for archive in ("engine.tar.gz", "trader.tar.gz"):
            open(os.path.join(d, archive), "w").close()
        env = dict(os.environ, PATH=d + os.pathsep + os.environ["PATH"], SETTINGS=settings, TAG=NEW,
                   ENGINE_ARCHIVE=os.path.join(d, "engine.tar.gz"), TRADER_ARCHIVE=os.path.join(d, "trader.tar.gz"),
                   HEALTH_TIMEOUT_SECONDS=str(timeout), POLL_SECONDS="0")
        if fail_push:
            env["FAIL_PUSH"] = "1"
        p = subprocess.run(["bash", SCRIPT], env=env, capture_output=True, text=True, timeout=60)
        out = p.stdout + p.stderr
        final = json.load(open(os.path.join(d, "state.json")))
        log_path = os.path.join(d, "calls.log")
        calls = open(log_path).read() if os.path.exists(log_path) else ""
    problems = []
    if p.returncode != expect_rc:
        problems.append(f"exit {p.returncode}, wanted {expect_rc}")
    if expect_engine and final["engine"] != expect_engine:
        problems.append(f"engine runs {final['engine']}, wanted {expect_engine}")
    if expect_jobs:
        for j in JOBS:
            if final["jobs"][j] != expect_jobs:
                problems.append(f"{j} runs {final['jobs'][j]}, wanted {expect_jobs}")
    for text in expect_in_output:
        if text not in out:
            problems.append(f"output lacks {text!r}")
    for call in expect_calls:
        if call not in calls:
            problems.append(f"no call {call!r}")
    for call in never_calls:
        if call in calls:
            problems.append(f"unexpected call {call!r}")
    if problems:
        print(f"FAIL {name}: " + "; ".join(problems))
        print(textwrap.indent(out.strip() + "\n--- calls\n" + calls.strip(), "    "))
        print(f"::error title=deploy-agents: {name}::" + "; ".join(problems))
        return False
    print(f"ok   {name}")
    return True


old_engine, new_engine = f"{SERVER}/cix-engine:{OLD}", f"{SERVER}/cix-engine:{NEW}"
old_agent, new_agent = f"{SERVER}/cix-trader:{OLD}", f"{SERVER}/cix-trader:{NEW}"
results = [
    run("deploys the engine, waits for it to be healthy, then the jobs",
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=["Engine revision rev-2 is healthy", f"Deployed {NEW}"],
        expect_calls=[f"docker push --quiet {new_engine}", f"docker push --quiet {new_agent}", "az acr login --name crcixdevabc123"]),
    run("a stopped engine gets its new image without waiting", start_engine=False,
        expect_engine=new_engine, expect_jobs=new_agent, expect_in_output=["The engine is stopped"], never_calls=["revision show"]),
    run("an engine that fails comes back to the old image, and the jobs stay", new_fails=True, expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["failed (provisioning Succeeded, running Failed)", "Rolled back"]),
    run("an engine that never becomes healthy is rolled back after the timeout", healthy_after=None, timeout=1, expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["wasn't healthy after 1 seconds", "Rolled back"]),
    run("a job that can't be updated rolls everything back", fail_job_images={JOBS[1]: [new_agent]}, expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["Rolled back"]),
    run("an engine update refused by Azure is rolled back too", fail_engine_images=[new_engine], expect_rc=1,
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["Rolled back"]),
    run("a failed rollback says so", new_fails=True, fail_engine_images=[old_engine], expect_rc=1,
        expect_in_output=["The rollback itself failed"]),
    run("a failed push changes nothing", fail_push=True, expect_rc=1, expect_engine=old_engine, expect_jobs=old_agent,
        never_calls=["containerapp update", "containerapp job update"]),
    run("nothing to do when the commit already runs", engine_image=new_engine, job_images={j: new_agent for j in JOBS},
        expect_in_output=["Already running"], never_calls=["docker", "update"]),
    run("agents.deploy platform only pushes the images, for provision.ps1 to create the apps from", deploy="platform",
        expect_engine=old_engine, expect_jobs=old_agent, expect_in_output=["provision.ps1 -ImageTag " + NEW],
        expect_calls=[f"docker push --quiet {new_engine}", f"docker push --quiet {new_agent}"], never_calls=["containerapp"]),
    run("agents.deploy none deploys nothing", deploy="none", expect_in_output=["no registry in Azure"],
        never_calls=["az ", "docker"]),
    run("never a protected environment", protected=True, expect_rc=1, expect_in_output=["protected environment"],
        never_calls=["az ", "docker"]),
    run("no registry tagged for the environment", registries=[{"name": "crother", "tags": {"cix-environment": "prod"}}],
        expect_rc=1, expect_in_output=["No single container registry"], never_calls=["docker", "update"]),
    run("an engine not running from the registry is left to provision.ps1", engine_image="mcr.microsoft.com/k8se/quickstart:latest",
        expect_rc=1, expect_in_output=["not an image from"], never_calls=["docker", "update"]),
]

FAKE_GH = textwrap.dedent(r'''
    #!/bin/sh
    case "$2" in
      */commits/main) [ -n "$TIP" ] && echo "$TIP" || exit 1 ;;
      */compare/*) [ -n "$COMPARE" ] && echo "$COMPARE" || exit 1 ;;
      *) exit 1 ;;
    esac
''').lstrip()


def tip(name, *, tip_sha, compare=None, expect, expect_rc=0):
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "gh")
        open(path, "w").write(FAKE_GH)
        os.chmod(path, 0o755)
        out_file = os.path.join(d, "out")
        env = dict(os.environ, PATH=d + os.pathsep + os.environ["PATH"], TIP=tip_sha,
                   COMPARE=json.dumps(compare) if compare is not None else "", GITHUB_REPOSITORY="o/r",
                   GITHUB_SHA=OLD, GITHUB_OUTPUT=out_file)
        p = subprocess.run(["bash", TIP_SCRIPT], env=env, capture_output=True, text=True, timeout=30)
        got = open(out_file).read().strip() if os.path.exists(out_file) else ""
    if p.returncode != expect_rc or got != (f"deploy={expect}" if expect else ""):
        problem = f"exit {p.returncode} and {got!r}, wanted exit {expect_rc} and deploy={expect}"
        print(f"FAIL {name}: {problem}\n" + textwrap.indent((p.stdout + p.stderr).strip(), "    "))
        print(f"::error title=agents-tip: {name}::{problem}")
        return False
    print(f"ok   {name}")
    return True


def files(*names, status="ahead"):
    return {"status": status, "files": [{"filename": n} for n in names]}


results += [
    tip("the tip of main deploys", tip_sha=OLD, expect="true"),
    tip("a newer tip with only docs and the dashboard still lets this run deploy", tip_sha=NEW,
        compare=files("docs/azure.md", "dashboard/app/app.js", "README.md"), expect="true"),
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
failed = results.count(False)
print(f"{len(results) - failed} passed, {failed} failed")
sys.exit(1 if failed else 0)
