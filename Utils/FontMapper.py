"""
FontMapper.py

Production-oriented utility that generates a mapping between Microsoft Typography
fonts and Windows Font Feature-on-Demand (FOD) capabilities.

Example
-------
python FontMapper.py --output FontMapping.json --workers 16
"""

from __future__ import annotations

import argparse
import json
import logging
import re
import subprocess
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from enum import Enum
from pathlib import Path
from typing import Optional
from urllib.parse import urljoin

import requests
from bs4 import BeautifulSoup
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry

BASE_URL = "https://learn.microsoft.com/en-us/typography/font-list/"
HEADERS = {"User-Agent": "FontMappingGenerator/4.0"}

CORE_SCRIPTS = {"latn", "cyrl", "grek", "armn"}
CJK_SCRIPTS = {"hans", "hant", "jpan", "kore"}
SPECIAL_SCRIPTS = {"zsym", "zxxx", "zyyy", "zzzz"}


class MappingStatus(Enum):
    MAPPED = "mapped"
    SYSTEM = "system"
    SYMBOL = "symbol"
    NO_MATCH = "no_match"
    NO_SCRIPTS = "no_scripts"


@dataclass(frozen=True)
class FontInfo:
    name: str
    slug: str


def configure_logging(verbose: bool = False) -> None:
    level = logging.DEBUG if verbose else logging.INFO
    logging.basicConfig(
        level=level,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )


logger = logging.getLogger("fontmapper")


def create_session() -> requests.Session:
    session = requests.Session()
    session.headers.update(HEADERS)

    retry = Retry(
        total=3,
        backoff_factor=1.0,
        status_forcelist=[429, 500, 502, 503, 504],
        allowed_methods=["GET"],
    )

    adapter = HTTPAdapter(max_retries=retry)
    session.mount("http://", adapter)
    session.mount("https://", adapter)

    return session


def get_windows_capabilities() -> list[str]:
    """
    Query Windows for available font Feature-on-Demand capabilities.

    Returns
    -------
    list[str]
        Capability names reported by Windows. Returns an empty list if
        capability discovery is unavailable.
    """
    cmd = [
        "powershell",
        "-Command",
        (
            "Get-WindowsCapability -Online | "
            "Where-Object Name -like 'Language.Fonts.*' | "
            "Select-Object -ExpandProperty Name"
        ),
    ]

    try:
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            check=True,
        )
        capabilities = [
            line.strip() for line in result.stdout.splitlines() if line.strip()
        ]
        logger.info("Discovered %d Windows font capabilities.", len(capabilities))
        return capabilities

    except (FileNotFoundError, subprocess.SubprocessError):
        logger.warning(
            "Windows capability discovery unavailable. "
            "Running without local capability data."
        )
        return []


def build_capability_index(capabilities: list[str]) -> dict[str, str]:
    """
    Build a fast lookup table keyed by OpenType script code.
    """
    index: dict[str, str] = {}

    for capability in capabilities:
        match = re.search(r"\.(\w{4})~~~", capability.lower())
        if match:
            index[match.group(1)] = capability

    return index


def get_font_catalog(session: requests.Session) -> dict[str, str]:
    """
    Retrieve the Microsoft Typography font index and map font names to slugs.
    """
    logger.info("Fetching Microsoft Typography index...")

    response = session.get(BASE_URL, timeout=30)
    response.raise_for_status()

    soup = BeautifulSoup(response.text, "html.parser")

    font_map: dict[str, str] = {}

    for link in soup.find_all("a", {"data-linktype": "relative-path"}):
        slug = link.get("href", "").strip()
        name = link.get_text(strip=True)

        if not slug or slug.startswith("#") or slug.startswith("..") or "/" in slug:
            continue

        clean_name = re.sub(r"[\*0-9]", "", name).strip()

        if not clean_name or len(clean_name) >= 60:
            continue

        if any(
            word in clean_name.lower()
            for word in ("feedback", "overview", "index", "typography")
        ):
            continue

        font_map[clean_name] = slug

    logger.info("Discovered %d font entries.", len(font_map))
    return font_map


