#!/usr/bin/env python3
"""Compare FH2 native-location research captures.

The debug host writes ``fh2-native-location-*.json`` files from the read-only
``Career::LocationTrackerScripted`` probe. This tool compares captures taken in
one Azimuth/FH2 process lifetime and ranks integer fields that change with the
observed location.

Use ``--label FILE=NAME`` for human ground-truth labels. The dump's existing
PolygonLocation is useful context, but must not be treated as proof when the
whole goal is to replace the polygon resolver.

No third-party packages are required.
"""

from __future__ import annotations

import argparse
import glob
import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable


@dataclass(frozen=True)
class Capture:
    path: Path
    data: dict[str, Any]
    label: str
    label_source: str
    x: float | None
    z: float | None


def _number(value: Any) -> float | None:
    if isinstance(value, (int, float)) and math.isfinite(float(value)):
        return float(value)
    return None


def _vehicle_xz(data: dict[str, Any]) -> tuple[float | None, float | None]:
    position = data.get("VehiclePosition")
    if not isinstance(position, dict):
        return None, None
    return _number(position.get("X")), _number(position.get("Z"))


def load_capture(path: Path, label_override: str | None = None) -> Capture:
    with path.open("r", encoding="utf-8-sig") as handle:
        data = json.load(handle)
    if not isinstance(data, dict):
        raise ValueError(f"{path}: top-level JSON is not an object")

    if label_override:
        label = label_override
        source = "manual"
    else:
        label = str(data.get("PolygonLocation") or "Unknown")
        source = "polygon"

    x, z = _vehicle_xz(data)
    return Capture(path=path, data=data, label=label, label_source=source, x=x, z=z)


def _candidate_key(candidate: dict[str, Any], index: int) -> str:
    address = candidate.get("Address")
    return str(address) if isinstance(address, str) and address else f"candidate#{index}"


def _emit_small_ints(out: dict[str, int], prefix: str, entries: Any) -> None:
    if not isinstance(entries, list):
        return
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        offset = entry.get("Offset")
        value = entry.get("Value")
        if isinstance(offset, str) and isinstance(value, int):
            out[f"{prefix}/int@{offset}"] = value


def flatten_capture(data: dict[str, Any]) -> dict[str, int]:
    """Return comparable integer observations keyed by stable-ish research paths.

    Captures are intended to be taken in the same FH2 process lifetime, so the
    LocationTrackerScripted object address is expected to remain stable. Within
    guided pointer graphs, the scanner's ``Via`` chain is used when available;
    the raw pointed-to address is deliberately not part of the key.
    """

    out: dict[str, int] = {}
    candidates = data.get("Candidates")
    if not isinstance(candidates, list):
        return out

    for candidate_index, candidate in enumerate(candidates):
        if not isinstance(candidate, dict):
            continue
        candidate_key = _candidate_key(candidate, candidate_index)
        base = candidate_key

        _emit_small_ints(out, f"{base}/object", candidate.get("CandidateSmallInts0To16"))
        _emit_small_ints(out, f"{base}/object-id3to9", candidate.get("CandidateLocationIds3To9"))

        chains = candidate.get("GuidedQueryChains")
        if not isinstance(chains, list):
            continue
        for chain_index, chain in enumerate(chains):
            if not isinstance(chain, dict):
                continue
            source_offset = chain.get("SourceOffset")
            chain_key = source_offset if isinstance(source_offset, str) else f"chain#{chain_index}"
            chain_base = f"{base}/source{chain_key}"
            _emit_small_ints(out, f"{chain_base}/container", chain.get("QueryContainerSmallInts"))

            graph = chain.get("PointerGraph")
            if not isinstance(graph, list):
                continue
            for node_index, node in enumerate(graph):
                if not isinstance(node, dict):
                    continue
                via = node.get("Via")
                depth = node.get("Depth")
                node_key = str(via) if isinstance(via, str) and via else f"depth{depth}-node{node_index}"
                node_base = f"{chain_base}/graph/{node_key}"

                for name in ("Field14", "Byte11", "Byte24", "Byte3B"):
                    value = node.get(name)
                    if isinstance(value, int):
                        out[f"{node_base}/{name}"] = value
                _emit_small_ints(out, node_base, node.get("CandidateSmallInts"))

    return out


def _label_consistency(values: list[int | None], labels: list[str]) -> tuple[bool, bool]:
    """Return (same_label_same_value, distinct_labels_separated)."""
    label_values: dict[str, set[int]] = {}
    for value, label in zip(values, labels):
        if value is None:
            continue
        label_values.setdefault(label, set()).add(value)

    same_label_same_value = all(len(items) <= 1 for items in label_values.values())
    singleton_values = [next(iter(items)) for items in label_values.values() if len(items) == 1]
    distinct_labels_separated = len(singleton_values) == len(set(singleton_values)) if singleton_values else False
    return same_label_same_value, distinct_labels_separated


