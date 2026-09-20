#!/usr/bin/env python3
"""Summarize Mini Metro Auto Planner sessions and flag behavioral regressions."""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Iterable


SUMMARY_FIELD = re.compile(r"\b([A-Za-z][A-Za-z0-9]*)=(-?\d+)\b")
OPERATION = re.compile(r"\boperation=([^\s|]+)")
ROUTE_LABEL = re.compile(r"(?:^|,)L(\d+)\[")


@dataclass
class Session:
    number: int
    city: str = "unknown"
    mode: str = "unknown"
    completed: bool = False
    end_fields: dict[str, int] = field(default_factory=dict)
    actions: Counter[str] = field(default_factory=Counter)
    crossing_operations: Counter[str] = field(default_factory=Counter)
    opportunistic_crossing_failures: int = 0
    exceptions: int = 0
    duplicate_route_snapshots: int = 0
    findings: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, object]:
        result = asdict(self)
        result["actions"] = dict(self.actions)
        result["crossing_operations"] = dict(self.crossing_operations)
        result["status"] = "PASS" if not self.findings else "WARN"
        return result


def read_log(path: Path) -> str:
    raw = path.read_bytes()
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")):
        return raw.decode("utf-16")
    return raw.decode("utf-8-sig", errors="replace")


def snapshot_value(line: str, name: str, default: str = "unknown") -> str:
    match = re.search(rf"\b{re.escape(name)}=([^\s|]+)", line)
    return match.group(1) if match else default


def snapshot_int(line: str, name: str) -> int | None:
    value = snapshot_value(line, name, "")
    try:
        return int(value)
    except ValueError:
        return None


def action_kind(line: str) -> str:
    payload = line.split("[Action]", 1)[1].split("|", 1)[0].strip()
    prefixes = (
        "Added transfer",
        "Upgraded interchange",
        "Relocated interchange",
        "Removed underused interchange",
        "Built line",
        "Extended line",
        "Reallocated",
        "Rolled back",
        "Weekly resources",
    )
    for prefix in prefixes:
        if payload.startswith(prefix):
            return prefix
    return payload.split(" ", 1)[0] if payload else "unknown"


def has_duplicate_routes(line: str) -> bool:
    marker = "routes="
    if marker not in line:
        return False
    labels = ROUTE_LABEL.findall(line.split(marker, 1)[1])
    return len(labels) != len(set(labels))


def analyze_lines(lines: Iterable[str]) -> list[Session]:
    sessions: list[Session] = []
    current: Session | None = None

    def ensure_session(line: str) -> Session:
        nonlocal current
        if current is None:
            current = Session(number=len(sessions) + 1)
            current.city = snapshot_value(line, "city")
            current.mode = snapshot_value(line, "mode")
            sessions.append(current)
        return current

    for raw_line in lines:
        line = raw_line.strip()
        if "[Auto Planner]" not in line and "[Extreme Auto Planner]" not in line:
            continue

        if "[Auto Planner][Session] START" in line:
            current = Session(number=len(sessions) + 1)
            current.city = snapshot_value(line, "city")
            current.mode = snapshot_value(line, "mode")
            sessions.append(current)
            continue

        session = ensure_session(line)
        if session.city == "unknown":
            session.city = snapshot_value(line, "city")
        if session.mode == "unknown":
            session.mode = snapshot_value(line, "mode")

        if "[Auto Planner][Action]" in line:
            session.actions[action_kind(line)] += 1

        if "[Auto Planner][Blocked] NoCrossing" in line:
            match = OPERATION.search(line)
            operation = match.group(1) if match else "unknown"
            session.crossing_operations[operation] += 1
            unconnected = snapshot_int(line, "unconnected")
            if operation in {"transfer", "rehome", "loop", "relief-line"} or unconnected == 0:
                session.opportunistic_crossing_failures += 1

        if "[Extreme Auto Planner]" in line or " recovered:" in line:
            session.exceptions += 1

        if has_duplicate_routes(line):
            session.duplicate_route_snapshots += 1

        if "[Auto Planner][Session] END" in line:
            session.completed = True
            before_snapshot = line.split("|", 1)[0]
            session.end_fields = {
                key: int(value) for key, value in SUMMARY_FIELD.findall(before_snapshot)
            }
            current = None

    for session in sessions:
        action_total = session.end_fields.get("actions", sum(session.actions.values()))
        no_crossing = session.end_fields.get(
            "noCrossing", sum(session.crossing_operations.values())
        )
        transfers = session.end_fields.get(
            "transfers", session.actions.get("Added transfer", 0)
        )
        interchanges = session.end_fields.get("interchange", sum(
            count for name, count in session.actions.items() if "interchange" in name.lower()
        ))

        if session.exceptions:
            session.findings.append(f"planner exceptions/recoveries: {session.exceptions}")
        if session.duplicate_route_snapshots:
            session.findings.append(
                f"duplicate route labels in snapshots: {session.duplicate_route_snapshots}"
            )
        if session.opportunistic_crossing_failures > 3:
            session.findings.append(
                "opportunistic NoCrossing attempts: "
                f"{session.opportunistic_crossing_failures}"
            )
        if no_crossing > max(8, action_total):
            session.findings.append(
                f"NoCrossing churn: {no_crossing} failures for {action_total} actions"
            )
        if transfers > max(4, int(action_total * 0.30)):
            session.findings.append(
                f"transfer churn: {transfers} transfers for {action_total} actions"
            )
        if interchanges > max(5, int(action_total * 0.35)):
            session.findings.append(
                f"interchange churn: {interchanges} changes for {action_total} actions"
            )

    return sessions


def print_text(path: Path, sessions: list[Session]) -> None:
    print(f"Log: {path}")
    if not sessions:
        print("No Auto Planner sessions found.")
        return
    for session in sessions:
        status = "PASS" if not session.findings else "WARN"
        completion = "complete" if session.completed else "incomplete"
        fields = session.end_fields
        print(
            f"Session {session.number}: {status} {session.city}/{session.mode} "
            f"({completion}) actions={fields.get('actions', sum(session.actions.values()))} "
            f"transfers={fields.get('transfers', session.actions.get('Added transfer', 0))} "
            f"interchanges={fields.get('interchange', 0)} "
            f"noCrossing={fields.get('noCrossing', sum(session.crossing_operations.values()))}"
        )
        if session.crossing_operations:
            details = ", ".join(
                f"{name}={count}" for name, count in sorted(session.crossing_operations.items())
            )
            print(f"  crossing operations: {details}")
        for finding in session.findings:
            print(f"  - {finding}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path, help="Path to Mini Metro Player.log")
    parser.add_argument("--json", action="store_true", help="Emit machine-readable JSON")
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Return exit code 1 when a regression warning is detected",
    )
    args = parser.parse_args()

    if not args.log.is_file():
        parser.error(f"log file not found: {args.log}")
    sessions = analyze_lines(read_log(args.log).splitlines())
    if args.json:
        print(json.dumps([session.to_dict() for session in sessions], indent=2))
    else:
        print_text(args.log, sessions)
    return 1 if args.strict and any(session.findings for session in sessions) else 0


if __name__ == "__main__":
    sys.exit(main())
