#!/usr/bin/env python3
"""Shared YAML subset parser for CodeStable tools (Python 3.9+)."""

import re
from typing import Any, Dict, List, Optional, Tuple


try:
    import yaml  # type: ignore
except ImportError:
    yaml = None


def _split_inline(value: str) -> List[str]:
    parts: List[str] = []
    current: List[str] = []
    quote: Optional[str] = None
    depth = 0
    for char in value:
        if quote:
            current.append(char)
            if char == quote:
                quote = None
            continue
        if char in {"'", '"'}:
            quote = char
            current.append(char)
        elif char in "[{":
            depth += 1
            current.append(char)
        elif char in "]}":
            depth -= 1
            current.append(char)
        elif char == "," and depth == 0:
            parts.append("".join(current).strip())
            current = []
        else:
            current.append(char)
    if current or value.strip():
        parts.append("".join(current).strip())
    return [part for part in parts if part]


def parse_scalar(raw: str) -> Any:
    value = raw.strip()
    if not value:
        return None
    if value.startswith("[") and value.endswith("]"):
        return [parse_scalar(item) for item in _split_inline(value[1:-1])]
    if value.startswith("{") and value.endswith("}"):
        mapping: Dict[str, Any] = {}
        for item in _split_inline(value[1:-1]):
            if ":" not in item:
                raise ValueError("invalid inline mapping item %r" % item)
            key, _, child = item.partition(":")
            mapping[key.strip().strip("'\"")] = parse_scalar(child)
        return mapping
    if len(value) >= 2 and value[0] == value[-1] and value[0] in {"'", '"'}:
        return value[1:-1]
    lowered = value.lower()
    if lowered in {"null", "~"}:
        return None
    if lowered in {"true", "yes"}:
        return True
    if lowered in {"false", "no"}:
        return False
    if re.match(r"^-?\d+$", value):
        try:
            return int(value)
        except ValueError:
            pass
    return value


def _logical_lines(text: str) -> List[Tuple[int, str, int]]:
    lines: List[Tuple[int, str, int]] = []
    for number, line in enumerate(text.splitlines(), 1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        prefix = line[:len(line) - len(line.lstrip(" "))]
        if "\t" in prefix:
            raise ValueError("tabs are not allowed for YAML indentation (line %d)" % number)
        lines.append((len(prefix), line.strip(), number))
    return lines


def _parse_node(lines: List[Tuple[int, str, int]], index: int, indent: int) -> Tuple[Any, int]:
    if index >= len(lines) or lines[index][0] < indent:
        return {}, index
    is_list = lines[index][1].startswith("-")
    container: Any = [] if is_list else {}
    while index < len(lines):
        current_indent, content, number = lines[index]
        if current_indent < indent:
            break
        if current_indent > indent:
            raise ValueError("unexpected indentation on line %d" % number)
        if is_list:
            if not content.startswith("-"):
                break
            item = content[1:].strip()
            if not item:
                if index + 1 >= len(lines) or lines[index + 1][0] <= indent:
                    container.append(None)
                    index += 1
                else:
                    child, index = _parse_node(lines, index + 1, lines[index + 1][0])
                    container.append(child)
                continue
            if ":" in item:
                key, _, raw = item.partition(":")
                mapping: Dict[str, Any] = {}
                mapping[key.strip()] = parse_scalar(raw) if raw.strip() else None
                index += 1
                if index < len(lines) and lines[index][0] > indent:
                    child, index = _parse_node(lines, index, lines[index][0])
                    if isinstance(child, dict):
                        if mapping[key.strip()] is None and len(child) == 1 and key.strip() in child:
                            mapping[key.strip()] = child[key.strip()]
                        else:
                            mapping.update(child)
                    else:
                        mapping[key.strip()] = child
                container.append(mapping)
                continue
            container.append(parse_scalar(item))
            index += 1
            continue
        if content.startswith("-") or ":" not in content:
            raise ValueError("expected key: value on line %d" % number)
        key, _, raw = content.partition(":")
        key = key.strip().strip("'\"")
        if not key or key in container:
            raise ValueError("empty or duplicate key on line %d" % number)
        if raw.strip():
            container[key] = parse_scalar(raw)
            index += 1
        elif index + 1 < len(lines) and lines[index + 1][0] > indent:
            child, index = _parse_node(lines, index + 1, lines[index + 1][0])
            container[key] = child
        else:
            container[key] = {}
            index += 1
    return container, index


def parse_builtin(text: str) -> Tuple[Optional[Dict[str, Any]], Optional[str]]:
    try:
        lines = _logical_lines(text)
        if not lines:
            return {}, None
        value, index = _parse_node(lines, 0, lines[0][0])
        if index != len(lines):
            return None, "could not parse YAML near line %d" % lines[index][2]
        if not isinstance(value, dict):
            return None, "expected a mapping"
        return value, None
    except (ValueError, IndexError) as exc:
        return None, str(exc)


def parse_yaml(text: str, prefer_pyyaml: bool = True) -> Tuple[Optional[Dict[str, Any]], Optional[str]]:
    if prefer_pyyaml and yaml is not None:
        try:
            value = yaml.safe_load(text)
        except Exception as exc:
            return None, str(exc)
        if value is None:
            return {}, None
        if not isinstance(value, dict):
            return None, "expected a mapping"
        return value, None
    return parse_builtin(text)


def split_frontmatter(text: str) -> Tuple[Dict[str, Any], str, Optional[str]]:
    if not text.startswith("---"):
        return {}, text, "missing opening frontmatter delimiter"
    end = text.find("\n---", 3)
    if end < 0:
        return {}, text, "missing closing frontmatter delimiter"
    meta, error = parse_yaml(text[3:end].strip())
    return meta or {}, text[end + 4:].strip(), error


def has_pyyaml() -> bool:
    return yaml is not None
