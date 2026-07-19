#!/usr/bin/env python3
"""Find official Microsoft Fluent System Icons and emit WinUI-friendly metadata."""

from __future__ import annotations

import argparse
import difflib
import html
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
CACHE = Path.home() / ".cache" / "fluent-system-icons"
REPO = "https://github.com/microsoft/fluentui-system-icons"
RAW = "https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main"
API = "https://api.github.com/repos/microsoft/fluentui-system-icons/contents/assets"
STYLE_FILES = {
    "regular": "FluentSystemIcons-Regular.json",
    "filled": "FluentSystemIcons-Filled.json",
}
ICON_RE = re.compile(r"^ic_fluent_(.+)_(12|16|20|24|28|32|48)_(regular|filled)$")


def request_bytes(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "fluent-system-icons-skill"})
    with urllib.request.urlopen(request, timeout=20) as response:
        return response.read()


def load_aliases() -> dict[str, list[str]]:
    path = ROOT / "references" / "aliases.json"
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}


def normalize(value: str) -> str:
    value = value.casefold().replace("_", " ").replace("-", " ")
    value = value.replace("ic fluent", " ")
    return re.sub(r"\s+", " ", value).strip()


def load_font_map(style: str, refresh: bool) -> dict[str, int]:
    CACHE.mkdir(parents=True, exist_ok=True)
    cache_file = CACHE / STYLE_FILES[style]
    if not refresh and cache_file.exists():
        try:
            return json.loads(cache_file.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            pass
    try:
        data = json.loads(request_bytes(f"{RAW}/fonts/{STYLE_FILES[style]}").decode("utf-8"))
    except Exception as exc:  # noqa: BLE001 - provide a useful offline error
        if cache_file.exists():
            print(f"warning: using stale {style} cache ({exc})", file=sys.stderr)
            return json.loads(cache_file.read_text(encoding="utf-8"))
        raise RuntimeError(f"cannot download the official {style} map: {exc}") from exc
    cache_file.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    return data


def parse_entry(icon_id: str, codepoint: int) -> dict[str, Any] | None:
    match = ICON_RE.match(icon_id)
    if not match:
        return None
    slug, size, style = match.groups()
    return {
        "id": icon_id,
        "slug": slug,
        "size": int(size),
        "style": style,
        "codepoint": codepoint,
        "hex": f"0x{codepoint:X}",
        "unicode": f"\\u{codepoint:04X}",
        "xaml_entity": f"&#x{codepoint:X};",
    }


def score_entry(entry: dict[str, Any], query: str) -> float:
    wanted = normalize(query)
    candidate = normalize(entry["slug"])
    wanted_tokens = set(wanted.split())
    candidate_tokens = set(candidate.split())
    overlap = len(wanted_tokens & candidate_tokens)
    score = overlap * 20
    if candidate == wanted:
        score += 100
    elif candidate.startswith(wanted):
        score += 45
    elif wanted in candidate:
        score += 25
    score += difflib.SequenceMatcher(None, wanted, candidate).ratio() * 10
    return score


def find_candidates(query: str, style: str, size: int | None, limit: int, refresh: bool) -> list[dict[str, Any]]:
    aliases = load_aliases()
    searches = aliases.get(query.casefold(), [query])
    maps: list[dict[str, int]] = []
    styles = [style] if style in STYLE_FILES else list(STYLE_FILES)
    for current_style in styles:
        maps.append(load_font_map(current_style, refresh))

    found: dict[str, dict[str, Any]] = {}
    for font_map in maps:
        for icon_id, codepoint in font_map.items():
            entry = parse_entry(icon_id, codepoint)
            if entry is None or (size is not None and entry["size"] != size):
                continue
            entry["score"] = max(score_entry(entry, search) for search in searches)
            if entry["id"] not in found or entry["score"] > found[entry["id"]]["score"]:
                found[entry["id"]] = entry
    return sorted(found.values(), key=lambda item: (-item["score"], item["id"]))[:limit]


def title_folder(slug: str) -> str:
    return " ".join(word[:1].upper() + word[1:] for word in slug.split("_"))


def svg_url(entry: dict[str, Any], folder: str | None = None) -> str:
    folder = folder or title_folder(entry["slug"])
    return f"{RAW}/assets/{urllib.parse.quote(folder)}/SVG/{entry['id']}.svg"


def load_asset_folders(refresh: bool) -> list[str]:
    cache_file = CACHE / "asset-folders.json"
    if not refresh and cache_file.exists():
        return json.loads(cache_file.read_text(encoding="utf-8"))
    folders: list[str] = []
    try:
        for page in range(1, 30):
            url = f"{API}?ref=main&per_page=100&page={page}"
            listing = json.loads(request_bytes(url).decode("utf-8"))
            if not listing:
                break
            folders.extend(item["name"] for item in listing if item.get("type") == "dir")
            if len(listing) < 100:
                break
    except Exception:  # noqa: BLE001 - a guessed URL remains useful offline
        return []
    CACHE.mkdir(parents=True, exist_ok=True)
    cache_file.write_text(json.dumps(folders, ensure_ascii=False, indent=2), encoding="utf-8")
    return folders


def fetch_svg(entry: dict[str, Any], refresh: bool) -> tuple[str, str | None]:
    url = svg_url(entry)
    try:
        return url, request_bytes(url).decode("utf-8")
    except urllib.error.HTTPError:
        wanted = normalize(entry["slug"])
        folder = next((item for item in load_asset_folders(refresh) if normalize(item) == wanted), None)
        if folder is None:
            return url, None
        url = svg_url(entry, folder)
        try:
            return url, request_bytes(url).decode("utf-8")
        except Exception:  # noqa: BLE001
            return url, None
    except Exception:  # noqa: BLE001
        return url, None


def path_data(svg: str) -> str | None:
    try:
        root = ET.fromstring(svg)
    except ET.ParseError:
        return None
    values = [element.attrib["d"] for element in root.iter() if element.tag.rsplit("}", 1)[-1] == "path" and "d" in element.attrib]
    return " ".join(values) if values else None


def enrich(entry: dict[str, Any], fetch: bool, refresh: bool) -> dict[str, Any]:
    entry = dict(entry)
    entry["svg_url"] = svg_url(entry)
    entry["font_xaml"] = f'<FontIcon FontFamily="FluentSystemIcons" Glyph="{entry["xaml_entity"]}" />'
    if fetch:
        url, svg = fetch_svg(entry, refresh)
        entry["svg_url"] = url
        entry["path_data"] = path_data(svg) if svg else None
        if entry["path_data"]:
            entry["path_xaml"] = f'<PathIcon Data="{html.escape(entry["path_data"], quote=True)}" />'
    return entry


def print_table(entries: list[dict[str, Any]]) -> None:
    print("ID                                      HEX      SIZE STYLE    SVG")
    print("-" * 100)
    for item in entries:
        print(f"{item['id']:<39} {item['hex']:<8} {item['size']:<4} {item['style']:<8} {item['svg_url']}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Find Microsoft Fluent System Icons and emit WinUI metadata.")
    parser.add_argument("query", help="semantic name, English words, or a bundled Chinese alias")
    parser.add_argument("--size", type=int, choices=[12, 16, 20, 24, 28, 32, 48])
    parser.add_argument("--style", choices=["regular", "filled", "both"], default="both")
    parser.add_argument("--limit", type=int, default=6)
    parser.add_argument("--format", choices=["table", "json", "xaml", "all"], default="table")
    parser.add_argument("--fetch-svg", action="store_true", help="download official SVG and emit PathIcon geometry")
    parser.add_argument("--refresh", action="store_true", help="refresh cached official metadata")
    args = parser.parse_args()

    style = "both" if args.style == "both" else args.style
    try:
        entries = find_candidates(args.query, style, args.size, max(1, args.limit), args.refresh)
    except RuntimeError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    if not entries:
        print("No official Fluent System Icons matched. Try a shorter English concept, such as 'panel left' or 'arrow download'.")
        return 1
    entries = [enrich(item, args.fetch_svg, args.refresh) for item in entries]

    if args.format in {"table", "all"}:
        print_table(entries)
    if args.format in {"xaml", "all"}:
        for item in entries:
            print(f"\n{item['id']}  {item['hex']}  {item['unicode']}")
            print(item["font_xaml"])
            if item.get("path_xaml"):
                print(item["path_xaml"])
            else:
                print(f"<!-- SVG: {item['svg_url']} -->")
    if args.format == "json":
        print(json.dumps(entries, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
