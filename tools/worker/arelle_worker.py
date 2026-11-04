#!/usr/bin/env python3
"""Offline Arelle worker using the supported Session API."""

import argparse
import json
import os
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import unquote, urlsplit

PROTOCOL_VERSION = 1
MAX_MESSAGE_BYTES = 4 * 1024 * 1024
ENGINE_VERSION = "arelle-worker/1.0"
CAPABILITIES = ["handshake", "load-taxonomy", "validate-report"]

try:
    from arelle.RuntimeOptions import RuntimeOptions
    from arelle.api.Session import Session

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
    target = Path(path).resolve(strict=True)
    if os.path.commonpath((str(root_path), str(target))) != str(root_path):
        raise ValueError("Path escapes the configured taxonomy root")
    return target


def safe_taxonomy_urls(report, taxonomy_root):
    info = report.get("documentInfo")
    if not isinstance(info, dict) or not isinstance(info.get("taxonomy"), list):
        raise ValueError("Report has no documentInfo.taxonomy array")
    fixed = []
    for reference in info["taxonomy"]:
        if not isinstance(reference, str):
            raise ValueError("Taxonomy references must be strings")
        parsed = urlsplit(reference)
        if parsed.scheme in ("http", "https"):
            fixed.append(reference)
        elif parsed.scheme == "file":
            if taxonomy_root is None:
                raise ValueError("A taxonomy root is required for file taxonomy references")
            local = within_root(unquote(parsed.path), taxonomy_root)
            fixed.append(local.as_uri())
        elif parsed.scheme == "":
            if taxonomy_root is None:
                raise ValueError("A taxonomy root is required for relative taxonomy references")
            local = within_root(Path(taxonomy_root) / unquote(parsed.path), taxonomy_root)
            fixed.append(local.as_uri())
        else:
            raise ValueError("Only HTTP(S) package references and root-scoped local taxonomy files are supported")
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
        plugins=",".join(args.plugin) if args.plugin else None,
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
        return response(op_id, "success", {
            "protocolVersion": PROTOCOL_VERSION,
            "engineVersion": ENGINE_VERSION,
            "capabilities": CAPABILITIES,
            "arelleAvailable": ARELLE_AVAILABLE,
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
            taxonomy_path = within_root(payload["taxonomyPath"], args.taxonomy_root)
            ok, logs, concept_count = run_arelle(taxonomy_path, args)
            diags = log_diagnostics(logs, op_id)
            if not ok or any(d["isBlocking"] for d in diags):
                return response(op_id, "domainFailure", {"conceptCount": concept_count}, diags,
                                "Arelle could not load the filing taxonomy")
            return response(op_id, "success", {"conceptCount": concept_count}, diags)
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
            report = json.loads(report_text)
            report = safe_taxonomy_urls(report, args.taxonomy_root)
            with tempfile.TemporaryDirectory(prefix="xms-arelle-") as workdir:
                os.chmod(workdir, 0o700)
                path = Path(workdir) / "report.json"
                path.write_text(json.dumps(report, ensure_ascii=False), encoding="utf-8")
                ok, logs, concept_count = run_arelle(path, args, workdir)
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
    parser.add_argument("--package", action="append", default=[])
    parser.add_argument("--plugin", action="append", default=[])
    parser.add_argument("--disclosure-system")
    args, unknown = parser.parse_known_args()
    if unknown:
        emit(response("-", "domainFailure", error="Unsupported worker startup arguments"))
        return
    if args.taxonomy_root:
        args.taxonomy_root = str(Path(args.taxonomy_root).resolve(strict=True))
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
