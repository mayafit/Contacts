#!/usr/bin/env python3
"""Conformance check of the rendered Contacts chart (AD-51 rule 8, AD-52 rule 1).

Usage:
    helm template contacts helm/contacts -n contacts -f <values.yaml> \
        | python3 helm/scripts/conformance.py [--values <values.yaml> ...]

Checks every rendered workload (Deployment, StatefulSet, DaemonSet, Job, CronJob):
  - no image on ``:latest`` or without a tag or digest;
  - every container, init containers included, runs restricted: runAsNonRoot
    (pod or container), seccomp RuntimeDefault (pod or container),
    allowPrivilegeEscalation false, readOnlyRootFilesystem true,
    capabilities.drop [ALL], no added capability, not privileged;
  - every container has cpu and memory requests and limits;
  - automountServiceAccountToken is false on the pod;
  - a NetworkPolicy with a non-empty podSelector selects the pod, and a
    namespace-wide default-deny (podSelector {}, Ingress and Egress) exists;
and in the rest of the manifests:
  - no Service of type NodePort;
  - no Secret rendered by the chart (credentials come from Vault or an existing
    Secret);
and in each ``--values`` file:
  - no ``allowExternal: true``;
  - no non-empty password, secret, token or key value (references such as
    ``existingSecret`` or ``pullSecret`` are names, not credentials).

Exits 1 and lists every violation when any check fails. Needs PyYAML.
"""
import argparse
import re
import sys

import yaml

WORKLOADS = {"Deployment", "StatefulSet", "DaemonSet", "Job", "CronJob"}
CREDENTIAL_KEY = re.compile(r"(password|secret|token|apikey|key)$", re.IGNORECASE)
# Keys that name a Secret, a key inside a Secret or a Vault path, not a credential
REFERENCE_KEY = re.compile(
    r"^(existing.*|pullsecret|imagepullsecrets|secretname|.*passwordkey|.*secretkey"
    r"|redispasswordkey|secretkeys|kvpath)$",
    re.IGNORECASE,
)


def pod_template(obj):
    kind = obj["kind"]
    if kind == "CronJob":
        return obj["spec"]["jobTemplate"]["spec"]["template"]
    return obj["spec"]["template"]


def selects(selector, labels):
    """matchLabels / matchExpressions (In, NotIn, Exists, DoesNotExist)."""
    for k, v in (selector.get("matchLabels") or {}).items():
        if labels.get(k) != v:
            return False
    for expr in selector.get("matchExpressions") or []:
        key, op, values = expr["key"], expr["operator"], expr.get("values") or []
        if op == "In" and labels.get(key) not in values:
            return False
        if op == "NotIn" and labels.get(key) in values:
            return False
        if op == "Exists" and key not in labels:
            return False
        if op == "DoesNotExist" and key in labels:
            return False
    return True


def check_container(where, c, pod_sc, errors):
    sc = c.get("securityContext") or {}
    name = f"{where} container {c['name']}"
    image = c.get("image", "")
    last = image.rsplit("/", 1)[-1]
    if "@sha256:" not in image and (":" not in last or last.endswith(":latest")):
        errors.append(f"{name}: image {image!r} is :latest or untagged")
    if not (sc.get("runAsNonRoot", pod_sc.get("runAsNonRoot")) is True):
        errors.append(f"{name}: runAsNonRoot is not true")
    if sc.get("runAsUser", pod_sc.get("runAsUser")) == 0:
        errors.append(f"{name}: runAsUser is 0")
    seccomp = (sc.get("seccompProfile") or pod_sc.get("seccompProfile") or {}).get("type")
    if seccomp not in ("RuntimeDefault", "Localhost"):
        errors.append(f"{name}: seccompProfile is {seccomp!r}, not RuntimeDefault")
    if sc.get("allowPrivilegeEscalation") is not False:
        errors.append(f"{name}: allowPrivilegeEscalation is not false")
    if sc.get("readOnlyRootFilesystem") is not True:
        errors.append(f"{name}: readOnlyRootFilesystem is not true")
    if sc.get("privileged"):
        errors.append(f"{name}: privileged")
    caps = sc.get("capabilities") or {}
    if [x.upper() for x in caps.get("drop") or []] != ["ALL"]:
        errors.append(f"{name}: capabilities.drop is not [ALL]")
    if caps.get("add"):
        errors.append(f"{name}: adds capabilities {caps['add']}")
    res = c.get("resources") or {}
    for part in ("requests", "limits"):
        for r in ("cpu", "memory"):
            if r not in (res.get(part) or {}):
                errors.append(f"{name}: no {part}.{r}")


def check_values(path, errors):
    def walk(node, trail):
        if isinstance(node, dict):
            for k, v in node.items():
                here = trail + [str(k)]
                if k == "allowExternal" and v is True:
                    errors.append(f"{path}: {'.'.join(here)} is true")
                if (
                    isinstance(v, (str, int))
                    and str(v) != ""
                    and CREDENTIAL_KEY.search(str(k))
                    and not REFERENCE_KEY.match(str(k))
                ):
                    errors.append(f"{path}: {'.'.join(here)} sets a credential value")
                walk(v, here)
        elif isinstance(node, list):
            for i, v in enumerate(node):
                walk(v, trail + [str(i)])

    with open(path, encoding="utf-8") as f:
        walk(yaml.safe_load(f) or {}, [])


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--values", action="append", default=[], help="values file to scan")
    args = ap.parse_args()

    docs = [d for d in yaml.safe_load_all(sys.stdin) if isinstance(d, dict) and d.get("kind")]
    errors = []
    policies = [d for d in docs if d["kind"] == "NetworkPolicy"]
    default_deny = any(
        not (p["spec"].get("podSelector") or {})
        and {"Ingress", "Egress"} <= set(p["spec"].get("policyTypes") or [])
        and not p["spec"].get("ingress")
        and not p["spec"].get("egress")
        for p in policies
    )
    if not default_deny:
        errors.append("no namespace-wide default-deny NetworkPolicy")

    workloads = 0
    containers = 0
    for d in docs:
        kind, name = d["kind"], d["metadata"]["name"]
        if kind == "Service" and d["spec"].get("type") == "NodePort":
            errors.append(f"Service {name} is a NodePort")
        if kind == "Secret":
            errors.append(f"Secret {name} is rendered by the chart (use Vault or an existing Secret)")
        if kind not in WORKLOADS:
            continue
        workloads += 1
        tpl = pod_template(d)
        spec = tpl["spec"]
        labels = (tpl.get("metadata") or {}).get("labels") or {}
        where = f"{kind} {name}"
        pod_sc = spec.get("securityContext") or {}
        if spec.get("automountServiceAccountToken") is not False:
            errors.append(f"{where}: automountServiceAccountToken is not false")
        if spec.get("hostNetwork") or spec.get("hostPID") or spec.get("hostIPC"):
            errors.append(f"{where}: uses a host namespace")
        for c in (spec.get("initContainers") or []) + spec["containers"]:
            containers += 1
            check_container(where, c, pod_sc, errors)
        if not any(
            (p["spec"].get("podSelector") or {}) and selects(p["spec"]["podSelector"], labels)
            for p in policies
        ):
            errors.append(f"{where}: no NetworkPolicy selects its pods")

    for path in args.values:
        check_values(path, errors)

    for e in errors:
        print(f"FAIL {e}")
    print(
        f"{workloads} workloads, {containers} containers, {len(policies)} NetworkPolicies, "
        f"{len(args.values)} values files: {'FAIL' if errors else 'OK'} ({len(errors)} violations)"
    )
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
