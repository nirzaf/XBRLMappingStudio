#!/usr/bin/env python3
"""Offline Arelle worker using the supported Session API."""

import argparse
from collections import deque
from collections.abc import Mapping
from decimal import Decimal, InvalidOperation
import gc
import hashlib
import json
import logging
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
from urllib.parse import quote, unquote, urlsplit, urlunsplit
from urllib.request import url2pathname
from xml.parsers import expat
from xml.etree import ElementTree

PROTOCOL_VERSION = 2
SUPPORTED_PROFILE_SCHEMA_VERSIONS = (2, 3, 4, 5)
MAX_MESSAGE_BYTES = 4 * 1024 * 1024
MAX_RESPONSE_DIAGNOSTICS = 128
MAX_DIAGNOSTIC_MESSAGE_CHARS = 2_000
MAX_ARELLE_LOG_RECORDS = 512
MAX_ARELLE_LOG_BYTES = 1024 * 1024
MAX_ARELLE_LOG_VALUE_ITEMS = 16
MAX_ARELLE_LOG_VALUE_CHARS = 2_048
MAX_ARELLE_LOG_VALUE_DEPTH = 4
ENGINE_VERSION = "arelle-worker/1.0"
CAPABILITIES = ["handshake", "load-taxonomy", "validate-report"]
MAX_PROFILE_MANIFEST_BYTES = 16 * 1024 * 1024
MAX_PROFILE_FILE_BYTES = 512 * 1024 * 1024
MAX_PROFILE_TOTAL_BYTES = 2 * 1024 * 1024 * 1024
MAX_PROFILE_FILES = 16384
MAX_PROFILE_PRESENTATION_RELATIONSHIPS = 1_000_000
MAX_PROFILE_RELATIONSHIPS = 1_000_000
MAX_DOCUMENT_INFO_BYTES = 4 * 1024 * 1024
MAX_LARGE_REPORT_BYTES = 2 * 1024 * 1024 * 1024
MAX_TAXONOMY_GRAPH_FILES = 4096
MAX_TAXONOMY_GRAPH_DEPTH = 128
MAX_TAXONOMY_GRAPH_XML_BYTES = 256 * 1024 * 1024
MAX_TAXONOMY_XML_FILE_BYTES = 16 * 1024 * 1024
MAX_TAXONOMY_GRAPH_EDGES = 16384
TAXONOMY_XML_READ_BYTES = 64 * 1024
MAX_TAXONOMY_OFFLINE_ALIAS_BYTES = 256 * 1024 * 1024
MAX_TAXONOMY_ARCHIVES = 64
MAX_TAXONOMY_ARCHIVE_FILES = 16384
MAX_TAXONOMY_ARCHIVE_BYTES = 512 * 1024 * 1024
MAX_TAXONOMY_ARCHIVE_TOTAL_BYTES = 1024 * 1024 * 1024

_OFFLINE_NETWORK_AUDIT_EVENTS = frozenset({
    "socket.bind",
    "socket.connect",
    "socket.connect_ex",
    "socket.sendto",
    "socket.getaddrinfo",
    "socket.gethostbyname",
    "socket.gethostbyname_ex",
    "socket.gethostbyaddr",
    "socket.getnameinfo",
})


def install_offline_network_denial():
    """Deny Python socket connection and name-resolution requests for this process."""

    def deny_network(event, _args):
        if event in _OFFLINE_NETWORK_AUDIT_EVENTS:
            raise PermissionError("Network access is disabled for the offline Arelle worker")

    sys.addaudithook(deny_network)


if __name__ == "__main__":
    # Install before importing Arelle so worker initialization and every request
    # run under the same offline socket boundary.
    install_offline_network_denial()

XSD_NAMESPACE = "http://www.w3.org/2001/XMLSchema"
XSI_NAMESPACE = "http://www.w3.org/2001/XMLSchema-instance"
XLINK_NAMESPACE = "http://www.w3.org/1999/xlink"
LINK_NAMESPACE = "http://www.xbrl.org/2003/linkbase"
XML_NAMESPACE = "http://www.w3.org/XML/1998/namespace"
OASIS_CATALOG_NAMESPACE = "urn:oasis:names:tc:entity:xmlns:xml:catalog"
TAXONOMY_PACKAGE_NAMESPACE = "http://xbrl.org/2016/taxonomy-package"
PRESENTATION_ARCROLE = "http://www.xbrl.org/2003/arcrole/parent-child"
CALCULATION_ARCROLE = "http://www.xbrl.org/2003/arcrole/summation-item"

try:
    from arelle import PluginManager
    from arelle.api.Session import Session
    from arelle.RuntimeOptions import RuntimeOptions

    ARELLE_AVAILABLE = True
except Exception:  # The host must fail closed if this optional runtime is absent.
    PluginManager = None
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
    severity = _diagnostic_severity(level)
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


def _diagnostic_severity(level):
    severity = str(level or "error").lower().replace("_", "-")
    severity = {
        "info": "information",
        "warn": "warning",
        "fatal": "critical",
        "info-result": "information",
        "info-semantic": "information",
        "warning-semantic": "warning",
        "assertion-satisfied": "information",
        "inconsistency": "warning",
        "error-semantic": "error",
        "assertion-not-satisfied": "error",
    }.get(severity, severity)
    if severity not in ("information", "warning", "error", "critical"):
        return "error"
    return severity


def _bounded_log_value(value, depth=0):
    """Copy a log value into a small JSON-safe shape without retaining raw objects."""
    if depth >= MAX_ARELLE_LOG_VALUE_DEPTH:
        return "<log-value-depth-limit>"
    if value is None or isinstance(value, bool):
        return value
    if isinstance(value, float):
        return value if math.isfinite(value) else str(value)
    if isinstance(value, int):
        if value.bit_length() > 64:
            return f"<large-integer:{value.bit_length()}-bits>"
        return value
    if isinstance(value, Decimal):
        sign, digits, exponent = value.as_tuple()
        if len(digits) <= MAX_ARELLE_LOG_VALUE_CHARS:
            return str(value)[:MAX_ARELLE_LOG_VALUE_CHARS]
        exponent_text = str(exponent)[:MAX_ARELLE_LOG_VALUE_CHARS - 3]
        sign_text = "-" if sign else ""
        marker = "…e"
        prefix_length = max(0, MAX_ARELLE_LOG_VALUE_CHARS - len(sign_text) - len(marker) - len(exponent_text))
        prefix = "".join(str(digit) for digit in digits[:prefix_length])
        return sign_text + prefix + marker + exponent_text
    if isinstance(value, str):
        return value[:MAX_ARELLE_LOG_VALUE_CHARS]
    if isinstance(value, bytes):
        return value[:MAX_ARELLE_LOG_VALUE_CHARS].decode("utf-8", errors="replace")
    if isinstance(value, Mapping):
        result = {}
        iterator = iter(value.items())
        truncated = False
        for _ in range(MAX_ARELLE_LOG_VALUE_ITEMS):
            try:
                key, item = next(iterator)
            except StopIteration:
                break
            safe_key = key[:128] if isinstance(key, str) else f"<{type(key).__name__}>"
            result[safe_key] = _bounded_log_value(item, depth + 1)
        else:
            sentinel = object()
            truncated = next(iterator, sentinel) is not sentinel
        if truncated:
            result["__xms_truncated__"] = True
        return result
    if isinstance(value, (list, tuple)):
        result = []
        iterator = iter(value)
        for _ in range(MAX_ARELLE_LOG_VALUE_ITEMS):
            try:
                item = next(iterator)
            except StopIteration:
                break
            result.append(_bounded_log_value(item, depth + 1))
        else:
            sentinel = object()
            if next(iterator, sentinel) is not sentinel:
                result.append("<additional-values-omitted>")
        return result
    return f"<{type(value).__name__}>"


def _bounded_log_message(record):
    message = _bounded_log_value(getattr(record, "msg", ""))
    raw_arguments = getattr(record, "args", {})
    arguments = _bounded_log_value(raw_arguments)
    if isinstance(raw_arguments, tuple) and isinstance(arguments, list):
        arguments = tuple(arguments)
    try:
        if arguments and len(arguments) > 0:
            message = message % arguments
    except (TypeError, ValueError, KeyError, OverflowError):
        pass
    return _bounded_log_value(message)


def _is_blocking_log_level(level):
    return _diagnostic_severity(level) in ("error", "critical")


class BoundedLogMessages(list):
    """Structured log rows with explicit counts for records omitted by bounded capture."""

    def __init__(self):
        super().__init__()
        self.bytes_retained = 0
        self.suppressed_count = 0
        self.suppressed_blocking = False

    def suppress(self, level):
        self.suppressed_count += 1
        self.suppressed_blocking = self.suppressed_blocking or _is_blocking_log_level(level)


