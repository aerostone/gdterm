#!/usr/bin/env python3
"""Check CodeStable project, workflow and architecture contracts.

This is a read-only semantic checker.  It complements validate-yaml.py:
validate-yaml checks syntax and required keys; this tool checks relationships,
state values, architecture anchors and the current git diff.

Python: 3.9+; standard library only.  PyYAML is optional.
"""

import argparse
import hashlib
import fnmatch
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

try:
    from yaml_support import parse_yaml as _parse_yaml
    from yaml_support import split_frontmatter
except ImportError:  # direct import by tests or embedding tools
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from yaml_support import parse_yaml as _parse_yaml
    from yaml_support import split_frontmatter


if sys.version_info < (3, 9):
    sys.stderr.write("check-compliance.py requires Python 3.9 or newer\n")
    sys.exit(2)


Result = Dict[str, Any]


def parse_simple_mapping(text: str) -> Tuple[Optional[Dict[str, Any]], Optional[str]]:
    """Compatibility wrapper for tests and callers of the former parser."""
    return _parse_yaml(text, prefer_pyyaml=False)


def parse_yaml(text: str) -> Tuple[Optional[Dict[str, Any]], Optional[str]]:
    return _parse_yaml(text)


def frontmatter(text: str) -> Tuple[Dict[str, Any], str, Optional[str]]:
    return split_frontmatter(text)


def result(check_id: str, status: str, message: str, evidence: Optional[List[str]] = None) -> Result:
    item: Result = {"id": check_id, "status": status, "message": message}
    if evidence:
        item["evidence"] = evidence
    return item


def read_file(path: Path) -> Tuple[Dict[str, Any], str, Optional[str]]:
    try:
        return frontmatter(path.read_text(encoding="utf-8"))
    except OSError as exc:
        return {}, "", str(exc)


def load_yaml_file(path: Path) -> Tuple[Optional[Dict[str, Any]], Optional[str]]:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        return None, str(exc)
    return parse_yaml(text)


def support_file(root: Path, name: str) -> Path:
    project_file = root / ".codestable" / "reference" / name
    if project_file.exists():
        return project_file
    return Path(__file__).resolve().parent.parent / "reference" / name


def load_workflow(root: Path) -> Tuple[Optional[Dict[str, Any]], Optional[str], Path]:
    path = support_file(root, "workflow.yaml")
    data, error = load_yaml_file(path)
    if error:
        return None, error, path
    if not isinstance(data, dict) or not isinstance(data.get("kinds"), dict):
        return None, "workflow.yaml needs a kinds mapping", path
    return data, None, path


DEFAULT_MODEL_PROFILE: Dict[str, Any] = {
    "version": 1,
    "profile": "modern-27b-128k",
    "profiles": {
        "modern-27b-128k": {"context": {"max_files": 32, "max_chars": 320000,
                                          "output": "any", "full_reference": True}},
        "constrained-27b-64k": {"context": {"max_files": 16, "max_chars": 160000,
                                               "output": "compact", "full_reference": False}},
    },
}


def load_model_profile(root: Path, requested: Optional[str] = None) -> Tuple[Dict[str, Any], Optional[str], str]:
    project_path = root / ".codestable" / "model-profile.yaml"
    path = project_path if project_path.exists() else support_file(root, "model-profile.yaml")
    data, error = load_yaml_file(path) if path.exists() else (DEFAULT_MODEL_PROFILE, None)
    if error or not isinstance(data, dict):
        return {}, error or "model-profile.yaml must be a mapping", str(path)
    profiles = data.get("profiles")
    if not isinstance(profiles, dict):
        return {}, "model-profile.yaml needs profiles mapping", str(path)
    name = requested or str(data.get("profile", "modern-27b-128k"))
    profile = profiles.get(name)
    if not isinstance(profile, dict) or not isinstance(profile.get("context"), dict):
        return {}, "unknown model profile %s" % name, str(path)
    return profile, None, name


def selected_context_chars(text: str, item: Dict[str, Any]) -> int:
    """Estimate only selected headings/symbol neighborhoods, not the whole file."""
    chunks: List[str] = []
    headings = item.get("headings") if isinstance(item.get("headings"), list) else []
    for heading in headings:
        match = re.search(r"^#{1,6}\s+.*" + re.escape(str(heading)) + r".*$", text,
                          re.MULTILINE | re.IGNORECASE)
        if match:
            level = len(match.group(0)) - len(match.group(0).lstrip("#"))
            next_heading = re.search(r"^#{1,%d}\s+" % level, text[match.end():], re.MULTILINE)
            end = match.end() + next_heading.start() if next_heading else len(text)
            chunks.append(text[match.start():end])
    symbols = item.get("symbols") if isinstance(item.get("symbols"), list) else []
    lines = text.splitlines()
    for symbol in symbols:
        needle = str(symbol).split(".")[-1]
        for index, line in enumerate(lines):
            if re.search(r"\b" + re.escape(needle) + r"\b", line):
                chunks.append("\n".join(lines[max(0, index - 20):min(len(lines), index + 41)]))
                break
    return min(len(text), sum(len(chunk) for chunk in chunks)) if chunks else len(text)


def unresolved_context_selectors(text: str, item: Dict[str, Any]) -> List[str]:
    unresolved: List[str] = []
    for heading in item.get("headings", []) if isinstance(item.get("headings"), list) else []:
        if not re.search(r"^#{1,6}\s+.*" + re.escape(str(heading)) + r".*$", text,
                         re.MULTILINE | re.IGNORECASE):
            unresolved.append("heading:%s" % heading)
    for symbol in item.get("symbols", []) if isinstance(item.get("symbols"), list) else []:
        needle = str(symbol).split(".")[-1]
        if not re.search(r"\b" + re.escape(needle) + r"\b", text):
            unresolved.append("symbol:%s" % symbol)
    return unresolved


def check_context_budget(root: Path, context: List[Dict[str, Any]], requested: Optional[str] = None,
                         compact_output: bool = True) -> Tuple[Result, Dict[str, Any]]:
    profile, error, name = load_model_profile(root, requested)
    if error:
        return result("context.profile", "fail", error), {"profile": name}
    limits = profile.get("context") or {}
    max_files = limits.get("max_files")
    max_chars = limits.get("max_chars")
    files = [item.get("path", "") for item in context]
    total_chars = 0
    missing: List[str] = []
    unscoped: List[str] = []
    unresolved: List[str] = []
    full_reference = bool(limits.get("full_reference", False))
    for item, relative in zip(context, files):
        path = root / str(relative)
        if not path.exists():
            continue
        try:
            text = path.read_text(encoding="utf-8")
            has_selector = bool(item.get("headings") or item.get("symbols"))
            mandatory = str(relative).endswith("/change.md") or str(relative).endswith("attention.md")
            if not full_reference and len(text) > 12000 and not has_selector and not mandatory:
                unscoped.append(str(relative))
            unresolved.extend("%s#%s" % (relative, selector)
                              for selector in unresolved_context_selectors(text, item))
            total_chars += selected_context_chars(text, item)
        except OSError:
            missing.append(str(relative))
    violations: List[str] = []
    if not isinstance(max_files, int) or not isinstance(max_chars, int):
        violations.append("profile context limits must be integers")
    else:
        if len(files) > max_files:
            violations.append("files=%d > max_files=%d" % (len(files), max_files))
        if total_chars > max_chars:
            violations.append("chars=%d > max_chars=%d" % (total_chars, max_chars))
    if missing:
        violations.extend("unreadable: " + item for item in missing)
    if unscoped:
        violations.extend("64K profile requires headings/symbols: " + item for item in unscoped)
    if unresolved:
        violations.extend("selector not found: " + item for item in unresolved)
    if limits.get("output") == "compact" and not compact_output:
        violations.append("profile requires --agent compact output")
    budget = {"profile": name, "files": len(files), "chars": total_chars,
              "max_files": max_files, "max_chars": max_chars,
              "output": limits.get("output", "compact"),
              "full_reference": full_reference}
    return result("context.budget", "fail" if violations else "pass",
                  "context exceeds model profile budget" if violations else "context fits model profile budget",
                  violations or ["%d files" % len(files), "%d chars" % total_chars]), budget


def workflow_required_sections(workflow: Dict[str, Any], kind: Any, status: Any) -> List[str]:
    kinds = workflow.get("kinds") or {}
    kind_config = kinds.get(kind) if isinstance(kinds, dict) else None
    if not isinstance(kind_config, dict):
        return []
    by_status = kind_config.get("required_sections") or {}
    sections = by_status.get(status) if isinstance(by_status, dict) else []
    return [str(section) for section in sections] if isinstance(sections, list) else []