def extract_scripts_from_font_page(
    session: requests.Session,
    slug: str,
) -> Optional[list[str]]:
    """
    Extract OpenType design-language script identifiers from a font page.

    Returns
    -------
    list[str]
        Unique script identifiers.

    None
        If the page is unavailable.
    """
    url = urljoin(BASE_URL, slug)

    try:
        response = session.get(url, timeout=15)

        if response.status_code == 404:
            return None

        response.raise_for_status()

    except requests.RequestException:
        return None

    match = re.search(
        r"dlng\s*:\s*((?:'[^']+'\s*,?\s*)+)",
        response.text,
        re.IGNORECASE,
    )

    if not match:
        return []

    scripts = re.findall(r"'([A-Za-z]{4})'", match.group(1))

    return list(dict.fromkeys(script.strip() for script in scripts))


def classify_font(
    scripts: list[str],
    capability_index: dict[str, str],
) -> tuple[Optional[str], MappingStatus]:
    """
    Classify a font and determine its Windows capability mapping.
    """
    if not scripts:
        return None, MappingStatus.NO_SCRIPTS

    normalized = list(dict.fromkeys(s.lower() for s in scripts))

    if any(script in SPECIAL_SCRIPTS for script in normalized):
        return None, MappingStatus.SYMBOL

    if any(script in CJK_SCRIPTS for script in normalized):
        for script in normalized:
            if script in capability_index:
                return capability_index[script], MappingStatus.MAPPED
        return None, MappingStatus.NO_MATCH

    if CORE_SCRIPTS.issubset(set(normalized)):
        return None, MappingStatus.SYSTEM

    for script in normalized:
        capability = capability_index.get(script)
        if capability:
            return capability, MappingStatus.MAPPED

    if set(normalized).issubset(CORE_SCRIPTS):
        return None, MappingStatus.SYSTEM

    return None, MappingStatus.NO_MATCH


def generate_mapping(
    session: requests.Session,
    font_catalog: dict[str, str],
    workers: int,
) -> dict[str, str]:
    """
    Generate the final font-to-capability mapping.
    """
    capabilities = get_windows_capabilities()
    capability_index = build_capability_index(capabilities)

    results: dict[str, str] = {}

    logger.info("Processing %d fonts...", len(font_catalog))

    with ThreadPoolExecutor(max_workers=workers) as executor:
        futures = {
            executor.submit(
                extract_scripts_from_font_page,
                session,
                slug,
            ): (font_name, slug)
            for font_name, slug in font_catalog.items()
        }

        for future in as_completed(futures):
            font_name, slug = futures[future]

            try:
                scripts = future.result()
            except Exception:
                logger.exception("Failed processing %s", font_name)
                continue

            if scripts is None:
                logger.warning("%s -> page not found (%s)", font_name, slug)
                continue

            capability, status = classify_font(
                scripts,
                capability_index,
            )

            if status is MappingStatus.MAPPED and capability:
                results[font_name] = capability
                logger.debug("%s -> %s", font_name, capability)

    logger.info("Generated %d capability mappings.", len(results))
    return results


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate Windows font capability mappings."
    )

    parser.add_argument(
        "--output",
        default="FontMapping.json",
        help="Output JSON file path.",
    )

    parser.add_argument(
        "--workers",
        type=int,
        default=16,
        help="Concurrent worker count for page retrieval.",
    )

    parser.add_argument(
        "--verbose",
        action="store_true",
        help="Enable verbose logging.",
    )

    return parser.parse_args()


def main() -> int:
    args = parse_args()

    configure_logging(args.verbose)

    session = create_session()

    try:
        font_catalog = get_font_catalog(session)

        if not font_catalog:
            logger.error("No fonts discovered from Microsoft Typography.")
            return 1

        mapping = generate_mapping(
            session=session,
            font_catalog=font_catalog,
            workers=args.workers,
        )

        output = {
            "windowsCapabilities": mapping,
        }

        output_path = Path(args.output)

        with output_path.open("w", encoding="utf-8") as fp:
            json.dump(output, fp, indent=2, ensure_ascii=False)

        logger.info("Wrote output to %s", output_path.resolve())
        return 0

    except Exception:
        logger.exception("Fatal error.")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
