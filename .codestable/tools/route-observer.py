#!/usr/bin/env python3
"""Privacy-preserving CodeStable routing telemetry for Codex and Pi (Python 3.9+)."""

import argparse
import collections
import datetime as dt
import hashlib
import json
import os
import re
import sys
import uuid
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple


SCHEMA = 1
SKILL_PATTERN = re.compile(r"(?:^|[/\\])(?P<skill>cs(?:-[a-z0-9]+)*)[/\\]SKILL\.md(?:$|[^a-z])", re.I)
ALLOWED_SOURCES = {"explicit", "implicit", "interactive", "rpc", "extension", "workflow", "manual"}


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def event_path(root: Path) -> Path:
    return root.resolve() / ".codestable" / "telemetry" / "routes.jsonl"


def session_state_path(root: Path, session_id: str) -> Path:
    digest = hashlib.sha256(session_id.encode("utf-8")).hexdigest()[:20]
    return event_path(root).parent / ("current-" + digest)


def store_current_request(root: Path, session_id: str, request_id: str) -> None:
    if not session_id:
        return
    path = session_state_path(root, session_id)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp-" + uuid.uuid4().hex)
    temporary.write_text(request_id + "\n", encoding="ascii")
    os.replace(str(temporary), str(path))


def load_current_request(root: Path, session_id: str) -> str:
    if not session_id:
        return ""
    try:
        return session_state_path(root, session_id).read_text(encoding="ascii").strip()
    except OSError:
        return ""


def append_event(root: Path, event: Dict[str, Any]) -> None:
    path = event_path(root)
    path.parent.mkdir(parents=True, exist_ok=True)
    clean = {key: value for key, value in event.items() if value not in (None, "", [], {})}
    clean["schema"] = SCHEMA
    clean.setdefault("timestamp", utc_now())
    payload = (json.dumps(clean, ensure_ascii=True, separators=(",", ":")) + "\n").encode("utf-8")
    descriptor = os.open(str(path), os.O_APPEND | os.O_CREAT | os.O_WRONLY, 0o600)
    try:
        os.write(descriptor, payload)
    finally:
        os.close(descriptor)


def make_request_id(session_id: str = "", turn_id: str = "") -> str:
    if session_id and turn_id:
        digest = hashlib.sha256((session_id + "\0" + turn_id).encode("utf-8")).hexdigest()[:20]
        return "req-" + digest
    return "req-" + uuid.uuid4().hex


def request_event(request_id: str, platform: str, session_id: str, source: str,
                  intent_category: str = "", candidate_skills: Optional[List[str]] = None) -> Dict[str, Any]:
    return {
        "event": "request", "request_id": request_id, "platform": platform,
        "session_id": session_id, "source": source,
        "intent_category": intent_category, "candidate_skills": candidate_skills or [],
    }


def invocation_event(request_id: str, platform: str, skill: str, source: str,
                     session_id: str = "") -> Dict[str, Any]:
    return {
        "event": "invocation", "request_id": request_id, "platform": platform,
        "session_id": session_id, "skill": skill.lower(), "source": source,
    }


def correction_event(request_id: str, expected_skill: str, original_skill: str = "",
                     reason_code: str = "manual", platform: str = "manual") -> Dict[str, Any]:
    return {
        "event": "correction", "request_id": request_id, "platform": platform,
        "expected_skill": expected_skill.lower(), "original_skill": original_skill.lower(),
        "reason_code": reason_code,
    }


def read_events(path: Path) -> Tuple[List[Dict[str, Any]], int]:
    events: List[Dict[str, Any]] = []
    malformed = 0
    if not path.exists():
        return events, malformed
    with path.open("r", encoding="utf-8", errors="replace") as stream:
        for line in stream:
            try:
                value = json.loads(line)
                if isinstance(value, dict) and value.get("event") in {"request", "invocation", "correction"}:
                    events.append(value)
                else:
                    malformed += 1
            except (TypeError, ValueError):
                malformed += 1
    return events, malformed