def workflow_contract_required(workflow: Dict[str, Any], phase: Optional[str], kind: Any) -> bool:
    if kind == "audit" or phase is None:
        return False
    phases = workflow.get("phases") or {}
    config = phases.get(phase) if isinstance(phases, dict) else None
    return bool(config.get("contract_required")) if isinstance(config, dict) else False


def state_key(status: Any, phase: Any) -> str:
    return "%s:%s" % (status, phase or "") if phase else str(status)


def workflow_state_check(root: Path, change_file: Path, workflow: Dict[str, Any],
                         kind: Any, status: Any, phase: Any) -> List[Result]:
    config = (workflow.get("kinds") or {}).get(kind) if isinstance(workflow.get("kinds"), dict) else None
    if not isinstance(config, dict):
        return [result("change.state", "fail", "workflow has no state definition for kind %r" % kind)]
    order = config.get("state_order") or []
    current = state_key(status, phase)
    if not isinstance(order, list) or not order:
        return [result("change.state", "warn", "workflow has no state_order", [current])]
    normalized = [str(item) for item in order]
    if current not in normalized:
        return [result("change.state", "fail", "status/phase combination is not allowed",
                       [current, "allowed: " + ", ".join(normalized)])]
    checks = [result("change.state", "pass", "status/phase combination is allowed", [current])]
    previous = previous_change_state(root, change_file)
    if previous is None:
        return checks
    previous_kind, previous_status, previous_phase = previous
    if previous_kind != kind:
        return checks + [result("change.state.kind", "fail", "change kind cannot change after creation",
                                [str(previous_kind), str(kind)])]
    old = state_key(previous_status, previous_phase)
    if old not in normalized:
        return checks + [result("change.state.history", "fail", "previous state is not in workflow",
                                [old])]
    old_index, new_index = normalized.index(old), normalized.index(current)
    if new_index < old_index:
        checks.append(result("change.state.regression", "fail", "change state moved backwards", [old, current]))
    elif new_index > old_index + 1:
        checks.append(result("change.state.skip", "fail", "change state skipped a workflow stage", [old, current]))
    else:
        checks.append(result("change.state.progression", "pass", "change state progressed legally", [old, current]))
    return checks


def previous_change_state(root: Path, change_file: Path) -> Optional[Tuple[Any, Any, Any]]:
    relative = str(change_file.resolve().relative_to(root.resolve()))
    try:
        proc = subprocess.run(["git", "show", "HEAD:" + relative], cwd=str(root),
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              universal_newlines=True, check=False)
    except OSError:
        return None
    if proc.returncode != 0:
        return None
    meta, _, error = frontmatter(proc.stdout)
    if error:
        return None
    return meta.get("kind"), meta.get("status"), meta.get("phase")


def git_head(root: Path) -> str:
    proc = subprocess.run(["git", "rev-parse", "--verify", "HEAD"], cwd=str(root),
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          universal_newlines=True, check=False)
    return proc.stdout.strip() if proc.returncode == 0 else "unborn"


def file_digest(path: Path) -> str:
    if not path.exists():
        return "<missing>"
    try:
        return hashlib.sha256(path.read_bytes()).hexdigest()
    except OSError:
        return "<unreadable>"


def baseline_snapshot(root: Path) -> Dict[str, Any]:
    changed, error = git_changed_files(root)
    return {
        "git_head": git_head(root),
        "dirty_hashes": {path: file_digest(root / path) for path in changed},
        "error": error,
    }


def baseline_checks(root: Path, contract: Dict[str, Any]) -> List[Result]:
    preexisting = contract.get("preexisting_changes") or []
    baseline = contract.get("baseline")
    if not preexisting:
        return []
    if not isinstance(baseline, dict):
        return [result("baseline.required", "fail",
                       "preexisting_changes requires a baseline snapshot; run --snapshot",
                       [str(path) for path in preexisting])]
    expected_head = baseline.get("git_head")
    checks: List[Result] = []
    if expected_head and expected_head != "unborn" and expected_head != git_head(root):
        checks.append(result("baseline.git_head", "fail", "git HEAD differs from task baseline",
                             [str(expected_head), git_head(root)]))
    hashes = baseline.get("dirty_hashes")
    if not isinstance(hashes, dict):
        checks.append(result("baseline.hashes", "fail", "baseline.dirty_hashes must be a mapping"))
        return checks
    missing = [str(path) for path in preexisting if str(path) not in hashes]
    checks.append(result("baseline.coverage", "fail" if missing else "pass",
                         "baseline misses preexisting files" if missing else "baseline covers preexisting files",
                         missing or [str(path) for path in preexisting]))
    return checks


def risk_for_change(meta: Dict[str, Any], body: str = "") -> Dict[str, Any]:
    declared = meta.get("risk") if isinstance(meta.get("risk"), dict) else {}
    level = declared.get("level")
    reasons = declared.get("reasons") if isinstance(declared.get("reasons"), list) else []
    if level in {"low", "medium", "high"}:
        return {"level": level, "reasons": [str(item) for item in reasons]}
    text = (body + " " + str(meta.get("summary", ""))).lower()
    signals = {
        "public_api": ("api", "接口", "endpoint", "公开"),
        "migration": ("migration", "迁移", "schema", "数据库"),
        "security": ("security", "安全", "权限", "认证", "token"),
        "data_change": ("delete", "删除数据", "不可逆", "data loss"),
        "cross_module": ("跨模块", "cross-module", "architecture"),
        "no_tests": ("无测试", "no test", "untested"),
    }
    detected = [name for name, terms in signals.items() if any(term in text for term in terms)]
    inferred = "high" if any(item in detected for item in ("migration", "security", "data_change")) else (
        "medium" if detected else "low")
    return {"level": inferred, "reasons": detected}


def check_risk_schema(meta: Dict[str, Any], body: str) -> List[Result]:
    risk = meta.get("risk")
    if risk is None:
        inferred = risk_for_change(meta, body)
        return [result("change.risk", "warn", "risk is inferred; declare risk.level and reasons for standard work",
                       [inferred["level"]] + inferred["reasons"])]
    if not isinstance(risk, dict) or risk.get("level") not in {"low", "medium", "high"}:
        return [result("change.risk", "fail", "risk.level must be low, medium or high")]
    reasons = risk.get("reasons", [])
    if not isinstance(reasons, list):
        return [result("change.risk", "fail", "risk.reasons must be a list")]
    checks = [result("change.risk", "pass", "risk level is explicit",
                     [str(risk.get("level"))] + [str(item) for item in reasons])]
    if risk.get("level") == "high" and meta.get("mode") == "lean":
        checks.append(result("change.risk_mode", "fail", "high-risk changes require standard mode"))
    return checks


def check_artifact_graph(root: Path, contract: Dict[str, Any], converge: bool = False) -> List[Result]:
    artifacts = contract.get("artifacts")
    if artifacts is None:
        return []
    if not isinstance(artifacts, list):
        return [result("artifacts.shape", "fail", "contract.artifacts must be a list")]
    ids: List[str] = []
    dependencies: Dict[str, List[str]] = {}
    path_errors: List[str] = []
    for index, artifact in enumerate(artifacts, 1):
        if not isinstance(artifact, dict) or not artifact.get("id"):
            path_errors.append("artifact-%d missing id" % index)
            continue
        artifact_id = str(artifact["id"])
        if artifact_id in ids:
            path_errors.append("duplicate artifact id %s" % artifact_id)
        ids.append(artifact_id)
        deps = artifact.get("depends_on") or []
        if not isinstance(deps, list):
            path_errors.append("artifact %s depends_on must be a list" % artifact_id)
            deps = []
        dependencies[artifact_id] = [str(item) for item in deps]
        paths = artifact.get("paths") or ([artifact.get("path")] if artifact.get("path") else [])
        if not isinstance(paths, list) or not paths:
            path_errors.append("artifact %s needs path or paths" % artifact_id)
        elif converge:
            for path in paths:
                if not (root / str(path)).exists() and not list(root.glob(str(path))):
                    path_errors.append("artifact %s path missing: %s" % (artifact_id, path))
    known = set(ids)
    for artifact_id, deps in dependencies.items():
        path_errors.extend("artifact %s depends on unknown %s" % (artifact_id, dep)
                           for dep in deps if dep not in known)
    if not path_errors:
        remaining = set(ids)
        completed: set = set()
        while remaining:
            wave = {item for item in remaining if set(dependencies.get(item, [])).issubset(completed)}
            if not wave:
                path_errors.append("artifact dependency cycle detected")
                break
            completed.update(wave)
            remaining.difference_update(wave)
    return [result("artifacts.graph", "fail" if path_errors else "pass",
                   "artifact graph is invalid" if path_errors else "artifact graph is valid",
                   path_errors or ids)]


