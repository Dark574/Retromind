#!/usr/bin/env bash
set -euo pipefail

if ! command -v python3 >/dev/null 2>&1; then
  echo "[ERROR] python3 is required." >&2
  exit 1
fi

python3 - "$@" <<'PY'
import argparse
import json
import os
import re
import shutil
import sys
import uuid
import xml.etree.ElementTree as ET
from datetime import datetime
from pathlib import Path

LOGGER = None


NODE_TYPE_AREA = 0
MEDIA_TYPE_NATIVE = 0
MEDIA_TYPE_COMMAND = 2
FILE_KIND_ABSOLUTE = 0
PLAY_STATUS_INCOMPLETE = 0
PLAY_STATUS_COMPLETED = 1
PLAY_STATUS_ABANDONED = 2

ASSET_TYPE = {
    "cover": 1,
    "wallpaper": 2,
    "logo": 3,
    "video": 4,
    "marquee": 5,
    "music": 6,
    "banner": 7,
    "manual": 10,
    "screenshot": 11,
}

ASSET_TYPE_NAME = {
    "cover": "Cover",
    "wallpaper": "Wallpaper",
    "logo": "Logo",
    "video": "Video",
    "marquee": "Marquee",
    "music": "Music",
    "banner": "Banner",
    "manual": "Manual",
    "screenshot": "Screenshot",
}

DISPLAY_MARKER = "__RM__"

ASSET_FIELDS = {
    "cover": [
        "BoxFrontImagePath",
        "FrontImagePath",
        "CoverImagePath",
        "Box3DImagePath",
    ],
    "logo": [
        "ClearLogoImagePath",
        "LogoImagePath",
    ],
    "wallpaper": [
        "FanartImagePath",
        "BackgroundImagePath",
    ],
    "screenshot": [
        "ScreenshotImagePath",
        "ScreenshotPath",
    ],
    "video": [
        "VideoPath",
    ],
    "marquee": [
        "MarqueeImagePath",
    ],
    "music": [
        "MusicPath",
        "BackgroundMusicPath",
    ],
    "banner": [
        "BannerImagePath",
    ],
    "manual": [
        "ManualPath",
        "ManualFilePath",
    ],
}

URI_RE = re.compile(r"^[a-zA-Z][a-zA-Z0-9+.-]*://")
WIN_DRIVE_RE = re.compile(r"^[a-zA-Z]:[\\/]")
SAFE_RE = re.compile(r"[^A-Za-z0-9._-]+")
MATCH_RE = re.compile(r"[^a-z0-9]+")
COUNTER_TOKEN_RE = re.compile(r"^0\d{1,3}$")

if os.name == "nt":
    INVALID_FILENAME_CHARS = set('<>:"/\\|?*') | {chr(i) for i in range(32)}
else:
    INVALID_FILENAME_CHARS = {'/', "\0"}

IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"}
VIDEO_EXTS = {".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mpg", ".mpeg", ".m4v"}
MUSIC_EXTS = {".mp3", ".ogg", ".flac", ".wav", ".m4a", ".aac", ".opus", ".sid", ".nsf", ".spc"}
MANUAL_EXTS = {".pdf", ".txt", ".doc", ".docx", ".rtf", ".html", ".htm", ".md"}

KIND_EXTS = {
    "cover": IMAGE_EXTS,
    "logo": IMAGE_EXTS,
    "wallpaper": IMAGE_EXTS,
    "screenshot": IMAGE_EXTS,
    "marquee": IMAGE_EXTS,
    "banner": IMAGE_EXTS,
    "video": VIDEO_EXTS,
    "music": MUSIC_EXTS,
    "manual": MANUAL_EXTS,
}

KIND_HINTS = {
    "cover": (
        "steam poster",
        "box - front",
        "box-front",
        "box front",
        "box - front - reconstructed",
        "fanart - box - front",
        "gog poster",
        "epic games poster",
        "uplay thumbnail",
        "origin poster",
        "amazon poster",
    ),
    "logo": ("clear logo", "logo"),
    "wallpaper": ("fanart", "background", "backdrop"),
    "screenshot": ("screenshot", "gameplay"),
    "marquee": ("marquee",),
    "banner": ("banner",),
    "video": ("video", "theme"),
    "music": ("music", "sound"),
    "manual": ("manual", "docs", "guide"),
}

SCAN_MEDIA_MAX_MATCHES_PER_KIND = 20

COVER_SCAN_FOLDERS = [
    "Steam Poster",
    "Box - Front",
    "Box - Front - Reconstructed",
    "Fanart - Box - Front",
    "GOG Poster",
    "Epic Games Poster",
    "Uplay Thumbnail",
    "Origin Poster",
    "Amazon Poster",
]


class MigrationLogger:
    def __init__(self, file_path=None, verbose=False):
        self.verbose = bool(verbose)
        self._fh = None
        self._path = None

        if file_path:
            self._path = Path(file_path).expanduser()
            self._path.parent.mkdir(parents=True, exist_ok=True)
            self._fh = self._path.open("a", encoding="utf-8")

    @staticmethod
    def _ts():
        return datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    def log(self, level, message):
        line = f"[{self._ts()}] [{level}] {message}"
        if self._fh:
            self._fh.write(line + "\n")
            self._fh.flush()

        if level in {"INFO", "WARN", "ERROR"} or (level == "DEBUG" and self.verbose):
            print(f"[{level}] {message}")

    def close(self):
        if self._fh:
            self._fh.close()
            self._fh = None

    @property
    def path(self):
        return str(self._path) if self._path else None


def log_debug(msg):
    if LOGGER:
        LOGGER.log("DEBUG", msg)