def aggregate(events: Iterable[Dict[str, Any]], malformed: int = 0) -> Dict[str, Any]:
    requests: Dict[str, Dict[str, Any]] = {}
    invocations: Dict[str, List[Dict[str, Any]]] = collections.defaultdict(list)
    corrections: Dict[str, List[Dict[str, Any]]] = collections.defaultdict(list)
    source_counts: collections.Counter = collections.Counter()
    skill_counts: collections.Counter = collections.Counter()
    platform_counts: collections.Counter = collections.Counter()
    invocation_seen = set()
    invocation_events = 0
    for item in events:
        request_id = str(item.get("request_id", ""))
        if not request_id:
            malformed += 1
            continue
        kind = item.get("event")
        if kind == "request":
            requests.setdefault(request_id, item)
            platform_counts[str(item.get("platform", "unknown"))] += 1
        elif kind == "invocation":
            invocation_events += 1
            unique_key = (request_id, str(item.get("skill", "")), str(item.get("source", "")))
            if unique_key in invocation_seen:
                continue
            invocation_seen.add(unique_key)
            invocations[request_id].append(item)
            source_counts[str(item.get("source", "unknown"))] += 1
            skill_counts[str(item.get("skill", "unknown"))] += 1
        else:
            corrections[request_id].append(item)

    zero_ids = [request_id for request_id in requests if not invocations.get(request_id)]
    confirmed_missed = 0
    confirmed_false_positive = 0
    confirmed_misroute = 0
    matrix: collections.Counter = collections.Counter()
    for request_id, labels in corrections.items():
        selected = list(dict.fromkeys(str(item.get("skill", "")) for item in invocations.get(request_id, [])))
        for label in labels:
            expected = str(label.get("expected_skill", "unknown"))
            original = str(label.get("original_skill", "")) or (selected[0] if selected else "none")
            matrix[(original, expected)] += 1
            if not selected and expected != "none":
                confirmed_missed += 1
            elif selected and expected == "none":
                confirmed_false_positive += 1
            elif expected not in selected:
                confirmed_misroute += 1

    request_count = len(requests)
    correction_count = sum(len(value) for value in corrections.values())
    correction_requests = len(corrections)
    return {
        "schema": SCHEMA,
        "requests": request_count,
        "invocations": sum(len(value) for value in invocations.values()),
        "invocation_events": invocation_events,
        "uncorrelated_invocations": sum(
            len(value) for request_id, value in invocations.items() if request_id not in requests
        ),
        "requests_with_invocation": request_count - len(zero_ids),
        "zero_invocation_requests": len(zero_ids),
        "zero_invocation_rate": round(len(zero_ids) / request_count, 4) if request_count else 0.0,
        "corrections": correction_count,
        "corrected_requests": correction_requests,
        "correction_rate": round(correction_requests / request_count, 4) if request_count else 0.0,
        "confirmed_missed": confirmed_missed,
        "confirmed_false_positive": confirmed_false_positive,
        "confirmed_misroute": confirmed_misroute,
        "confirmed_wrong": confirmed_false_positive + confirmed_misroute,
        "confirmed_miss_rate_among_labeled": round(
            confirmed_missed / correction_requests, 4
        ) if correction_requests else 0.0,
        "confirmed_wrong_rate_among_labeled": round(
            (confirmed_false_positive + confirmed_misroute) / correction_requests, 4
        ) if correction_requests else 0.0,
        "platforms": dict(sorted(platform_counts.items())),
        "invocation_sources": dict(sorted(source_counts.items())),
        "skills": dict(sorted(skill_counts.items())),
        "confusion_matrix": [
            {"selected": pair[0], "expected": pair[1], "count": count}
            for pair, count in sorted(matrix.items())
        ],
        "malformed_lines": malformed,
    }


def nested_text(value: Any) -> str:
    if isinstance(value, str):
        return value
    if isinstance(value, dict):
        return "\n".join(nested_text(item) for item in value.values())
    if isinstance(value, list):
        return "\n".join(nested_text(item) for item in value)
    return ""