def check_constitution(root: Path) -> List[Result]:
    path = root / ".codestable" / "constitution.yaml"
    if not path.exists():
        return []
    data, error = load_yaml_file(path)
    if error or not isinstance(data, dict):
        return [result("constitution.load", "fail", error or "constitution must be a mapping", [str(path)])]
    errors: List[str] = []
    if not isinstance(data.get("principles", []), list):
        errors.append("principles must be a list")
    terminology = data.get("terminology", {})
    if not isinstance(terminology, dict):
        errors.append("terminology must be a mapping")
    if not isinstance(data.get("required_commands", {}), dict):
        errors.append("required_commands must be a mapping")
    checks = [result("constitution.schema", "fail" if errors else "pass",
                     "; ".join(errors) if errors else "constitution is valid", errors or [str(path)])]
    if not errors:
        standards = {key: data.get(key, {}) for key in ("required_commands", "path_rules")}
        checks.extend(check_project_standards(root, standards))
    return checks


def check_terminology_conflicts(root: Path) -> List[Result]:
    definitions: Dict[str, Tuple[str, str]] = {}
    conflicts: List[str] = []
    constitution = root / ".codestable" / "constitution.yaml"
    if constitution.exists():
        data, _ = load_yaml_file(constitution)
        terms = data.get("terminology", {}) if isinstance(data, dict) else {}
        if isinstance(terms, dict):
            for term, definition in terms.items():
                definitions[str(term).lower()] = (str(definition), str(constitution))
    compound = root / ".codestable" / "compound"
    for path in sorted(compound.glob("*.md")) if compound.is_dir() else []:
        meta, _, error = read_file(path)
        if error:
            continue
        if meta.get("status") in {"superseded", "deprecated", "outdated"}:
            continue
        terms = meta.get("terms") if isinstance(meta.get("terms"), dict) else {}
        if isinstance(meta.get("term"), str) and isinstance(meta.get("definition"), str):
            terms = dict(terms)
            terms[meta["term"]] = meta["definition"]
        for term, definition in terms.items():
            key = str(term).lower()
            value = str(definition)
            previous = definitions.get(key)
            if previous and previous[0] != value:
                conflicts.append("%s: %s vs %s" % (term, previous[1], str(path)))
            else:
                definitions[key] = (value, str(path))
    return [result("terminology.conflicts", "fail" if conflicts else "pass",
                   "conflicting active terminology found" if conflicts else "no terminology conflicts",
                   conflicts[:50])]


def check_evidence_ledger(root: Path, change_dir: Path, contract: Dict[str, Any], converge: bool) -> List[Result]:
    if not contract.get("evidence_ledger"):
        return []
    ledger = change_dir / "evidence.jsonl"
    if not ledger.exists():
        return [result("evidence.ledger", "fail" if converge else "warn",
                       "evidence ledger is required but missing", [str(ledger)])]
    errors: List[str] = []
    try:
        lines = ledger.read_text(encoding="utf-8").splitlines()
    except OSError as exc:
        return [result("evidence.ledger", "fail", str(exc), [str(ledger)])]
    for number, line in enumerate(lines, 1):
        if not line.strip():
            continue
        try:
            item = json.loads(line)
        except json.JSONDecodeError:
            errors.append("line %d is not JSON" % number)
            continue
        if not isinstance(item, dict) or not item.get("step") or not item.get("command"):
            errors.append("line %d needs step and command" % number)
        if not isinstance(item.get("exit_code"), int):
            errors.append("line %d needs integer exit_code" % number)
        if not item.get("assertion"):
            errors.append("line %d needs assertion" % number)
    return [result("evidence.ledger", "fail" if errors else "pass",
                   "evidence ledger is invalid" if errors else "evidence ledger is valid",
                   errors or [str(ledger)])]


def acceptance_scenarios(body: str) -> List[Dict[str, Any]]:
    heading = re.search(r"^##+\s+.*行为增量.*$", body, re.MULTILINE)
    if not heading:
        return []
    next_heading = re.search(r"^##(?!#)\s+", body[heading.end():], re.MULTILINE)
    section = body[heading.end():heading.end() + next_heading.start()] if next_heading else body[heading.end():]
    scenarios: List[Dict[str, Any]] = []
    for index, match in enumerate(re.finditer(r"^\s*[-*]\s+(?:\[[ xX]\]\s*)?(.+)$", section, re.MULTILINE), 1):
        text = match.group(1).strip()
        if text and not text.startswith("###"):
            scenarios.append({"id": "scenario-%02d" % index, "item": text, "status": "pending"})
    return scenarios


def archive_plan(root: Path, change_dir: Path) -> Dict[str, Any]:
    meta, body, error = read_file(change_dir / "change.md")
    if error:
        return {"ready": False, "error": error}
    contract = meta.get("contract") if isinstance(meta.get("contract"), dict) else {}
    merge: List[str] = []
    for topic, directory in (("architecture", "architecture"), ("requirement", "requirements")):
        if contract.get(topic + "_impact") == "update":
            for ref in contract.get(topic + "_refs") or []:
                value = str(ref)
                if not value.startswith(".codestable/"):
                    value = ".codestable/%s/%s" % (directory, value.replace(directory + "/", "", 1))
                merge.append(value)
    return {"ready": meta.get("status") == "accepted",
            "source": str((change_dir / "change.md").relative_to(root)),
            "merge_targets": merge,
            "behavior_scenarios": acceptance_scenarios(body)}


def build_changes_index(root: Path) -> List[Dict[str, Any]]:
    changes = root / ".codestable" / "changes"
    entries: List[Dict[str, Any]] = []
    for change_file in sorted(changes.glob("*/change.md")) if changes.is_dir() else []:
        meta, _, error = read_file(change_file)
        if error or meta.get("doc_type") != "change":
            continue
        entry = {"slug": str(meta.get("slug", change_file.parent.name)),
                 "path": str(change_file.relative_to(root)),
                 "kind": meta.get("kind"), "status": meta.get("status"),
                 "phase": meta.get("phase"),
                 "risk": risk_for_change(meta)["level"]}
        entry["next"] = next_action(root, change_file.parent).get("skill")
        entries.append(entry)
    return entries


def write_changes_index(root: Path, entries: List[Dict[str, Any]]) -> Path:
    target = root / ".codestable" / "changes" / "index.yaml"
    target.parent.mkdir(parents=True, exist_ok=True)
    lines = ["version: 1", "changes:"]
    for entry in entries:
        lines.append("  - slug: %s" % json.dumps(str(entry.get("slug", "")), ensure_ascii=False))
        for key in ("path", "kind", "status", "phase", "risk", "next"):
            value = entry.get(key)
            if value is not None:
                lines.append("    %s: %s" % (key, json.dumps(str(value), ensure_ascii=False)))
    target.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return target


def find_one(directory: Path, suffix: str) -> Optional[Path]:
    files = sorted(directory.glob("*" + suffix)) if directory.is_dir() else []
    return files[0] if files else None


def check_attention(root: Path) -> List[Result]:
    path = root / ".codestable" / "attention.md"
    if not path.exists():
        return [result("attention.present", "warn", "attention.md is missing; run cs-onboard")]
    meta, _, error = read_file(path)
    if error:
        return [result("attention.frontmatter", "warn", error, [str(path)])]
    mode = meta.get("workflow_mode")
    checks = []
    if mode not in (None, "lean", "standard"):
        checks.append(result("attention.workflow_mode", "fail", "workflow_mode must be lean or standard"))
    else:
        checks.append(result("attention.workflow_mode", "pass", "workflow_mode is valid"))
    standards = meta.get("standards")
    if standards is not None and not isinstance(standards, dict):
        checks.append(result("attention.standards", "fail", "standards must be a mapping"))
    elif standards is None:
        checks.append(result("attention.standards", "warn", "no structured standards block; only prose rules can be checked"))
    else:
        checks.append(result("attention.standards", "pass", "structured standards loaded"))
        checks.extend(check_project_standards(root, standards))
    return checks