def log_info(msg):
    if LOGGER:
        LOGGER.log("INFO", msg)
    else:
        print(f"[INFO] {msg}")


def log_warn(msg):
    if LOGGER:
        LOGGER.log("WARN", msg)
    else:
        print(f"[WARN] {msg}")


def log_error(msg):
    if LOGGER:
        LOGGER.log("ERROR", msg)
    else:
        print(f"[ERROR] {msg}", file=sys.stderr)


def add_warning(stats, message, key=None):
    warning_keys = stats.setdefault("_warning_keys", set())
    if key and key in warning_keys:
        return

    if key:
        warning_keys.add(key)

    max_warnings = max(0, int(stats.get("max_warnings", 300)))
    if len(stats["warnings"]) >= max_warnings:
        stats["suppressed_warnings"] = int(stats.get("suppressed_warnings", 0)) + 1
        if not stats.get("_warnings_limit_notified"):
            stats["_warnings_limit_notified"] = True
            limit_msg = f"Warning limit reached ({max_warnings}). Further warnings are suppressed."
            stats["warnings"].append(limit_msg)
            log_warn(limit_msg)
        return

    stats["warnings"].append(message)
    log_warn(message)


def bump(stats, key, amount=1):
    diagnostics = stats.setdefault("diagnostics", {})
    diagnostics[key] = int(diagnostics.get(key, 0)) + amount


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Convert LaunchBox metadata to a Retromind retromind_tree.json-compatible file. "
            "Optionally copies media assets into Retromind/Library/<Platform>/<AssetType>/."
        )
    )
    parser.add_argument("launchbox_root", help="Path to LaunchBox root folder")
    parser.add_argument("retromind_root", help="Path to Retromind root folder")
    parser.add_argument(
        "--output",
        help="Output JSON path (default: <retromind_root>/retromind_tree.launchbox.json)",
    )
    parser.add_argument(
        "--replace",
        action="store_true",
        help=(
            "After generating output JSON, backup and replace <retromind_root>/retromind_tree.json"
        ),
    )
    parser.add_argument(
        "--no-copy-assets",
        action="store_true",
        help="Do not copy assets into Retromind Library folders",
    )
    parser.add_argument(
        "--scan-media-folders",
        action="store_true",
        help=(
            "Scan LaunchBox media folders (Images/Videos/Music/Manuals) and match by game title. "
            "For cover images, scanned results are prioritized by configured folder order."
        ),
    )
    parser.add_argument(
        "--suppress-missing-launch-path-warnings",
        action="store_true",
        help="Do not report warnings when ApplicationPath/LaunchingCommand is missing.",
    )
    parser.add_argument(
        "--library-subdir",
        default="",
        help=(
            "Optional subfolder under Library for copied assets (e.g. ImportedLaunchBox). "
            "By default, imported platform nodes are wrapped under a root node with the same name."
        ),
    )
    parser.add_argument(
        "--stage-assets-only",
        action="store_true",
        help=(
            "Only stage copied assets under --library-subdir, but keep the JSON tree unchanged "
            "(no wrapper root node). In this mode, assets must be moved/copied manually afterwards."
        ),
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Parse and report only, do not write JSON or copy files",
    )
    parser.add_argument(
        "--map",
        action="append",
        default=[],
        metavar="SRC=DST",
        help=(
            "Path prefix mapping (repeatable), e.g. --map 'C:\\Games=/mnt/games'. "
            "Useful for Windows LaunchBox paths on Linux."
        ),
    )
    parser.add_argument(
        "--log-file",
        help=(
            "Path to migration log file "
            "(default: <retromind_root>/launchbox_migration.log)."
        ),
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="Enable debug-level logging.",
    )
    parser.add_argument(
        "--max-warnings",
        type=int,
        default=300,
        help="Maximum number of warning lines kept in summary/output (default: 300).",
    )
    return parser.parse_args()


