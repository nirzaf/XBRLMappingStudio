#!/usr/bin/env python3
"""Create an SPDX 2.3 inventory from the locked release dependency graphs."""

from __future__ import annotations

import argparse
import base64
import hashlib
import importlib.metadata
import json
import os
import re
import subprocess
import sys
import urllib.parse
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath, PureWindowsPath
from typing import Any


RELEASE_LOCKS = (
    Path("src/XBRLMappingStudio.CLI/packages.lock.json"),
    Path("src/XBRLMappingStudio.Desktop/packages.Release.lock.json"),
)
WORKER_LOCK = Path("tools/worker/requirements.lock")
MAX_LICENSE_FILE_BYTES = 1024 * 1024
LICENSE_FILENAME = re.compile(r"^(?:licen[cs]e|copying)(?:[._-].*)?$", re.IGNORECASE)


def normalize_name(value: str) -> str:
    return re.sub(r"[-_.]+", "-", value).lower()


def spdx_ref(ecosystem: str, name: str, version: str) -> str:
    identity = f"{ecosystem}|{normalize_name(name)}|{version}".encode("utf-8")
    return f"SPDXRef-{ecosystem}-{hashlib.sha256(identity).hexdigest()[:20]}"


def is_spdx_expression(value: str) -> bool:
    token_pattern = re.compile(r"\(|\)|[A-Za-z0-9][A-Za-z0-9.+-]*(?::[A-Za-z0-9][A-Za-z0-9.+-]*)?")
    tokens = token_pattern.findall(value)
    if not tokens or "".join(tokens) != re.sub(r"\s+", "", value):
        return False
    if len(tokens) == 1 and tokens[0] in {"NONE", "NOASSERTION"}:
        return True

    index = 0

    def parse_primary() -> tuple[bool, bool]:
        nonlocal index
        if index >= len(tokens):
            return False, False
        token = tokens[index]
        if token == "(":
            index += 1
            if not parse_or() or index >= len(tokens) or tokens[index] != ")":
                return False, False
            index += 1
            return True, False
        if token in {"AND", "OR", "WITH", ")"}:
            return False, False
        index += 1
        return True, True

    def parse_with() -> bool:
        nonlocal index
        valid, simple_license = parse_primary()
        if not valid:
            return False
        if index < len(tokens) and tokens[index] == "WITH":
            if not simple_license:
                return False
            index += 1
            if index >= len(tokens) or tokens[index] in {"AND", "OR", "WITH", "(", ")"}:
                return False
            index += 1
        return True

    def parse_and() -> bool:
        nonlocal index
        if not parse_with():
            return False
        while index < len(tokens) and tokens[index] == "AND":
            index += 1
            if not parse_with():
                return False
        return True

    def parse_or() -> bool:
        nonlocal index
        if not parse_and():
            return False
        while index < len(tokens) and tokens[index] == "OR":
            index += 1
            if not parse_and():
                return False
        return True

    return parse_or() and index == len(tokens)