def check_project_standards(root: Path, standards: Dict[str, Any]) -> List[Result]:
    changed, diff_error = git_changed_files(root)
    checks: List[Result] = []
    if diff_error:
        checks.append(result("standards.git_diff", "warn", diff_error))
        changed = []
    list_fields = ("forbidden_paths", "required_files", "required_terms", "forbidden_terms")
    for field in list_fields:
        if field in standards and not isinstance(standards[field], list):
            checks.append(result("standards.%s.shape" % field, "fail", "%s must be a list" % field))
    forbidden_paths = standards.get("forbidden_paths") if isinstance(standards.get("forbidden_paths"), list) else []
    forbidden = [path for path in changed if any(fnmatch.fnmatch(path, pattern) for pattern in forbidden_paths)]
    checks.append(result("standards.forbidden_paths", "fail" if forbidden else "pass",
                         "forbidden project paths changed" if forbidden else "no forbidden project paths changed",
                         forbidden))
    required_files = standards.get("required_files") if isinstance(standards.get("required_files"), list) else []
    missing_files = [path for path in required_files if not (root / path).exists()]
    checks.append(result("standards.required_files", "fail" if missing_files else "pass",
                         "required project files missing" if missing_files else "required project files exist",
                         missing_files or required_files))
    contents = ""
    for relative in changed:
        path = root / relative
        if path.is_file():
            try:
                contents += "\n" + path.read_text(encoding="utf-8")
            except OSError:
                pass
    required_terms = standards.get("required_terms") if isinstance(standards.get("required_terms"), list) else []
    missing_terms = [term for term in required_terms if term not in contents]
    checks.append(result("standards.required_terms", "warn" if missing_terms else "pass",
                         "required terms were not found in changed files" if missing_terms else "required terms found",
                         missing_terms or required_terms))
    forbidden_terms = standards.get("forbidden_terms") if isinstance(standards.get("forbidden_terms"), list) else []
    found_terms = [term for term in forbidden_terms if term in contents]
    checks.append(result("standards.forbidden_terms", "fail" if found_terms else "pass",
                         "forbidden terms found in changed files" if found_terms else "no forbidden terms found",
                         found_terms))
    commands = standards.get("required_commands") or {}
    if not isinstance(commands, dict):
        checks.append(result("standards.required_commands.shape", "fail", "required_commands must be a mapping"))
    elif commands:
        checks.append(result("standards.required_commands", "pass",
                             "required commands are declared; acceptance checks recorded evidence",
                             [str(value) for value in commands.values()]))
    path_rules = standards.get("path_rules") or {}
    if not isinstance(path_rules, dict):
        checks.append(result("standards.path_rules", "fail", "path_rules must be a mapping"))
    else:
        checks.extend(check_path_rules(root, changed, path_rules))
    return checks


def check_path_rules(root: Path, changed: List[str], rules: Dict[str, Any]) -> List[Result]:
    checks: List[Result] = []
    for rule_id, value in rules.items():
        if not isinstance(value, dict):
            checks.append(result("standards.path_rule.%s" % rule_id, "fail", "path rule must be a mapping"))
            continue
        patterns = value.get("files") or []
        if not isinstance(patterns, list) or not patterns:
            checks.append(result("standards.path_rule.%s" % rule_id, "fail",
                                 "path rule needs a non-empty files list"))
            continue
        matched = [path for path in changed
                   if any(fnmatch.fnmatch(path, str(pattern)) for pattern in patterns)]
        contents: Dict[str, str] = {}
        for relative in matched:
            try:
                contents[relative] = (root / relative).read_text(encoding="utf-8")
            except OSError:
                contents[relative] = ""
        forbidden_terms = value.get("forbidden_terms") or []
        required_terms = value.get("required_terms") or []
        if not isinstance(forbidden_terms, list) or not isinstance(required_terms, list):
            checks.append(result("standards.path_rule.%s" % rule_id, "fail",
                                 "required_terms and forbidden_terms must be lists"))
            continue
        violations = ["%s: %s" % (path, term) for path, content in contents.items()
                      for term in forbidden_terms if str(term) in content]
        combined = "\n".join(contents.values())
        missing = [str(term) for term in required_terms if str(term) not in combined] if matched else []
        evidence = violations + ["missing: %s" % term for term in missing]
        checks.append(result("standards.path_rule.%s" % rule_id, "fail" if evidence else "pass",
                             "path-scoped project rule violated" if evidence else "path-scoped project rule passed",
                             evidence or matched))
    return checks


def check_checklist(root: Path, feature_dir: Path) -> List[Result]:
    checks: List[Result] = []
    design = find_one(feature_dir, "-design.md")
    checklist = find_one(feature_dir, "-checklist.yaml")
    if design is None:
        checks.append(result("feature.design", "warn", "design file not found", [str(feature_dir)]))
        return checks
    if checklist is None:
        checks.append(result("feature.checklist", "warn", "checklist.yaml not found", [str(feature_dir)]))
        return checks

    design_meta, _, design_error = read_file(design)
    if design_error:
        checks.append(result("feature.design.frontmatter", "fail", design_error, [str(design)]))
    elif design_meta.get("doc_type") != "feature-design":
        checks.append(result("feature.design.doc_type", "fail", "unexpected design doc_type", [str(design)]))
    else:
        checks.append(result("feature.design.frontmatter", "pass", "design frontmatter is readable"))

    data, yaml_error = load_yaml_file(checklist)
    if yaml_error or data is None:
        checks.append(result("feature.checklist.yaml", "fail", yaml_error or "invalid YAML", [str(checklist)]))
        return checks

    expected_feature = data.get("feature")
    actual_feature = feature_dir.name
    if expected_feature and expected_feature != actual_feature:
        checks.append(result("feature.slug_match", "fail", "checklist feature does not match directory", [str(checklist)]))
    else:
        checks.append(result("feature.slug_match", "pass", "feature identity matches"))

    steps = data.get("steps")
    item_checks = data.get("checks")
    if not isinstance(steps, list) or not isinstance(item_checks, list):
        checks.append(result("feature.checklist.shape", "fail", "steps and checks must be lists", [str(checklist)]))
        return checks

    step_statuses = {"pending", "in-progress", "done", "completed", "blocked"}
    check_statuses = {"pending", "in-progress", "done", "passed", "failed", "skipped"}
    # Keep the original six source names and accept the names already used by
    # older checklists, so introducing the checker does not invalidate history.
    sources = {"名词契约", "编排骨架", "流程级约束", "挂载点", "范围守护", "验收场景",
               "接口契约", "测试约束"}
    errors = []
    for index, step in enumerate(steps, 1):
        if not isinstance(step, dict) or not step.get("action") or not step.get("exit_signal"):
            errors.append("step %d missing action or exit_signal" % index)
        elif step.get("status", "pending") not in step_statuses:
            errors.append("step %d has invalid status %r" % (index, step.get("status")))
    for index, item in enumerate(item_checks, 1):
        if not isinstance(item, dict) or not item.get("item"):
            errors.append("check %d missing item" % index)
        else:
            if item.get("status", "pending") not in check_statuses:
                errors.append("check %d has invalid status %r" % (index, item.get("status")))
            if item.get("source") not in sources:
                errors.append("check %d has invalid source %r" % (index, item.get("source")))
    if errors:
        checks.append(result("feature.checklist.semantic", "fail", "; ".join(errors), [str(checklist)]))
    else:
        checks.append(result("feature.checklist.semantic", "pass", "steps and checks are structurally valid"))

    contract = data.get("contract")
    if contract is not None and not isinstance(contract, dict):
        checks.append(result("feature.contract", "fail", "contract must be a mapping", [str(checklist)]))
    elif contract is None:
        checks.append(result("feature.contract", "warn", "no scope/architecture contract; diff boundary cannot be checked"))
    else:
        checks.append(result("feature.contract", "pass", "scope contract is available"))
        checks.extend(check_diff_contract(root, contract))
    checks.extend(check_roadmap_link(root, feature_dir, design_meta))
    return checks