def local_tag(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def safe_name(value: str) -> str:
    value = (value or "").strip()
    value = SAFE_RE.sub("_", value)
    value = value.strip("._-")
    return value or "item"


def sanitize_for_filename(value: str) -> str:
    value = (value or "").strip()
    if not value:
        return "Unknown"

    value = value.replace(" ", "_")
    value = "".join(ch for ch in value if ch not in INVALID_FILENAME_CHARS)

    while "__" in value:
        value = value.replace("__", "_")

    value = value.strip()
    return value or "Unknown"


def get_item_id_token(item_id: str) -> str:
    raw = (item_id or "").replace("-", "").strip()
    if not raw:
        return "00000000"

    if len(raw) <= 8:
        return raw.upper()

    return raw[:8].upper()


def build_item_asset_prefix(title: str, item_id: str) -> str:
    return f"{sanitize_for_filename(title)}__{get_item_id_token(item_id)}"


def build_item_asset_prefix_for_asset(title: str, item_id: str, kind: str, display_name: str) -> str:
    base_prefix = build_item_asset_prefix(title, item_id)
    if kind not in {"music", "manual"}:
        return base_prefix

    display = sanitize_for_filename(display_name)
    if not display:
        display = "Unknown"
    return f"{display}{DISPLAY_MARKER}{base_prefix}"


def get_next_asset_filename(type_folder: Path, clean_prefix: str, type_name: str, extension: str) -> str:
    prefix = f"{clean_prefix}_{type_name}_"
    max_counter = 0

    if type_folder.is_dir():
        for entry in type_folder.iterdir():
            if not entry.is_file():
                continue
            name = entry.name
            if not name.lower().startswith(prefix.lower()):
                continue
            match = re.search(r"_(\d+)\.", name)
            if not match:
                continue
            try:
                num = int(match.group(1))
            except ValueError:
                continue
            if num > max_counter:
                max_counter = num

    next_counter = max_counter + 1
    return f"{clean_prefix}_{type_name}_{next_counter:02d}{extension}"


def parse_bool(raw: str) -> bool:
    return (raw or "").strip().lower() in {"1", "true", "yes", "y"}


def parse_date(raw: str):
    raw = (raw or "").strip()
    if not raw:
        return None

    formats = [
        "%Y-%m-%d",
        "%Y-%m-%dT%H:%M:%S",
        "%Y-%m-%dT%H:%M:%S.%f",
        "%m/%d/%Y",
    ]

    for fmt in formats:
        try:
            dt = datetime.strptime(raw, fmt)
            return dt.strftime("%Y-%m-%dT00:00:00")
        except ValueError:
            pass

    if len(raw) >= 10 and raw[4] == "-" and raw[7] == "-":
        return f"{raw[:10]}T00:00:00"

    return None


def parse_datetime(raw: str, force_midnight: bool = False):
    raw = (raw or "").strip()
    if not raw:
        return None

    candidates = [
        "%Y-%m-%d",
        "%Y-%m-%dT%H:%M:%S",
        "%Y-%m-%dT%H:%M:%S.%f",
        "%Y-%m-%d %H:%M:%S",
        "%Y-%m-%d %H:%M",
        "%m/%d/%Y",
        "%m/%d/%Y %H:%M:%S",
        "%m/%d/%Y %H:%M",
    ]

    for fmt in candidates:
        try:
            dt = datetime.strptime(raw, fmt)
            if force_midnight:
                dt = dt.replace(hour=0, minute=0, second=0, microsecond=0)
            return dt.strftime("%Y-%m-%dT%H:%M:%S")
        except ValueError:
            pass

    # basic ISO fallback (supports timezone suffixes like "Z")
    try:
        normalized = raw.replace("Z", "+00:00")
        dt = datetime.fromisoformat(normalized)
        if force_midnight:
            dt = dt.replace(hour=0, minute=0, second=0, microsecond=0)
        return dt.strftime("%Y-%m-%dT%H:%M:%S")
    except ValueError:
        pass

    if len(raw) >= 10 and raw[4] == "-" and raw[7] == "-":
        date_part = raw[:10]
        return f"{date_part}T00:00:00" if force_midnight else f"{date_part}T00:00:00"

    return None


def parse_int(raw: str):
    raw = (raw or "").strip()
    if not raw:
        return None
    try:
        return int(float(raw))
    except ValueError:
        return None


def parse_rating(raw: str):
    raw = (raw or "").strip()
    if not raw:
        return None

    m = re.search(r"(?P<num>\d+(?:[.,]\d+)?)\s*(?:/\s*(?P<den>\d+(?:[.,]\d+)?))?", raw)
    if not m:
        return None

    num = float(m.group("num").replace(",", "."))
    den_raw = m.group("den")
    if den_raw:
        den = float(den_raw.replace(",", "."))
        if den > 0:
            rating = (num / den) * 100.0
        else:
            rating = num
    else:
        # LaunchBox often stores stars (0..5). Normalize to 0..100.
        if num <= 5:
            rating = num * 20.0
        elif num <= 10:
            rating = num * 10.0
        else:
            rating = num

    return max(0.0, min(100.0, round(rating, 2)))


def parse_timespan(raw: str):
    raw = (raw or "").strip()
    if not raw:
        return None

    # numeric raw value -> interpret as seconds
    try:
        seconds = int(float(raw))
        if seconds < 0:
            seconds = 0
        days, rem = divmod(seconds, 86400)
        hours, rem = divmod(rem, 3600)
        minutes, secs = divmod(rem, 60)
        if days > 0:
            return f"{days}.{hours:02}:{minutes:02}:{secs:02}"
        return f"{hours:02}:{minutes:02}:{secs:02}"
    except ValueError:
        pass

    # hh:mm:ss(.fff) or mm:ss
    parts = raw.split(":")
    if len(parts) in (2, 3):
        try:
            if len(parts) == 2:
                hours = 0
                minutes = int(parts[0])
                secs = int(float(parts[1]))
            else:
                hours = int(parts[0])
                minutes = int(parts[1])
                secs = int(float(parts[2]))

            if hours < 0 or minutes < 0 or secs < 0:
                return None

            extra_hours, minutes = divmod(minutes, 60)
            hours += extra_hours
            extra_minutes, secs = divmod(secs, 60)
            minutes += extra_minutes
            extra_hours, minutes = divmod(minutes, 60)
            hours += extra_hours

            days, hours = divmod(hours, 24)
            if days > 0:
                return f"{days}.{hours:02}:{minutes:02}:{secs:02}"
            return f"{hours:02}:{minutes:02}:{secs:02}"
        except ValueError:
            return None

    return None


def split_multi_paths(raw: str):
    raw = (raw or "").strip()
    if not raw:
        return []

    parts = re.split(r"[;|]", raw)
    return [p.strip() for p in parts if p.strip()]


def parse_mappings(raw_maps):
    mappings = []
    for entry in raw_maps:
        if "=" not in entry:
            raise ValueError(f"Invalid --map value '{entry}', expected SRC=DST")
        src, dst = entry.split("=", 1)
        src = src.strip().replace("\\", "/").rstrip("/")
        dst = dst.strip()
        if not src or not dst:
            raise ValueError(f"Invalid --map value '{entry}', expected SRC=DST")
        mappings.append((src, dst))
    if mappings:
        for src, dst in mappings:
            log_info(f"Path mapping active: {src} -> {dst}")
    return mappings


def resolve_path(raw: str, lb_root: Path, mappings):
    raw = (raw or "").strip()
    if not raw:
        return None

    if URI_RE.match(raw):
        log_debug(f"Resolved URI path as-is: {raw}")
        return raw

    unified = raw.replace("\\", "/")

    if WIN_DRIVE_RE.match(unified):
        unified_lower = unified.lower()
        for src, dst in mappings:
            src_lower = src.lower()
            if unified_lower.startswith(src_lower):
                rest = unified[len(src):].lstrip("/\\")
                resolved = str((Path(dst) / Path(rest)).resolve())
                log_debug(f"Mapped Windows path '{raw}' -> '{resolved}'")
                return resolved
        log_debug(f"Unmapped Windows path remains unchanged: {raw}")
        return unified

    p = Path(unified)
    if p.is_absolute():
        resolved = str(p)
        log_debug(f"Resolved absolute path: {resolved}")
        return resolved

    resolved = str((lb_root / p).resolve())
    log_debug(f"Resolved relative path '{raw}' -> '{resolved}'")
    return resolved


def direct_text(parent, names):
    name_set = {n.lower() for n in names}
    for child in list(parent):
        if local_tag(child.tag).lower() in name_set:
            value = (child.text or "").strip()
            if value:
                return value
    return ""


def direct_or_attr_text(node, names):
    value = direct_text(node, names).strip()
    if value:
        return value

    name_set = {n.lower() for n in names}
    for key, value in node.attrib.items():
        if key.lower() in name_set and (value or "").strip():
            return value.strip()
    return ""


def normalize_game_id(raw):
    raw = (raw or "").strip()
    if not raw:
        return ""
    return raw.strip("{}").strip().lower()


def normalize_for_match(value):
    value = (value or "").strip().lower()
    if not value:
        return ""
    return MATCH_RE.sub("", value)


def tokenize_for_match(value):
    value = (value or "").strip().lower()
    if not value:
        return []
    return [token for token in MATCH_RE.split(value) if token]


def title_matches_file_stem(title_tokens, stem):
    stem_tokens = tokenize_for_match(stem)
    if not stem_tokens:
        return False

    if len(stem_tokens) < len(title_tokens):
        return False

    if stem_tokens[:len(title_tokens)] != title_tokens:
        return False

    extra = stem_tokens[len(title_tokens):]
    if not extra:
        return True

    # Allow one trailing numeric media index (e.g. "-01").
    # Reject additional suffix words/numbers to avoid sequel cross-matches.
    return len(extra) == 1 and COUNTER_TOKEN_RE.match(extra[0]) is not None


def path_hint_matches(path_obj: Path, kind: str, match_root: Path = None):
    hint_tokens = KIND_HINTS.get(kind, ())
    if not hint_tokens:
        return True

    candidate = path_obj
    if match_root is not None:
        try:
            candidate = path_obj.resolve().relative_to(match_root.resolve())
        except Exception:
            candidate = path_obj

    # Match hints on the relevant tail of the path so generic root names
    # (e.g. "LaunchBox") do not bias type detection.
    parts = [part for part in str(candidate).replace("\\", "/").split("/") if part]
    if len(parts) > 6:
        parts = parts[-6:]
    haystack = "/".join(parts).lower()

    return any(token in haystack for token in hint_tokens)


def merge_unique_paths(paths):
    unique = []
    seen = set()
    for value in paths:
        key = (value or "").strip().lower()
        if not key or key in seen:
            continue
        seen.add(key)
        unique.append(value)
    return unique


def cover_folder_rank_from_path_text(path_text: str):
    normalized = "/" + (path_text or "").replace("\\", "/").strip("/").lower() + "/"
    for idx, folder in enumerate(COVER_SCAN_FOLDERS):
        token = f"/{folder.lower()}/"
        if token in normalized:
            return idx
    return None


def filter_and_sort_cover_paths(raw_paths):
    ranked = []
    for raw in raw_paths:
        rank = cover_folder_rank_from_path_text(raw)
        if rank is None:
            continue
        ranked.append((rank, raw.lower(), raw))

    ranked.sort(key=lambda item: (item[0], item[1]))
    return merge_unique_paths([item[2] for item in ranked])


def scan_cover_candidates(lb_root: Path, platform_name: str, title: str, max_matches: int):
    title_tokens = tokenize_for_match(title)
    if not title_tokens:
        return []

    platform_root = lb_root / "Images" / platform_name
    if not platform_root.is_dir():
        return []

    matches = []
    for folder in COVER_SCAN_FOLDERS:
        folder_root = platform_root / folder
        if not folder_root.is_dir():
            continue

        folder_hits = []
        for entry in folder_root.rglob("*"):
            if not entry.is_file():
                continue
            if entry.suffix.lower() not in IMAGE_EXTS:
                continue
            if not title_matches_file_stem(title_tokens, entry.stem):
                continue
            folder_hits.append(str(entry.resolve()))

        for match in sorted(folder_hits, key=lambda v: v.lower()):
            matches.append(match)
            if len(matches) >= max_matches:
                break

        if len(matches) >= max_matches:
            break

    return merge_unique_paths(matches)[:max_matches]



def candidate_media_roots(lb_root: Path, platform_name: str, kind: str):
    roots = []
    platform_folder = platform_name

    if kind in {"cover", "logo", "wallpaper", "screenshot", "marquee", "banner"}:
        roots.append(lb_root / "Images" / platform_folder)
    elif kind == "video":
        roots.append(lb_root / "Videos" / platform_folder)
        roots.append(lb_root / "Images" / platform_folder)
    elif kind == "music":
        roots.append(lb_root / "Music" / platform_folder)
        roots.append(lb_root / "Sounds" / platform_folder)
        roots.append(lb_root / "Sound" / platform_folder)
        roots.append(lb_root / "Images" / platform_folder)
    elif kind == "manual":
        roots.append(lb_root / "Manuals" / platform_folder)
        roots.append(lb_root / "Images" / platform_folder)

    unique = []
    seen = set()
    for root in roots:
        key = str(root).lower()
        if key in seen:
            continue
        seen.add(key)
        unique.append(root)
    return unique


def scan_media_candidates(lb_root: Path, platform_name: str, title: str, kind: str, max_matches: int):
    if kind == "cover":
        return scan_cover_candidates(
            lb_root=lb_root,
            platform_name=platform_name,
            title=title,
            max_matches=max_matches,
        )

    title_tokens = tokenize_for_match(title)
    if not title_tokens:
        return []

    exts = KIND_EXTS.get(kind, set())
    if not exts:
        return []

    matches = []
    for root in candidate_media_roots(lb_root, platform_name, kind):
        if not root.is_dir():
            continue

        for entry in root.rglob("*"):
            if not entry.is_file():
                continue

            ext = entry.suffix.lower()
            if ext not in exts:
                continue

            if not path_hint_matches(entry, kind, match_root=lb_root):
                continue

            if not title_matches_file_stem(title_tokens, entry.stem):
                continue

            matches.append(str(entry.resolve()))

            if len(matches) >= max_matches:
                break

        if len(matches) >= max_matches:
            break

    # deterministic and de-duplicated
    return merge_unique_paths(matches)[:max_matches]


def collect_custom_fields_from_game_node(game_node):
    custom_fields = {}

    for node in game_node.iter():
        if local_tag(node.tag).lower() != "customfield":
            continue

        name = direct_text(node, ["Name"]).strip()
        value = direct_text(node, ["Value"]).strip()
        if not name or not value:
            continue

        custom_fields[name] = value

    return custom_fields


def load_custom_fields_by_game_id(lb_root: Path, stats):
    data_dir = lb_root / "Data"
    if not data_dir.is_dir():
        return {}

    candidate_files = [
        data_dir / "CustomFields.xml",
        data_dir / "GameCustomFields.xml",
        data_dir / "PlatformCustomFields.xml",
    ]
    candidate_files.extend(sorted(data_dir.rglob("*Custom*Field*.xml")))
    candidate_files.extend(sorted((data_dir / "Platforms").glob("*.xml")) if (data_dir / "Platforms").is_dir() else [])

    custom_fields_by_game_id = {}
    seen_files = set()

    for custom_path in candidate_files:
        key = str(custom_path.resolve()).lower()
        if key in seen_files:
            continue
        seen_files.add(key)

        if not custom_path.is_file():
            continue

        try:
            root = ET.parse(custom_path).getroot()
        except Exception as ex:
            add_warning(stats, f"Failed to parse {custom_path.name}: {ex}", key=f"parse:{custom_path}")
            continue

        for node in root.iter():
            node_tag = local_tag(node.tag).lower()
            if node_tag not in {"customfield", "gamecustomfield", "platformcustomfield"}:
                continue

            game_id = direct_or_attr_text(
                node,
                ["GameID", "GameId", "Game", "GameGuid", "GameEntryId", "ParentGameID", "ParentId"],
            ).strip()
            game_id = normalize_game_id(game_id)
            field_name = direct_or_attr_text(node, ["Name", "Field", "Key"]).strip()
            field_value = direct_or_attr_text(node, ["Value", "Text"]).strip()

            if not game_id or not field_name or not field_value:
                continue

            custom_fields_by_game_id.setdefault(game_id, {})[field_name] = field_value

    log_info(f"Loaded custom fields for {len(custom_fields_by_game_id)} game IDs.")
    return custom_fields_by_game_id


def first_existing_field(game_node, names):
    return direct_text(game_node, names)


def collect_existing_fields(game_node, names):
    name_set = {n.lower() for n in names}
    values = []

    for child in list(game_node):
        if local_tag(child.tag).lower() not in name_set:
            continue

        raw = (child.text or "").strip()
        if not raw:
            continue

        values.extend(split_multi_paths(raw))

    if not values:
        fallback = direct_text(game_node, names)
        values.extend(split_multi_paths(fallback))

    unique = []
    seen = set()
    for value in values:
        key = value.strip().lower()
        if not key or key in seen:
            continue
        seen.add(key)
        unique.append(value)
    return unique


def derive_status(completed: bool, raw_status: str):
    status = (raw_status or "").strip().lower()

    if completed or status in {"completed", "finished", "beaten", "done", "clear"}:
        return PLAY_STATUS_COMPLETED

    if status in {"abandoned", "dropped", "quit"}:
        return PLAY_STATUS_ABANDONED

    return PLAY_STATUS_INCOMPLETE


def copy_asset(
    src_path: str,
    platform_name: str,
    title: str,
    item_id: str,
    kind: str,
    rm_root: Path,
    library_base: Path,
):
    if URI_RE.match(src_path) or WIN_DRIVE_RE.match(src_path):
        return None, "non_local_path"

    src = Path(src_path)
    if not src.is_file():
        return None, "missing_file"

    ext = src.suffix.lower() or ".bin"
    type_name = ASSET_TYPE_NAME[kind]
    node_dir = library_base / sanitize_for_filename(platform_name)
    dst_dir = node_dir / type_name
    dst_dir.mkdir(parents=True, exist_ok=True)

    display_name = src.stem
    asset_prefix = build_item_asset_prefix_for_asset(title, item_id, kind, display_name)
    dst_name = get_next_asset_filename(dst_dir, asset_prefix, type_name, ext)
    dst_path = dst_dir / dst_name

    src_stat = src.stat()
    for existing in dst_dir.iterdir():
        if not existing.is_file():
            continue
        if existing.suffix.lower() != ext:
            continue
        if not existing.name.lower().startswith(f"{asset_prefix}_{type_name}_".lower()):
            continue
        dst_stat = existing.stat()
        if src_stat.st_size == dst_stat.st_size and int(src_stat.st_mtime) == int(dst_stat.st_mtime):
            return os.path.relpath(existing, rm_root).replace(os.sep, "/"), "already_present"

    try:
        shutil.copy2(src, dst_path)
    except Exception as ex:
        return None, f"copy_failed:{ex}"
    return os.path.relpath(dst_path, rm_root).replace(os.sep, "/"), "copied"


def game_to_item(
    game_node,
    platform_name,
    lb_root: Path,
    rm_root: Path,
    library_base: Path,
    copy_assets: bool,
    mappings,
    custom_fields_by_game_id,
    scan_media_folders: bool,
    suppress_missing_launch_path_warnings: bool,
    stats,
):
    title = direct_text(game_node, ["Title"]) or "Untitled"
    game_id = normalize_game_id(direct_text(game_node, ["ID", "Id"]).strip())
    item_id = str(uuid.uuid4())
    item_key = f"{platform_name}::{title}::{game_id or 'no-id'}"

    app_path_raw = direct_text(game_node, ["ApplicationPath", "LaunchingCommand"])
    app_path = resolve_path(app_path_raw, lb_root, mappings) if app_path_raw else None
    if not app_path_raw:
        bump(stats, "missing_app_path")
        if not suppress_missing_launch_path_warnings:
            add_warning(
                stats,
                f"[{platform_name}] '{title}': no ApplicationPath/LaunchingCommand.",
                key=f"missing-app:{item_key}",
            )
    elif app_path and WIN_DRIVE_RE.match(app_path):
        bump(stats, "unmapped_windows_launch_path")
        add_warning(
            stats,
            f"[{platform_name}] '{title}': unresolved Windows launch path '{app_path}' (consider --map).",
            key=f"windows-app:{item_key}:{app_path}",
        )

    command_line = direct_text(game_node, ["CommandLine", "ApplicationCommandLine"])
    notes = direct_text(game_node, ["Notes"])
    developer = direct_text(game_node, ["Developer"])
    publisher = direct_text(game_node, ["Publisher"])
    genre = direct_text(game_node, ["Genre"])
    platform = direct_text(game_node, ["Platform"]) or platform_name
    series = direct_text(game_node, ["Series"])
    source = direct_text(game_node, ["Source"])
    release_type = direct_text(game_node, ["ReleaseType"])
    sort_title = direct_text(game_node, ["SortTitle"])
    play_mode = direct_text(game_node, ["PlayMode"])
    max_players = direct_text(game_node, ["MaxPlayers"])
    favorite = parse_bool(direct_text(game_node, ["Favorite", "Star"]))
    completed = parse_bool(direct_text(game_node, ["Completed"]))
    status_raw = direct_text(game_node, ["Status", "CompletionStatus"])
    release_date = parse_datetime(direct_text(game_node, ["ReleaseDate"]), force_midnight=True)
    if not release_date:
        release_date = parse_date(direct_text(game_node, ["ReleaseDate"]))
    rating = parse_rating(direct_text(game_node, ["CommunityStarRating", "StarRating", "UserStarRating", "Rating"]))
    play_count = parse_int(direct_text(game_node, ["PlayCount", "TimesPlayed"]))
    last_played = parse_datetime(direct_text(game_node, ["LastPlayedDate", "LastPlayed"]))
    total_play_time = parse_timespan(direct_text(game_node, ["TotalPlayTime", "PlayTime", "TotalTimePlayed"]))

    media_type = MEDIA_TYPE_NATIVE
    files = []
    if app_path:
        if URI_RE.match(app_path):
            media_type = MEDIA_TYPE_COMMAND
            bump(stats, "command_items")
        files.append(
            {
                "Kind": FILE_KIND_ABSOLUTE,
                "Path": app_path,
                "Label": None,
                "Index": 1,
            }
        )

    tags = ["Source:LaunchBox", f"Platform:{platform}"]
    custom_fields = {}
    if game_id and game_id in custom_fields_by_game_id:
        custom_fields.update(custom_fields_by_game_id[game_id])
    custom_fields.update(collect_custom_fields_from_game_node(game_node))

    assets = []
    linked_paths = set()
    for kind, fields in ASSET_FIELDS.items():
        raw_paths = collect_existing_fields(game_node, fields)

        if kind == "cover":
            raw_paths = filter_and_sort_cover_paths(raw_paths)

        if scan_media_folders and (kind == "cover" or not raw_paths):
            scanned_paths = scan_media_candidates(
                lb_root=lb_root,
                platform_name=platform_name,
                title=title,
                kind=kind,
                max_matches=SCAN_MEDIA_MAX_MATCHES_PER_KIND,
            )
            if scanned_paths:
                bump(stats, f"asset_scanned_{kind}")
                log_debug(
                    f"[{platform_name}] '{title}': matched {len(scanned_paths)} {kind} asset(s) via media folder scan."
                )
                if kind == "cover":
                    # Keep configured folder priority from scan results, then append XML-defined paths.
                    raw_paths = merge_unique_paths(scanned_paths + raw_paths)
                else:
                    raw_paths = scanned_paths

        if not raw_paths:
            continue

        for raw_asset_path in raw_paths:
            resolved = resolve_path(raw_asset_path, lb_root, mappings)
            if not resolved:
                bump(stats, "asset_unresolved")
                add_warning(
                    stats,
                    f"[{platform_name}] '{title}': could not resolve asset path '{raw_asset_path}' ({kind}).",
                    key=f"asset-unresolved:{item_key}:{kind}:{raw_asset_path}",
                )
                continue

            if WIN_DRIVE_RE.match(resolved):
                bump(stats, "asset_windows_path_unmapped")
                add_warning(
                    stats,
                    f"[{platform_name}] '{title}': asset path still Windows-style '{resolved}' ({kind}); use --map.",
                    key=f"asset-windows:{item_key}:{kind}:{resolved}",
                )
                continue

            if URI_RE.match(resolved):
                bump(stats, "asset_uri_skipped")
                add_warning(
                    stats,
                    f"[{platform_name}] '{title}': skipping remote URI asset '{resolved}' ({kind}).",
                    key=f"asset-uri:{item_key}:{kind}:{resolved}",
                )
                continue

            # Avoid broad-field misclassification and duplicate linking across kinds.
            resolved_path_obj = Path(resolved)
            if resolved_path_obj.is_file() and not path_hint_matches(resolved_path_obj, kind, match_root=lb_root):
                bump(stats, "asset_kind_hint_mismatch")
                log_debug(
                    f"[{platform_name}] '{title}': skipped {kind} asset due to hint mismatch: '{resolved}'"
                )
                continue

            linked_key = resolved.lower()
            if linked_key in linked_paths:
                bump(stats, "asset_duplicate_path_skipped")
                continue

            relative_path = None
            if copy_assets:
                relative_path, copy_state = copy_asset(
                    src_path=resolved,
                    platform_name=platform_name,
                    title=title,
                    item_id=item_id,
                    kind=kind,
                    rm_root=rm_root,
                    library_base=library_base,
                )
                if copy_state == "copied":
                    bump(stats, "asset_files_copied")
                    log_debug(f"[{platform_name}] '{title}': copied {kind} asset -> {relative_path}")
                elif copy_state == "already_present":
                    bump(stats, "asset_files_reused")
                else:
                    bump(stats, "asset_copy_skipped_or_failed")
                    add_warning(
                        stats,
                        f"[{platform_name}] '{title}': asset '{resolved}' not imported ({kind}, reason={copy_state}).",
                        key=f"asset-copy:{item_key}:{kind}:{resolved}:{copy_state}",
                    )
            else:
                bump(stats, "asset_copy_disabled_paths_seen")
                log_debug(f"[{platform_name}] '{title}': copy disabled, skipped asset path '{resolved}' ({kind})")

            if relative_path:
                assets.append(
                    {
                        "Id": str(uuid.uuid4()),
                        "Type": ASSET_TYPE[kind],
                        "RelativePath": relative_path,
                    }
                )
                linked_paths.add(linked_key)
                bump(stats, "assets_linked_to_items")

    item = {
        "Id": item_id,
        "Title": title,
        "Files": files,
        "MediaType": media_type,
        "Description": notes,
        "Developer": developer or None,
        "Publisher": publisher or None,
        "Platform": platform or None,
        "Source": source or None,
        "Genre": genre or None,
        "Series": series or None,
        "ReleaseType": release_type or None,
        "SortTitle": sort_title or None,
        "PlayMode": play_mode or None,
        "MaxPlayers": max_players or None,
        "ReleaseDate": release_date,
        "Rating": rating,
        "Status": derive_status(completed, status_raw),
        "IsFavorite": favorite,
        "IsProtected": False,
        "LastPlayed": last_played,
        "PlayCount": play_count,
        "TotalPlayTime": total_play_time,
        "Tags": tags,
        "CustomFields": custom_fields,
        "Assets": assets,
        "LauncherArgs": command_line or None,
        "EnvironmentOverrides": {},
    }

    return {k: v for k, v in item.items() if v is not None}


def build_tree(
    lb_root: Path,
    rm_root: Path,
    copy_assets: bool,
    library_subdir: str,
    stage_assets_only: bool,
    mappings,
    max_warnings=300,
    scan_media_folders=False,
    suppress_missing_launch_path_warnings=False,
):
    platforms_dir = lb_root / "Data" / "Platforms"
    if not platforms_dir.is_dir():
        raise FileNotFoundError(f"LaunchBox platforms folder not found: {platforms_dir}")

    library_base = rm_root / "Library"
    root_wrapper_name = (library_subdir or "").strip()
    if root_wrapper_name:
        library_base = library_base / sanitize_for_filename(root_wrapper_name)
    if copy_assets:
        library_base.mkdir(parents=True, exist_ok=True)

    nodes = []
    stats = {
        "platforms": 0,
        "items": 0,
        "assets": 0,
        "custom_fields": 0,
        "warnings": [],
        "suppressed_warnings": 0,
        "max_warnings": max_warnings,
        "diagnostics": {},
    }

    custom_fields_by_game_id = load_custom_fields_by_game_id(lb_root, stats)
    xml_files = sorted(platforms_dir.glob("*.xml"), key=lambda p: p.name.lower())

    for xml_path in xml_files:
        platform_name = xml_path.stem
        try:
            root = ET.parse(xml_path).getroot()
        except Exception as ex:
            add_warning(stats, f"Failed to parse {xml_path.name}: {ex}", key=f"parse:{xml_path}")
            continue

        games = [n for n in root.iter() if local_tag(n.tag).lower() == "game"]
        if not games:
            continue

        items = []
        for game in games:
            item = game_to_item(
                game_node=game,
                platform_name=platform_name,
                lb_root=lb_root,
                rm_root=rm_root,
                library_base=library_base,
                copy_assets=copy_assets,
                mappings=mappings,
                custom_fields_by_game_id=custom_fields_by_game_id,
                scan_media_folders=scan_media_folders,
                suppress_missing_launch_path_warnings=suppress_missing_launch_path_warnings,
                stats=stats,
            )
            items.append(item)
            stats["assets"] += len(item.get("Assets", []))
            stats["custom_fields"] += len(item.get("CustomFields", {}))

        items.sort(key=lambda x: x.get("Title", "").lower())

        node = {
            "Id": str(uuid.uuid4()),
            "Name": platform_name,
            "Type": NODE_TYPE_AREA,
            "IsExpanded": True,
            "Description": "Imported from LaunchBox",
            "LogoFallbackEnabled": False,
            "WallpaperFallbackEnabled": False,
            "VideoFallbackEnabled": False,
            "MarqueeFallbackEnabled": False,
            "Assets": [],
            "Children": [],
            "Items": items,
        }

        nodes.append(node)
        stats["platforms"] += 1
        stats["items"] += len(items)

    nodes.sort(key=lambda n: n["Name"].lower())

    if root_wrapper_name and not stage_assets_only:
        wrapped = {
            "Id": str(uuid.uuid4()),
            "Name": root_wrapper_name,
            "Type": NODE_TYPE_AREA,
            "IsExpanded": True,
            "Description": "Imported from LaunchBox",
            "LogoFallbackEnabled": False,
            "WallpaperFallbackEnabled": False,
            "VideoFallbackEnabled": False,
            "MarqueeFallbackEnabled": False,
            "Assets": [],
            "Children": nodes,
            "Items": [],
        }
        return [wrapped], stats

    return nodes, stats


def main():
    global LOGGER
    args = parse_args()

    lb_root = Path(args.launchbox_root).expanduser().resolve()
    rm_root = Path(args.retromind_root).expanduser().resolve()

    if not lb_root.exists():
        raise SystemExit(f"[ERROR] LaunchBox root does not exist: {lb_root}")

    rm_root.mkdir(parents=True, exist_ok=True)

    output_path = Path(args.output).expanduser().resolve() if args.output else (rm_root / "retromind_tree.launchbox.json")
    log_path = Path(args.log_file).expanduser().resolve() if args.log_file else (rm_root / "launchbox_migration.log")
    LOGGER = MigrationLogger(file_path=log_path, verbose=args.verbose)
    log_info("Starting LaunchBox migration.")
    log_info(f"LaunchBox root: {lb_root}")
    log_info(f"Retromind root: {rm_root}")
    log_info(f"Output JSON: {output_path}")
    log_info(f"Copy assets: {not args.no_copy_assets and not args.dry_run}")
    log_info(
        "Library subdir: "
        + (sanitize_for_filename(args.library_subdir.strip()) if (args.library_subdir or "").strip() else "(none)")
    )
    log_info(f"Stage assets only: {args.stage_assets_only}")
    log_info(f"Dry run: {args.dry_run}")
    log_info(f"Scan media folders: {args.scan_media_folders}")
    log_info(f"Suppress missing launch path warnings: {args.suppress_missing_launch_path_warnings}")
    log_info(f"Verbose: {args.verbose}")

    try:
        mappings = parse_mappings(args.map)
    except ValueError as ex:
        log_error(str(ex))
        if LOGGER:
            LOGGER.close()
        raise SystemExit(f"[ERROR] {ex}")

    if args.stage_assets_only and not (args.library_subdir or "").strip():
        msg = "--stage-assets-only requires --library-subdir."
        log_error(msg)
        if LOGGER:
            LOGGER.close()
        raise SystemExit(f"[ERROR] {msg}")

    nodes, stats = build_tree(
        lb_root=lb_root,
        rm_root=rm_root,
        copy_assets=not args.no_copy_assets and not args.dry_run,
        library_subdir=args.library_subdir,
        stage_assets_only=args.stage_assets_only,
        mappings=mappings,
        max_warnings=args.max_warnings,
        scan_media_folders=args.scan_media_folders,
        suppress_missing_launch_path_warnings=args.suppress_missing_launch_path_warnings,
    )

    log_info(f"Platforms converted: {stats['platforms']}")
    log_info(f"Items converted: {stats['items']}")
    log_info(f"Assets linked in JSON: {stats['assets']}")
    log_info(f"Custom fields imported: {stats['custom_fields']}")
    diagnostics = stats.get("diagnostics", {})
    if diagnostics:
        log_info("Diagnostics:")
        for key in sorted(diagnostics):
            log_info(f"  - {key}: {diagnostics[key]}")

    if stats["warnings"]:
        log_warn("Issues detected:")
        for warning in stats["warnings"]:
            print(f"  - {warning}")
        suppressed = int(stats.get("suppressed_warnings", 0))
        if suppressed > 0:
            log_warn(f"Suppressed warnings due to cap: {suppressed}")

    if args.dry_run:
        log_info("Dry run complete. No files were written.")
        if LOGGER and LOGGER.path:
            log_info(f"Detailed log: {LOGGER.path}")
            LOGGER.close()
        return

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8") as fh:
        json.dump(nodes, fh, indent=2, ensure_ascii=False)

    log_info(f"Wrote converted tree to: {output_path}")

    if args.replace:
        target = rm_root / "retromind_tree.json"
        backup = rm_root / "retromind_tree.pre_launchbox_backup.json"

        if target.exists():
            shutil.copy2(target, backup)
            log_info(f"Backup created: {backup}")

        shutil.copy2(output_path, target)
        log_info(f"Replaced active tree: {target}")

    if LOGGER and LOGGER.path:
        log_info(f"Detailed log: {LOGGER.path}")
        LOGGER.close()


if __name__ == "__main__":
    main()
PY