def skill_names(value: Any) -> List[str]:
    return sorted({match.group("skill").lower() for match in SKILL_PATTERN.finditer(nested_text(value))})


def codex_hook(root: Path, payload: Dict[str, Any]) -> List[Dict[str, Any]]:
    event_name = str(payload.get("hook_event_name", ""))
    session_id = str(payload.get("session_id", ""))
    turn_id = str(payload.get("turn_id", ""))
    recorded: List[Dict[str, Any]] = []
    if event_name == "UserPromptSubmit":
        request_id = make_request_id(session_id, turn_id)
        store_current_request(root, session_id, request_id)
        recorded.append(request_event(request_id, "codex", session_id, "interactive"))
    elif event_name in {"PreToolUse", "PostToolUse"}:
        request_id = make_request_id(session_id, turn_id) if turn_id else load_current_request(root, session_id)
        for skill in skill_names(payload.get("tool_input", {})):
            if request_id:
                recorded.append(invocation_event(request_id, "codex", skill, "implicit", session_id))
    for item in recorded:
        append_event(root, item)
    return recorded


def add_common(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--root", type=Path, default=Path.cwd())


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description=__doc__)
    sub = result.add_subparsers(dest="command", required=True)
    request = sub.add_parser("record-request")
    add_common(request)
    request.add_argument("--platform", required=True, choices=["codex", "pi", "manual"])
    request.add_argument("--session-id", default="")
    request.add_argument("--source", default="manual", choices=sorted(ALLOWED_SOURCES))
    request.add_argument("--request-id", default="")
    request.add_argument("--intent-category", default="")
    request.add_argument("--candidate-skills", default="")
    invocation = sub.add_parser("record-invocation")
    add_common(invocation)
    invocation.add_argument("--request-id", required=True)
    invocation.add_argument("--platform", required=True, choices=["codex", "pi", "manual"])
    invocation.add_argument("--skill", required=True)
    invocation.add_argument("--source", default="manual", choices=sorted(ALLOWED_SOURCES))
    invocation.add_argument("--session-id", default="")
    correction = sub.add_parser("record-correction")
    add_common(correction)
    correction.add_argument("--request-id", required=True)
    correction.add_argument("--expected-skill", required=True)
    correction.add_argument("--original-skill", default="")
    correction.add_argument("--reason-code", default="manual")
    correction.add_argument("--platform", default="manual", choices=["codex", "pi", "manual"])
    stats = sub.add_parser("stats")
    add_common(stats)
    stats.add_argument("--json", action="store_true")
    hook = sub.add_parser("codex-hook")
    add_common(hook)
    return result


def main() -> int:
    args = parser().parse_args()
    if args.command == "record-request":
        request_id = args.request_id or make_request_id(args.session_id)
        candidates = [item for item in args.candidate_skills.split(",") if item]
        append_event(args.root, request_event(
            request_id, args.platform, args.session_id, args.source, args.intent_category, candidates
        ))
        print(json.dumps({"request_id": request_id}, separators=(",", ":")))
    elif args.command == "record-invocation":
        append_event(args.root, invocation_event(
            args.request_id, args.platform, args.skill, args.source, args.session_id
        ))
    elif args.command == "record-correction":
        append_event(args.root, correction_event(
            args.request_id, args.expected_skill, args.original_skill, args.reason_code, args.platform
        ))
    elif args.command == "stats":
        events, malformed = read_events(event_path(args.root))
        report = aggregate(events, malformed)
        print(json.dumps(report, ensure_ascii=False, indent=2 if args.json else None))
    else:
        try:
            payload = json.load(sys.stdin)
            if not isinstance(payload, dict):
                raise ValueError("hook input must be an object")
            codex_hook(args.root, payload)
        except (OSError, TypeError, ValueError, json.JSONDecodeError) as error:
            print("route observer ignored invalid hook input: %s" % error, file=sys.stderr)
            return 0
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