def check_change(root: Path, change_dir: Path, phase: Optional[str], converge: bool = False,
                 archive: bool = False) -> List[Result]:
    """Validate the single-file Change Package format."""
    change_file = change_dir / "change.md"
    if not change_file.exists():
        return [result("change.file", "fail", "change.md not found", [str(change_dir)])]
    meta, body, error = read_file(change_file)
    checks: List[Result] = []
    if error:
        checks.append(result("change.frontmatter", "fail", error, [str(change_file)]))
        return checks
    if meta.get("doc_type") != "change":
        checks.append(result("change.doc_type", "fail", "doc_type must be change", [str(change_file)]))
    else:
        checks.append(result("change.doc_type", "pass", "change doc_type is valid"))
    kind = meta.get("kind")
    if kind not in {"feature", "issue", "refactor", "audit"}:
        checks.append(result("change.kind", "fail", "kind must be feature, issue, refactor or audit"))
    else:
        checks.append(result("change.kind", "pass", "change kind is valid"))
    mode = meta.get("mode")
    if mode not in {"lean", "standard"}:
        checks.append(result("change.mode", "fail", "mode must be lean or standard"))
    else:
        checks.append(result("change.mode", "pass", "change mode is valid"))
    statuses = {"draft", "approved", "in-progress", "accepted", "closed"}
    status = meta.get("status")
    if status not in statuses:
        checks.append(result("change.status", "fail", "invalid change status %r" % status))
    else:
        checks.append(result("change.status", "pass", "change status is valid"))
    checks.extend(check_risk_schema(meta, body))
    workflow, workflow_error, workflow_path = load_workflow(root)
    if workflow_error or workflow is None:
        checks.append(result("workflow.load", "fail", workflow_error or "workflow.yaml is invalid",
                             [str(workflow_path)]))
        required_sections = []
    else:
        checks.append(result("workflow.load", "pass", "workflow definition loaded", [str(workflow_path)]))
        required_sections = workflow_required_sections(workflow, kind, status)
        checks.extend(workflow_state_check(root, change_file, workflow, kind, status, meta.get("phase")))
    missing_sections = [section for section in required_sections
                        if not re.search(r"^##+\s+.*" + re.escape(section), body, re.MULTILINE | re.IGNORECASE)]
    checks.append(result("change.sections", "fail" if missing_sections else "pass",
                         "required change sections missing" if missing_sections else "required change sections present",
                         missing_sections))
    if kind != "audit" and status in {"approved", "in-progress", "accepted", "closed"}:
        checks.extend(check_behavior_delta(body, converge))
    contract = meta.get("contract")
    contract_required = workflow_contract_required(workflow or {}, phase, kind)
    if contract is None:
        status_value = "fail" if contract_required else "warn"
        checks.append(result("change.contract", status_value,
                             "frontmatter contract is required for this phase" if contract_required
                             else "no frontmatter contract; git diff boundary cannot be checked"))
    elif isinstance(contract, dict):
        checks.append(result("change.contract", "pass", "change contract is available"))
        checks.extend(check_contract_schema(contract, phase))
        checks.extend(baseline_checks(root, contract))
        checks.extend(check_artifact_graph(root, contract, converge=converge or archive))
        checks.extend(check_evidence_ledger(root, change_dir, contract, converge=converge or archive))
        checks.extend(check_diff_contract(root, contract, phase))
    else:
        checks.append(result("change.contract", "fail", "contract must be a mapping"))
    checks.extend(check_roadmap_link(root, change_dir, meta, require_done=converge))
    if phase == "accept":
        checks.extend(check_required_command_evidence(root, body))
    if phase in ("impl", "accept") and status not in {"approved", "in-progress", "accepted", "closed"}:
        checks.append(result("change.phase", "fail", "change must be approved or active before %s" % phase))
    checks_file = change_dir / "checks.yaml"
    if checks_file.exists():
        data, yaml_error = load_yaml_file(checks_file)
        if yaml_error or data is None:
            checks.append(result("change.checks_yaml", "fail", yaml_error or "invalid checks.yaml", [str(checks_file)]))
        else:
            steps = data.get("steps")
            items = data.get("checks")
            if not isinstance(steps, list) or not isinstance(items, list):
                checks.append(result("change.checks_shape", "fail", "checks.yaml needs steps and checks lists", [str(checks_file)]))
            else:
                checks.append(result("change.checks_shape", "pass", "checks.yaml shape is valid"))
                semantic_errors = []
                for index, step in enumerate(steps, 1):
                    if not isinstance(step, dict) or not step.get("action") or not step.get("exit_signal"):
                        semantic_errors.append("step %d missing action or exit_signal" % index)
                    elif step.get("status", "pending") not in {"pending", "in-progress", "done", "completed", "blocked"}:
                        semantic_errors.append("step %d has invalid status" % index)
                for index, item in enumerate(items, 1):
                    if not isinstance(item, dict) or not item.get("item"):
                        semantic_errors.append("check %d missing item" % index)
                    elif item.get("status", "pending") not in {"pending", "in-progress", "done", "passed", "failed", "skipped"}:
                        semantic_errors.append("check %d has invalid status" % index)
                checks.append(result("change.checks_semantic", "fail" if semantic_errors else "pass",
                                     "; ".join(semantic_errors) if semantic_errors else "checks.yaml semantics are valid",
                                     [str(checks_file)] if semantic_errors else None))
                graph_errors, waves = step_waves(steps)
                checks.append(result("change.step_graph", "fail" if graph_errors else "pass",
                                     "; ".join(graph_errors) if graph_errors else "step dependency graph is valid",
                                     graph_errors or ["wave %d: %s" % (index, ", ".join(wave))
                                                      for index, wave in enumerate(waves, 1)]))
                if converge:
                    incomplete_steps = [str(step.get("id") or "step-%d" % index)
                                        for index, step in enumerate(steps, 1)
                                        if isinstance(step, dict) and step.get("status", "pending") not in {"done", "completed"}]
                    incomplete_checks = [str(item.get("id") or "check-%d" % index)
                                         for index, item in enumerate(items, 1)
                                         if isinstance(item, dict) and item.get("status", "pending") not in {"passed", "skipped"}]
                    incomplete = incomplete_steps + incomplete_checks
                    checks.append(result("change.checks_complete", "fail" if incomplete else "pass",
                                         "steps/checks are incomplete" if incomplete else "steps/checks are complete",
                                         incomplete))
    if converge:
        checks.extend(check_embedded_task_completion(body))
    if archive:
        checks.append(result("change.archive_state", "pass" if status == "accepted" else "fail",
                             "change is ready to archive" if status == "accepted" else "only accepted changes can be archived",
                             [str(status)]))
    return checks


def check_behavior_delta(body: str, converge: bool) -> List[Result]:
    heading = re.search(r"^##+\s+.*行为增量.*$", body, re.MULTILINE)
    if not heading:
        return [result("change.delta", "fail", "behavior delta section is missing")]
    next_heading = re.search(r"^##(?!#)\s+", body[heading.end():], re.MULTILINE)
    end = heading.end() + next_heading.start() if next_heading else len(body)
    delta = body[heading.end():end]
    explicit = re.search(r"\b(?:ADDED|MODIFIED|REMOVED)\b", delta, re.IGNORECASE) or re.search(r"无|none", delta, re.IGNORECASE)
    status = "fail" if converge and not explicit else ("warn" if not explicit else "pass")
    return [result("change.delta", status,
                   "behavior delta needs ADDED/MODIFIED/REMOVED or an explicit none"
                   if not explicit else "behavior delta is explicit")]


def step_waves(steps: List[Any]) -> Tuple[List[str], List[List[str]]]:
    errors: List[str] = []
    ids: List[str] = []
    dependencies: Dict[str, List[str]] = {}
    for index, step in enumerate(steps, 1):
        if not isinstance(step, dict):
            continue
        step_id = str(step.get("id") or "step-%d" % index)
        if step_id in ids:
            errors.append("duplicate step id %s" % step_id)
            continue
        ids.append(step_id)
        depends_on = step.get("depends_on") or []
        if not isinstance(depends_on, list):
            errors.append("step %s depends_on must be a list" % step_id)
            depends_on = []
        dependencies[step_id] = [str(item) for item in depends_on]
    known = set(ids)
    for step_id, deps in dependencies.items():
        missing = [dep for dep in deps if dep not in known]
        if missing:
            errors.append("step %s has unknown dependencies %s" % (step_id, ", ".join(missing)))
    if errors:
        return errors, []
    remaining = set(ids)
    completed: set = set()
    waves: List[List[str]] = []
    while remaining:
        wave = sorted(step_id for step_id in remaining
                      if set(dependencies.get(step_id, [])).issubset(completed))
        if not wave:
            errors.append("step dependency cycle detected: %s" % ", ".join(sorted(remaining)))
            return errors, waves
        waves.append(wave)
        completed.update(wave)
        remaining.difference_update(wave)
    return [], waves


def check_embedded_task_completion(body: str) -> List[Result]:
    pending = re.findall(r"^\s*[-*]\s*\[\s\]\s*(.+)$", body, re.MULTILINE)
    completed = re.findall(r"^\s*[-*]\s*\[[xX]\]\s*(.+)$", body, re.MULTILINE)
    if pending:
        return [result("change.tasks_complete", "fail", "embedded tasks are incomplete", pending[:50])]
    if completed:
        return [result("change.tasks_complete", "pass", "embedded tasks are complete",
                       ["%d completed" % len(completed)])]
    return [result("change.tasks_complete", "warn", "no embedded task checkboxes found")]


def check_required_command_evidence(root: Path, body: str) -> List[Result]:
    commands: Dict[str, Any] = {}
    attention = root / ".codestable" / "attention.md"
    if attention.exists():
        meta, _, error = read_file(attention)
        if not error and isinstance(meta.get("standards"), dict):
            values = meta["standards"].get("required_commands") or {}
            if isinstance(values, dict):
                commands.update(values)
    constitution = root / ".codestable" / "constitution.yaml"
    if constitution.exists():
        data, error = load_yaml_file(constitution)
        values = data.get("required_commands") if not error and isinstance(data, dict) else {}
        if isinstance(values, dict):
            commands.update(values)
    if not commands:
        return []
    evidence_heading = re.search(r"^##+\s+.*(?:执行证据|验收结果).*$", body, re.MULTILINE)
    evidence_text = body[evidence_heading.start():] if evidence_heading else ""
    missing = ["%s: %s" % (name, command) for name, command in commands.items()
               if str(command) not in evidence_text]
    return [result("standards.command_evidence", "fail" if missing else "pass",
                   "required command evidence missing" if missing else "required command evidence is recorded",
                   missing or [str(value) for value in commands.values()])]