def rank_fields(captures: list[Capture]) -> list[dict[str, Any]]:
    flattened = [flatten_capture(c.data) for c in captures]
    keys = sorted(set().union(*(set(item) for item in flattened)))
    labels = [c.label for c in captures]
    ranked: list[dict[str, Any]] = []

    for key in keys:
        values: list[int | None] = [item.get(key) for item in flattened]
        present = [value for value in values if value is not None]
        distinct = sorted(set(present))
        if len(distinct) < 2:
            continue

        coverage = len(present) / len(captures)
        id_range = all(3 <= value <= 9 for value in present)
        low_enum = all(0 <= value <= 16 for value in present)
        same_label, separated_labels = _label_consistency(values, labels)

        score = coverage * 30.0 + min(len(distinct), 8) * 4.0
        if id_range:
            score += 35.0
        elif low_enum:
            score += 12.0
        if same_label:
            score += 12.0
        if separated_labels and len(set(labels)) > 1:
            score += 22.0
        if key.endswith("/Field14"):
            score += 8.0

        ranked.append(
            {
                "score": round(score, 2),
                "path": key,
                "values": values,
                "distinct": distinct,
                "coverage": round(coverage, 3),
                "id3to9": id_range,
                "same_label_same_value": same_label,
                "distinct_labels_separated": separated_labels,
            }
        )

    ranked.sort(key=lambda item: (-item["score"], item["path"]))
    return ranked


def _fmt_position(capture: Capture) -> str:
    if capture.x is None or capture.z is None:
        return "?, ?"
    return f"{capture.x:.1f}, {capture.z:.1f}"


def print_report(captures: list[Capture], ranked: list[dict[str, Any]], limit: int) -> None:
    print("FH2 native-location capture comparison")
    print("=" * 38)
    for index, capture in enumerate(captures, 1):
        timestamp = capture.data.get("CapturedUtc") or "?"
        count = capture.data.get("Count")
        print(
            f"[{index}] {capture.label:<16} ({capture.label_source}) "
            f"pos=({_fmt_position(capture)}) objects={count!s:<3} "
            f"{timestamp}  {capture.path.name}"
        )

    print()
    if not all(c.label_source == "manual" for c in captures):
        print("NOTE: captures without --label use PolygonLocation only as provisional context, not independent ground truth.")
        print()
    if len({c.label for c in captures}) <= 1:
        print("WARNING: all captures have the same label. Capture another known location before drawing semantic conclusions.")
        print()

    if not ranked:
        print("No changing comparable integer fields found.")
        return

    print("Top changing fields")
    print("-------------------")
    for index, item in enumerate(ranked[:limit], 1):
        values = ", ".join("-" if value is None else str(value) for value in item["values"])
        flags: list[str] = []
        if item["id3to9"]:
            flags.append("ID3..9")
        if item["same_label_same_value"]:
            flags.append("same-label stable")
        if item["distinct_labels_separated"]:
            flags.append("labels separated")
        suffix = f"  [{' | '.join(flags)}]" if flags else ""
        print(f"{index:>2}. score={item['score']:>6.2f}  {item['path']}")
        print(f"    values: {values}{suffix}")

    best = ranked[0]
    print()
    if best["id3to9"] and best["same_label_same_value"] and best["distinct_labels_separated"]:
        print("LEAD: the top field behaves like a strong Road Trip/location enum candidate. Repeat each location at least twice before promoting it to a runtime offset.")
    else:
        print("No field is proven yet. Prefer repeated captures per location and require the same field/value to survive movement, menus and a process restart.")


def discover(patterns: Iterable[str]) -> list[Path]:
    paths: set[Path] = set()
    for pattern in patterns:
        matches = glob.glob(pattern)
        if matches:
            paths.update(Path(match) for match in matches)
        else:
            candidate = Path(pattern)
            if candidate.exists():
                paths.add(candidate)
    return sorted(paths, key=lambda path: path.stat().st_mtime)


def parse_label_overrides(entries: list[str]) -> dict[str, str]:
    overrides: dict[str, str] = {}
    for entry in entries:
        if "=" not in entry:
            raise ValueError(f"invalid --label {entry!r}; expected FILE=NAME")
        file_name, label = entry.split("=", 1)
        file_name = file_name.strip()
        label = label.strip()
        if not file_name or not label:
            raise ValueError(f"invalid --label {entry!r}; expected FILE=NAME")
        overrides[file_name] = label
    return overrides


def label_for(path: Path, overrides: dict[str, str]) -> str | None:
    for key in (str(path), path.name, str(path.resolve())):
        if key in overrides:
            return overrides[key]
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Compare fh2-native-location research dumps")
    parser.add_argument(
        "inputs",
        nargs="*",
        default=["dumps/fh2-native-location-*.json"],
        help="JSON files or glob patterns (default: dumps/fh2-native-location-*.json)",
    )
    parser.add_argument(
        "--label",
        action="append",
        default=[],
        metavar="FILE=NAME",
        help="human ground-truth label; repeat once per capture",
    )
    parser.add_argument("--limit", type=int, default=30, help="number of ranked fields to print")
    parser.add_argument("--json", dest="json_output", help="optional path for a machine-readable comparison report")
    args = parser.parse_args()

    paths = discover(args.inputs)
    if len(paths) < 2:
        parser.error("need at least two native-location captures")

    try:
        overrides = parse_label_overrides(args.label)
    except ValueError as error:
        parser.error(str(error))

    captures = [load_capture(path, label_for(path, overrides)) for path in paths]
    ranked = rank_fields(captures)
    print_report(captures, ranked, max(1, args.limit))

    if args.json_output:
        report = {
            "captures": [
                {
                    "path": str(c.path),
                    "label": c.label,
                    "labelSource": c.label_source,
                    "x": c.x,
                    "z": c.z,
                    "capturedUtc": c.data.get("CapturedUtc"),
                    "candidateCount": c.data.get("Count"),
                }
                for c in captures
            ],
            "rankedFields": ranked,
        }
        Path(args.json_output).write_text(json.dumps(report, indent=2), encoding="utf-8")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
