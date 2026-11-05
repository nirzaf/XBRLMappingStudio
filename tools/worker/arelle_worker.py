#!/usr/bin/env python3
"""Offline Arelle worker using the supported Session API."""

import argparse
import hashlib
import json
import os
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import unquote, urlsplit
from urllib.request import url2pathname

PROTOCOL_VERSION = 1
PROFILE_SCHEMA_VERSION = 2
MAX_MESSAGE_BYTES = 4 * 1024 * 1024
ENGINE_VERSION = "arelle-worker/1.0"
CAPABILITIES = ["handshake", "load-taxonomy", "validate-report"]
MAX_PROFILE_MANIFEST_BYTES = 16 * 1024 * 1024
MAX_PROFILE_FILE_BYTES = 512 * 1024 * 1024
MAX_PROFILE_TOTAL_BYTES = 2 * 1024 * 1024 * 1024
MAX_PROFILE_FILES = 16384

try:
    from arelle.api.Session import Session
    from arelle.RuntimeOptions import RuntimeOptions

    ARELLE_AVAILABLE = True
except Exception:  # The host must fail closed if this optional runtime is absent.
    RuntimeOptions = None
    Session = None
    ARELLE_AVAILABLE = False


def emit(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n")
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
    return isinstance(req, dict) and all(isinstance(req.get(key), str) for key in required)


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
            raise ValueError("Taxonomy package paths cannot contain symbolic links")
    target = within_root(current, root)
    if not target.is_file():
        raise ValueError("Taxonomy package path is not a regular file")
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
    if manifest.get("schemaVersion") != PROFILE_SCHEMA_VERSION:
        raise ValueError("Profile manifest uses an unsupported schema version")
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
        if (parsed_reference is None or parsed_reference.scheme not in ("http", "https")
                or not parsed_reference.hostname or parsed_reference.username is not None
                or parsed_reference.password is not None or not isinstance(relative_path, str)
                or relative_path not in verified_files):
            raise ValueError("Offline taxonomy references must map HTTP(S) URIs to pinned package files")
    return manifest


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
        message = str(message or "Arelle reported an unspecified validation finding.")[:4000]
        if temporary_path:
            message = message.replace(str(temporary_path), "<job>")
        result.append(diagnostic(op_id, "XBR009", raw_level, message, code))
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
    op_id = req.get("operationId", "-")
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
                taxonomy_path = safe_package_path(relative, args.taxonomy_root)
            else:
                taxonomy_path = within_root(payload["taxonomyPath"], args.taxonomy_root)
            ok, logs, concept_count = run_arelle(taxonomy_path, args)
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
            relative_entry_point = os.path.relpath(taxonomy_path, Path(args.taxonomy_root).resolve(strict=True)).replace(os.sep, "/")
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
        if not isinstance(report_text, str) or not report_text:
            return response(op_id, "domainFailure", error="Missing xBRL-JSON report text")
        try:
            if getattr(args, "profile_error", None):
                raise ValueError(args.profile_error)
            profile = getattr(args, "profile_manifest_data", None)
            if profile is not None and req["profileDigest"] != profile.get("profileDigest"):
                raise ValueError("Worker request profile digest does not match the installed profile")
            report = json.loads(report_text)
            report = safe_taxonomy_urls(report, args.taxonomy_root,
                                        profile.get("offlineReferences") if profile else None,
                                        profile.get("packageFiles") if profile else None)
            with tempfile.TemporaryDirectory(prefix="xms-arelle-") as workdir:
                os.chmod(workdir, 0o700)
                path = Path(workdir) / "report.json"
                path.write_text(json.dumps(report, ensure_ascii=False), encoding="utf-8")
                ok, logs, concept_count = run_arelle(path, args, workdir)
                plugin_failures = required_plugin_load_failures(
                    logs, profile.get("requiredPlugins", []) if profile else [])
                if plugin_failures:
                    diags = [diagnostic(op_id, "XBR010", "error",
                                        "Required Arelle plugin could not be loaded: " + plugin,
                                        "plugin:" + plugin)
                             for plugin in plugin_failures]
                    return response(op_id, "incompleteValidation", {
                        "engine": "Arelle", "engineVersion": ENGINE_VERSION,
                        "completed": False, "conceptCount": concept_count,
                    }, diags, "A required Arelle plugin is unavailable")
                diags = log_diagnostics(logs, op_id, workdir)
                result = {"engine": "Arelle", "engineVersion": ENGINE_VERSION,
                          "completed": True, "conceptCount": concept_count}
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