def check_roadmap_link(root: Path, feature_dir: Path, design_meta: Dict[str, Any],
                       require_done: bool = False) -> List[Result]:
    roadmap = design_meta.get("roadmap")
    item_slug = design_meta.get("roadmap_item")
    if not roadmap and not item_slug:
        return []
    if not roadmap or not item_slug:
        return [result("roadmap.link", "fail", "roadmap and roadmap_item must be provided together")]
    items_file = root / ".codestable" / "roadmap" / str(roadmap) / (str(roadmap) + "-items.yaml")
    data, error = load_yaml_file(items_file)
    if error or data is None:
        return [result("roadmap.link", "fail", error or "roadmap items file is invalid", [str(items_file)])]
    items = data.get("items")
    if not isinstance(items, list):
        return [result("roadmap.items", "fail", "items must be a list", [str(items_file)])]
    matches = [item for item in items if isinstance(item, dict) and item.get("slug") == item_slug]
    if not matches:
        return [result("roadmap.item", "fail", "roadmap item was not found", [str(items_file), str(item_slug)])]
    item = matches[0]
    status = item.get("status")
    if status not in {"planned", "in-progress", "done", "dropped"}:
        return [result("roadmap.status", "fail", "invalid roadmap item status %r" % status, [str(items_file)])]
    feature_value = item.get("feature")
    if status in {"in-progress", "done"} and feature_value not in (None, feature_dir.name):
        return [result("roadmap.feature_link", "fail", "roadmap item points to another feature", [str(feature_value), feature_dir.name])]
    if require_done and status != "done":
        return [result("roadmap.completion", "fail", "roadmap item must be done before converge",
                       [str(items_file), str(item_slug), str(status)])]
    return [result("roadmap.link", "pass", "roadmap item and feature link are valid", [str(items_file), str(item_slug)])]


def git_changed_files(root: Path) -> Tuple[List[str], Optional[str]]:
    try:
        proc = subprocess.run(
            ["git", "diff", "HEAD", "--name-only", "--diff-filter=ACMRT"],
            cwd=str(root), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            universal_newlines=True, check=False,
        )
    except OSError as exc:
        return [], str(exc)
    if proc.returncode != 0:
        head = subprocess.run(
            ["git", "rev-parse", "--verify", "HEAD"], cwd=str(root),
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, universal_newlines=True, check=False,
        )
        if head.returncode == 0:
            return [], proc.stderr.strip() or "git diff failed"
        initial = subprocess.run(
            ["git", "ls-files", "--cached", "--others", "--exclude-standard"],
            cwd=str(root), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            universal_newlines=True, check=False,
        )
        if initial.returncode != 0:
            return [], initial.stderr.strip() or "git ls-files failed"
        files = [line.strip() for line in initial.stdout.splitlines() if line.strip()]
        return sorted(set(files)), None
    changed = [line.strip() for line in proc.stdout.splitlines() if line.strip()]
    try:
        untracked_proc = subprocess.run(
            ["git", "ls-files", "--others", "--exclude-standard"],
            cwd=str(root), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            universal_newlines=True, check=False,
        )
    except OSError as exc:
        return changed, str(exc)
    if untracked_proc.returncode != 0:
        return changed, untracked_proc.stderr.strip() or "git ls-files failed"
    untracked = [line.strip() for line in untracked_proc.stdout.splitlines() if line.strip()]
    return sorted(set(changed + untracked)), None


def check_contract_schema(contract: Dict[str, Any], phase: Optional[str]) -> List[Result]:
    checks: List[Result] = []
    include = contract.get("include")
    if not isinstance(include, list) or not include:
        checks.append(result("contract.include", "fail", "contract.include must be a non-empty list"))
    else:
        checks.append(result("contract.include", "pass", "contract include scope is explicit", [str(item) for item in include]))

    exclude = contract.get("exclude")
    if exclude is not None and not isinstance(exclude, list):
        checks.append(result("contract.exclude", "fail", "contract.exclude must be a list"))
    preexisting = contract.get("preexisting_changes")
    if preexisting is not None and not isinstance(preexisting, list):
        checks.append(result("contract.preexisting_changes", "fail",
                             "contract.preexisting_changes must be a list"))
    baseline = contract.get("baseline")
    if baseline is not None and not isinstance(baseline, dict):
        checks.append(result("contract.baseline", "fail", "contract.baseline must be a mapping"))

    checks.extend(check_impact_schema(contract, "architecture"))
    checks.extend(check_impact_schema(contract, "requirement"))
    context_refs = contract.get("context_refs") or {}
    if not isinstance(context_refs, dict):
        checks.append(result("contract.context_refs", "fail", "context_refs must be a mapping"))
    else:
        invalid: List[str] = []
        for name, refs in context_refs.items():
            if name not in {"design", "impl", "accept"} or not isinstance(refs, list):
                invalid.append(str(name))
                continue
            for index, ref in enumerate(refs, 1):
                if isinstance(ref, str):
                    continue
                if not isinstance(ref, dict) or not isinstance(ref.get("path"), str):
                    invalid.append("%s[%d]" % (name, index))
                    continue
                for field in ("headings", "symbols"):
                    if field in ref and not isinstance(ref[field], list):
                        invalid.append("%s[%d].%s" % (name, index, field))
        checks.append(result("contract.context_refs", "fail" if invalid else "pass",
                             "context_refs has invalid phase/list entries" if invalid else "context_refs is valid",
                             [str(item) for item in invalid]))
    return checks


def check_impact_schema(contract: Dict[str, Any], topic: str) -> List[Result]:
    impact = contract.get(topic + "_impact")
    refs = contract.get(topic + "_refs") or []
    reason = contract.get(topic + "_reason")
    allowed = {"unchanged", "update", "not-applicable"}
    checks: List[Result] = []
    if impact not in allowed:
        return [result("%s.impact" % topic, "fail",
                       "%s_impact must be unchanged, update or not-applicable" % topic)]
    checks.append(result("%s.impact" % topic, "pass", "%s impact is explicit" % topic, [str(impact)]))
    if not isinstance(refs, list):
        checks.append(result("%s.refs_shape" % topic, "fail", "%s_refs must be a list" % topic))
    elif impact == "update" and not refs:
        checks.append(result("%s.update_refs" % topic, "fail",
                             "%s update requires at least one %s_refs entry" % (topic, topic)))
    if impact in {"unchanged", "not-applicable"} and not reason:
        checks.append(result("%s.reason" % topic, "fail",
                             "unchanged/not-applicable %s impact requires %s_reason" % (topic, topic)))
    return checks


def check_diff_contract(root: Path, contract: Dict[str, Any], phase: Optional[str] = None) -> List[Result]:
    changed, error = git_changed_files(root)
    if error:
        return [result("git.diff", "warn", error)]
    include = contract.get("include") or ["**"]
    exclude = contract.get("exclude") or []
    current = effective_changed_files(root, changed, contract)
    outside = [path for path in current if not any(fnmatch.fnmatch(path, pattern) for pattern in include)]
    forbidden = [path for path in current if any(fnmatch.fnmatch(path, pattern) for pattern in exclude)]
    checks = [result("git.diff.scope", "fail" if outside else "pass",
                     "changed files outside contract" if outside else "changed files are within contract",
                     outside or current)]
    checks.append(result("git.diff.exclude", "fail" if forbidden else "pass",
                         "forbidden paths changed" if forbidden else "no forbidden paths changed",
                         forbidden or []))
    checks.extend(check_impact_files(root, current, contract, "architecture", phase))
    checks.extend(check_impact_files(root, current, contract, "requirement", phase))
    contents = ""
    for relative in current:
        path = root / relative
        if path.is_file():
            try:
                contents += "\n" + path.read_text(encoding="utf-8")
            except OSError:
                pass
    required_terms = contract.get("required_terms") or []
    missing_terms = [term for term in required_terms if term not in contents]
    checks.append(result("contract.required_terms", "warn" if missing_terms else "pass",
                         "required contract terms were not found" if missing_terms else "required contract terms found",
                         missing_terms or required_terms))
    forbidden_terms = contract.get("forbidden_terms") or []
    found_terms = [term for term in forbidden_terms if term in contents]
    checks.append(result("contract.forbidden_terms", "fail" if found_terms else "pass",
                         "forbidden contract terms found" if found_terms else "no forbidden contract terms found",
                         found_terms))
    return checks


