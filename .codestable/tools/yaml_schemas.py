#!/usr/bin/env python3
"""Schema checks for codestable YAML artifacts."""

import re
from typing import Any, Dict, List, Set


SCHEMAS = ("roadmap-items",)
ROADMAP_STATUSES = {"planned", "in-progress", "done", "dropped"}
SLUG_PATTERN = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")


def _roadmap_items(data: Dict[str, Any], legacy_compatible: bool = False) -> List[str]:
    errors: List[str] = []
    if not isinstance(data.get("roadmap"), str) or not data.get("roadmap"):
        errors.append("roadmap must be a non-empty string")
    if "sub_features" in data:
        errors.append("legacy field 'sub_features' is not supported; use 'items'")
    items = data.get("items")
    if not isinstance(items, list) or not items:
        errors.append("items must be a non-empty list")
        return errors

    slugs: Set[str] = set()
    dependencies: Dict[str, List[str]] = {}
    minimal_loops = 0
    for index, item in enumerate(items, 1):
        prefix = "items[%d]" % index
        if not isinstance(item, dict):
            errors.append("%s must be a mapping" % prefix)
            continue
        slug = item.get("slug")
        if not isinstance(slug, str) or not SLUG_PATTERN.match(slug):
            errors.append("%s.slug must use lowercase kebab-case" % prefix)
            slug = ""
        elif slug in slugs:
            errors.append("duplicate roadmap slug %r" % slug)
        else:
            slugs.add(slug)
        if not isinstance(item.get("description"), str) or not item.get("description"):
            errors.append("%s.description must be a non-empty string" % prefix)
        depends_on = item.get("depends_on")
        if not isinstance(depends_on, list) or not all(isinstance(value, str) for value in depends_on):
            errors.append("%s.depends_on must be a string list" % prefix)
            depends_on = []
        if slug:
            dependencies[slug] = list(depends_on)
        status = item.get("status")
        if status not in ROADMAP_STATUSES:
            errors.append("%s.status must be planned, in-progress, done or dropped" % prefix)
        feature = item.get("feature")
        if feature is not None and not isinstance(feature, str):
            errors.append("%s.feature must be a string or null" % prefix)
        if status in {"in-progress", "done"} and not feature:
            errors.append("%s.feature is required when status is %s" % (prefix, status))
        minimal_loop = item.get("minimal_loop")
        if minimal_loop is None and legacy_compatible:
            minimal_loop = False
        elif not isinstance(minimal_loop, bool):
            errors.append("%s.minimal_loop must be boolean" % prefix)
        elif minimal_loop:
            minimal_loops += 1
        if status == "dropped" and not item.get("notes"):
            errors.append("%s.notes is required when status is dropped" % prefix)

    if minimal_loops != 1:
        errors.append("items must contain exactly one minimal_loop: true")
    for slug, values in dependencies.items():
        for dependency in values:
            if dependency not in slugs:
                errors.append("item %s depends on unknown slug %s" % (slug, dependency))
            elif dependency == slug:
                errors.append("item %s cannot depend on itself" % slug)

    visiting: Set[str] = set()
    visited: Set[str] = set()

    def visit(slug: str) -> None:
        if slug in visited:
            return
        if slug in visiting:
            errors.append("roadmap dependency cycle includes %s" % slug)
            return
        visiting.add(slug)
        for dependency in dependencies.get(slug, []):
            if dependency in dependencies:
                visit(dependency)
        visiting.remove(slug)
        visited.add(slug)

    for slug in dependencies:
        visit(slug)
    return errors


def validate_schema(data: Dict[str, Any], schema: str,
                    legacy_compatible: bool = False) -> List[str]:
    if schema == "roadmap-items":
        return _roadmap_items(data, legacy_compatible)
    return ["unknown schema %r" % schema]
