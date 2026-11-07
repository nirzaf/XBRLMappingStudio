#!/usr/bin/env python3
"""Offline Arelle worker using the supported Session API."""

import argparse
from collections import deque
from decimal import Decimal, InvalidOperation
import hashlib
import json
import math
import os
import posixpath
import re
import sys
import tempfile
import time
import stat
import shutil
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import unquote, urlsplit, urlunsplit
from urllib.request import url2pathname
from xml.parsers import expat
from xml.etree import ElementTree

PROTOCOL_VERSION = 2
SUPPORTED_PROFILE_SCHEMA_VERSIONS = (2, 3, 4)
MAX_MESSAGE_BYTES = 4 * 1024 * 1024
MAX_RESPONSE_DIAGNOSTICS = 128
MAX_DIAGNOSTIC_MESSAGE_CHARS = 2_000
ENGINE_VERSION = "arelle-worker/1.0"
CAPABILITIES = ["handshake", "load-taxonomy", "validate-report"]
MAX_PROFILE_MANIFEST_BYTES = 16 * 1024 * 1024
MAX_PROFILE_FILE_BYTES = 512 * 1024 * 1024
MAX_PROFILE_TOTAL_BYTES = 2 * 1024 * 1024 * 1024
MAX_PROFILE_FILES = 16384
MAX_PROFILE_PRESENTATION_RELATIONSHIPS = 1_000_000
MAX_DOCUMENT_INFO_BYTES = 4 * 1024 * 1024
MAX_LARGE_REPORT_BYTES = 2 * 1024 * 1024 * 1024
MAX_TAXONOMY_GRAPH_FILES = 4096
MAX_TAXONOMY_GRAPH_DEPTH = 128
MAX_TAXONOMY_GRAPH_XML_BYTES = 256 * 1024 * 1024
MAX_TAXONOMY_XML_FILE_BYTES = 16 * 1024 * 1024
MAX_TAXONOMY_GRAPH_EDGES = 16384
TAXONOMY_XML_READ_BYTES = 64 * 1024
MAX_TAXONOMY_OFFLINE_ALIAS_BYTES = 256 * 1024 * 1024

XSD_NAMESPACE = "http://www.w3.org/2001/XMLSchema"
XSI_NAMESPACE = "http://www.w3.org/2001/XMLSchema-instance"
XLINK_NAMESPACE = "http://www.w3.org/1999/xlink"
LINK_NAMESPACE = "http://www.xbrl.org/2003/linkbase"
XML_NAMESPACE = "http://www.w3.org/XML/1998/namespace"
OASIS_CATALOG_NAMESPACE = "urn:oasis:names:tc:entity:xmlns:xml:catalog"

try:
    from arelle.api.Session import Session
    from arelle.RuntimeOptions import RuntimeOptions

    ARELLE_AVAILABLE = True
except Exception:  # The host must fail closed if this optional runtime is absent.
    RuntimeOptions = None
    Session = None
    ARELLE_AVAILABLE = False


def emit(obj):
    encoded = json.dumps(obj, ensure_ascii=False, separators=(",", ":"))
    if len(encoded.encode("utf-8")) + 1 > MAX_MESSAGE_BYTES:
        candidate = obj.get("operationId") if isinstance(obj, dict) else None
        operation_id = candidate if isinstance(candidate, str) and len(candidate) <= 128 else "-"
        encoded = json.dumps(response(
            operation_id, "domainFailure", error="Worker response exceeds the 4 MiB limit."),
            ensure_ascii=False, separators=(",", ":"))
    sys.stdout.write(encoded + "\n")
    sys.stdout.flush()


def response(op_id, status, result=None, diagnostics=None, error=None):
    return {
        "operationId": op_id,
        "status": status,
        "result": result,
        "diagnostics": diagnostics or [],
        "error": error,
    }


def diagnostic(op_id, code, level, message, rule=None):
    severity = str(level or "error").lower()
    severity = {"info": "information", "warn": "warning", "fatal": "critical"}.get(severity, severity)
    if severity not in ("information", "warning", "error", "critical"):
        severity = "error"
    return {
        "code": code,
        "subsystem": "XBRL",
        "severity": severity,
        "stage": "validation",
        "isBlocking": severity in ("error", "critical"),
        "message": message,
        "operationId": op_id,
        "ruleIdentifier": rule,
    }


def valid_request(req):
    required = ("operation", "operationId", "jobId", "profileDigest", "deadlineUtc")
    if not isinstance(req, dict) or not all(isinstance(req.get(key), str) for key in required):
        return False
    limits = {"operation": 64, "operationId": 128, "jobId": 128,
              "profileDigest": 256, "deadlineUtc": 64}
    return all(0 < len(req[key]) <= limits[key] for key in required)


def is_expired(req):
    deadline = datetime.fromisoformat(req["deadlineUtc"].replace("Z", "+00:00"))
    if deadline.tzinfo is None:
        deadline = deadline.replace(tzinfo=timezone.utc)
    return deadline.timestamp() <= time.time()


def within_root(path, root):
    root_path = Path(root).resolve(strict=True)
    target_path = Path(path)
    if not target_path.is_absolute():
        target_path = root_path / target_path
    target = target_path.resolve(strict=True)
    if os.path.commonpath((str(root_path), str(target))) != str(root_path):
        raise ValueError("Path escapes the configured taxonomy root")
    return target


def safe_package_path(relative_path, taxonomy_root):
    if (not isinstance(relative_path, str) or not relative_path or len(relative_path) > 1024
            or "\\" in relative_path or ":" in relative_path or "\x00" in relative_path
            or relative_path.startswith("/")
            or any(part in ("", ".", "..") for part in relative_path.split("/"))):
        raise ValueError("Taxonomy package paths must be safe relative paths using forward slashes")
    if taxonomy_root is None or is_linklike(Path(taxonomy_root)):
        raise ValueError("A non-symbolic taxonomy root is required")
    root = Path(taxonomy_root).resolve(strict=True)
    current = root
    for part in relative_path.split("/"):
        current = current / part
        if is_linklike(current):
            raise ValueError("Taxonomy package paths cannot contain symbolic links: " + relative_path)
    target = within_root(current, root)
    if not target.is_file():
        raise ValueError("Taxonomy package path is not a regular file: " + relative_path)
    return target


def is_linklike(path):
    is_junction = getattr(path, "is_junction", None)
    return path.is_symlink() or (is_junction is not None and is_junction())