def effective_changed_files(root: Path, changed: List[str], contract: Dict[str, Any]) -> List[str]:
    """Exclude unchanged preexisting files, but keep files modified after baseline."""
    preexisting = set(str(path) for path in (contract.get("preexisting_changes") or []))
    baseline = contract.get("baseline") if isinstance(contract.get("baseline"), dict) else {}
    hashes = baseline.get("dirty_hashes") if isinstance(baseline.get("dirty_hashes"), dict) else {}
    current: List[str] = []
    for path in changed:
        if path not in preexisting or str(hashes.get(path)) != file_digest(root / path):
            current.append(path)
    return current


def check_impact_files(root: Path, changed: List[str], contract: Dict[str, Any],
                       topic: str, phase: Optional[str]) -> List[Result]:
    refs = contract.get(topic + "_refs") or []
    directory = "architecture" if topic == "architecture" else "requirements"
    prefix = directory + "/"
    expected: List[str] = []
    missing: List[str] = []
    for ref in refs if isinstance(refs, list) else []:
        relative = str(ref)
        if relative.startswith(prefix):
            relative = relative[len(prefix):]
        project_path = ".codestable/%s/%s" % (directory, relative)
        expected.append(project_path)
        if not (root / project_path).exists():
            missing.append(str(ref))
    checks = [result("%s.refs" % topic, "fail" if missing else "pass",
                     "%s references missing" % topic if missing else "%s references exist" % topic,
                     missing or [str(ref) for ref in refs] if isinstance(refs, list) else [])]
    if phase == "accept" and contract.get(topic + "_impact") == "update":
        updated = [path for path in changed if path in expected]
        checks.append(result("%s.updated" % topic, "fail" if not updated else "pass",
                             "%s update was declared but referenced documents did not change" % topic
                             if not updated else "declared %s update is present" % topic,
                             updated or expected))
    return checks


def check_architecture(root: Path, architecture_dir: Optional[Path]) -> List[Result]:
    if architecture_dir is None:
        architecture_dir = root / ".codestable" / "architecture"
    if not architecture_dir.is_dir():
        return [result("architecture.directory", "warn", "architecture directory not found", [str(architecture_dir)])]
    checks: List[Result] = []
    docs = sorted(architecture_dir.rglob("*.md"))
    missing_anchors: List[str] = []
    for doc in docs:
        _, body, error = read_file(doc)
        if error:
            checks.append(result("architecture.read", "fail", error, [str(doc)]))
            continue
        for target, line in re.findall(r"`([^`\n]+):(\d+)`", body):
            if "://" in target:
                continue
            target_path = root / target
            if not target_path.exists():
                missing_anchors.append("%s -> %s:%s" % (doc, target, line))
            else:
                try:
                    line_number = int(line)
                    total = len(target_path.read_text(encoding="utf-8").splitlines())
                    if line_number < 1 or line_number > total:
                        missing_anchors.append("%s -> %s:%s (line out of range)" % (doc, target, line))
                except (OSError, ValueError):
                    missing_anchors.append("%s -> %s:%s" % (doc, target, line))
    checks.append(result("architecture.anchors", "fail" if missing_anchors else "pass",
                         "invalid architecture code anchors" if missing_anchors else "architecture code anchors are valid",
                         missing_anchors[:50]))
    return checks


def normalize_context_ref(ref: Any) -> Dict[str, Any]:
    if isinstance(ref, str):
        return {"path": ref}
    if isinstance(ref, dict):
        item = {"path": str(ref.get("path", ""))}
        for field in ("headings", "symbols"):
            if isinstance(ref.get(field), list):
                item[field] = [str(value) for value in ref[field]]
        return item
    return {"path": ""}


def context_for_change(root: Path, change_dir: Path, phase: str) -> Tuple[List[Dict[str, Any]], List[str]]:
    change_file = change_dir / "change.md"
    meta, _, error = read_file(change_file)
    if error:
        return [], [str(change_file)]
    contract = meta.get("contract") if isinstance(meta.get("contract"), dict) else {}
    refs: List[Any] = []
    attention = root / ".codestable" / "attention.md"
    if attention.exists():
        refs.append(str(attention.relative_to(root)))
    refs.append(str(change_file.relative_to(root)))
    context_refs = contract.get("context_refs") or {}
    if isinstance(context_refs, dict) and isinstance(context_refs.get(phase), list):
        refs.extend(context_refs[phase])
    for topic in ("architecture", "requirement"):
        directory = "architecture" if topic == "architecture" else "requirements"
        topic_refs = contract.get(topic + "_refs") or []
        if isinstance(topic_refs, list):
            for ref in topic_refs:
                relative = str(ref.get("path") if isinstance(ref, dict) else ref)
                if relative.startswith(directory + "/"):
                    relative = relative[len(directory) + 1:]
                refs.append(".codestable/%s/%s" % (directory, relative))
    unique: List[Dict[str, Any]] = []
    seen: set = set()
    for ref in refs:
        item = normalize_context_ref(ref)
        path = str(Path(item.get("path", "")))
        if not path or path in seen:
            continue
        item["path"] = path
        seen.add(path)
        unique.append(item)
    missing = [item["path"] for item in unique if not (root / item["path"]).exists()]
    return unique, missing


def checks_are_ready(change_dir: Path, body: str) -> bool:
    checks_file = change_dir / "checks.yaml"
    if checks_file.exists():
        data, error = load_yaml_file(checks_file)
        if error or not isinstance(data, dict):
            return False
        steps = data.get("steps") or []
        checks = data.get("checks") or []
        return (isinstance(steps, list) and isinstance(checks, list) and bool(steps or checks)
                and all(isinstance(step, dict) and step.get("status") in {"done", "completed"} for step in steps)
                and all(isinstance(item, dict) and item.get("status") in {"passed", "skipped"} for item in checks))
    pending = re.search(r"^\s*[-*]\s*\[\s\]", body, re.MULTILINE)
    completed = re.search(r"^\s*[-*]\s*\[[xX]\]", body, re.MULTILINE)
    return pending is None and completed is not None and bool(re.search(r"^##+\s+.*执行证据", body, re.MULTILINE))


def next_action(root: Path, change_dir: Path) -> Dict[str, Any]:
    change_file = change_dir / "change.md"
    meta, body, error = read_file(change_file)
    if error:
        return {"skill": "cs", "reason": error, "status": "unknown"}
    workflow, workflow_error, _ = load_workflow(root)
    if workflow_error or workflow is None:
        return {"skill": "cs", "reason": workflow_error or "workflow unavailable", "status": "unknown"}
    kind = meta.get("kind")
    status = meta.get("status")
    phase = meta.get("phase")
    kind_config = (workflow.get("kinds") or {}).get(kind) or {}
    phase_map = kind_config.get("next_phase") or {}
    status_map = kind_config.get("next") or {}
    key = "ready" if status == "in-progress" and checks_are_ready(change_dir, body) else status
    skill = phase_map.get(phase) if phase in phase_map else status_map.get(key)
    risk = risk_for_change(meta, body)
    return {"skill": None if skill in (None, "none") else skill,
            "reason": "workflow %s/%s/%s" % (kind, phase or "-", key),
            "kind": kind, "status": status, "phase": phase, "ready": key == "ready",
            "risk": risk, "standard_required": risk["level"] == "high"}


def build_remediation(checks: List[Result]) -> List[Dict[str, Any]]:
    hints = {
        "git.diff.scope": ("scope.restrict", "撤销越界修改，或回到 design 扩大 include"),
        "git.diff.exclude": ("scope.exclude", "撤销对禁止路径的修改"),
        "architecture.updated": ("architecture.sync", "验收前更新全部 architecture_refs"),
        "requirement.updated": ("requirement.sync", "验收前更新全部 requirement_refs"),
        "roadmap.completion": ("roadmap.complete", "将关联 roadmap 条目标记 done 并校验 YAML"),
        "standards.command_evidence": ("evidence.command", "运行必需命令并记录命令、退出码和关键断言"),
        "change.tasks_complete": ("tasks.complete", "完成或明确移除未完成任务"),
        "change.checks_complete": ("checks.complete", "完成 pending steps/checks 并记录证据"),
        "change.delta": ("delta.record", "记录 ADDED/MODIFIED/REMOVED，或明确写无"),
        "baseline.required": ("baseline.capture", "先运行 --snapshot，将结果写入 contract.baseline"),
        "baseline.git_head": ("baseline.head", "恢复任务基线或记录新的任务包"),
    }
    remediation = []
    for item in checks:
        if item["status"] != "fail":
            continue
        code, action = hints.get(item["id"], ("check.fix", item["message"]))
        remediation.append({"check": item["id"], "code": code, "action": action,
                            "evidence": item.get("evidence", [])})
    return remediation


