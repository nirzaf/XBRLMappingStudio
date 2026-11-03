#!/usr/bin/env python3
"""Arelle worker for XBRL Mapping Studio (XBR02).

Speaks UTF-8 framed JSON over stdin/stdout. Reserved stdout carries only protocol
messages; all Arelle log output is redirected to stderr with sanitization.

This is a scaffolding implementation. For the G1 synthetic profile the C#
LocalSyntheticWorker handles validation without Python; this file exercises the
same protocol so the architecture is tested end-to-end with real Arelle.

Run with: python arelle_worker.py
"""

import json
import sys
import traceback

PROTOCOL_VERSION = 1
ENGINE_VERSION = "arelle-worker/0.1"
CAPABILITIES = ["handshake", "load-taxonomy", "validate-report"]

# Optional Arelle import: falls back to no-op when the package is absent so the
# handshake still succeeds in CI without Arelle installed.
try:
    from arelle import Session  # type: ignore
    ARELLE_AVAILABLE = True
except Exception:
    Session = None
    ARELLE_AVAILABLE = False


def emit(obj):
    """Write one framed JSON message to stdout (UTF-8, newline terminated)."""
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def log_err(msg):
    sys.stderr.write(str(msg) + "\n")
    sys.stderr.flush()


def make_response(op_id, status, result=None, diagnostics=None, error=None):
    return {
        "operationId": op_id,
        "status": status,
        "result": result,
        "diagnostics": diagnostics or [],
        "error": error,
    }


def handle_handshake(req):
    return make_response(
        req["operationId"],
        "success",
        {
            "protocolVersion": PROTOCOL_VERSION,
            "engineVersion": ENGINE_VERSION,
            "capabilities": CAPABILITIES,
            "arelleAvailable": ARELLE_AVAILABLE,
        },
    )


def handle_load_taxonomy(req):
    payload = req.get("payload") or {}
    path = payload.get("taxonomyPath")
    if not path:
        return make_response(req["operationId"], "domainFailure", error="Missing taxonomyPath")

    if not ARELLE_AVAILABLE:
        # Synthetic profile does not need Arelle. Report success with an in-process stub.
        return make_response(
            req["operationId"], "success", {"engine": "stub", "conceptCount": 0}
        )

    try:
        sess = Session.Session()
        sess.load(path)
        return make_response(
            req["operationId"],
            "success",
            {"engine": "arelle", "conceptCount": len(list(sess.model.xbrlObjects))},
        )
    except Exception as ex:  # pragma: no cover - depends on runtime environment
        log_err(traceback.format_exc())
        return make_response(req["operationId"], "crash", error=str(ex))


def handle_validate_report(req):
    payload = req.get("payload") or {}
    report = payload.get("xBrlJson")
    if not report:
        return make_response(req["operationId"], "domainFailure", error="Missing report payload")

    if not ARELLE_AVAILABLE:
        return make_response(
            req["operationId"],
            "incompleteValidation",
            diagnostics=[{
                "code": "XBR010",
                "subsystem": "Worker",
                "severity": "warning",
                "stage": "Validation",
                "isBlocking": True,
                "message": "Arelle not available in this worker installation; validation is incomplete.",
            }],
        )

    try:
        sess = Session.Session()
        sess.validate(report)
        # Map Arelle log entries to C# Diagnostic shape (see DiagnosticContract.cs).
        diags = []
        for entry in sess.logResponse or []:
            diags.append({
                "code": getattr(entry, "code", "XBR000"),
                "subsystem": "XBRL",
                "severity": getattr(entry, "level", "info"),
                "stage": "Validation",
                "isBlocking": getattr(entry, "level", "info") == "error",
                "message": getattr(entry, "message", ""),
                "ruleIdentifier": getattr(entry, "ruleId", None),
            })
        return make_response(req["operationId"], "success", diagnostics=diags)
    except Exception as ex:  # pragma: no cover
        log_err(traceback.format_exc())
        return make_response(req["operationId"], "crash", error=str(ex))


HANDLERS = {
    "handshake": handle_handshake,
    "load-taxonomy": handle_load_taxonomy,
    "validate-report": handle_validate_report,
}


def main():
    """Protocol loop. Reads one JSON request per line, writes one JSON response."""
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError as ex:
            emit(make_response("-", "domainFailure", error=f"Malformed request: {ex}"))
            continue

        op = req.get("operation")
        handler = HANDLERS.get(op)
        if handler is None:
            emit(make_response(req.get("operationId", "-"), "domainFailure",
                               error=f"Unknown operation: {op}"))
            continue

        if op == "shutdown":
            break

        try:
            resp = handler(req)
        except Exception as ex:  # pragma: no cover
            log_err(traceback.format_exc())
            resp = make_response(req.get("operationId", "-"), "crash", error=str(ex))
        emit(resp)

        if req.get("operation") == "shutdown":
            break


if __name__ == "__main__":
    main()