def load_profile_manifest(manifest_path, taxonomy_root, expected_file_digest=None):
    if taxonomy_root is None:
        raise ValueError("A taxonomy root is required for a profile manifest")
    root_value = Path(taxonomy_root)
    if is_linklike(root_value):
        raise ValueError("Profile taxonomy root cannot be a symbolic link")
    root = root_value.resolve(strict=True)
    manifest_value = Path(manifest_path)
    try:
        relative_manifest = Path(os.path.abspath(manifest_value)).relative_to(root).as_posix()
    except (OSError, ValueError) as ex:
        raise ValueError("Profile manifest must be inside the configured taxonomy root") from ex
    path = safe_package_path(relative_manifest, root)
    if path.stat().st_size <= 0 or path.stat().st_size > MAX_PROFILE_MANIFEST_BYTES:
        raise ValueError("Profile manifest is empty or exceeds the size limit")
    try:
        with path.open("rb") as source:
            manifest_bytes = source.read(MAX_PROFILE_MANIFEST_BYTES + 1)
        if len(manifest_bytes) > MAX_PROFILE_MANIFEST_BYTES:
            raise ValueError("Profile manifest exceeds the size limit")
        actual_manifest_digest = "sha256:" + hashlib.sha256(manifest_bytes).hexdigest()
        if expected_file_digest is not None and actual_manifest_digest != expected_file_digest:
            raise ValueError("Installed profile manifest changed after the application verified it")
        manifest = json.loads(manifest_bytes.decode("utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as ex:
        raise ValueError("Profile manifest is not valid UTF-8 JSON") from ex
    if not isinstance(manifest, dict):
        raise ValueError("Profile manifest must be a JSON object")
    schema_version = manifest.get("schemaVersion")
    if type(schema_version) is not int or schema_version not in SUPPORTED_PROFILE_SCHEMA_VERSIONS:
        raise ValueError("Profile manifest uses an unsupported schema version")
    dimension_domains = manifest.get("dimensionDomains", {})
    if not isinstance(dimension_domains, dict):
        raise ValueError("Profile dimensionDomains must be an object")
    if schema_version < 3 and dimension_domains:
        raise ValueError("Profile dimension domains require manifest schema version 3")
    if dimension_domains:
        concepts = manifest.get("concepts")
        if not isinstance(concepts, list) or len(concepts) > 250_000:
            raise ValueError("Profile concepts must be an array within the 250000-concept limit")
        qualified_names = set()
        for concept in concepts:
            qualified_name = concept.get("qualifiedName") if isinstance(concept, dict) else None
            if not isinstance(qualified_name, str) or not qualified_name:
                raise ValueError("Profile concepts must have qualified names for dimension domain validation")
            qualified_names.add(qualified_name)
        for dimension_qname, member_qnames in dimension_domains.items():
            if not isinstance(dimension_qname, str) or dimension_qname not in qualified_names:
                raise ValueError("Profile dimension domains must name indexed taxonomy concepts")
            if (not isinstance(member_qnames, list) or not member_qnames
                    or any(not isinstance(member, str) or not member for member in member_qnames)
                    or len(set(member_qnames)) != len(member_qnames)):
                raise ValueError("Profile dimension domains must contain unique, non-empty member arrays")
            if any(member not in qualified_names for member in member_qnames):
                raise ValueError("Profile dimension domains cannot refer to unindexed taxonomy members")
    presentation_links = manifest.get("presentationLinks", [])
    if not isinstance(presentation_links, list):
        raise ValueError("Profile presentationLinks must be an array")
    if schema_version < 4 and presentation_links:
        raise ValueError("Presentation relationships require manifest schema version 4")
    if len(presentation_links) > MAX_PROFILE_PRESENTATION_RELATIONSHIPS:
        raise ValueError("Profile presentation relationships exceed the relationship limit")
    if presentation_links:
        concepts = manifest.get("concepts")
        if not isinstance(concepts, list) or len(concepts) > 250_000:
            raise ValueError("Profile concepts must be an array within the 250000-concept limit")
        qualified_names = {
            concept.get("qualifiedName")
            for concept in concepts
            if isinstance(concept, dict) and isinstance(concept.get("qualifiedName"), str)
        }
        relationship_keys = set()
        for relationship in presentation_links:
            if not isinstance(relationship, dict):
                raise ValueError("Profile presentation relationships must be objects")
            role_uri = relationship.get("roleUri")
            parent_qname = relationship.get("parentQName")
            child_qname = relationship.get("childQName")
            order = relationship.get("order", 0)
            try:
                parsed_role_uri = urlsplit(role_uri) if isinstance(role_uri, str) else None
                numeric_order = Decimal(str(order)) if not isinstance(order, bool) else None
            except (InvalidOperation, ValueError):
                parsed_role_uri = None
                numeric_order = None
            if (parsed_role_uri is None or not parsed_role_uri.scheme
                    or not isinstance(role_uri, str) or not role_uri or len(role_uri) > 2048
                    or role_uri != role_uri.strip()
                    or any(ch.isspace() or ord(ch) < 0x20 or 0x7f <= ord(ch) <= 0x9f for ch in role_uri)
                    or (parsed_role_uri.scheme.lower() in ("http", "https")
                        and (not parsed_role_uri.hostname or parsed_role_uri.username is not None
                             or parsed_role_uri.password is not None))
                    or not isinstance(parent_qname, str) or parent_qname not in qualified_names
                    or not isinstance(child_qname, str) or child_qname not in qualified_names
                    or not isinstance(order, (int, float)) or isinstance(order, bool)
                    or (isinstance(order, float) and not math.isfinite(order))
                    or numeric_order is None
                    or abs(numeric_order) > Decimal("79228162514264337593543950335")):
                raise ValueError("Profile presentation relationships must have an absolute role URI, numeric order, and indexed endpoints")
            relationship_key = (role_uri, parent_qname, child_qname, numeric_order)
            if relationship_key in relationship_keys:
                raise ValueError("Profile presentation relationships cannot contain duplicate arcs")
            relationship_keys.add(relationship_key)
    for field in ("profileId", "version", "profileDigest", "entryPoint"):
        if not isinstance(manifest.get(field), str) or not manifest[field] or len(manifest[field]) > 1024:
            raise ValueError("Profile manifest has invalid " + field)
    files = manifest.get("packageFiles")
    references = manifest.get("offlineReferences", {})
    if not isinstance(files, dict) or not files or len(files) > MAX_PROFILE_FILES:
        raise ValueError("Profile manifest has no valid hash-pinned package files")
    if not isinstance(references, dict):
        raise ValueError("Profile offlineReferences must be an object")
    required_plugins = manifest.get("requiredPlugins", [])
    if not isinstance(required_plugins, list) or len(required_plugins) > 128:
        raise ValueError("Profile requiredPlugins must be an array with at most 128 entries")
    if (any(not isinstance(plugin, str) or not plugin or len(plugin) > 256
            or plugin.startswith("/") or "\\" in plugin or "," in plugin
            or any(ch.isspace() for ch in plugin)
            or any(part in ("", ".", "..") for part in plugin.split("/"))
            or any(ch not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.+/" for ch in plugin)
            for plugin in required_plugins)
            or len(set(required_plugins)) != len(required_plugins)):
        raise ValueError("Profile required plugin IDs are invalid or duplicated")
    required_checks = manifest.get("requiredChecks", [])
    if (not isinstance(required_checks, list) or len(required_checks) > 2048
            or any(not isinstance(check, str) or not check.strip() or len(check) > 512
                   or any(ord(char) < 0x20 or 0x7f <= ord(char) <= 0x9f for char in check)
                   for check in required_checks)
            or len(set(required_checks)) != len(required_checks)):
        raise ValueError("Profile required check IDs must be unique non-empty strings within the 2048-check limit")
    total_bytes = 0
    verified_files = {}
    for relative_path, expected in files.items():
        if not isinstance(expected, str) or len(expected) != 71 or not expected.startswith("sha256:"):
            raise ValueError("Every profile package file must have a sha256 digest")
        if any(ch not in "0123456789abcdef" for ch in expected[7:]):
            raise ValueError("Profile package sha256 digests must be lowercase hexadecimal")
        package_path = safe_package_path(relative_path, root)
        size = package_path.stat().st_size
        if size > MAX_PROFILE_FILE_BYTES:
            raise ValueError("A taxonomy package file exceeds the size limit")
        total_bytes += size
        if total_bytes > MAX_PROFILE_TOTAL_BYTES:
            raise ValueError("Taxonomy package files exceed the total size limit")
        digest = hashlib.sha256()
        bytes_hashed = 0
        with package_path.open("rb") as source:
            for chunk in iter(lambda: source.read(1024 * 1024), b""):
                bytes_hashed += len(chunk)
                if bytes_hashed > MAX_PROFILE_FILE_BYTES:
                    raise ValueError("A taxonomy package file exceeds the size limit")
                if bytes_hashed > size:
                    total_bytes += bytes_hashed - size
                    size = bytes_hashed
                    if total_bytes > MAX_PROFILE_TOTAL_BYTES:
                        raise ValueError("Taxonomy package files exceed the total size limit")
                digest.update(chunk)
        if "sha256:" + digest.hexdigest() != expected:
            raise ValueError("Taxonomy package hash mismatch: " + relative_path)
        verified_files[relative_path] = expected
    if manifest.get("entryPoint") not in verified_files:
        raise ValueError("Taxonomy entry point is not a hash-pinned package file")
    for reference, relative_path in references.items():
        parsed_reference = urlsplit(reference) if isinstance(reference, str) else None
        if (parsed_reference is None or len(reference) > 2048
                or parsed_reference.scheme not in ("http", "https")
                or not parsed_reference.hostname or parsed_reference.username is not None
                or parsed_reference.password is not None or parsed_reference.query
                or parsed_reference.fragment or "?" in reference or "#" in reference
                or not parsed_reference.path or parsed_reference.path == "/"
                or "\\" in reference or any(ch.isspace() or ord(ch) < 0x20 for ch in reference)
                or not isinstance(relative_path, str)
                or relative_path not in verified_files):
            raise ValueError("Offline taxonomy references must map canonical HTTP(S) file URIs to pinned package files")
    return manifest


def _xml_expanded_name(name):
    if "}" not in name:
        return "", name
    namespace, local_name = name.rsplit("}", 1)
    return namespace, local_name


def _read_taxonomy_xml_edges(relative_path, path, expected_digest, remaining_budget, remaining_edges):
    size = path.stat().st_size
    if size > MAX_TAXONOMY_XML_FILE_BYTES:
        raise ValueError("Taxonomy dependency XML file exceeds the size limit: " + relative_path)

    parser = expat.ParserCreate(namespace_separator="}")
    edges = []

    def reject_xml_declaration(*_):
        raise ValueError("Taxonomy XML DTD/entity declarations are forbidden: " + relative_path)

    parser.StartDoctypeDeclHandler = reject_xml_declaration
    parser.EntityDeclHandler = reject_xml_declaration
    parser.UnparsedEntityDeclHandler = reject_xml_declaration
    parser.ExternalEntityRefHandler = reject_xml_declaration
    parser.SetParamEntityParsing(expat.XML_PARAM_ENTITY_PARSING_NEVER)

    def add_edge(value, kind):
        if not isinstance(value, str) or not value or value != value.strip():
            raise ValueError("Taxonomy dependency has an empty or malformed " + kind + ": " + relative_path)
        remaining_edges[0] += 1
        if remaining_edges[0] > MAX_TAXONOMY_GRAPH_EDGES:
            raise ValueError("Taxonomy dependency graph exceeds the edge limit")
        edges.append((value, kind))

    def on_start(name, attributes):
        namespace, local_name = _xml_expanded_name(name)
        if XML_NAMESPACE + "}base" in attributes:
            raise ValueError("Taxonomy XML xml:base resolution is unsupported: " + relative_path)

        if namespace == XSD_NAMESPACE and local_name in ("import", "include", "redefine", "override"):
            schema_location = attributes.get("schemaLocation")
            if not schema_location:
                raise ValueError("Taxonomy XSD dependency has no schemaLocation: " + relative_path)
            add_edge(schema_location, "schemaLocation")

        xsi_schema_locations = attributes.get(XSI_NAMESPACE + "}schemaLocation")
        if xsi_schema_locations is not None:
            locations = xsi_schema_locations.split()
            if not locations or len(locations) % 2:
                raise ValueError("Taxonomy xsi:schemaLocation must contain namespace/location pairs: " + relative_path)
            for schema_location in locations[1::2]:
                add_edge(schema_location, "schemaLocation")
        no_namespace_schema_location = attributes.get(XSI_NAMESPACE + "}noNamespaceSchemaLocation")
        if no_namespace_schema_location is not None:
            add_edge(no_namespace_schema_location, "schemaLocation")

        href = attributes.get(XLINK_NAMESPACE + "}href")
        if href is not None:
            if namespace == LINK_NAMESPACE and local_name in ("linkbaseRef", "roleRef", "arcroleRef") and not href:
                raise ValueError("Taxonomy XBRL reference has an empty xlink:href: " + relative_path)
            add_edge(href, "xlink:href")

    parser.StartElementHandler = on_start
    digest = hashlib.sha256()
    bytes_read = 0
    try:
        with path.open("rb") as source:
            while True:
                chunk = source.read(TAXONOMY_XML_READ_BYTES)
                if not chunk:
                    break
                bytes_read += len(chunk)
                remaining_budget[0] += len(chunk)
                if bytes_read > MAX_TAXONOMY_XML_FILE_BYTES:
                    raise ValueError("Taxonomy dependency XML file exceeds the size limit: " + relative_path)
                if remaining_budget[0] > MAX_TAXONOMY_GRAPH_XML_BYTES:
                    raise ValueError("Taxonomy dependency graph exceeds the XML byte limit")
                digest.update(chunk)
                parser.Parse(chunk, False)
            parser.Parse(b"", True)
    except expat.ExpatError as ex:
        raise ValueError("Taxonomy dependency XML is malformed or unsupported: " + relative_path) from ex

    if "sha256:" + digest.hexdigest() != expected_digest:
        raise ValueError("Taxonomy package hash mismatch while scanning dependency: " + relative_path)
    return edges


class TaxonomyDependencyClosure(tuple):
    """Ordered verified dependency paths plus the exact offline URI mappings they use."""

    def __new__(cls, paths, offline_references):
        result = super().__new__(cls, paths)
        result.offline_references = dict(offline_references)
        return result


def _resolve_taxonomy_edge(current_path, reference, kind, package_files,
                           offline_references=None, used_offline_references=None):
    try:
        parsed = urlsplit(reference)
    except ValueError as ex:
        raise ValueError("Taxonomy dependency URI is malformed in " + current_path) from ex
    if parsed.scheme or parsed.netloc:
        if (parsed.scheme not in ("http", "https") or not parsed.hostname
                or parsed.username is not None or parsed.password is not None):
            raise ValueError("External taxonomy dependency must be an HTTP(S) URI without credentials: "
                             + reference[:256])
        if (not parsed.path or parsed.path == "/" or "\\" in reference
                or any(ch.isspace() or ord(ch) < 0x20 for ch in reference)):
            raise ValueError("External taxonomy dependency must identify a canonical file URI: "
                             + reference[:256])
        if parsed.query or "?" in reference.split("#", 1)[0]:
            raise ValueError("External taxonomy dependency queries are unsupported: " + current_path)
        if kind == "schemaLocation" and (parsed.fragment or "#" in reference):
            raise ValueError("External taxonomy schemaLocation fragments are unsupported: " + current_path)
        base_reference = urlunsplit((parsed.scheme, parsed.netloc, parsed.path, "", ""))
        target = (offline_references or {}).get(base_reference)
        if not isinstance(target, str) or target not in package_files:
            raise ValueError("External taxonomy dependency has no exact hash-pinned offlineReferences mapping: "
                             + base_reference[:256])
        if target.lower().endswith(".zip"):
            raise ValueError("Arelle ZIP package catalog dependency is unsupported: " + target)
        if used_offline_references is not None:
            used_offline_references[base_reference] = target
        return target
    if parsed.query:
        if kind == "schemaLocation":
            raise ValueError("Taxonomy XSD schemaLocation queries are unsupported: " + current_path)
        raise ValueError("Taxonomy dependency URI queries are unsupported: " + current_path)
    if kind == "schemaLocation" and parsed.fragment:
        raise ValueError("Taxonomy XSD schemaLocation fragments are unsupported: " + current_path)
    if not parsed.path:
        # Fragment-only xlink references point within the current XML document.
        if kind == "schemaLocation":
            raise ValueError("Taxonomy XSD schemaLocation must name a relative file: " + current_path)
        return None
    if re.search(r"%(?![0-9a-fA-F]{2})", parsed.path):
        raise ValueError("Taxonomy dependency URI has invalid percent encoding: " + current_path)
    try:
        decoded_path = unquote(parsed.path, errors="strict")
    except UnicodeDecodeError as ex:
        raise ValueError("Taxonomy dependency URI is not valid UTF-8: " + current_path) from ex
    if "\\" in decoded_path or "\x00" in decoded_path or any(ch.isspace() for ch in decoded_path):
        raise ValueError("Taxonomy dependency URI contains an unsafe path: " + current_path)
    if decoded_path.startswith("/"):
        raise ValueError("Taxonomy dependency URI is not relative to its source: " + current_path)
    resolved = posixpath.normpath(posixpath.join(posixpath.dirname(current_path), decoded_path))
    if resolved in ("", "."):
        resolved = current_path
    if resolved == ".." or resolved.startswith("../") or resolved.startswith("/"):
        raise ValueError("Taxonomy dependency path escapes the package root: " + current_path)
    if resolved not in package_files:
        raise ValueError("Taxonomy dependency is not hash-pinned by packageFiles: " + resolved)
    if resolved.lower().endswith(".zip"):
        raise ValueError("Arelle ZIP package catalog dependency is unsupported: " + resolved)
    return resolved


def validate_taxonomy_dependency_closure(entry_point, taxonomy_root, package_files, offline_references=None):
    """Validate the bounded, file-based taxonomy dependency graph before Arelle loads it.

    Exact external HTTP(S) references may resolve only through the profile's
    hash-pinned offlineReferences map. Their closure targets are copied into a
    private snapshot and an ephemeral local Arelle catalog maps only those URI
    directories to the copied files.
    """
    if not isinstance(package_files, dict) or entry_point not in package_files:
        raise ValueError("Taxonomy entry point is not hash-pinned by packageFiles")
    zip_paths = sorted(path for path in package_files if isinstance(path, str) and path.lower().endswith(".zip"))
    if zip_paths:
        raise ValueError("Arelle ZIP package catalogs are unsupported for bounded taxonomy loading: " + zip_paths[0])

    root = Path(taxonomy_root).resolve(strict=True)
    pending = deque([(entry_point, 0)])
    visited = set()
    scanned = []
    used_offline_references = {}
    remaining_budget = [0]
    remaining_edges = [0]
    while pending:
        relative_path, depth = pending.popleft()
        if relative_path in visited:
            continue
        if depth > MAX_TAXONOMY_GRAPH_DEPTH:
            raise ValueError("Taxonomy dependency graph exceeds the depth limit")
        if len(visited) >= MAX_TAXONOMY_GRAPH_FILES:
            raise ValueError("Taxonomy dependency graph exceeds the file limit")
        expected_digest = package_files.get(relative_path)
        if not isinstance(expected_digest, str):
            raise ValueError("Taxonomy dependency is not hash-pinned by packageFiles: " + relative_path)
        path = safe_package_path(relative_path, root)
        visited.add(relative_path)
        scanned.append(relative_path)
        edges = _read_taxonomy_xml_edges(relative_path, path, expected_digest,
                                         remaining_budget, remaining_edges)
        discovered = set()
        for reference, kind in edges:
            target = _resolve_taxonomy_edge(relative_path, reference, kind, package_files,
                                            offline_references, used_offline_references)
            if target is not None and target not in visited:
                # Validate path/symlink/existence before it enters the work queue.
                try:
                    safe_package_path(target, root)
                except (OSError, ValueError) as ex:
                    raise ValueError("Pinned taxonomy dependency is unavailable: " + target) from ex
                discovered.add(target)
        for target in sorted(discovered):
            pending.append((target, depth + 1))
    return TaxonomyDependencyClosure(scanned, used_offline_references)


def _write_offline_taxonomy_catalog(snapshot_root, offline_references):
    """Create a local-only Arelle catalog for the exact external references in the closure."""
    root = Path(snapshot_root).resolve(strict=True)
    alias_root = root / ".xms-offline-uri"
    alias_root.mkdir(mode=0o700, exist_ok=True)
    remappings = {}
    copied_bytes = 0
    alias_targets = {}
    source_digests = {}

    for reference, relative_path in sorted(offline_references.items()):
        try:
            parsed = urlsplit(reference)
            decoded_path = unquote(parsed.path, errors="strict")
        except (UnicodeDecodeError, ValueError) as ex:
            raise ValueError("Offline taxonomy URI cannot be represented as a safe local path") from ex
        if (parsed.scheme not in ("http", "https") or not parsed.hostname
                or parsed.query or parsed.fragment or "?" in reference or "#" in reference
                or not parsed.path.startswith("/")
                or "\\" in decoded_path or "\x00" in decoded_path
                or any(ord(ch) < 0x20 or 0x7f <= ord(ch) <= 0x9f for ch in decoded_path)):
            raise ValueError("Offline taxonomy URI is not a canonical HTTP(S) file reference: "
                             + reference[:256])

        parent_path, separator, raw_suffix = parsed.path.rpartition("/")
        if not separator or not raw_suffix:
            raise ValueError("Offline taxonomy URI must identify a file, not a directory: "
                             + reference[:256])
        prefix = urlunsplit((parsed.scheme, parsed.netloc, parent_path + "/", "", ""))
        directory_key = hashlib.sha256(prefix.encode("utf-8")).hexdigest()
        local_alias_directory = alias_root / directory_key
        local_alias_directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        source = safe_package_path(relative_path, root)
        source_digest = source_digests.get(relative_path)
        if source_digest is None:
            digest = hashlib.sha256()
            with source.open("rb") as source_file:
                for chunk in iter(lambda: source_file.read(TAXONOMY_XML_READ_BYTES), b""):
                    digest.update(chunk)
            source_digest = digest.hexdigest()
            source_digests[relative_path] = source_digest

        # Arelle's package catalog remaps URI prefixes. Create a private alias whose
        # suffix is the URI's path so an unmapped sibling still resolves to no file.
        decoded_suffix = unquote(raw_suffix, errors="strict")
        if ("/" in decoded_suffix or "\\" in decoded_suffix
                or len(raw_suffix) > 255 or len(decoded_suffix) > 255
                or len(os.fsencode(decoded_suffix)) > 255):
            raise ValueError("Offline taxonomy URI encodes a path separator in its file name: "
                             + reference[:256])
        suffixes = {raw_suffix, decoded_suffix}
        for suffix in suffixes:
            normalized = posixpath.normpath(suffix)
            parts = suffix.split("/")
            if (not suffix or normalized != suffix or suffix.startswith("/")
                    or any(part in ("", ".", "..") for part in parts)
                    or any(ch in "\\:*?\"<>|\x00" or ord(ch) < 0x20 or 0x7f <= ord(ch) <= 0x9f
                           for ch in suffix)):
                raise ValueError("Offline taxonomy URI contains an unsafe relative file path: "
                                 + reference[:256])
            alias = local_alias_directory.joinpath(*suffix.split("/"))
            alias.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            existing_digest = alias_targets.get(str(alias))
            if existing_digest is not None:
                if existing_digest != source_digest:
                    raise ValueError("Offline taxonomy URI aliases collide on different pinned files")
                continue
            if alias.exists():
                if not alias.is_file() or hashlib.sha256(alias.read_bytes()).hexdigest() != source_digest:
                    raise ValueError("Offline taxonomy URI aliases collide with a different snapshot file")
            else:
                try:
                    os.link(source, alias)
                except OSError:
                    copied_bytes += source.stat().st_size
                    if copied_bytes > MAX_TAXONOMY_OFFLINE_ALIAS_BYTES:
                        raise ValueError("Offline taxonomy URI aliases exceed the local copy budget")
                    shutil.copyfile(source, alias)
                    if os.name != "nt":
                        alias.chmod(0o400)
            alias_targets[str(alias)] = source_digest

        remappings[prefix] = str(local_alias_directory) + os.sep

    ElementTree.register_namespace("", OASIS_CATALOG_NAMESPACE)
    catalog = ElementTree.Element("{" + OASIS_CATALOG_NAMESPACE + "}catalog")
    for prefix, rewrite_prefix in sorted(remappings.items(), key=lambda pair: (-len(pair[0]), pair[0])):
        ElementTree.SubElement(catalog, "{" + OASIS_CATALOG_NAMESPACE + "}rewriteURI", {
            "uriStartString": prefix,
            "rewritePrefix": rewrite_prefix,
        })
    catalog_bytes = ElementTree.tostring(catalog, encoding="utf-8", xml_declaration=True)
    package_namespace = "http://xbrl.org/2016/taxonomy-package"
    metadata = ElementTree.Element("{" + package_namespace + "}taxonomyPackage", {
        "{" + XML_NAMESPACE + "}lang": "en",
    })
    ElementTree.SubElement(metadata, "{" + package_namespace + "}identifier").text = (
        "urn:xbrlmappingstudio:worker:offline-taxonomy-catalog")
    metadata_bytes = ElementTree.tostring(metadata, encoding="utf-8", xml_declaration=True)
    catalog_path = root / "offline-taxonomy-catalog.zip"
    with zipfile.ZipFile(catalog_path, "w", compression=zipfile.ZIP_STORED) as archive:
        archive.writestr("xms-offline/META-INF/taxonomyPackage.xml", metadata_bytes)
        archive.writestr("xms-offline/META-INF/catalog.xml", catalog_bytes)
    if os.name != "nt":
        catalog_path.chmod(0o400)
    return catalog_path


def _copy_verified_taxonomy_snapshot(entry_point, relative_paths, taxonomy_root, snapshot_root,
                                     package_files, offline_references=None):
    """Copy verified closure bytes into a private tree so source edits cannot race Arelle."""
    source_root = Path(taxonomy_root).resolve(strict=True)
    target_root = Path(snapshot_root).resolve(strict=True)
    total_bytes = 0
    for relative_path in relative_paths:
        expected_digest = package_files.get(relative_path)
        if not isinstance(expected_digest, str):
            raise ValueError("Taxonomy dependency is not hash-pinned by packageFiles: " + relative_path)
        source_path = safe_package_path(relative_path, source_root)
        target_path = target_root.joinpath(*relative_path.split("/"))
        target_path.parent.mkdir(parents=True, exist_ok=True)
        digest = hashlib.sha256()
        file_bytes = 0
        try:
            with source_path.open("rb") as source, target_path.open("xb") as target:
                while True:
                    chunk = source.read(TAXONOMY_XML_READ_BYTES)
                    if not chunk:
                        break
                    file_bytes += len(chunk)
                    total_bytes += len(chunk)
                    if file_bytes > MAX_TAXONOMY_XML_FILE_BYTES:
                        raise ValueError("Taxonomy dependency XML file exceeds the size limit: " + relative_path)
                    if total_bytes > MAX_TAXONOMY_GRAPH_XML_BYTES:
                        raise ValueError("Taxonomy dependency graph exceeds the XML byte limit")
                    digest.update(chunk)
                    target.write(chunk)
            if "sha256:" + digest.hexdigest() != expected_digest:
                raise ValueError("Taxonomy package hash mismatch while snapshotting dependency: " + relative_path)
            if os.name != "nt":
                target_path.chmod(0o400)
        except OSError as ex:
            raise ValueError("Could not create private taxonomy snapshot for: " + relative_path) from ex

    if offline_references:
        _write_offline_taxonomy_catalog(target_root, offline_references)
    return safe_package_path(entry_point, target_root)


def run_arelle_with_verified_taxonomy(entry_point, relative_paths, taxonomy_root, package_files, args):
    """Keep a verified private dependency snapshot alive for the complete Arelle load."""
    with tempfile.TemporaryDirectory(prefix="xms-taxonomy-") as snapshot_directory:
        os.chmod(snapshot_directory, 0o700)
        snapshot_entry = _copy_verified_taxonomy_snapshot(
            entry_point, relative_paths, taxonomy_root, snapshot_directory, package_files,
            getattr(relative_paths, "offline_references", {}))
        run_args = args
        if getattr(relative_paths, "offline_references", {}):
            import copy
            run_args = copy.copy(args)
            run_args.package = list(getattr(args, "package", []) or [])
            run_args.package.append(str(Path(snapshot_directory) / "offline-taxonomy-catalog.zip"))
        return run_arelle(snapshot_entry, run_args)


def safe_taxonomy_urls(report, taxonomy_root, offline_references=None, package_files=None):
    info = report.get("documentInfo")
    if not isinstance(info, dict) or not isinstance(info.get("taxonomy"), list):
        raise ValueError("Report has no documentInfo.taxonomy array")
    fixed = []
    for reference in info["taxonomy"]:
        if not isinstance(reference, str):
            raise ValueError("Taxonomy references must be strings")
        parsed = urlsplit(reference)
        if parsed.scheme in ("http", "https"):
            if not isinstance(offline_references, dict) or reference not in offline_references:
                raise ValueError("Taxonomy URI has no approved offline package mapping")
            relative = offline_references[reference]
        elif parsed.scheme == "file":
            if taxonomy_root is None:
                raise ValueError("A taxonomy root is required for file taxonomy references")
            if parsed.query or parsed.fragment:
                raise ValueError("File taxonomy references cannot include a query or fragment")
            local = Path(url2pathname(unquote(parsed.path)))
            relative = os.path.relpath(local, Path(taxonomy_root).resolve(strict=True)).replace(os.sep, "/")
        elif parsed.scheme == "":
            if taxonomy_root is None:
                raise ValueError("A taxonomy root is required for relative taxonomy references")
            if parsed.query or parsed.fragment:
                raise ValueError("Relative taxonomy references cannot include a query or fragment")
            relative = unquote(parsed.path)
        else:
            raise ValueError("Only pinned HTTP(S) references and root-scoped local taxonomy files are supported")
        local = safe_package_path(relative, taxonomy_root)
        if package_files is not None and relative not in package_files:
            raise ValueError("Taxonomy reference does not point to a hash-pinned package file")
        fixed.append(str(local) if os.name == "nt" else local.as_uri())
    info["taxonomy"] = fixed
    return report


def rewrite_staged_xml_taxonomy(report_path, output_path, taxonomy_root, offline_references,
                                package_files, expected_digest):
    """Verify an exact staged XML instance, then localize only its schemaRef URIs."""
    digest = hashlib.sha256()
    total = 0
    with report_path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            total += len(chunk)
            if total > MAX_LARGE_REPORT_BYTES:
                raise ValueError("Staged xBRL-XML exceeds the worker report size budget")
            digest.update(chunk)
    if total <= 0 or "sha256:" + digest.hexdigest() != expected_digest:
        raise ValueError("Staged xBRL-XML digest did not match the requested digest")

    try:
        from lxml import etree
    except ImportError as ex:
        raise ValueError("The pinned Arelle runtime does not provide the required XML parser") from ex
    try:
        events = etree.iterparse(str(report_path), events=("start", "end"),
                                 resolve_entities=False, no_network=True, load_dtd=False,
                                 recover=False, huge_tree=False)
        first_event, root = next(events)
    except StopIteration as ex:
        raise ValueError("Staged xBRL-XML is empty") from ex
    except etree.XMLSyntaxError as ex:
        raise ValueError("Staged xBRL-XML is malformed") from ex
    if first_event != "start" or root.getroottree().docinfo.doctype:
        raise ValueError("Staged xBRL-XML must have an instance root and no DTD")
    if root.tag != "{http://www.xbrl.org/2003/instance}xbrl":
        raise ValueError("Staged XML root is not an xBRL instance")

    schema_ref_count = 0
    depth = 1
    with etree.xmlfile(str(output_path), encoding="utf-8") as writer:
        writer.write_declaration()
        with writer.element(root.tag, attrib=dict(root.attrib), nsmap=root.nsmap):
            for event, element in events:
                if event == "start":
                    depth += 1
                    continue
                if depth == 2:
                    if element.tag == "{http://www.xbrl.org/2003/linkbase}schemaRef":
                        reference = element.get("{http://www.w3.org/1999/xlink}href")
                        if not isinstance(reference, str) or not reference:
                            raise ValueError("Staged xBRL-XML has a schemaRef without an xlink:href")
                        local_reference = safe_taxonomy_urls(
                            {"documentInfo": {"taxonomy": [reference]}}, taxonomy_root,
                            offline_references, package_files)["documentInfo"]["taxonomy"][0]
                        element.set("{http://www.w3.org/1999/xlink}href", local_reference)
                        schema_ref_count += 1
                    writer.write(element)
                    parent = element.getparent()
                    element.clear()
                    while element.getprevious() is not None:
                        del parent[0]
                depth -= 1
    if schema_ref_count == 0:
        raise ValueError("Staged xBRL-XML has no schemaRef")


def reject_duplicate_json_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON object key: " + str(key)[:120])
        result[key] = value
    return result


def rewrite_large_report_taxonomy(report_path, output_path, taxonomy_root, offline_references, package_files,
                                 expected_digest):
    """Validate the bounded documentInfo header and stream-copy the large facts object."""
    with report_path.open("rb") as source:
        raw_prefix = source.read(MAX_DOCUMENT_INFO_BYTES + 64 * 1024)
        prefix = None
        for trim in range(4):
            try:
                prefix = raw_prefix[:len(raw_prefix) - trim if trim else None].decode("utf-8")
                break
            except UnicodeDecodeError:
                continue
        if prefix is None:
            raise ValueError("Report documentInfo header is not valid bounded UTF-8")

        decoder = json.JSONDecoder(object_pairs_hook=reject_duplicate_json_keys,
                                   parse_constant=lambda value: (_ for _ in ()).throw(
                                       ValueError("Invalid JSON constant: " + value)))
        position = 0
        while position < len(prefix) and prefix[position].isspace():
            position += 1
        if position >= len(prefix) or prefix[position] != "{":
            raise ValueError("Report root must be a JSON object")
        position += 1
        while position < len(prefix) and prefix[position].isspace():
            position += 1
        key, position = decoder.raw_decode(prefix, position)
        if key != "documentInfo":
            raise ValueError("Large report must begin with documentInfo")
        while position < len(prefix) and prefix[position].isspace():
            position += 1
        if position >= len(prefix) or prefix[position] != ":":
            raise ValueError("Report documentInfo member is malformed")
        position += 1
        while position < len(prefix) and prefix[position].isspace():
            position += 1
        document_info, position = decoder.raw_decode(prefix, position)
        header_bytes = len(prefix[:position].encode("utf-8"))
        if header_bytes > MAX_DOCUMENT_INFO_BYTES:
            raise ValueError("Report documentInfo exceeds its size limit")
        safe_report = safe_taxonomy_urls({"documentInfo": document_info}, taxonomy_root,
                                         offline_references, package_files)
        document_info = safe_report["documentInfo"]

        while position < len(prefix) and prefix[position].isspace():
            position += 1
        if position >= len(prefix) or prefix[position] != ",":
            raise ValueError("Large report must contain a facts member after documentInfo")
        position += 1
        while position < len(prefix) and prefix[position].isspace():
            position += 1
        facts_key, position = decoder.raw_decode(prefix, position)
        if facts_key != "facts":
            raise ValueError("Large report facts member is missing")
        while position < len(prefix) and prefix[position].isspace():
            position += 1
        if position >= len(prefix) or prefix[position] != ":":
            raise ValueError("Report facts member is malformed")
        position += 1
        while position < len(prefix) and prefix[position].isspace():
            position += 1
        if position >= len(prefix) or prefix[position] != "{":
            raise ValueError("Report facts member must be a JSON object")
        facts_start = len(prefix[:position].encode("utf-8"))
        if facts_start >= len(raw_prefix):
            raise ValueError("Report facts object did not begin within the bounded header probe")

        digest = hashlib.sha256(raw_prefix)
        with output_path.open("wb") as target:
            target.write(b'{"documentInfo":')
            target.write(json.dumps(document_info, ensure_ascii=False,
                                    separators=(",", ":")).encode("utf-8"))
            target.write(b',"facts":')
            stack = []
            in_string = False
            escaped = False
            facts_ended = False
            report_ended = False
            def process_report_structure_byte(byte):
                nonlocal in_string, escaped, facts_ended, report_ended
                if report_ended:
                    if byte not in b" \t\r\n":
                        raise ValueError("Report contains trailing data after its root object")
                    return
                if facts_ended:
                    if byte in b" \t\r\n":
                        return
                    if byte != ord("}"):
                        raise ValueError("Report contains an unexpected top-level member after facts")
                    report_ended = True
                    return
                if not stack:
                    if byte != ord("{"):
                        raise ValueError("Report facts member must begin with an object")
                    stack.append(ord("}"))
                    return
                if in_string:
                    if escaped:
                        escaped = False
                    elif byte == ord("\\"):
                        escaped = True
                    elif byte == ord('"'):
                        in_string = False
                    return
                if byte == ord('"'):
                    in_string = True
                elif byte == ord("{"):
                    stack.append(ord("}"))
                elif byte == ord("["):
                    stack.append(ord("]"))
                elif byte in (ord("}"), ord("]")):
                    if not stack or stack[-1] != byte:
                        raise ValueError("Report facts member contains mismatched JSON delimiters")
                    stack.pop()
                    if not stack:
                        facts_ended = True

            chunk = raw_prefix[facts_start:]
            target.write(chunk)
            for byte in chunk:
                process_report_structure_byte(byte)
            while True:
                chunk = source.read(1024 * 1024)
                if not chunk:
                    break
                digest.update(chunk)
                target.write(chunk)
                for byte in chunk:
                    process_report_structure_byte(byte)
            if not facts_ended or not report_ended:
                raise ValueError("Report is truncated or has a malformed top-level object")
        if "sha256:" + digest.hexdigest() != expected_digest:
            raise ValueError("Staged xBRL-JSON changed while it was streamed to the validator")


def run_arelle(entrypoint, args, temporary_path=None):
    if not ARELLE_AVAILABLE:
        raise RuntimeError("arelle-release is not installed in this worker environment")
    options = RuntimeOptions(
        entrypointFile=str(entrypoint),
        validate=True,
        workOffline=True,
        internetConnectivity="offline",
        keepOpen=True,
        disablePersistentConfig=True,
        logFile="logToStructuredMessage",
        logPropagate=False,
        packages=args.package or None,
        plugins="|".join(args.plugin) if args.plugin else None,
        disclosureSystemName=args.disclosure_system,
    )
    with Session() as session:
        ok = session.run(options)
        logs = session.get_log_messages()
        models = session.get_models()
        concept_count = sum(len(getattr(model, "qnameConcepts", {})) for model in models)
    return ok, logs, concept_count


def log_diagnostics(logs, op_id, temporary_path=None):
    result = []
    suppressed = 0
    suppressed_blocking = False
    for item in logs or []:
        if not isinstance(item, dict):
            continue
        raw_level = str(item.get("levelname", item.get("level", item.get("severity", "error")))).lower()
        if raw_level == "info" and item.get("messageCode", item.get("code")) == "info":
            continue
        code = str(item.get("messageCode", item.get("code", "ARELLE")))[:160]
        message = item.get("msg")
        if message is None:
            structured = item.get("message")
            message = structured.get("text") if isinstance(structured, dict) else structured
        message = str(message or "Arelle reported an unspecified validation finding.")
        if len(message) > MAX_DIAGNOSTIC_MESSAGE_CHARS:
            message = message[:MAX_DIAGNOSTIC_MESSAGE_CHARS - 1] + "…"
        if temporary_path:
            message = message.replace(str(temporary_path), "<job>")
        finding = diagnostic(op_id, "XBR009", raw_level, message, code)
        if len(result) < MAX_RESPONSE_DIAGNOSTICS:
            result.append(finding)
        else:
            suppressed += 1
            suppressed_blocking = suppressed_blocking or finding["isBlocking"]
    if suppressed:
        result.append(diagnostic(
            op_id,
            "XBR009",
            "error" if suppressed_blocking else "warning",
            f"{suppressed} additional Arelle validation findings were omitted after the first {MAX_RESPONSE_DIAGNOSTICS} details.",
            "arelle:diagnostic-limit",
        ))
    return result


def required_plugin_load_failures(logs, required_plugins):
    failure_phrases = ("unable to load", "failed to load", "could not load", "not found")
    failures = []
    for plugin in required_plugins:
        for item in logs or []:
            if not isinstance(item, dict):
                continue
            code = str(item.get("messageCode", item.get("code", ""))).lower()
            message = item.get("msg")
            if message is None:
                message = item.get("message")
            if isinstance(message, dict):
                message = message.get("text", "")
            text = str(message or "").lower()
            combined = code + " " + text
            if plugin.lower() in combined and "plugin" in combined \
                    and any(phrase in combined for phrase in failure_phrases):
                failures.append(plugin)
                break
    return failures


def handle(req, args):
    candidate = req.get("operationId") if isinstance(req, dict) else None
    op_id = candidate if isinstance(candidate, str) and len(candidate) <= 128 else "-"
    if not valid_request(req):
        return response(op_id, "domainFailure", error="Request is missing required protocol fields")
    if is_expired(req):
        return response(op_id, "timedOut", error="Request deadline has elapsed")
    operation = req["operation"]
    payload = req.get("payload")

    if operation == "handshake":
        requested = payload if isinstance(payload, dict) else {}
        minimum = requested.get("minimumProtocolVersion", PROTOCOL_VERSION)
        maximum = requested.get("maximumProtocolVersion", PROTOCOL_VERSION)
        if not isinstance(minimum, int) or not isinstance(maximum, int) or not minimum <= PROTOCOL_VERSION <= maximum:
            return response(op_id, "domainFailure", error="No mutually supported protocol version")
        if not ARELLE_AVAILABLE:
            message = "The Arelle runtime is not installed in the configured worker environment."
            return response(op_id, "incompleteValidation", diagnostics=[diagnostic(
                op_id, "XBR007", "error", message)], error=message)
        if getattr(args, "profile_error", None):
            message = "Installed profile verification failed: " + args.profile_error
            return response(op_id, "domainFailure", diagnostics=[diagnostic(
                op_id, "XBR015", "error", message)], error=message)
        profile = getattr(args, "profile_manifest_data", None)
        required_plugins = profile.get("requiredPlugins", []) if profile else []
        required_checks = profile.get("requiredChecks", []) if profile else []
        missing_plugins = sorted(set(required_plugins) - set(args.plugin))
        if missing_plugins:
            message = "Required Arelle plugins were not configured: " + ", ".join(missing_plugins)
            return response(op_id, "incompleteValidation", diagnostics=[diagnostic(
                op_id, "XBR010", "error", message)], error=message)
        return response(op_id, "success", {
            "protocolVersion": PROTOCOL_VERSION,
            "engineVersion": ENGINE_VERSION,
            "capabilities": CAPABILITIES,
            "arelleAvailable": True,
            "profileSchemaVersion": profile.get("schemaVersion") if profile else None,
            "profileId": profile.get("profileId") if profile else None,
            "profileVersion": profile.get("version") if profile else None,
            "profileDigest": profile.get("profileDigest") if profile else None,
            "taxonomyEntryPoint": profile.get("entryPoint") if profile else None,
            "requiredPlugins": required_plugins,
            "requiredChecks": required_checks,
        })

    if operation == "shutdown":
        return response(op_id, "success", {"shutdown": True})

    if operation == "load-taxonomy":
        if not ARELLE_AVAILABLE:
            return response(op_id, "incompleteValidation", error="arelle-release is not installed")
        if not isinstance(payload, dict) or not isinstance(payload.get("taxonomyPath"), str):
            return response(op_id, "domainFailure", error="Missing taxonomyPath")
        try:
            if args.taxonomy_root is None:
                raise ValueError("A configured taxonomy root is required")
            if getattr(args, "profile_error", None):
                raise ValueError(args.profile_error)
            profile = getattr(args, "profile_manifest_data", None)
            if profile is not None and req["profileDigest"] != profile.get("profileDigest"):
                raise ValueError("Worker request profile digest does not match the installed profile")
            if profile is not None:
                value = Path(payload["taxonomyPath"])
                relative = (os.path.relpath(value, Path(args.taxonomy_root).resolve(strict=True))
                            if value.is_absolute() else str(value)).replace(os.sep, "/")
                if relative not in profile["packageFiles"]:
                    raise ValueError("Taxonomy entry point is not hash-pinned by the installed profile")
                dependency_paths = validate_taxonomy_dependency_closure(
                    relative,
                    args.taxonomy_root,
                    profile["packageFiles"],
                    profile.get("offlineReferences", {}),
                )
                ok, logs, concept_count = run_arelle_with_verified_taxonomy(
                    relative, dependency_paths, args.taxonomy_root, profile["packageFiles"], args)
                relative_entry_point = relative
            else:
                taxonomy_path = within_root(payload["taxonomyPath"], args.taxonomy_root)
                ok, logs, concept_count = run_arelle(taxonomy_path, args)
                relative_entry_point = os.path.relpath(
                    taxonomy_path, Path(args.taxonomy_root).resolve(strict=True)).replace(os.sep, "/")
            plugin_failures = required_plugin_load_failures(
                logs, profile.get("requiredPlugins", []) if profile else [])
            if plugin_failures:
                diags = [diagnostic(op_id, "XBR010", "error",
                                    "Required Arelle plugin could not be loaded: " + plugin,
                                    "plugin:" + plugin)
                         for plugin in plugin_failures]
                return response(op_id, "incompleteValidation", {"conceptCount": concept_count}, diags,
                                "A required Arelle plugin is unavailable")
            diags = log_diagnostics(logs, op_id)
            if not ok or any(d["isBlocking"] for d in diags):
                return response(op_id, "domainFailure", {"conceptCount": concept_count}, diags,
                                "Arelle could not load the filing taxonomy")
            return response(op_id, "success", {
                "engine": "Arelle",
                "engineVersion": ENGINE_VERSION,
                "capabilities": CAPABILITIES,
                "profileSchemaVersion": profile.get("schemaVersion") if profile else None,
                "profileId": profile.get("profileId") if profile else None,
                "profileVersion": profile.get("version") if profile else None,
                "profileDigest": profile.get("profileDigest") if profile else None,
                "taxonomyEntryPoint": relative_entry_point,
                "requiredPlugins": profile.get("requiredPlugins", []) if profile else [],
                "requiredChecks": profile.get("requiredChecks", []) if profile else [],
                "conceptCount": concept_count,
                "completed": True,
            }, diags)
        except Exception as ex:
            return response(op_id, "domainFailure", error=str(ex)[:1000])

    if operation == "validate-report":
        if not ARELLE_AVAILABLE:
            return response(op_id, "incompleteValidation", diagnostics=[diagnostic(
                op_id, "XBR010", "warning", "arelle-release is not installed; report validation is incomplete.")])
        report_text = payload if isinstance(payload, str) else payload.get("xbrlJson") if isinstance(payload, dict) else None
        large_report_path = None
        staged_xml_path = None
        if isinstance(payload, dict) and isinstance(payload.get("xbrlJsonPath"), str):
            expected_digest = payload.get("sha256")
            if not isinstance(expected_digest, str) or not expected_digest.startswith("sha256:"):
                return response(op_id, "domainFailure", error="Missing staged xBRL-JSON digest")
            try:
                report_path = Path(payload["xbrlJsonPath"])
                if not report_path.is_absolute() or not report_path.is_file():
                    raise ValueError("Staged xBRL-JSON path is not an existing absolute file")
                resolved_path = report_path.resolve(strict=True)
                temp_root = Path(tempfile.gettempdir()).resolve(strict=True)
                if (report_path.is_symlink() or report_path.parent.is_symlink()
                        or resolved_path.parent.parent != temp_root
                        or not resolved_path.parent.name.startswith("xms-large-")
                        or resolved_path.name != "report.json"
                        or (os.name != "nt" and stat.S_IMODE(resolved_path.parent.stat().st_mode) != 0o700)):
                    raise ValueError("Staged xBRL-JSON path is outside the private artifact directory")
                if resolved_path.stat().st_size <= 0 or resolved_path.stat().st_size > MAX_LARGE_REPORT_BYTES:
                    raise ValueError("Staged xBRL-JSON exceeds the worker report size budget")
                digest = hashlib.sha256()
                hashed_bytes = 0
                with report_path.open("rb") as report_file:
                    for chunk in iter(lambda: report_file.read(1024 * 1024), b""):
                        hashed_bytes += len(chunk)
                        if hashed_bytes > MAX_LARGE_REPORT_BYTES:
                            raise ValueError("Staged xBRL-JSON exceeds the worker report size budget")
                        digest.update(chunk)
                actual_digest = "sha256:" + digest.hexdigest()
                if actual_digest != expected_digest:
                    raise ValueError("Staged xBRL-JSON digest did not match the requested digest")
                large_report_path = resolved_path
            except (OSError, UnicodeError, ValueError) as ex:
                return response(op_id, "domainFailure", error=str(ex)[:1000])
        if isinstance(payload, dict) and isinstance(payload.get("xbrlXmlPath"), str):
            expected_digest = payload.get("sha256")
            if large_report_path is not None or not isinstance(expected_digest, str) \
                    or not expected_digest.startswith("sha256:"):
                return response(op_id, "domainFailure", error="Missing or ambiguous staged xBRL-XML digest")
            try:
                report_path = Path(payload["xbrlXmlPath"])
                if not report_path.is_absolute() or not report_path.is_file():
                    raise ValueError("Staged xBRL-XML path is not an existing absolute file")
                resolved_path = report_path.resolve(strict=True)
                temp_root = Path(tempfile.gettempdir()).resolve(strict=True)
                if (report_path.is_symlink() or report_path.parent.is_symlink()
                        or resolved_path.parent.parent != temp_root
                        or not resolved_path.parent.name.startswith("xms-staged-xml-")
                        or resolved_path.name != "report.xml"
                        or (os.name != "nt" and stat.S_IMODE(resolved_path.parent.stat().st_mode) != 0o700)):
                    raise ValueError("Staged xBRL-XML path is outside the private artifact directory")
                size = resolved_path.stat().st_size
                if size <= 0 or size > MAX_LARGE_REPORT_BYTES:
                    raise ValueError("Staged xBRL-XML exceeds the worker report size budget")
                digest = hashlib.sha256()
                hashed_bytes = 0
                with report_path.open("rb") as report_file:
                    for chunk in iter(lambda: report_file.read(1024 * 1024), b""):
                        hashed_bytes += len(chunk)
                        if hashed_bytes > MAX_LARGE_REPORT_BYTES:
                            raise ValueError("Staged xBRL-XML exceeds the worker report size budget")
                        digest.update(chunk)
                if hashed_bytes != size or "sha256:" + digest.hexdigest() != expected_digest:
                    raise ValueError("Staged xBRL-XML digest did not match the requested digest")
                staged_xml_path = resolved_path
            except (OSError, UnicodeError, ValueError) as ex:
                return response(op_id, "domainFailure", error=str(ex)[:1000])
        if large_report_path is None and staged_xml_path is None \
                and (not isinstance(report_text, str) or not report_text):
            return response(op_id, "domainFailure", error="Missing xBRL-JSON report text")
        try:
            if getattr(args, "profile_error", None):
                raise ValueError(args.profile_error)
            profile = getattr(args, "profile_manifest_data", None)
            if profile is not None and req["profileDigest"] != profile.get("profileDigest"):
                raise ValueError("Worker request profile digest does not match the installed profile")
            requirements = req.get("validationRequirements")
            if not isinstance(requirements, dict):
                raise ValueError("Missing validationRequirements for profile completion")
            profile_id = requirements.get("profileId")
            profile_digest = requirements.get("profileDigest")
            required_rules = requirements.get("requiredRules")
            required_plugins = requirements.get("requiredPlugins")
            if (not isinstance(profile_id, str) or not profile_id or len(profile_id) > 1024
                    or not isinstance(profile_digest, str) or profile_digest != req["profileDigest"]
                    or not isinstance(required_rules, list) or len(required_rules) > 2048
                    or not isinstance(required_plugins, list) or len(required_plugins) > 128
                    or any(not isinstance(identifier, str) or not identifier or len(identifier) > 512
                           for identifier in required_rules)
                    or any(not isinstance(identifier, str) or not identifier or len(identifier) > 256
                           for identifier in required_plugins)
                    or len(set(required_rules)) != len(required_rules)
                    or len(set(required_plugins)) != len(required_plugins)):
                raise ValueError("Validation requirements are malformed")
            if profile is not None and (
                    profile_id != profile.get("profileId")
                    or sorted(required_rules) != sorted(profile.get("requiredChecks", []))
                    or sorted(required_plugins) != sorted(profile.get("requiredPlugins", []))):
                raise ValueError("Validation requirements do not match the installed profile manifest")
            missing_plugins = sorted(set(required_plugins) - set(args.plugin))
            rule_completions = [{
                "identifier": rule,
                "status": "unavailable",
                "detail": "Arelle does not expose a machine-readable completion signal for this required rule.",
            } for rule in required_rules]
            plugin_completions = [{
                "identifier": plugin,
                "status": "unavailable",
                "detail": ("Required plugin was not configured for this worker."
                           if plugin in missing_plugins else
                           "Arelle did not provide machine-readable evidence that the required plugin loaded."),
            } for plugin in required_plugins]
            completion = {
                "profileId": profile_id,
                "profileDigest": profile_digest,
                "requiredRules": rule_completions,
                "requiredPlugins": plugin_completions,
            }
            if missing_plugins:
                diags = [diagnostic(op_id, "XBR010", "error",
                                    "Required Arelle plugin was not configured: " + plugin,
                                    "plugin:" + plugin)
                         for plugin in missing_plugins]
                return response(op_id, "incompleteValidation", {
                    "completed": False, "validationCompletion": completion,
                }, diags, "A required Arelle plugin is unavailable")
            with tempfile.TemporaryDirectory(prefix="xms-arelle-") as workdir:
                os.chmod(workdir, 0o700)
                path = Path(workdir) / ("report.xml" if staged_xml_path is not None else "report.json")
                if staged_xml_path is not None:
                    rewrite_staged_xml_taxonomy(staged_xml_path, path, args.taxonomy_root,
                        profile.get("offlineReferences") if profile else None,
                        profile.get("packageFiles") if profile else None,
                        payload.get("sha256"))
                elif large_report_path is not None:
                    rewrite_large_report_taxonomy(large_report_path, path, args.taxonomy_root,
                        profile.get("offlineReferences") if profile else None,
                        profile.get("packageFiles") if profile else None,
                        payload.get("sha256"))
                else:
                    report = json.loads(report_text, object_pairs_hook=reject_duplicate_json_keys)
                    report = safe_taxonomy_urls(report, args.taxonomy_root,
                        profile.get("offlineReferences") if profile else None,
                        profile.get("packageFiles") if profile else None)
                    path.write_text(json.dumps(report, ensure_ascii=False), encoding="utf-8")
                ok, logs, concept_count = run_arelle(path, args, workdir)
                plugin_failures = required_plugin_load_failures(logs, required_plugins)
                if plugin_failures:
                    diags = [diagnostic(op_id, "XBR010", "error",
                                        "Required Arelle plugin could not be loaded: " + plugin,
                                        "plugin:" + plugin)
                             for plugin in plugin_failures]
                    for item in plugin_completions:
                        if item["identifier"] in plugin_failures:
                            item["status"] = "unavailable"
                            item["detail"] = "Arelle reported that the required plugin could not be loaded."
                    return response(op_id, "incompleteValidation", {
                        "engine": "Arelle", "engineVersion": ENGINE_VERSION,
                        "completed": False, "conceptCount": concept_count,
                        "validationCompletion": completion,
                    }, diags, "A required Arelle plugin is unavailable")
                diags = log_diagnostics(logs, op_id, workdir)
                result = {"engine": "Arelle", "engineVersion": ENGINE_VERSION,
                          "completed": True, "conceptCount": concept_count,
                          "validationCompletion": completion}
                return response(op_id, "success" if ok else "domainFailure", result, diags,
                                None if ok else "Arelle did not complete report validation")
        except Exception as ex:
            return response(op_id, "domainFailure", error=str(ex)[:1000])

    return response(op_id, "domainFailure", error="Unsupported operation: " + str(operation)[:100])


def main():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--taxonomy-root")
    parser.add_argument("--profile-manifest")
    parser.add_argument("--profile-manifest-sha256")
    parser.add_argument("--package", action="append", default=[])
    parser.add_argument("--plugin", action="append", default=[])
    parser.add_argument("--disclosure-system")
    args, unknown = parser.parse_known_args()
    if unknown:
        emit(response("-", "domainFailure", error="Unsupported worker startup arguments"))
        return
    if args.taxonomy_root:
        args.taxonomy_root = str(Path(args.taxonomy_root).resolve(strict=True))
    args.profile_manifest_data = None
    args.profile_error = None
    if args.profile_manifest:
        try:
            args.profile_manifest_data = load_profile_manifest(
                args.profile_manifest, args.taxonomy_root, args.profile_manifest_sha256)
        except (OSError, ValueError) as ex:
            args.profile_error = str(ex)
    for package in args.package:
        if not Path(package).is_file():
            emit(response("-", "domainFailure", error="Configured taxonomy package is missing"))
            return

    while True:
        raw = sys.stdin.buffer.readline(MAX_MESSAGE_BYTES + 1)
        if not raw:
            return
        if len(raw) > MAX_MESSAGE_BYTES:
            emit(response("-", "domainFailure", error="Worker request exceeds the 4 MiB limit"))
            return
        try:
            req = json.loads(raw.decode("utf-8"))
            result = handle(req, args)
        except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as ex:
            result = response("-", "domainFailure", error="Malformed request: " + str(ex)[:500])
        emit(result)
        if isinstance(req, dict) and req.get("operation") == "shutdown":
            return


if __name__ == "__main__":
    main()