def compact_payload(status: str, root: Path, checks: List[Result],
                    next_step: Optional[Dict[str, Any]], context: List[Any],
                    remediation: List[Dict[str, Any]], waves: List[List[str]],
                    emit: Optional[str], context_budget: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    failures = [{"id": item["id"], "message": item["message"],
                 "evidence": item.get("evidence", [])} for item in checks
                if item.get("status") == "fail"]
    warnings = [item["id"] for item in checks if item.get("status") == "warn"]
    requested = {part.strip() for part in (emit or "status,next,context,failures,remediation,waves").split(",") if part.strip()}
    payload: Dict[str, Any] = {"status": status}
    if "next" in requested and next_step is not None:
        payload["next"] = next_step
    if "context" in requested:
        payload["context"] = context
        if context_budget:
            payload["context_budget"] = context_budget
    if "failures" in requested:
        payload["failures"] = failures
    if "warnings" in requested:
        payload["warnings"] = warnings
    if "remediation" in requested:
        payload["remediation"] = remediation
    if "waves" in requested and waves:
        payload["waves"] = waves
    return payload


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Check CodeStable semantic compliance contracts.")
    parser.add_argument("--root", default=".", help="Project root (default: current directory)")
    parser.add_argument("--feature", help="Feature directory to check")
    parser.add_argument("--change", help="Change Package directory to check")
    parser.add_argument("--architecture", help="Architecture directory to check")
    parser.add_argument("--phase", choices=("design", "impl", "accept"), help="Workflow phase for stricter checks")
    parser.add_argument("--next", action="store_true", dest="show_next", help="Return the next skill from workflow.yaml")
    parser.add_argument("--context", action="store_true", help="Return the minimal context file list for --phase")
    parser.add_argument("--converge", action="store_true", help="Run final completion gates and return remediation")
    parser.add_argument("--archive", action="store_true", help="Check accepted Change Package archive readiness")
    parser.add_argument("--index", action="store_true", help="Return a compact index of all Change Packages")
    parser.add_argument("--write-index", action="store_true", help="Write .codestable/changes/index.yaml (requires --index)")
    parser.add_argument("--scenarios", action="store_true", help="Generate acceptance scenarios from behavior delta")
    parser.add_argument("--check", action="store_true", help="Run phase/project compliance checks")
    parser.add_argument("--snapshot", action="store_true", help="Print a read-only task baseline snapshot")
    parser.add_argument("--snapshot-files", help="Comma-separated preexisting files to hash")
    parser.add_argument("--agent", action="store_true", help="Emit compact machine-oriented JSON")
    parser.add_argument("--emit", help="Compact fields: next,context,failures,warnings,remediation,waves")
    parser.add_argument("--profile", help="Model context profile name, e.g. constrained-27b-64k")
    parser.add_argument("--json", action="store_true", dest="as_json")
    parser.add_argument("--strict", action="store_true", help="Treat warnings as failures")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    root = Path(args.root).resolve()
    checks: List[Result] = []
    context: List[Any] = []
    context_budget: Dict[str, Any] = {}
    next_step: Optional[Dict[str, Any]] = None
    waves: List[List[str]] = []
    change_dir: Optional[Path] = None
    if args.snapshot:
        snapshot = baseline_snapshot(root)
        if args.snapshot_files:
            selected = {item.strip() for item in args.snapshot_files.split(",") if item.strip()}
            snapshot["dirty_hashes"] = {path: digest for path, digest in snapshot["dirty_hashes"].items()
                                        if path in selected}
        status = "warn" if snapshot.get("error") else "pass"
        payload = {"status": status, "baseline": snapshot}
        print(json.dumps(payload, ensure_ascii=False, indent=None if args.agent else 2))
        return 0 if status == "pass" else 1
    if args.index:
        entries = build_changes_index(root)
        written = str(write_changes_index(root, entries).relative_to(root)) if args.write_index else None
        payload = {"status": "pass", "changes": entries}
        if written:
            payload["written"] = written
        print(json.dumps(payload, ensure_ascii=False, indent=None if args.agent else 2))
        return 0
    if args.write_index:
        sys.stderr.write("--write-index requires --index\n")
        return 2
    if (args.show_next or args.context or args.converge or args.archive or args.scenarios) and not args.change:
        sys.stderr.write("--next, --context, --converge, --archive and --scenarios require --change\n")
        return 2
    if args.context and not args.phase:
        sys.stderr.write("--context requires --phase\n")
        return 2
    if args.converge and args.phase != "accept":
        sys.stderr.write("--converge requires --phase accept\n")
        return 2
    if args.archive and args.phase != "accept":
        sys.stderr.write("--archive requires --phase accept\n")
        return 2
    run_checks = (args.check or args.converge or args.archive or bool(args.feature)
                  or (bool(args.change) and not args.show_next and not args.context and not args.scenarios)
                  or (not args.change and not args.feature and not args.show_next and not args.context))
    if run_checks:
        checks.extend(check_attention(root))
        checks.extend(check_architecture(root, Path(args.architecture) if args.architecture else None))
        checks.extend(check_constitution(root))
        if (root / ".codestable" / "compound").is_dir():
            checks.extend(check_terminology_conflicts(root))
    if args.feature:
        feature_dir = Path(args.feature)
        if not feature_dir.is_absolute():
            feature_dir = root / feature_dir
        feature_checks = check_checklist(root, feature_dir)
        if args.phase in ("impl", "accept"):
            design = find_one(feature_dir, "-design.md")
            if design is not None:
                meta, _, _ = read_file(design)
                if meta.get("status") != "approved":
                    feature_checks.append(result("feature.phase", "fail", "design must be approved before impl/accept", [str(design)]))
        checks.extend(feature_checks)
    if args.change:
        change_dir = Path(args.change)
        if not change_dir.is_absolute():
            change_dir = root / change_dir
        if run_checks:
            checks.extend(check_change(root, change_dir, args.phase, converge=args.converge, archive=args.archive))
            checks_file = change_dir / "checks.yaml"
            if checks_file.exists():
                data, _ = load_yaml_file(checks_file)
                if isinstance(data, dict) and isinstance(data.get("steps"), list):
                    _, waves = step_waves(data["steps"])
        if args.context:
            context, missing_context = context_for_change(root, change_dir, args.phase)
            budget_check, context_budget = check_context_budget(root, context, args.profile,
                                                                 compact_output=args.agent)
            if run_checks:
                checks.append(result("context.files", "fail" if missing_context else "pass",
                                     "context references are missing" if missing_context else "context references exist",
                                     missing_context or [str(item) for item in context]))
                checks.append(budget_check)
            elif missing_context:
                checks.append(result("context.files", "fail", "context references are missing", missing_context))
            else:
                checks.append(budget_check)
        if args.show_next:
            next_step = next_action(root, change_dir)
            if next_step.get("status") == "unknown":
                checks.append(result("workflow.next", "fail", next_step.get("reason", "workflow next action unavailable")))
        if args.scenarios:
            _, body, error = read_file(change_dir / "change.md")
            scenarios = acceptance_scenarios(body) if not error else []
        else:
            scenarios = []
    failures = [item for item in checks if item["status"] == "fail"]
    warnings = [item for item in checks if item["status"] == "warn"]
    status = "fail" if failures or (args.strict and warnings) else ("warn" if warnings else "pass")
    payload = {"status": status, "root": str(root), "checks": checks,
               "summary": {"total": len(checks), "failed": len(failures), "warnings": len(warnings)}}
    if args.context:
        payload["context"] = context
        payload["context_budget"] = context_budget
    if args.show_next:
        payload["next"] = next_step
    if args.converge or args.archive:
        payload["remediation"] = build_remediation(checks)
    if args.archive and change_dir is not None:
        payload["archive"] = archive_plan(root, change_dir)
    if args.scenarios:
        payload["scenarios"] = scenarios
    if args.agent:
        compact_emit = args.emit or ("status" if args.scenarios else None)
        payload = compact_payload(status, root, checks, next_step, context,
                                   build_remediation(checks) if (args.converge or args.archive) else [], waves, compact_emit,
                                   context_budget)
        if args.scenarios:
            payload["scenarios"] = scenarios
        if args.archive and change_dir is not None:
            payload["archive"] = archive_plan(root, change_dir)
    if args.as_json:
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    elif args.agent:
        print(json.dumps(payload, ensure_ascii=False, indent=None))
    else:
        print("Compliance: %s (%d checks, %d failures, %d warnings)" %
              (status, len(checks), len(failures), len(warnings)))
        for item in checks:
            print("[%s] %s: %s" % (item["status"].upper(), item["id"], item["message"]))
            for evidence in item.get("evidence", []):
                print("  - %s" % evidence)
        if args.context:
            print("Context:")
            for path in context:
                print("  - %s" % path)
        if args.show_next:
            print("Next: %s (%s)" % ((next_step or {}).get("skill") or "none",
                                      (next_step or {}).get("reason") or "no reason"))
        if args.converge:
            for item in build_remediation(checks):
                print("Remediation %s: %s" % (item["check"], item["action"]))
    return 1 if status == "fail" else 0


if __name__ == "__main__":
    sys.exit(main())