def _local_name(element: ET.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def _child_text(parent: ET.Element, name: str) -> str | None:
    for child in parent:
        if _local_name(child) == name and child.text:
            return child.text.strip()
    return None


def _capture_license_file(
    path: Path, ecosystem: str, name: str, version: str, source_path: str
) -> tuple[str | None, dict[str, str] | None, str]:
    try:
        with path.open("rb") as source:
            raw = source.read(MAX_LICENSE_FILE_BYTES + 1)
    except OSError as error:
        return None, None, f"Could not read license evidence {source_path}: {error}."
    if len(raw) > MAX_LICENSE_FILE_BYTES:
        return None, None, f"License evidence {source_path} exceeds the 1 MiB capture limit; text was not embedded."
    digest = hashlib.sha256(raw).hexdigest()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        return None, None, f"License evidence {source_path} is not valid UTF-8 (SHA-256 {digest}); text was not embedded."
    if not text.strip():
        return None, None, f"License evidence {source_path} is empty (SHA-256 {digest})."

    license_id = f"LicenseRef-XMS-{digest[:24]}"
    evidence = {
        "licenseId": license_id,
        "name": f"Captured license text {digest[:12]}",
        "extractedText": text,
        "comment": (
            f"Verbatim UTF-8 text from {ecosystem} package {name} {version}, file {source_path}; "
            f"source-file SHA-256 {digest}. This evidence does not constitute independent classification or redistribution approval."
        ),
    }
    note = f"Captured {source_path} verbatim as {license_id}; source-file SHA-256 {digest}."
    return license_id, evidence, note


def _merge_extracted_licenses(
    destination: dict[str, dict[str, str]], evidence_items: list[dict[str, str]]
) -> None:
    for evidence in evidence_items:
        license_id = evidence["licenseId"]
        existing = destination.get(license_id)
        if existing is None:
            destination[license_id] = evidence
        elif evidence["comment"] not in existing["comment"]:
            existing["comment"] += " Also observed: " + evidence["comment"]


def _package_license_files(package_dir: Path) -> list[Path]:
    if not package_dir.is_dir():
        return []
    return sorted(
        path for path in package_dir.iterdir()
        if path.is_file() and LICENSE_FILENAME.match(path.name)
    )


def read_nuget_license(
    package_cache: Path, name: str, version: str
) -> tuple[str, str | None, list[dict[str, str]]]:
    # NuGet global-packages paths lowercase IDs but retain dots and underscores;
    # PEP 503 normalization is only correct for the PyPI ecosystem.
    package_dir = package_cache / name.lower() / version.lower()
    nuspecs = list(package_dir.glob("*.nuspec")) if package_dir.is_dir() else []
    if not nuspecs:
        notes = ["NuGet nuspec metadata was not present in the package cache."]
        captured: list[dict[str, str]] = []
        for license_file in _package_license_files(package_dir):
            _, evidence, note = _capture_license_file(
                license_file, "NuGet", name, version, license_file.name
            )
            notes.append(note)
            if evidence:
                captured.append(evidence)
        return "NOASSERTION", " ".join(notes), captured

    try:
        document = ET.parse(nuspecs[0]).getroot()
    except ET.ParseError as error:
        return "NOASSERTION", f"NuGet package metadata could not be parsed: {error}.", []

    metadata = next((node for node in document.iter() if _local_name(node) == "metadata"), None)
    if metadata is None:
        return "NOASSERTION", "NuGet package metadata has no metadata element.", []

    license_node = next((node for node in metadata if _local_name(node) == "license"), None)
    if license_node is not None:
        license_type = license_node.attrib.get("type", "").strip().lower()
        value = (license_node.text or "").strip()
        if license_type == "expression" and value:
            if is_spdx_expression(value):
                return value, "SPDX expression copied from the package nuspec; redistribution review is separate.", []
            return "NOASSERTION", f"Nuspec license expression is not valid SPDX syntax: {value[:300]}.", []
        if license_type == "file" and value:
            normalized_value = value.replace("\\", "/")
            relative = PurePosixPath(normalized_value)
            windows_relative = PureWindowsPath(value)
            if (
                relative.is_absolute()
                or ".." in relative.parts
                or windows_relative.is_absolute()
                or windows_relative.drive
            ):
                return "NOASSERTION", f"Nuspec names an unsafe license-file path: {value}.", []
            try:
                package_root = package_dir.resolve()
                license_file = package_dir.joinpath(*relative.parts).resolve(strict=False)
                license_file.relative_to(package_root)
            except (OSError, RuntimeError, ValueError):
                return "NOASSERTION", f"Nuspec names an unsafe license-file path: {value}.", []
            if license_file.is_file():
                license_id, evidence, note = _capture_license_file(
                    license_file, "NuGet", name, version, relative.as_posix()
                )
                if license_id and evidence:
                    return license_id, f"Nuspec declares license file {value}; {note}", [evidence]
                return "NOASSERTION", f"Nuspec declares license file {value}; {note}", []
            return "NOASSERTION", f"Nuspec names license file {value}, which was not present in the package cache.", []

    legacy_url = _child_text(metadata, "licenseUrl")
    notes = []
    if legacy_url:
        notes.append(f"Legacy nuspec license URL (not dereferenced): {legacy_url}.")
    else:
        notes.append("The nuspec does not declare a machine-readable license expression.")
    captured = []
    for license_file in _package_license_files(package_dir):
        _, evidence, note = _capture_license_file(
            license_file, "NuGet", name, version, license_file.name
        )
        notes.append(note)
        if evidence:
            captured.append(evidence)
    return "NOASSERTION", " ".join(notes), captured


def _nuget_package(
    name: str,
    version: str,
    content_hash: str | None,
    package_cache: Path,
    lock_sources: set[str],
    direct_projects: set[str],
) -> tuple[dict[str, Any], list[dict[str, str]]]:
    license_declared, license_comments, extracted_licenses = read_nuget_license(package_cache, name, version)
    package: dict[str, Any] = {
        "name": name,
        "SPDXID": spdx_ref("NuGet", name, version),
        "versionInfo": version,
        "downloadLocation": "NOASSERTION",
        "filesAnalyzed": False,
        "licenseConcluded": "NOASSERTION",
        "licenseDeclared": license_declared,
        "copyrightText": "NOASSERTION",
        "externalRefs": [
            {
                "referenceCategory": "PACKAGE-MANAGER",
                "referenceType": "purl",
                "referenceLocator": f"pkg:nuget/{urllib.parse.quote(name.lower(), safe='-._~')}@{urllib.parse.quote(version, safe='-._~')}",
            }
        ],
        "sourceInfo": "Locked release dependency; lock sources: " + ", ".join(sorted(lock_sources)),
        "comment": "Direct release dependencies: "
        + (", ".join(sorted(direct_projects)) if direct_projects else "none; transitive only"),
    }
    if content_hash:
        try:
            raw_hash = base64.b64decode(content_hash, validate=True)
            package["checksums"] = [{"algorithm": "SHA512", "checksumValue": raw_hash.hex()}]
        except (ValueError, base64.binascii.Error):
            package["comment"] += " Invalid NuGet contentHash in lock file."
    if license_comments:
        package["licenseComments"] = license_comments
    return package, extracted_licenses


def read_nuget_graph(
    root: Path, package_cache: Path
) -> tuple[list[dict[str, Any]], set[tuple[str, str]], list[dict[str, str]]]:
    packages: dict[tuple[str, str], dict[str, Any]] = {}
    relationships: set[tuple[str, str]] = set()
    extracted_licenses: dict[str, dict[str, str]] = {}

    for relative_path in RELEASE_LOCKS:
        path = root / relative_path
        if not path.is_file():
            raise FileNotFoundError(f"Required release lock file is missing: {relative_path}")
        lock = json.loads(path.read_text(encoding="utf-8"))
        for target_name, graph in lock.get("dependencies", {}).items():
            resolved_by_name: dict[str, str] = {}
            package_rows: dict[str, dict[str, Any]] = {}
            for package_name, record in graph.items():
                version = record.get("resolved")
                if not version or record.get("type") not in {"Direct", "Transitive", "Package"}:
                    continue
                normalized = normalize_name(package_name)
                resolved_by_name[normalized] = version
                package_rows[normalized] = record
                key = (normalized, version)
                row = packages.get(key)
                if row is None:
                    row = {
                        "name": package_name,
                        "version": version,
                        "content_hash": record.get("contentHash"),
                        "lock_sources": set(),
                        "direct_projects": set(),
                    }
                    packages[key] = row
                elif row["content_hash"] and record.get("contentHash") and row["content_hash"] != record["contentHash"]:
                    raise ValueError(f"Conflicting locked hashes for NuGet package {package_name} {version}.")
                row["lock_sources"].add(f"{relative_path.as_posix()} ({target_name})")
                if record.get("type") == "Direct":
                    row["direct_projects"].add(path.parent.name)

            for package_name, record in package_rows.items():
                parent_version = resolved_by_name[package_name]
                parent_ref = spdx_ref("NuGet", package_name, parent_version)
                for dependency_name in record.get("dependencies", {}):
                    child_name = normalize_name(dependency_name)
                    child_version = resolved_by_name.get(child_name)
                    if child_version:
                        relationships.add((parent_ref, spdx_ref("NuGet", child_name, child_version)))

    output = []
    for _, row in sorted(packages.items()):
        package, license_evidence = _nuget_package(
            row["name"],
            row["version"],
            row["content_hash"],
            package_cache,
            row["lock_sources"],
            row["direct_projects"],
        )
        output.append(package)
        _merge_extracted_licenses(extracted_licenses, license_evidence)
    return output, relationships, list(extracted_licenses.values())


def read_worker_lock(path: Path) -> list[dict[str, Any]]:
    package_pattern = re.compile(r"^\s*([A-Za-z0-9_.-]+)==([^\s\\]+)(?:\s+\\)?\s*$")
    hash_pattern = re.compile(r"--hash=sha256:([0-9a-fA-F]{64})")
    packages: list[dict[str, Any]] = []
    current: dict[str, Any] | None = None
    reading_via = False

    for line in path.read_text(encoding="utf-8").splitlines():
        match = package_pattern.match(line)
        if match:
            if current is not None:
                packages.append(current)
            current = {
                "name": match.group(1),
                "version": match.group(2),
                "hashes": set(),
                "parents": set(),
                "direct": False,
            }
            reading_via = False
            continue
        if current is None:
            continue
        hash_match = hash_pattern.search(line)
        if hash_match:
            current["hashes"].add(hash_match.group(1).lower())
            continue
        stripped = line.strip()
        if stripped.startswith("# via"):
            reading_via = True
            parent = stripped[len("# via"):].strip()
            if parent.startswith("-r "):
                current["direct"] = True
            elif parent:
                current["parents"].add(normalize_name(parent))
            continue
        if reading_via and stripped.startswith("#"):
            parent = stripped[1:].strip()
            if parent:
                current["parents"].add(normalize_name(parent))
            continue
        if stripped.startswith("#") or not stripped:
            reading_via = False

    if current is not None:
        packages.append(current)
    if not packages:
        raise ValueError(f"No pinned Python packages were found in {path}.")

    for package in packages:
        if not package["hashes"]:
            raise ValueError(f"Python package {package['name']} {package['version']} has no SHA-256 lock hashes.")
    return packages


def _installed_metadata() -> dict[str, importlib.metadata.Distribution]:
    result: dict[str, importlib.metadata.Distribution] = {}
    for distribution in importlib.metadata.distributions():
        name = distribution.metadata.get("Name")
        if name:
            result[normalize_name(name)] = distribution
    return result


def _python_license_evidence(
    distribution: importlib.metadata.Distribution, name: str, version: str
) -> tuple[list[dict[str, str]], list[str]]:
    evidence: dict[str, dict[str, str]] = {}
    notes: list[str] = []
    for file in sorted(distribution.files or [], key=str):
        relative = Path(str(file))
        if not LICENSE_FILENAME.match(relative.name):
            continue
        try:
            path = Path(distribution.locate_file(file))
        except (OSError, TypeError, ValueError) as error:
            notes.append(f"Could not locate Python license evidence {relative}: {error}.")
            continue
        license_id, item, note = _capture_license_file(
            path, "PyPI", name, version, relative.as_posix()
        )
        notes.append(note)
        if license_id and item:
            evidence[license_id] = item
    return list(evidence.values()), notes


def read_python_graph(
    root: Path,
    worker_lock: Path,
    installed: dict[str, importlib.metadata.Distribution] | None = None,
) -> tuple[list[dict[str, Any]], set[tuple[str, str]], list[dict[str, str]]]:
    locked = read_worker_lock(root / worker_lock)
    installed = _installed_metadata() if installed is None else installed
    packages: list[dict[str, Any]] = []
    relationships: set[tuple[str, str]] = set()
    extracted_licenses: dict[str, dict[str, str]] = {}
    refs_by_name = {normalize_name(row["name"]): spdx_ref("PyPI", row["name"], row["version"]) for row in locked}

    for row in locked:
        name = row["name"]
        version = row["version"]
        distribution = installed.get(normalize_name(name))
        license_expression = "NOASSERTION"
        license_notes: list[str] = []
        package_licenses: list[dict[str, str]] = []
        if distribution is None:
            license_notes.append("Installed Python distribution metadata was unavailable; license remains NOASSERTION.")
        elif distribution.version != version:
            license_notes.append(
                f"Installed metadata version {distribution.version} does not match locked version {version}; license remains NOASSERTION."
            )
        else:
            package_licenses, file_notes = _python_license_evidence(distribution, name, version)
            license_notes.extend(file_notes)
            expression = distribution.metadata.get("License-Expression")
            if expression:
                expression = expression.strip()
                if is_spdx_expression(expression):
                    license_expression = expression
                    license_notes.append("SPDX expression copied from installed wheel metadata.")
                else:
                    license_notes.append(f"Wheel License-Expression is not valid SPDX syntax: {expression[:300]}.")
            else:
                legacy_license = distribution.metadata.get("License")
                if legacy_license:
                    legacy_license = legacy_license.strip()
                    if is_spdx_expression(legacy_license):
                        license_expression = legacy_license
                        license_notes.append("Legacy wheel License metadata contains SPDX syntax and is copied verbatim; redistribution review remains separate.")
                    else:
                        license_notes.append(f"Legacy wheel License metadata (not interpreted): {legacy_license[:500]}.")
                if license_expression == "NOASSERTION" and len(package_licenses) == 1:
                    license_expression = package_licenses[0]["licenseId"]
                    license_notes.append("No SPDX License-Expression was supplied; the single packaged license text is represented by its SPDX LicenseRef.")
                elif license_expression == "NOASSERTION":
                    license_notes.append("Wheel metadata has no SPDX License-Expression or single license text; redistribution review remains required.")

        package = {
            "name": name,
            "SPDXID": spdx_ref("PyPI", name, version),
            "versionInfo": version,
            "downloadLocation": "NOASSERTION",
            "filesAnalyzed": False,
            "licenseConcluded": "NOASSERTION",
            "licenseDeclared": license_expression,
            "copyrightText": "NOASSERTION",
            "externalRefs": [
                {
                    "referenceCategory": "PACKAGE-MANAGER",
                    "referenceType": "purl",
                    "referenceLocator": f"pkg:pypi/{urllib.parse.quote(normalize_name(name), safe='-._~')}@{urllib.parse.quote(version, safe='-._~')}",
                }
            ],
            "sourceInfo": f"Pinned in {worker_lock.as_posix()} using pip --require-hashes.",
            "comment": (
                ("Direct worker runtime requirement. " if row["direct"] else "Pinned transitive worker dependency. ")
                + "Accepted Python distribution SHA-256 hashes from the lock: "
                + ", ".join(sorted(row["hashes"]))
            ),
            "licenseComments": " ".join(license_notes),
        }
        packages.append(package)
        _merge_extracted_licenses(extracted_licenses, package_licenses)
        for parent in row["parents"]:
            parent_ref = refs_by_name.get(parent)
            if parent_ref:
                relationships.add((parent_ref, package["SPDXID"]))

    return packages, relationships, list(extracted_licenses.values())


def _source_revision(root: Path) -> tuple[str, str]:
    try:
        revision = subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=root, check=True, capture_output=True, text=True
        ).stdout.strip()
        timestamp = subprocess.run(
            ["git", "show", "-s", "--format=%cI", "HEAD"], cwd=root, check=True, capture_output=True, text=True
        ).stdout.strip()
        created = datetime.fromisoformat(timestamp.replace("Z", "+00:00")).astimezone(timezone.utc)
        return revision, created.strftime("%Y-%m-%dT%H:%M:%SZ")
    except (OSError, subprocess.CalledProcessError, ValueError):
        return "uncommitted", datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def build_spdx_document(
    root: Path,
    package_cache: Path | None = None,
    worker_lock: Path = WORKER_LOCK,
    installed_python: dict[str, importlib.metadata.Distribution] | None = None,
) -> dict[str, Any]:
    package_cache = package_cache or Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))
    nuget_packages, nuget_relationships, nuget_licenses = read_nuget_graph(root, package_cache)
    python_packages, python_relationships, python_licenses = read_python_graph(root, worker_lock, installed_python)
    packages = sorted(nuget_packages + python_packages, key=lambda row: row["SPDXID"])
    relationships = nuget_relationships | python_relationships
    relationships.update(("SPDXRef-DOCUMENT", package["SPDXID"]) for package in packages)
    revision, created = _source_revision(root)
    content_digest = hashlib.sha256(
        json.dumps(packages, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    return {
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "SPDXID": "SPDXRef-DOCUMENT",
        "name": f"XBRLMappingStudio release dependencies ({revision[:12]})",
        "documentNamespace": f"https://spdx.org/spdxdocs/xbrlmappingstudio-{revision}-{content_digest}",
        "creationInfo": {
            "creators": ["Tool: XBRLMappingStudio dependency inventory"],
            "created": created,
        },
        "packages": packages,
        "hasExtractedLicensingInfo": sorted(
            nuget_licenses + python_licenses, key=lambda item: item["licenseId"]
        ),
        "relationships": [
            {
                "spdxElementId": parent,
                "relationshipType": "DESCRIBES" if parent == "SPDXRef-DOCUMENT" else "DEPENDS_ON",
                "relatedSpdxElement": child,
            }
            for parent, child in sorted(relationships)
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="Output path for SPDX 2.3 JSON")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--nuget-package-cache", type=Path)
    args = parser.parse_args()

    document = build_spdx_document(args.root.resolve(), args.nuget_package_cache)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    unresolved = sum(package["licenseDeclared"] == "NOASSERTION" for package in document["packages"])
    print(f"SPDX components: {len(document['packages'])}; unresolved license assertions: {unresolved}; output: {args.output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