class BoundedArelleLogHandler(logging.Handler):
    """Capture Arelle messages without retaining its unbounded raw LogRecord buffer."""

    def __init__(self, max_records=MAX_ARELLE_LOG_RECORDS, max_bytes=MAX_ARELLE_LOG_BYTES):
        super().__init__()
        if max_records < 1 or max_bytes < 1:
            raise ValueError("Arelle log capture limits must be positive.")
        self.max_records = max_records
        self.max_bytes = max_bytes
        self.messages = BoundedLogMessages()

    def emit(self, record):
        level = getattr(record, "levelname", "error")
        try:
            if len(self.messages) >= self.max_records:
                self.messages.suppress(level)
                return
            arguments = _bounded_log_value(getattr(record, "args", {}))
            message = _bounded_log_message(record)
            if isinstance(message, str):
                message = message[:MAX_DIAGNOSTIC_MESSAGE_CHARS]
            else:
                message = _bounded_log_value(message)
            args = arguments.get("args", {}) if isinstance(arguments, Mapping) else {}
            row = {
                "levelname": str(level)[:128],
                "messageCode": _bounded_log_value(getattr(record, "messageCode", "")),
                "msg": message,
                "refs": _bounded_log_value(getattr(record, "refs", [])),
                "args": _bounded_log_value(args),
            }
            encoded_size = len(json.dumps(row, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
            if self.messages.bytes_retained + encoded_size > self.max_bytes:
                self.messages.suppress(level)
                return
            self.messages.append(row)
            self.messages.bytes_retained += encoded_size
        except Exception:
            # A malformed extension log record cannot break validation. Record that
            # it was omitted as blocking uncertainty rather than silently succeeding.
            self.messages.suppress("error")


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
    calculation_links = manifest.get("calculationLinks", [])
    definition_links = manifest.get("definitionLinks", [])
    if not isinstance(calculation_links, list):
        raise ValueError("Profile calculationLinks must be an array")
    if not isinstance(definition_links, list):
        raise ValueError("Profile definitionLinks must be an array")
    if schema_version < 5 and (calculation_links or definition_links):
        raise ValueError("Calculation and definition relationships require manifest schema version 5")
    if len(presentation_links) + len(calculation_links) + len(definition_links) > MAX_PROFILE_RELATIONSHIPS:
        raise ValueError("Profile relationships exceed the relationship limit")
    if calculation_links or definition_links:
        concepts = manifest.get("concepts")
        if not isinstance(concepts, list) or len(concepts) > 250_000:
            raise ValueError("Profile concepts must be an array within the 250000-concept limit")
        qualified_names = {
            concept.get("qualifiedName")
            for concept in concepts
            if isinstance(concept, dict) and isinstance(concept.get("qualifiedName"), str)
        }

        def valid_relationship_uri(value):
            try:
                parsed = urlsplit(value) if isinstance(value, str) else None
            except ValueError:
                return False
            if (parsed is None or not parsed.scheme or not value or len(value) > 2048
                    or value != value.strip()
                    or any(ch.isspace() or ord(ch) < 0x20 or 0x7f <= ord(ch) <= 0x9f for ch in value)):
                return False
            return (parsed.scheme.lower() not in ("http", "https")
                    or (bool(parsed.hostname) and parsed.username is None and parsed.password is None))

        def numeric_order(value):
            if not isinstance(value, (int, float)) or isinstance(value, bool):
                return None
            if isinstance(value, float) and not math.isfinite(value):
                return None
            try:
                result = Decimal(str(value))
            except (InvalidOperation, ValueError):
                return None
            if not result.is_finite() or abs(result) > Decimal("79228162514264337593543950335"):
                return None
            return result

        calculation_keys = set()
        for relationship in calculation_links:
            if not isinstance(relationship, dict):
                raise ValueError("Profile calculation relationships must be objects")
            role_uri = relationship.get("roleUri")
            parent_qname = relationship.get("parentQName")
            child_qname = relationship.get("childQName")
            target_role_uri = relationship.get("targetRoleUri")
            order = numeric_order(relationship.get("order", 0))
            weight = numeric_order(relationship.get("weight"))
            if (not valid_relationship_uri(role_uri)
                    or (target_role_uri is not None and not valid_relationship_uri(target_role_uri))
                    or not isinstance(parent_qname, str) or parent_qname not in qualified_names
                    or not isinstance(child_qname, str) or child_qname not in qualified_names
                    or order is None or weight not in (Decimal("-1"), Decimal("1"))):
                raise ValueError("Profile calculation relationships must have valid role URIs, +/-1 weight, numeric order, and indexed endpoints")
            key = (role_uri, parent_qname, child_qname, order, weight, target_role_uri)
            if key in calculation_keys:
                raise ValueError("Profile calculation relationships cannot contain duplicate arcs")
            calculation_keys.add(key)

        definition_keys = set()
        for relationship in definition_links:
            if not isinstance(relationship, dict):
                raise ValueError("Profile definition relationships must be objects")
            role_uri = relationship.get("roleUri")
            arc_role_uri = relationship.get("arcRoleUri")
            parent_qname = relationship.get("parentQName")
            child_qname = relationship.get("childQName")
            target_role_uri = relationship.get("targetRoleUri")
            context_element = relationship.get("contextElement")
            order = numeric_order(relationship.get("order", 0))
            usable = relationship.get("usable")
            closed = relationship.get("closed")
            if (not valid_relationship_uri(role_uri) or not valid_relationship_uri(arc_role_uri)
                    or (target_role_uri is not None and not valid_relationship_uri(target_role_uri))
                    or not isinstance(parent_qname, str) or parent_qname not in qualified_names
                    or not isinstance(child_qname, str) or child_qname not in qualified_names
                    or order is None or (context_element is not None
                        and context_element not in ("segment", "scenario"))
                    or (usable is not None and type(usable) is not bool)
                    or (closed is not None and type(closed) is not bool)):
                raise ValueError("Profile definition relationships must have valid role and arcrole URIs, supported attributes, numeric order, and indexed endpoints")
            key = (role_uri, arc_role_uri, parent_qname, child_qname, order,
                   target_role_uri, usable, closed, context_element)
            if key in definition_keys:
                raise ValueError("Profile definition relationships cannot contain duplicate arcs")
            definition_keys.add(key)
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

    def __new__(cls, paths, offline_references, package_archives=(), package_catalog_rewrites=()):
        result = super().__new__(cls, paths)
        result.offline_references = dict(offline_references)
        result.package_archives = tuple(package_archives)
        result.package_catalog_rewrites = tuple(package_catalog_rewrites)
        return result


def _safe_taxonomy_archive_member(member_name, is_directory=False):
    if not isinstance(member_name, str) or not member_name or "\\" in member_name or "\x00" in member_name:
        raise ValueError("Taxonomy package archive contains an unsafe member path")
    if member_name.startswith("/") or any(ord(character) < 0x20 or 0x7f <= ord(character) <= 0x9f
                                           for character in member_name):
        raise ValueError("Taxonomy package archive contains an unsafe member path")
    normalized = member_name[:-1] if is_directory and member_name.endswith("/") else member_name
    parts = normalized.split("/")
    if (not normalized or any(part in ("", ".", "..") for part in parts)
            or any(any(character in ":*?\"<>|" for character in part)
                   or part.endswith((" ", "."))
                   or part.split(".", 1)[0].upper() in {
                       "CON", "PRN", "AUX", "NUL",
                       *("COM" + str(index) for index in range(1, 10)),
                       *("LPT" + str(index) for index in range(1, 10)),
                   }
                   for part in parts)):
        raise ValueError("Taxonomy package archive member escapes its package root")
    return normalized


def _hash_package_file(path, expected_digest, description):
    if not isinstance(expected_digest, str) or len(expected_digest) != 71 \
            or not expected_digest.startswith("sha256:") \
            or any(character not in "0123456789abcdef" for character in expected_digest[7:]):
        raise ValueError(description + " does not have a valid pinned SHA-256 digest")
    digest = hashlib.sha256()
    byte_count = 0
    with path.open("rb") as source:
        while True:
            chunk = source.read(TAXONOMY_XML_READ_BYTES)
            if not chunk:
                break
            byte_count += len(chunk)
            if byte_count > MAX_PROFILE_FILE_BYTES:
                raise ValueError(description + " exceeds the profile file limit")
            digest.update(chunk)
    if "sha256:" + digest.hexdigest() != expected_digest:
        raise ValueError(description + " hash does not match packageFiles")
    return byte_count


def _parse_taxonomy_package_xml(raw_xml, description):
    lowered = raw_xml.lower()
    if b"<!doctype" in lowered or b"<!entity" in lowered:
        raise ValueError(description + " must not contain a DTD or entity declaration")
    try:
        root = ElementTree.fromstring(raw_xml)
    except ElementTree.ParseError as ex:
        raise ValueError(description + " is malformed XML") from ex
    if root.tag != "{" + TAXONOMY_PACKAGE_NAMESPACE + "}taxonomyPackage":
        raise ValueError(description + " must have a taxonomyPackage root element")
    identifiers = [element for element in root if element.tag == "{" + TAXONOMY_PACKAGE_NAMESPACE + "}identifier"]
    if len(identifiers) != 1 or not (identifiers[0].text or "").strip():
        raise ValueError(description + " must contain exactly one non-empty taxonomy-package identifier")
    return root


def _decode_safe_package_uri_path(raw_path, description):
    if (re.search(r"%(?![0-9a-fA-F]{2})", raw_path)
            or re.search(r"%(?:2f|5c)", raw_path, re.IGNORECASE)):
        raise ValueError(description + " has invalid or encoded path separators")
    try:
        decoded = unquote(raw_path, errors="strict")
    except UnicodeDecodeError as ex:
        raise ValueError(description + " is not valid UTF-8") from ex
    if ("\\" in decoded or "\x00" in decoded
            or any(ord(character) < 0x20 or 0x7f <= ord(character) <= 0x9f
                   for character in decoded)):
        raise ValueError(description + " contains an unsafe path")
    return decoded


def _validate_taxonomy_package_archive(relative_archive, taxonomy_root, package_files):
    archive_path = safe_package_path(relative_archive, taxonomy_root)
    archive_digest = package_files.get(relative_archive)
    archive_size = _hash_package_file(archive_path, archive_digest,
                                      "Taxonomy package archive " + relative_archive)
    if archive_size > MAX_TAXONOMY_ARCHIVE_BYTES:
        raise ValueError("Taxonomy package archive exceeds the compressed size limit: " + relative_archive)

    member_names = set()
    folded_member_names = set()
    package_roots = set()
    archive_members = set()
    rewrite_rules = []
    metadata_xml = None
    catalog_xml = None
    total_uncompressed = 0
    file_count = 0
    try:
        with zipfile.ZipFile(archive_path, "r") as archive:
            infos = archive.infolist()
            if len(infos) > MAX_TAXONOMY_ARCHIVE_FILES:
                raise ValueError("Taxonomy package archive exceeds the member-count limit")
            for info in infos:
                raw_name = getattr(info, "orig_filename", info.filename)
                is_directory = info.is_dir()
                member_name = _safe_taxonomy_archive_member(raw_name, is_directory)
                mode = info.external_attr >> 16
                file_type = stat.S_IFMT(mode)
                if (info.flag_bits & 0x1) or file_type == stat.S_IFLNK \
                        or file_type not in (0, stat.S_IFREG, stat.S_IFDIR):
                    raise ValueError("Taxonomy package archive contains an encrypted or non-regular member")
                if (is_directory and file_type == stat.S_IFREG) \
                        or (not is_directory and file_type == stat.S_IFDIR):
                    raise ValueError("Taxonomy package archive member has conflicting file type metadata")
                if raw_name != info.filename:
                    raise ValueError("Taxonomy package archive contains a truncated member name")
                if member_name in member_names or member_name.casefold() in folded_member_names:
                    raise ValueError("Taxonomy package archive contains duplicate or case-colliding member paths")
                member_names.add(member_name)
                folded_member_names.add(member_name.casefold())
                package_roots.add(member_name.split("/", 1)[0])
                if is_directory:
                    continue
                file_count += 1
                if file_count > MAX_TAXONOMY_ARCHIVE_FILES:
                    raise ValueError("Taxonomy package archive exceeds the member-count limit")
                if info.file_size < 0 or info.file_size > MAX_TAXONOMY_ARCHIVE_BYTES:
                    raise ValueError("Taxonomy package archive member exceeds the size limit")
                total_uncompressed += info.file_size
                if total_uncompressed > MAX_TAXONOMY_ARCHIVE_BYTES:
                    raise ValueError("Taxonomy package archive exceeds the uncompressed byte limit")

                expected_digest = package_files.get(member_name)
                if not isinstance(expected_digest, str):
                    raise ValueError("Taxonomy package archive member is not hash-pinned by packageFiles: "
                                     + member_name)
                extracted_path = safe_package_path(member_name, taxonomy_root)
                extracted_size = _hash_package_file(
                    extracted_path, expected_digest,
                    "Extracted taxonomy package member " + member_name)
                if extracted_size != info.file_size:
                    raise ValueError("Extracted taxonomy package member size differs from its archive: "
                                     + member_name)

                digest = hashlib.sha256()
                read_size = 0
                small_xml = bytearray() if member_name.endswith(("/META-INF/taxonomyPackage.xml",
                                                                  "/META-INF/catalog.xml")) else None
                with archive.open(info, "r") as member:
                    while True:
                        chunk = member.read(TAXONOMY_XML_READ_BYTES)
                        if not chunk:
                            break
                        read_size += len(chunk)
                        if read_size > info.file_size or read_size > MAX_TAXONOMY_ARCHIVE_BYTES:
                            raise ValueError("Taxonomy package archive member exceeds its declared size")
                        digest.update(chunk)
                        if small_xml is not None:
                            if len(small_xml) + len(chunk) > MAX_TAXONOMY_XML_FILE_BYTES:
                                raise ValueError("Taxonomy package metadata exceeds the XML size limit")
                            small_xml.extend(chunk)
                if read_size != info.file_size or "sha256:" + digest.hexdigest() != expected_digest:
                    raise ValueError("Taxonomy package archive member hash differs from packageFiles: "
                                     + member_name)
                archive_members.add(member_name)
                if member_name.endswith("/META-INF/taxonomyPackage.xml"):
                    metadata_xml = bytes(small_xml or b"")
                    metadata_path = member_name
                elif member_name.endswith("/META-INF/catalog.xml"):
                    catalog_xml = bytes(small_xml or b"")
                    catalog_path = member_name
    except (OSError, zipfile.BadZipFile, RuntimeError, NotImplementedError) as ex:
        raise ValueError("Taxonomy package archive is malformed or unreadable: " + relative_archive) from ex

    if len(package_roots) != 1:
        raise ValueError("Taxonomy package archive must contain one top-level directory")
    package_root = next(iter(package_roots))
    expected_metadata_path = package_root + "/META-INF/taxonomyPackage.xml"
    if metadata_xml is None or metadata_path != expected_metadata_path:
        raise ValueError("Taxonomy package archive is missing META-INF/taxonomyPackage.xml")
    metadata_root = _parse_taxonomy_package_xml(metadata_xml, "Taxonomy package metadata")
    entry_point_documents = []
    for element in metadata_root.iter():
        if "{" + XML_NAMESPACE + "}base" in element.attrib:
            raise ValueError("Taxonomy package metadata xml:base is unsupported")
        if element.tag == "{" + TAXONOMY_PACKAGE_NAMESPACE + "}entryPointDocument":
            href = element.get("href")
            if (not isinstance(href, str) or not href or len(href) > 2048
                    or any(character.isspace() or ord(character) < 0x20 for character in href)):
                raise ValueError("Taxonomy package entry point document has an invalid href")
            entry_point_documents.append(href)
    entry_points = [element for element in metadata_root.iter()
                    if element.tag == "{" + TAXONOMY_PACKAGE_NAMESPACE + "}entryPoint"]
    if any(not any(document.tag == "{" + TAXONOMY_PACKAGE_NAMESPACE + "}entryPointDocument"
                   for document in entry_point.iter()) for entry_point in entry_points):
        raise ValueError("Taxonomy package entry points must declare at least one document")

    if catalog_xml is not None:
        lowered = catalog_xml.lower()
        if b"<!doctype" in lowered or b"<!entity" in lowered:
            raise ValueError("Taxonomy package catalog must not contain a DTD or entity declaration")
        try:
            catalog = ElementTree.fromstring(catalog_xml)
        except ElementTree.ParseError as ex:
            raise ValueError("Taxonomy package catalog is malformed XML") from ex
        if catalog.tag != "{" + OASIS_CATALOG_NAMESPACE + "}catalog":
            raise ValueError("Taxonomy package catalog must use the OASIS XML Catalog namespace")
        catalog_directory = posixpath.dirname(catalog_path)
        xml_base = catalog.get("{" + XML_NAMESPACE + "}base")
        if xml_base is not None:
            parsed_base = urlsplit(xml_base)
            if (parsed_base.scheme or parsed_base.netloc or parsed_base.query or parsed_base.fragment
                    or parsed_base.path.startswith("/") or "\\" in xml_base
                    or any(character.isspace() for character in xml_base)):
                raise ValueError("Taxonomy package catalog has an unsupported xml:base")
            decoded_base = _decode_safe_package_uri_path(parsed_base.path,
                                                         "Taxonomy package catalog xml:base")
            catalog_directory = posixpath.normpath(posixpath.join(catalog_directory, decoded_base))
            if catalog_directory != package_root and not catalog_directory.startswith(package_root + "/"):
                raise ValueError("Taxonomy package catalog xml:base escapes its package root")

        seen_prefixes = set()
        for child in catalog:
            if child.tag != "{" + OASIS_CATALOG_NAMESPACE + "}rewriteURI":
                raise ValueError("Taxonomy package catalog uses an unsupported XML Catalog entry")
            prefix = child.get("uriStartString")
            rewrite_prefix = child.get("rewritePrefix")
            if not isinstance(prefix, str) or not isinstance(rewrite_prefix, str):
                raise ValueError("Taxonomy package catalog rewriteURI is incomplete")
            parsed_prefix = urlsplit(prefix)
            if (parsed_prefix.scheme not in ("http", "https") or not parsed_prefix.hostname
                    or parsed_prefix.username is not None or parsed_prefix.password is not None
                    or parsed_prefix.query or parsed_prefix.fragment or not parsed_prefix.path.endswith("/")
                    or "\\" in prefix or any(character.isspace() or ord(character) < 0x20
                                              for character in prefix)):
                raise ValueError("Taxonomy package catalog URI prefix is not a canonical HTTP(S) directory")
            if (re.search(r"%(?![0-9a-fA-F]{2})", parsed_prefix.path)
                    or re.search(r"%(?:2f|5c)", parsed_prefix.path, re.IGNORECASE)):
                raise ValueError("Taxonomy package catalog URI prefix has invalid percent encoding")
            if prefix in seen_prefixes:
                raise ValueError("Taxonomy package catalog contains duplicate rewriteURI prefixes")
            seen_prefixes.add(prefix)

            parsed_target = urlsplit(rewrite_prefix)
            if (parsed_target.scheme or parsed_target.netloc or parsed_target.query or parsed_target.fragment
                    or parsed_target.path.startswith("/") or "\\" in rewrite_prefix
                    or any(character.isspace() for character in rewrite_prefix)
                    or not parsed_target.path.endswith("/")):
                raise ValueError("Taxonomy package catalog rewritePrefix must be a relative directory")
            decoded_target = _decode_safe_package_uri_path(
                parsed_target.path, "Taxonomy package catalog rewritePrefix")
            target_directory = posixpath.normpath(
                posixpath.join(catalog_directory, decoded_target)).rstrip("/")
            if target_directory != package_root and not target_directory.startswith(package_root + "/"):
                raise ValueError("Taxonomy package catalog rewritePrefix escapes its package root")
            rewrite_rules.append((prefix, target_directory + "/", relative_archive))

    return {
        "path": relative_archive,
        "members": frozenset(archive_members),
        "rewrites": tuple(rewrite_rules),
        "uncompressed_bytes": total_uncompressed,
        "compressed_bytes": archive_size,
        "entry_point_documents": tuple(entry_point_documents),
        "metadata_directory": posixpath.dirname(metadata_path),
    }


def validate_taxonomy_package_archives(taxonomy_root, package_files, entry_point=None,
                                       offline_references=None):
    """Validate pinned XBRL Taxonomy Packages and return their restricted offline catalogs."""
    if not isinstance(package_files, dict):
        raise ValueError("Taxonomy packageFiles must be an object")
    archive_paths = sorted(path for path in package_files
                           if isinstance(path, str) and path.lower().endswith(".zip"))
    if len(archive_paths) > MAX_TAXONOMY_ARCHIVES:
        raise ValueError("Profile contains too many taxonomy package archives")
    archives = [_validate_taxonomy_package_archive(path, taxonomy_root, package_files)
                for path in archive_paths]
    total_members = sum(len(archive["members"]) for archive in archives)
    total_uncompressed = sum(archive["uncompressed_bytes"] for archive in archives)
    total_compressed = sum(archive["compressed_bytes"] for archive in archives)
    if total_members > MAX_TAXONOMY_ARCHIVE_FILES:
        raise ValueError("Taxonomy packages exceed the combined member-count limit")
    if total_uncompressed > MAX_TAXONOMY_ARCHIVE_TOTAL_BYTES \
            or total_compressed > MAX_TAXONOMY_ARCHIVE_TOTAL_BYTES:
        raise ValueError("Taxonomy packages exceed the combined archive byte limit")
    rewrites = []
    by_prefix = {}
    for archive in archives:
        for prefix, target_directory, archive_path in archive["rewrites"]:
            previous = by_prefix.get(prefix)
            if previous is not None and previous[0] != target_directory:
                raise ValueError("Taxonomy packages contain conflicting offline URI mappings: " + prefix)
            by_prefix[prefix] = (target_directory, archive_path)
            rewrites.append((prefix, target_directory, archive_path))
    if entry_point is not None:
        for archive in archives:
            if entry_point not in archive["members"] or not archive["entry_point_documents"]:
                continue
            resolved_documents = set()
            for href in archive["entry_point_documents"]:
                parsed = urlsplit(href)
                if parsed.scheme or parsed.netloc:
                    if (parsed.scheme not in ("http", "https") or not parsed.hostname
                            or parsed.username is not None or parsed.password is not None
                            or parsed.query or parsed.fragment or "\\" in href):
                        raise ValueError("Taxonomy package entry point document URI is unsupported")
                    _decode_safe_package_uri_path(parsed.path,
                                                  "Taxonomy package entry point document URI")
                    base_reference = urlunsplit((parsed.scheme, parsed.netloc, parsed.path, "", ""))
                    explicit_target = (offline_references or {}).get(base_reference)
                    catalog_target = _resolve_taxonomy_package_reference(
                        base_reference, rewrites, package_files)
                    if explicit_target is not None and catalog_target is not None \
                            and explicit_target != catalog_target:
                        raise ValueError("Taxonomy package entry point has conflicting offline mappings")
                    target = explicit_target or catalog_target
                    if target is not None:
                        resolved_documents.add(target)
                    continue
                if parsed.query or parsed.fragment or parsed.path.startswith("/") or "\\" in parsed.path:
                    raise ValueError("Taxonomy package relative entry point document URI is unsafe")
                try:
                    decoded = _decode_safe_package_uri_path(
                        parsed.path, "Taxonomy package entry point document URI")
                except ValueError as ex:
                    raise ValueError("Taxonomy package entry point document URI is unsafe") from ex
                target = posixpath.normpath(posixpath.join(archive["metadata_directory"], decoded))
                package_root = next(iter(archive["members"])).split("/", 1)[0]
                if target != package_root and not target.startswith(package_root + "/"):
                    raise ValueError("Taxonomy package entry point document escapes its package root")
                if target in package_files:
                    resolved_documents.add(target)
            if entry_point not in resolved_documents:
                raise ValueError("Selected profile entry point is not declared by its pinned taxonomyPackage.xml")
    return archives, tuple(rewrites)


def _resolve_taxonomy_package_reference(reference, package_catalog_rewrites, package_files):
    matches = _best_taxonomy_package_rewrites(reference, package_catalog_rewrites)
    if not matches:
        return None
    targets = set()
    for prefix, target_directory, _archive_path in matches:
        raw_suffix = reference[len(prefix):]
        suffix = _decode_safe_package_uri_path(raw_suffix, "Taxonomy package URI mapping")
        parts = suffix.split("/")
        if any(part in ("", ".", "..") for part in parts):
            raise ValueError("Taxonomy package URI mapping escapes its package root")
        target = posixpath.normpath(posixpath.join(target_directory, suffix))
        if target not in package_files:
            raise ValueError("Taxonomy package URI mapping targets an unpinned package file: " + target)
        targets.add(target)
    if len(targets) != 1:
        raise ValueError("Taxonomy packages contain ambiguous offline URI mappings: " + reference[:256])
    return next(iter(targets))


def _best_taxonomy_package_rewrites(reference, package_catalog_rewrites):
    matches = [rule for rule in package_catalog_rewrites if reference.startswith(rule[0])]
    if not matches:
        return []
    longest = max(len(rule[0]) for rule in matches)
    return [rule for rule in matches if len(rule[0]) == longest]


def _resolve_taxonomy_edge(current_path, reference, kind, package_files,
                           offline_references=None, used_offline_references=None,
                           package_catalog_rewrites=()):
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
        if re.search(r"%(?![0-9a-fA-F]{2})", parsed.path):
            raise ValueError("External taxonomy dependency has invalid percent encoding: " + current_path)
        if re.search(r"%(?:2f|5c)", parsed.path, re.IGNORECASE):
            raise ValueError("External taxonomy dependency has an encoded path separator: " + current_path)
        if kind == "schemaLocation" and (parsed.fragment or "#" in reference):
            raise ValueError("External taxonomy schemaLocation fragments are unsupported: " + current_path)
        base_reference = urlunsplit((parsed.scheme, parsed.netloc, parsed.path, "", ""))
        explicit_target = (offline_references or {}).get(base_reference)
        catalog_target = _resolve_taxonomy_package_reference(
            base_reference, package_catalog_rewrites, package_files)
        if explicit_target is not None and catalog_target is not None \
                and explicit_target != catalog_target:
            raise ValueError("Taxonomy dependency has conflicting exact and package-catalog mappings: "
                             + base_reference[:256])
        target = explicit_target or catalog_target
        if not isinstance(target, str) or target not in package_files:
            raise ValueError("External taxonomy dependency has no exact hash-pinned offlineReferences mapping "
                             "or taxonomy-package catalog rule: "
                             + base_reference[:256])
        if target.lower().endswith(".zip"):
            raise ValueError("External taxonomy dependency must map to an extracted, hash-pinned package file: "
                             + target)
        if used_offline_references is not None and explicit_target is not None:
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
        raise ValueError("Taxonomy dependency must resolve to an extracted, hash-pinned package file: " + resolved)
    return resolved


def validate_taxonomy_dependency_closure(entry_point, taxonomy_root, package_files, offline_references=None,
                                         validated_package_catalog_rewrites=None):
    """Validate a bounded file and ZIP taxonomy dependency graph before Arelle loads it.

    External HTTP(S) references may resolve only through the profile's exact
    hash-pinned offlineReferences map or a validated ZIP Taxonomy Package catalog.
    The closure and each used archive are copied into a private snapshot before load.
    """
    if not isinstance(package_files, dict) or entry_point not in package_files:
        raise ValueError("Taxonomy entry point is not hash-pinned by packageFiles")

    root = Path(taxonomy_root).resolve(strict=True)
    package_archives, package_catalog_rewrites = validate_taxonomy_package_archives(
        root, package_files, entry_point, offline_references)
    if validated_package_catalog_rewrites is not None \
            and tuple(validated_package_catalog_rewrites) != package_catalog_rewrites:
        raise ValueError("Validated taxonomy package catalog mappings changed before snapshotting")
    pending = deque([(entry_point, 0)])
    visited = set()
    scanned = []
    used_offline_references = {}
    used_package_catalog_rewrites = set()
    used_package_archives = {archive["path"] for archive in package_archives
                             if entry_point in archive["members"]}
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
                                            offline_references, used_offline_references,
                                            package_catalog_rewrites)
            parsed_reference = urlsplit(reference)
            base_reference = urlunsplit((parsed_reference.scheme, parsed_reference.netloc,
                                         parsed_reference.path, "", ""))
            if target is not None and (offline_references or {}).get(base_reference) is None:
                for rewrite in _best_taxonomy_package_rewrites(base_reference, package_catalog_rewrites):
                    _prefix, _target_directory, archive_path = rewrite
                    used_package_archives.add(archive_path)
                    used_package_catalog_rewrites.add(rewrite)
            if target is not None and target not in visited:
                # Validate path/symlink/existence before it enters the work queue.
                try:
                    safe_package_path(target, root)
                except (OSError, ValueError) as ex:
                    raise ValueError("Pinned taxonomy dependency is unavailable: " + target) from ex
                discovered.add(target)
        for target in sorted(discovered):
            pending.append((target, depth + 1))
    return TaxonomyDependencyClosure(scanned, used_offline_references,
                                     sorted(used_package_archives, key=str.casefold),
                                     sorted(used_package_catalog_rewrites))


def _write_offline_taxonomy_mapping_plugin(snapshot_root, offline_references, package_files,
                                           package_catalog_rewrites=()):
    """Create a private Arelle hook that maps validated taxonomy URIs to local snapshots."""
    root = Path(snapshot_root).resolve(strict=True)
    alias_root = root / ".xms-offline-uri"
    if offline_references and alias_root.exists():
        raise ValueError("Taxonomy package paths conflict with the private offline URI directory")
    plugin_directory = root / ".xms-offline-catalog"
    if plugin_directory.exists():
        raise ValueError("Taxonomy package paths conflict with the private offline catalog directory")
    plugin_directory.mkdir(mode=0o700)
    remappings = {}
    for prefix, target_directory, _archive_path in package_catalog_rewrites:
        target_parts = target_directory.rstrip("/").split("/")
        if (not target_directory or any(part in ("", ".", "..") for part in target_parts)
                or any("\\" in part or ":" in part for part in target_parts)):
            raise ValueError("Validated taxonomy package catalog target is unsafe")
        local_directory = root.joinpath(*target_parts)
        try:
            resolved_directory = local_directory.resolve(strict=False)
            resolved_directory.relative_to(root)
            if resolved_directory.exists() and not resolved_directory.is_dir():
                raise ValueError("Taxonomy package catalog target conflicts with a snapshot file")
            resolved_directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        except (OSError, ValueError) as ex:
            raise ValueError("Could not create private taxonomy package catalog target") from ex
        replacement = str(resolved_directory) + os.sep
        previous = remappings.get(prefix)
        if previous is not None and previous != replacement:
            raise ValueError("Offline taxonomy URI mappings conflict on their local prefix")
        remappings[prefix] = replacement

    if offline_references:
        alias_root.mkdir(mode=0o700, exist_ok=True)
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
        package_matches = _best_taxonomy_package_rewrites(reference, package_catalog_rewrites)
        if package_matches:
            catalog_target = _resolve_taxonomy_package_reference(
                reference, package_catalog_rewrites, package_files)
            if catalog_target != relative_path:
                raise ValueError("Offline taxonomy URI mappings conflict on their target")
            continue
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

    plugin_path = plugin_directory / "arelle_offline_taxonomy_catalog.py"
    plugin_source = '''\
MAPPINGS = {mappings!r}

def apply_offline_taxonomy_mappings(cntlr, _options, _filesource, _entrypoints, **_kwargs):
    package_manager = cntlr.packages._package_manager
    package_config = package_manager.packagesConfig
    if not isinstance(package_config, dict):
        raise RuntimeError("Arelle taxonomy package manager is not initialized")
    remappings = package_config.setdefault("remappings", {{}})
    for prefix, target in MAPPINGS.items():
        existing = remappings.get(prefix)
        if existing is not None and existing != target:
            raise ValueError("Arelle taxonomy URI mapping conflicts with a configured package")
        remappings[prefix] = target

__pluginInfo__ = {{
    "name": "XBRL Mapping Studio Offline Taxonomy Mappings",
    "version": "1.0",
    "description": "Applies private hash-verified offline taxonomy mappings.",
    "license": "Apache-2",
    "author": "XBRL Mapping Studio",
    "CntlrCmdLine.Filing.Start": apply_offline_taxonomy_mappings,
}}
'''.format(mappings=remappings)
    plugin_path.write_text(plugin_source, encoding="utf-8")
    if os.name != "nt":
        plugin_path.chmod(0o400)
    return plugin_path


def _copy_verified_taxonomy_snapshot(entry_point, relative_paths, taxonomy_root, snapshot_root,
                                     package_files, offline_references=None,
                                     package_catalog_rewrites=()):
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

    if offline_references or package_catalog_rewrites:
        _write_offline_taxonomy_mapping_plugin(
            target_root, offline_references or {}, package_files, package_catalog_rewrites)
    return safe_package_path(entry_point, target_root)


def run_arelle_with_verified_taxonomy(entry_point, relative_paths, taxonomy_root, package_files, args,
                                      relationship_metadata_consumer=None):
    """Keep a verified private dependency snapshot alive for the complete Arelle load."""
    with tempfile.TemporaryDirectory(prefix="xms-taxonomy-") as snapshot_directory:
        os.chmod(snapshot_directory, 0o700)
        snapshot_entry = _copy_verified_taxonomy_snapshot(
            entry_point, relative_paths, taxonomy_root, snapshot_directory, package_files,
            getattr(relative_paths, "offline_references", {}),
            getattr(relative_paths, "package_catalog_rewrites", ()))
        run_args = args
        mapping_plugin = Path(snapshot_directory) / ".xms-offline-catalog" / "arelle_offline_taxonomy_catalog.py"
        if mapping_plugin.is_file():
            import copy
            run_args = copy.copy(args)
            run_args.plugin = list(getattr(args, "plugin", []) or [])
            run_args.plugin.append(str(mapping_plugin))
        if relationship_metadata_consumer is None:
            return run_arelle(snapshot_entry, run_args)
        return run_arelle(snapshot_entry, run_args,
                          relationship_metadata_consumer=relationship_metadata_consumer)


def safe_taxonomy_urls(report, taxonomy_root, offline_references=None, package_files=None,
                       package_catalog_rewrites=()):
    info = report.get("documentInfo")
    if not isinstance(info, dict) or not isinstance(info.get("taxonomy"), list):
        raise ValueError("Report has no documentInfo.taxonomy array")
    fixed = []
    for reference in info["taxonomy"]:
        if not isinstance(reference, str):
            raise ValueError("Taxonomy references must be strings")
        parsed = urlsplit(reference)
        if parsed.scheme in ("http", "https"):
            if parsed.query or parsed.fragment or "?" in reference or "#" in reference:
                raise ValueError("Taxonomy URI cannot include a query or fragment")
            if "\\" in reference or any(character.isspace() for character in reference):
                raise ValueError("Taxonomy URI is not a canonical offline file URI")
            _decode_safe_package_uri_path(parsed.path, "Taxonomy URI")
            explicit_target = (offline_references or {}).get(reference)
            catalog_target = None
            if package_files is not None:
                base_reference = urlunsplit((parsed.scheme, parsed.netloc, parsed.path, "", ""))
                catalog_target = _resolve_taxonomy_package_reference(
                    base_reference, package_catalog_rewrites, package_files)
            if explicit_target is not None and catalog_target is not None \
                    and explicit_target != catalog_target:
                raise ValueError("Taxonomy URI has conflicting exact and package-catalog mappings")
            relative = explicit_target or catalog_target
            if relative is None:
                raise ValueError("Taxonomy URI has no approved offline package mapping")
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


def _staged_taxonomy_relative_path(reference, taxonomy_root, offline_references, package_files,
                                   package_catalog_rewrites=()):
    localized = safe_taxonomy_urls(
        {"documentInfo": {"taxonomy": [reference]}}, taxonomy_root,
        offline_references, package_files, package_catalog_rewrites)["documentInfo"]["taxonomy"][0]
    if os.name == "nt":
        local_path = Path(localized)
    else:
        parsed = urlsplit(localized)
        local_path = Path(url2pathname(unquote(parsed.path))) if parsed.scheme == "file" else Path(localized)
    root = Path(taxonomy_root).resolve(strict=True)
    try:
        relative = local_path.resolve(strict=True).relative_to(root).as_posix()
    except ValueError as ex:
        raise ValueError("Localized schemaRef is outside the configured taxonomy root") from ex
    safe_package_path(relative, root)
    return relative


def _snapshot_report_taxonomy(taxonomy_references, taxonomy_root, snapshot_root,
                              offline_references, package_files,
                              package_catalog_rewrites=()):
    """Localize report taxonomy references into a hash-verified private closure snapshot."""
    if not isinstance(taxonomy_references, list):
        raise ValueError("Report documentInfo.taxonomy must be an array")
    if any(not isinstance(reference, str) for reference in taxonomy_references):
        raise ValueError("Taxonomy references must be strings")
    if not taxonomy_references:
        return [], None
    root = Path(taxonomy_root).resolve(strict=True)
    target_root = Path(snapshot_root).resolve(strict=True)
    relative_by_reference = {
        reference: _staged_taxonomy_relative_path(
            reference, root, offline_references, package_files, package_catalog_rewrites)
        for reference in dict.fromkeys(taxonomy_references)
    }
    closure_paths = set()
    used_offline_references = {}
    used_package_catalog_rewrites = set()
    for relative in dict.fromkeys(relative_by_reference.values()):
        closure = validate_taxonomy_dependency_closure(
            relative, root, package_files, offline_references,
            package_catalog_rewrites)
        closure_paths.update(closure)
        used_offline_references.update(closure.offline_references)
        used_package_catalog_rewrites.update(closure.package_catalog_rewrites)
    entry_point = next(iter(relative_by_reference.values()))
    _copy_verified_taxonomy_snapshot(
        entry_point, sorted(closure_paths), root, target_root, package_files,
        used_offline_references, sorted(used_package_catalog_rewrites))
    localized = []
    for reference in taxonomy_references:
        relative = relative_by_reference[reference]
        local_path = safe_package_path(relative, target_root)
        localized.append(str(local_path) if os.name == "nt" else local_path.as_uri())
    mapping_plugin = target_root / ".xms-offline-catalog" / "arelle_offline_taxonomy_catalog.py"
    return localized, mapping_plugin if mapping_plugin.is_file() else None


def _copy_staged_taxonomy_snapshot(relative_paths, taxonomy_root, snapshot_root,
                                   package_files, offline_references,
                                   package_catalog_rewrites=()):
    """Copy only referenced taxonomy closure bytes beside the private staged instance."""
    root = Path(taxonomy_root).resolve(strict=True)
    target_root = Path(snapshot_root).resolve(strict=True)
    relative_paths = sorted(set(relative_paths))
    if not relative_paths:
        raise ValueError("Staged xBRL-XML has no localized taxonomy entry point")

    if package_files is not None:
        closure_paths = set()
        used_offline_references = {}
        used_package_catalog_rewrites = set()
        for relative in relative_paths:
            closure = validate_taxonomy_dependency_closure(
                relative, root, package_files, offline_references,
                package_catalog_rewrites)
            closure_paths.update(closure)
            used_offline_references.update(closure.offline_references)
            used_package_catalog_rewrites.update(closure.package_catalog_rewrites)
        _copy_verified_taxonomy_snapshot(
            relative_paths[0], sorted(closure_paths), root, target_root,
            package_files, used_offline_references,
            sorted(used_package_catalog_rewrites))
        mapping_plugin = target_root / ".xms-offline-catalog" / "arelle_offline_taxonomy_catalog.py"
        return (mapping_plugin if mapping_plugin.is_file() else None), []

    total_bytes = 0
    for relative in relative_paths:
        source = safe_package_path(relative, root)
        target = target_root.joinpath(*relative.split("/"))
        target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        file_bytes = 0
        try:
            with source.open("rb") as source_file, target.open("xb") as target_file:
                for chunk in iter(lambda: source_file.read(TAXONOMY_XML_READ_BYTES), b""):
                    file_bytes += len(chunk)
                    total_bytes += len(chunk)
                    if file_bytes > MAX_TAXONOMY_XML_FILE_BYTES or total_bytes > MAX_TAXONOMY_GRAPH_XML_BYTES:
                        raise ValueError("Staged XML taxonomy exceeds the bounded offline snapshot budget")
                    target_file.write(chunk)
            if os.name != "nt":
                target.chmod(0o400)
        except OSError as ex:
            raise ValueError("Could not create private staged XML taxonomy snapshot") from ex
    return None, []


def rewrite_staged_xml_taxonomy(report_path, output_path, taxonomy_root, offline_references,
                                package_files, expected_digest, worker_args=None,
                                package_catalog_rewrites=()):
    """Verify an exact staged XML instance, then localize schemaRefs to a private snapshot."""
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
        raise ValueError(f"Staged xBRL-XML is malformed: {ex}") from ex
    if first_event != "start" or root.getroottree().docinfo.doctype:
        raise ValueError("Staged xBRL-XML must have an instance root and no DTD")
    if root.tag != "{http://www.xbrl.org/2003/instance}xbrl":
        raise ValueError("Staged XML root is not an xBRL instance")

    schema_references = []
    depth = 1
    try:
        for event, element in events:
            if event == "start":
                depth += 1
                continue
            if depth == 2 and element.tag == "{http://www.xbrl.org/2003/linkbase}schemaRef":
                reference = element.get("{http://www.w3.org/1999/xlink}href")
                if not isinstance(reference, str) or not reference:
                    raise ValueError("Staged xBRL-XML has a schemaRef without an xlink:href")
                if len(schema_references) >= MAX_TAXONOMY_GRAPH_FILES:
                    raise ValueError("Staged xBRL-XML has too many schemaRef elements")
                schema_references.append(reference)
            if depth > 1:
                parent = element.getparent()
                element.clear()
                while element.getprevious() is not None:
                    del parent[0]
            depth -= 1
    except etree.XMLSyntaxError as ex:
        raise ValueError(f"Staged xBRL-XML is malformed: {ex}") from ex
    if not schema_references:
        raise ValueError("Staged xBRL-XML has no schemaRef")

    relative_by_reference = {
        reference: _staged_taxonomy_relative_path(
            reference, taxonomy_root, offline_references, package_files,
            package_catalog_rewrites)
        for reference in dict.fromkeys(schema_references)
    }
    mapping_plugin, package_paths = _copy_staged_taxonomy_snapshot(
        relative_by_reference.values(), taxonomy_root, output_path.parent,
        package_files, offline_references, package_catalog_rewrites)
    arelle_args = worker_args
    if (mapping_plugin is not None or package_paths) and worker_args is not None:
        import copy
        arelle_args = copy.copy(worker_args)
        arelle_args.plugin = list(getattr(worker_args, "plugin", []) or [])
        for package_path in package_paths:
            package_text = str(package_path)
            if package_text not in arelle_args.plugin:
                arelle_args.plugin.append(package_text)
        if mapping_plugin is not None:
            arelle_args.plugin.append(str(mapping_plugin))

    try:
        events = etree.iterparse(str(report_path), events=("start", "end"),
                                 resolve_entities=False, no_network=True, load_dtd=False,
                                 recover=False, huge_tree=False)
        second_event, second_root = next(events)
    except StopIteration as ex:
        raise ValueError("Staged xBRL-XML is empty") from ex
    except etree.XMLSyntaxError as ex:
        raise ValueError(f"Staged xBRL-XML is malformed: {ex}") from ex
    if second_event != "start" or second_root.tag != "{http://www.xbrl.org/2003/instance}xbrl" \
            or second_root.getroottree().docinfo.doctype:
        raise ValueError("Staged xBRL-XML changed or is not a DTD-free xBRL instance")

    schema_ref_count = 0
    depth = 1
    with etree.xmlfile(str(output_path), encoding="utf-8") as writer:
        writer.write_declaration()
        with writer.element(second_root.tag, attrib=dict(second_root.attrib), nsmap=second_root.nsmap):
            for event, element in events:
                if event == "start":
                    depth += 1
                    continue
                if depth == 2:
                    if element.tag == "{http://www.xbrl.org/2003/linkbase}schemaRef":
                        reference = element.get("{http://www.w3.org/1999/xlink}href")
                        if reference not in relative_by_reference:
                            raise ValueError("Staged xBRL-XML schemaRef changed while being localized")
                        element.set("{http://www.w3.org/1999/xlink}href",
                                    quote(relative_by_reference[reference], safe="/-._~"))
                        schema_ref_count += 1
                    writer.write(element)
                    parent = element.getparent()
                    element.clear()
                    while element.getprevious() is not None:
                        del parent[0]
                depth -= 1
    if schema_ref_count != len(schema_references):
        raise ValueError("Staged xBRL-XML changed while its schemaRefs were localized")
    return arelle_args


def reject_duplicate_json_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON object key: " + str(key)[:120])
        result[key] = value
    return result


def rewrite_large_report_taxonomy(report_path, output_path, taxonomy_root, offline_references, package_files,
                                 expected_digest, package_catalog_rewrites=()):
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
        mapping_plugin = None
        if package_files is not None:
            localized, mapping_plugin = _snapshot_report_taxonomy(
                document_info.get("taxonomy"), taxonomy_root, output_path.parent,
                offline_references or {}, package_files, package_catalog_rewrites)
            document_info["taxonomy"] = localized
        else:
            safe_report = safe_taxonomy_urls({"documentInfo": document_info}, taxonomy_root,
                                             offline_references, package_files,
                                             package_catalog_rewrites)
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
    return mapping_plugin


def _relationship_decimal(relationship, decimal_attribute, value_attribute, default):
    value = getattr(relationship, decimal_attribute, None)
    if value is None:
        value = getattr(relationship, value_attribute, None)
    if value is None:
        value = default
    if isinstance(value, bool):
        raise ValueError("Arelle returned a non-numeric taxonomy relationship attribute")
    try:
        result = Decimal(str(value))
    except (InvalidOperation, ValueError) as ex:
        raise ValueError("Arelle returned an invalid taxonomy relationship attribute") from ex
    if not result.is_finite() or abs(result) > Decimal("79228162514264337593543950335"):
        raise ValueError("Arelle returned an out-of-range taxonomy relationship attribute")
    return result


def _relationship_attribute(relationship, name, fallback=None):
    getter = getattr(relationship, "get", None)
    if callable(getter):
        return getter(name)
    return getattr(relationship, fallback or name, None)


def _relationship_boolean_attribute(relationship, name):
    value = _relationship_attribute(relationship, name)
    if value is None or isinstance(value, bool):
        return value
    if value in ("true", "1"):
        return True
    if value in ("false", "0"):
        return False
    raise ValueError("Arelle returned an invalid boolean taxonomy relationship attribute")


def _relationship_link_type(qname):
    if getattr(qname, "namespaceURI", None) != LINK_NAMESPACE:
        return None
    local_name = getattr(qname, "localName", None)
    return local_name if local_name in ("presentationLink", "calculationLink", "definitionLink") else None


def _profile_concept_names(profile):
    concepts = profile.get("concepts", []) if isinstance(profile, dict) else []
    if not isinstance(concepts, list):
        raise ValueError("Installed profile concepts are unavailable for relationship extraction")
    result = {}
    for concept in concepts:
        if not isinstance(concept, dict):
            raise ValueError("Installed profile contains an invalid concept for relationship extraction")
        namespace = concept.get("namespace")
        local_name = concept.get("localName")
        qualified_name = concept.get("qualifiedName")
        if not all(isinstance(value, str) and value for value in (namespace, local_name, qualified_name)):
            raise ValueError("Installed profile concept identity is incomplete for relationship extraction")
        key = (namespace, local_name)
        if key in result and result[key] != qualified_name:
            raise ValueError("Installed profile has ambiguous qualified names for a relationship endpoint")
        result[key] = qualified_name
    return result


def _relationship_endpoint_name(model_object, concept_names):
    qname = getattr(model_object, "qname", None)
    namespace = getattr(qname, "namespaceURI", None)
    local_name = getattr(qname, "localName", None)
    qualified_name = concept_names.get((namespace, local_name))
    if qualified_name is None:
        raise ValueError("Arelle relationship endpoint is not present in the installed profile concept index")
    return qualified_name


def _profile_relationship_signature(kind, relationship):
    if not isinstance(relationship, dict):
        raise ValueError("Installed profile relationship metadata contains a non-object entry")

    def decimal_field(name, default):
        value = relationship.get(name, default)
        if isinstance(value, bool) or not isinstance(value, (int, float, Decimal)):
            raise ValueError("Installed profile relationship metadata contains a non-numeric attribute")
        try:
            result = Decimal(str(value))
        except (InvalidOperation, ValueError) as ex:
            raise ValueError("Installed profile relationship metadata contains an invalid numeric attribute") from ex
        if not result.is_finite():
            raise ValueError("Installed profile relationship metadata contains a non-finite numeric attribute")
        return result

    if kind == "presentationLinks":
        return (relationship.get("roleUri"), relationship.get("parentQName"),
                relationship.get("childQName"), decimal_field("order", 0))
    if kind == "calculationLinks":
        return (relationship.get("roleUri"), relationship.get("parentQName"),
                relationship.get("childQName"), decimal_field("weight", 0),
                decimal_field("order", 0), relationship.get("targetRoleUri"))
    return (relationship.get("roleUri"), relationship.get("arcRoleUri"),
            relationship.get("parentQName"), relationship.get("childQName"),
            decimal_field("order", 0), relationship.get("targetRoleUri"),
            relationship.get("usable"), relationship.get("closed"),
            relationship.get("contextElement"))


def _assert_relationship_metadata_matches_profile(extracted, profile):
    for kind in ("presentationLinks", "calculationLinks", "definitionLinks"):
        expected = profile.get(kind, []) if isinstance(profile, dict) else []
        actual = extracted.get(kind, [])
        if not isinstance(expected, list):
            raise ValueError("Installed profile relationship metadata is not an array")
        expected_signatures = {_profile_relationship_signature(kind, row) for row in expected}
        actual_signatures = {_profile_relationship_signature(kind, row) for row in actual}
        if expected_signatures != actual_signatures:
            raise ValueError(
                "Installed profile " + kind + " do not match Arelle's effective taxonomy relationships")


def extract_taxonomy_relationships(models, profile):
    """Read effective presentation, calculation and definition arcs from loaded Arelle models."""
    concept_names = _profile_concept_names(profile)
    found = {"presentationLinks": {}, "calculationLinks": {}, "definitionLinks": {}}
    link_kinds = {"presentationLink": "presentationLinks",
                  "calculationLink": "calculationLinks",
                  "definitionLink": "definitionLinks"}
    relationship_count = 0

    for model in models:
        arcroles_by_type = {}
        for base_set_key in getattr(model, "baseSets", {}) or {}:
            if not isinstance(base_set_key, tuple) or not base_set_key:
                continue
            arcrole = base_set_key[0]
            if not isinstance(arcrole, str) or not arcrole:
                continue
            for candidate in base_set_key[2:]:
                link_type = _relationship_link_type(candidate)
                if link_type is not None:
                    arcroles_by_type.setdefault(link_type, set()).add(arcrole)

        for link_type, kind in link_kinds.items():
            for arcrole in sorted(arcroles_by_type.get(link_type, ())):
                relationship_set = model.relationshipSet(arcrole)
                for relationship in getattr(relationship_set, "modelRelationships", ()) or ():
                    actual_link_type = _relationship_link_type(getattr(relationship, "linkQname", None))
                    if actual_link_type != link_type:
                        continue
                    relationship_arcrole = getattr(relationship, "arcrole", arcrole)
                    if link_type == "presentationLink" and relationship_arcrole != PRESENTATION_ARCROLE:
                        raise ValueError("Arelle found an unsupported arcrole in a presentation link")
                    if link_type == "calculationLink" and relationship_arcrole != CALCULATION_ARCROLE:
                        raise ValueError("Arelle found an unsupported arcrole in a calculation link")
                    role_uri = getattr(relationship, "linkrole", None)
                    parent = _relationship_endpoint_name(
                        getattr(relationship, "fromModelObject", None), concept_names)
                    child = _relationship_endpoint_name(
                        getattr(relationship, "toModelObject", None), concept_names)
                    order = _relationship_decimal(relationship, "orderDecimal", "order", Decimal("1"))
                    if kind == "presentationLinks":
                        row = {"roleUri": role_uri, "parentQName": parent,
                               "childQName": child, "order": order}
                    elif kind == "calculationLinks":
                        row = {"roleUri": role_uri, "parentQName": parent,
                               "childQName": child,
                               "weight": _relationship_decimal(
                                   relationship, "weightDecimal", "weight", Decimal("1")),
                               "order": order,
                               "targetRoleUri": _relationship_attribute(relationship, "targetRole") or None}
                    else:
                        row = {"roleUri": role_uri, "arcRoleUri": relationship_arcrole,
                               "parentQName": parent, "childQName": child, "order": order,
                               "targetRoleUri": _relationship_attribute(relationship, "targetRole") or None,
                               "usable": _relationship_boolean_attribute(relationship, "usable"),
                               "closed": _relationship_boolean_attribute(relationship, "closed"),
                               "contextElement": _relationship_attribute(relationship, "contextElement")}
                    signature = _profile_relationship_signature(kind, row)
                    if signature not in found[kind]:
                        found[kind][signature] = row
                        relationship_count += 1
                        if relationship_count > MAX_PROFILE_RELATIONSHIPS:
                            raise ValueError("Arelle taxonomy relationships exceed the configured relationship limit")

    return {
        kind: sorted(rows.values(), key=lambda row: tuple(
            "" if row.get(field) is None else str(row.get(field))
            for field in ("roleUri", "arcRoleUri", "parentQName", "childQName", "order",
                          "weight", "targetRoleUri", "usable", "closed", "contextElement")))
        for kind, rows in found.items()
    }


def run_arelle(entrypoint, args, temporary_path=None, relationship_metadata_consumer=None):
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
    log_handler = BoundedArelleLogHandler()
    with Session() as session:
        models = []
        try:
            ok = session.run(options, logHandler=log_handler)
            logs = log_handler.messages
            loaded_plugins = loaded_plugin_identifiers()
            models = session.get_models()
            concept_count = sum(len(getattr(model, "qnameConcepts", {})) for model in models)
            if relationship_metadata_consumer is not None:
                relationship_metadata_consumer(
                    extract_taxonomy_relationships(models, getattr(args, "profile_manifest_data", None)))
        finally:
            for model in models:
                session._cntlr.modelManager.close(model)
    gc.collect()
    return ok, logs, concept_count, loaded_plugins


def loaded_plugin_identifiers():
    """Return identifiers from Arelle's registry of modules imported in this session."""
    if PluginManager is None:
        return ()
    try:
        manager = PluginManager.getInstance()
        module_plugin_infos = getattr(manager, "modulePluginInfos", None)
    except Exception:
        return ()
    if not isinstance(module_plugin_infos, dict):
        return ()

    plugin_config = getattr(manager, "pluginConfig", {})
    configured_modules = plugin_config.get("modules", {}) if isinstance(plugin_config, dict) else {}
    if not isinstance(configured_modules, dict):
        configured_modules = {}
    identifiers = set()
    for name, plugin_info in module_plugin_infos.items():
        if isinstance(name, str) and name:
            identifiers.add(name)
        metadata = [plugin_info, configured_modules.get(name)]
        for item in metadata:
            if isinstance(item, dict):
                for key in ("name", "moduleURL"):
                    identifier = item.get(key)
                    if isinstance(identifier, str) and identifier:
                        identifiers.add(identifier)
                entry_point = item.get("entryPoint")
                if isinstance(entry_point, dict):
                    for key in ("name", "module"):
                        identifier = entry_point.get(key)
                        if isinstance(identifier, str) and identifier:
                            identifiers.add(identifier)
    return tuple(sorted(identifiers))


def required_plugin_completions(required_plugins, loaded_identifiers, failures=(), missing=()):
    """Adjudicate required plugin IDs using only the current run's loaded module registry."""
    loaded = set(loaded_identifiers)
    failed = set(failures)
    not_configured = set(missing)
    completions = []
    for plugin in required_plugins:
        if plugin in not_configured:
            status = "unavailable"
            detail = "Required plugin was not configured for this worker."
        elif plugin in failed:
            status = "fail"
            detail = "Arelle reported that the required plugin could not be loaded."
        elif plugin in loaded:
            status = "pass"
            detail = "Arelle's runtime registry confirms the required plugin module loaded."
        else:
            status = "unavailable"
            detail = "Arelle did not report the required plugin in its loaded module registry."
        completions.append({"identifier": plugin, "status": status, "detail": detail})
    return completions


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
    suppressed += getattr(logs, "suppressed_count", 0)
    suppressed_blocking = suppressed_blocking or getattr(logs, "suppressed_blocking", False)
    if suppressed:
        result.append(diagnostic(
            op_id,
            "XBR009",
            "error" if suppressed_blocking else "warning",
            f"{suppressed} additional Arelle log records were omitted by bounded capture or response limits.",
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
                relationship_metadata = []
                ok, logs, concept_count, loaded_plugins = run_arelle_with_verified_taxonomy(
                    relative, dependency_paths, args.taxonomy_root, profile["packageFiles"], args,
                    relationship_metadata_consumer=relationship_metadata.append)
                relative_entry_point = relative
            else:
                taxonomy_path = within_root(payload["taxonomyPath"], args.taxonomy_root)
                ok, logs, concept_count, loaded_plugins = run_arelle(taxonomy_path, args)
                relationship_metadata = []
                relative_entry_point = os.path.relpath(
                    taxonomy_path, Path(args.taxonomy_root).resolve(strict=True)).replace(os.sep, "/")
            required_plugins = profile.get("requiredPlugins", []) if profile else []
            plugin_failures = required_plugin_load_failures(logs, required_plugins)
            plugin_completions = required_plugin_completions(
                required_plugins, loaded_plugins, failures=plugin_failures)
            incomplete_plugins = [item for item in plugin_completions if item["status"] != "pass"]
            if incomplete_plugins:
                diags = [diagnostic(
                    op_id, "XBR010", "error",
                    ("Required Arelle plugin failed to load: " if item["status"] == "fail"
                     else "Arelle did not confirm that required plugin loaded: ") + item["identifier"],
                    "plugin:" + item["identifier"])
                    for item in incomplete_plugins]
                return response(op_id, "incompleteValidation", {
                    "completed": False,
                    "conceptCount": concept_count,
                    "requiredPlugins": required_plugins,
                    "loadedPlugins": [item["identifier"] for item in plugin_completions
                                      if item["status"] == "pass"],
                }, diags, "A required Arelle plugin did not complete loading")
            diags = log_diagnostics(logs, op_id)
            if not ok or any(d["isBlocking"] for d in diags):
                return response(op_id, "domainFailure", {"conceptCount": concept_count}, diags,
                                "Arelle could not load the filing taxonomy")
            if profile is not None and relationship_metadata:
                _assert_relationship_metadata_matches_profile(relationship_metadata[0], profile)
            return response(op_id, "success", {
                "engine": "Arelle",
                "engineVersion": ENGINE_VERSION,
                "capabilities": CAPABILITIES,
                "profileSchemaVersion": profile.get("schemaVersion") if profile else None,
                "profileId": profile.get("profileId") if profile else None,
                "profileVersion": profile.get("version") if profile else None,
                "profileDigest": profile.get("profileDigest") if profile else None,
                "taxonomyEntryPoint": relative_entry_point,
                "requiredPlugins": required_plugins,
                "loadedPlugins": [item["identifier"] for item in plugin_completions],
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
            plugin_completions = required_plugin_completions(
                required_plugins, (), missing=missing_plugins)
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
                run_args = args
                mapping_plugin = None
                package_catalog_rewrites = ()
                if profile is not None:
                    _package_archives, package_catalog_rewrites = validate_taxonomy_package_archives(
                        args.taxonomy_root, profile.get("packageFiles", {}),
                        profile.get("entryPoint"), profile.get("offlineReferences", {}))
                if staged_xml_path is not None:
                    run_args = rewrite_staged_xml_taxonomy(staged_xml_path, path, args.taxonomy_root,
                        profile.get("offlineReferences") if profile else None,
                        profile.get("packageFiles") if profile else None,
                        payload.get("sha256"), worker_args=args,
                        package_catalog_rewrites=package_catalog_rewrites)
                elif large_report_path is not None:
                    mapping_plugin = rewrite_large_report_taxonomy(large_report_path, path, args.taxonomy_root,
                        profile.get("offlineReferences") if profile else None,
                        profile.get("packageFiles") if profile else None,
                        payload.get("sha256"), package_catalog_rewrites)
                else:
                    report = json.loads(report_text, object_pairs_hook=reject_duplicate_json_keys)
                    if profile is not None and isinstance(profile.get("packageFiles"), dict):
                        localized, mapping_plugin = _snapshot_report_taxonomy(
                            report.get("documentInfo", {}).get("taxonomy"),
                            args.taxonomy_root, workdir,
                            profile.get("offlineReferences", {}),
                            profile["packageFiles"], package_catalog_rewrites)
                        report["documentInfo"]["taxonomy"] = localized
                    else:
                        report = safe_taxonomy_urls(report, args.taxonomy_root,
                            profile.get("offlineReferences") if profile else None,
                            profile.get("packageFiles") if profile else None,
                            package_catalog_rewrites)
                    path.write_text(json.dumps(report, ensure_ascii=False), encoding="utf-8")
                if mapping_plugin is not None:
                    import copy
                    run_args = copy.copy(args)
                    run_args.plugin = list(getattr(args, "plugin", []) or [])
                    run_args.plugin.append(str(mapping_plugin))
                ok, logs, concept_count, loaded_plugins = run_arelle(path, run_args, workdir)
                plugin_failures = required_plugin_load_failures(logs, required_plugins)
                plugin_completions = required_plugin_completions(
                    required_plugins, loaded_plugins, failures=plugin_failures)
                completion["requiredPlugins"] = plugin_completions
                incomplete_plugins = [item for item in plugin_completions if item["status"] != "pass"]
                if incomplete_plugins:
                    diags = [diagnostic(
                        op_id, "XBR010", "error",
                        ("Required Arelle plugin failed to load: " if item["status"] == "fail"
                         else "Arelle did not confirm that required plugin loaded: ") + item["identifier"],
                        "plugin:" + item["identifier"])
                        for item in incomplete_plugins]
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
