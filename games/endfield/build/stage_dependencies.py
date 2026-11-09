"""Stage verified header-only build inputs for the Endfield add-ons."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from hashlib import sha256
import json
from pathlib import Path, PurePosixPath
import re
from urllib.error import URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


def valid_relative(path):
    p = PurePosixPath(path)
    return bool(path) and not p.is_absolute() and not any(
        part in ("..", ".") or "\\" in part or ":" in part for part in p.parts
    )


def download(url):
    request = Request(url, headers={"User-Agent": "GameRenderLab-Endfield-Build"})
    for attempt in range(3):
        try:
            with urlopen(request, timeout=30) as response:
                return response.read()
        except (TimeoutError, URLError):
            if attempt == 2:
                raise


def stage_header(item):
    url, expected, destination, offline = item
    if destination.exists():
        data = destination.read_bytes()
    elif offline:
        raise FileNotFoundError(destination)
    else:
        data = download(url)
    if sha256(data).hexdigest() != expected:
        raise ValueError(f"Dependency digest mismatch: {destination}")
    if not destination.exists():
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--offline", action="store_true", help="Only validate a staged cache.")
    args = parser.parse_args()
    lock = json.loads(Path(__file__).with_name("dependencies.lock.json").read_text(encoding="utf-8"))
    items = []
    for prefix, entry in lock.items():
        repo, commit = entry["repository"], entry["commit"]
        if not (
            valid_relative(prefix)
            and re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo)
            and re.fullmatch(r"[a-f0-9]{40}", commit)
        ):
            raise ValueError(f"Invalid lock entry: {prefix}")
        for path, digest in entry["sha256"].items():
            if not valid_relative(path) or not re.fullmatch(r"[a-f0-9]{64}", digest):
                raise ValueError(f"Invalid locked header: {path}")
            url = f"https://raw.githubusercontent.com/{repo}/{commit}/{quote(path)}"
            items.append((url, digest, args.destination / prefix / path, args.offline))
    if not items:
        raise ValueError("Empty dependency lock")
    with ThreadPoolExecutor(max_workers=12) as workers:
        for _ in workers.map(stage_header, items):
            pass
    print(f"Verified {len(items)} locked Endfield build headers.")


if __name__ == "__main__":
    main()
