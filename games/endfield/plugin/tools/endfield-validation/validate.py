# SPDX-License-Identifier: MIT
"""Read-only preflight and PresentMon analysis for the Endfield Vulkan fork."""

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import subprocess


MIN_DRIVER = (615, 0)
SUPPORTED_INTERPOSERS = {
    "39925bb5216ee1b3d164d1b9dac9e127869c2bcd7ea60f29c1c24b416de7a1f4",
    "fabb19bb6495d743182c1bc51bcbbdc4481acc3dcaf445403f0b304a31a56ccc",
}
LATENCY_FIELDS = (
    "MsClickToPhotonLatency",
    "MsAllInputToPhotonLatency",
    "MsPCLatency",
    "MsGPUBusy",
)


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def preflight(game_dir):
    blockers = []
    if not (game_dir / "Endfield.exe").is_file():
        blockers.append("Endfield.exe was not found in the selected directory")

    try:
        result = subprocess.run(
            ["nvidia-smi", "--query-gpu=name,driver_version", "--format=csv,noheader"],
            check=True, capture_output=True, text=True, timeout=10,
        )
        adapters = list(csv.reader(result.stdout.splitlines(), skipinitialspace=True))
    except (OSError, subprocess.SubprocessError):
        adapters = []

    drivers = [tuple(map(int, driver.split("."))) for _, driver in adapters
               if re.fullmatch(r"\d+\.\d+", driver)]
    if not drivers or min(drivers) < MIN_DRIVER:
        blockers.append("NVIDIA driver 615.00 or newer is required by this upstream release")

    interposer = game_dir / "sl.interposer.dll"
    fingerprint = sha256(interposer) if interposer.is_file() else None
    if fingerprint not in SUPPORTED_INTERPOSERS:
        blockers.append("The installed Streamline interposer is outside this validation profile")

    for name in ("dlss5-feed.addon64", "d3d12.dll"):
        if (game_dir / name).is_file():
            blockers.append(f"{name} is present; isolate the Vulkan baseline from the existing D3D12 setup")

    return {
        "status": "blocked" if blockers else "ready_for_manual_vulkan_check",
        "adapters": adapters,
        "streamline_sha256": fingerprint,
        "blockers": blockers,
    }


def number(value):
    try:
        result = float(value)
    except (TypeError, ValueError):
        return None
    return result if math.isfinite(result) else None


def distribution(values):
    if not values:
        return None
    ordered = sorted(values)
    return {
        "samples": len(ordered),
        "p50_ms": round(statistics.median(ordered), 3),
        "p95_ms": round(ordered[math.ceil(len(ordered) * 0.95) - 1], 3),
        "p99_ms": round(ordered[math.ceil(len(ordered) * 0.99) - 1], 3),
        "max_ms": round(ordered[-1], 3),
    }


def analyze(path, swap_chain=None, confirm_fg_off=False):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        required = {"Application", "ProcessID", "SwapChainAddress", "CPUStartQPCTime"}
        if not required.issubset(reader.fieldnames or ()):
            raise ValueError("Use a PresentMon v2 CSV captured with --qpc_time_ms")
        rows = [row for row in reader if row["Application"].casefold() == "endfield.exe"]

    if swap_chain is not None:
        rows = [row for row in rows if row["SwapChainAddress"].casefold() == swap_chain.casefold()]
    if len({(row["ProcessID"], row["SwapChainAddress"]) for row in rows}) != 1:
        raise ValueError("Expected one Endfield process and swap chain; select --swap-chain or recapture")

    kinds = {(row.get("FrameType") or "").strip().casefold() for row in rows}
    unmarked = {"", "notset", "not set", "unknown", "na", "n/a"}
    if kinds - {"application"} - unmarked:
        raise ValueError("The capture contains non-application frame types; disable all frame generation and recapture")
    if kinds & unmarked and not confirm_fg_off:
        raise ValueError("Frame types are unmarked; verify frame generation is off before using --confirm-fg-off")

    timestamps = [number(row["CPUStartQPCTime"]) for row in rows]
    if len(timestamps) < 2 or any(value is None for value in timestamps):
        raise ValueError("At least two valid application-frame timestamps are required")
    intervals = [later - earlier for earlier, later in zip(timestamps, timestamps[1:])]
    if any(value <= 0 for value in intervals):
        raise ValueError("Application frame timestamps must be strictly increasing")

    measured = {}
    for field in LATENCY_FIELDS:
        values = [number(row.get(field)) for row in rows]
        measured[field] = distribution([value for value in values if value is not None and value > 0])

    return {
        "application_frames": len(rows),
        "frame_type_verified": not bool(kinds & unmarked),
        "capture_seconds": round(sum(intervals) / 1000, 3),
        "real_fps": round(1000 / statistics.mean(intervals), 3),
        "frame_interval": distribution(intervals),
        "intervals_over_100_ms": sum(value > 100 for value in intervals),
        "measured_latency_and_gpu": measured,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    check = commands.add_parser("preflight")
    check.add_argument("game_dir", type=Path)
    capture = commands.add_parser("analyze")
    capture.add_argument("csv", type=Path)
    capture.add_argument("--swap-chain")
    capture.add_argument("--confirm-fg-off", action="store_true")
    args = parser.parse_args()
    try:
        if args.command == "preflight":
            result = preflight(args.game_dir)
        else:
            result = analyze(args.csv, args.swap_chain, args.confirm_fg_off)
    except (OSError, ValueError) as error:
        parser.exit(2, f"{error}\n")
    print(json.dumps(result, indent=2))
    return int(result.get("status") == "blocked")


if __name__ == "__main__":
    raise SystemExit(main())
