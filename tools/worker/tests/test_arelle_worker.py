import hashlib
import http.server
import io
import json
import logging
import runpy
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
import zipfile
from decimal import Decimal
from pathlib import Path
from types import SimpleNamespace
from unittest import mock
from xml.etree import ElementTree

WORKER_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKER_DIR))
import arelle_worker  # noqa: E402


FIXTURES = Path(__file__).resolve().parents[3] / "tests" / "XBRLMappingStudio.XBRL.Tests" / "fixtures"
TAXONOMY_ROOT = WORKER_DIR / "taxonomy"


def write_taxonomy_files(root, files):
    package_files = {}
    for relative_path, content in files.items():
        path = root / relative_path
        path.parent.mkdir(parents=True, exist_ok=True)
        raw = content if isinstance(content, bytes) else content.encode("utf-8")
        path.write_bytes(raw)
        package_files[relative_path] = "sha256:" + hashlib.sha256(raw).hexdigest()
    return package_files


def write_taxonomy_package(root, archive_name="taxonomy.zip",
                           base_uri="https://taxonomy.example/test/",
                           rewrite_prefix="../schemas/"):
    files = {
        "SyntheticPackage/META-INF/taxonomyPackage.xml": (
            '<tp:taxonomyPackage xmlns:tp="http://xbrl.org/2016/taxonomy-package" '
            'xmlns:xml="http://www.w3.org/XML/1998/namespace">'
            '<tp:identifier>urn:xms:test:taxonomy-package</tp:identifier>'
            '<tp:entryPoints><tp:entryPoint><tp:name xml:lang="en">Synthetic</tp:name>'
            f'<tp:entryPointDocument href="{base_uri}entry.xsd"/>'
            '</tp:entryPoint></tp:entryPoints></tp:taxonomyPackage>'
        ),
        "SyntheticPackage/META-INF/catalog.xml": (
            '<catalog xmlns="urn:oasis:names:tc:entity:xmlns:xml:catalog">'
            f'<rewriteURI uriStartString="{base_uri}" '
            f'rewritePrefix="{rewrite_prefix}"/></catalog>'
        ),
        "SyntheticPackage/schemas/entry.xsd": (
            '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
            '<xs:import namespace="urn:xms:dependency" '
            f'schemaLocation="{base_uri}dependency.xsd"/>'
            '<xs:element name="Entry" type="dep:DependencyType" '
            'xmlns:dep="urn:xms:dependency"/>'
            '</xs:schema>'
        ),
        "SyntheticPackage/schemas/dependency.xsd": (
            '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" '
            'targetNamespace="urn:xms:dependency"><xs:simpleType name="DependencyType">'
            '<xs:restriction base="xs:string"/></xs:simpleType>'
            '<xs:element name="Dependency" type="xs:string"/></xs:schema>'
        ),
    }
    archive_path = root / archive_name
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for relative_path, content in files.items():
            archive.writestr(relative_path, content.encode("utf-8"))
    package_files = write_taxonomy_files(root, files)
    archive_bytes = archive_path.read_bytes()
    package_files[archive_name] = "sha256:" + hashlib.sha256(archive_bytes).hexdigest()
    return package_files


def report(concept="synthetic:Assets"):
    return {
        "documentInfo": {
            "documentType": "https://xbrl.org/2021/xbrl-json",
            "namespaces": {
                "synthetic": "urn:synthetic:finance:1.0",
                "lei": "http://standards.iso.org/iso/17442",
                "iso4217": "http://www.xbrl.org/2003/iso4217",
            },
            "taxonomy": ["arelle-test-taxonomy.xsd"],
        },
        "facts": {
            "f1": {
                "value": "123.45",
                "decimals": 2,
                "dimensions": {
                    "concept": concept,
                    "entity": "lei:00EHHQ2ZHDCFXJCPCL49",
                    "period": "2025-12-31T00:00:00",
                    "unit": "iso4217:USD",
                },
            }
        },
    }


class ArelleWorkerTests(unittest.TestCase):
    def setUp(self):
        self.args = SimpleNamespace(
            taxonomy_root=str(TAXONOMY_ROOT), package=[], plugin=[], disclosure_system=None,
            profile_manifest_data=None, profile_error=None,
        )

    def request(self, value):
        profile = self.args.profile_manifest_data or {}
        profile_digest = profile.get("profileDigest", "test")
        return {
            "operation": "validate-report",
            "operationId": "test-op",
            "jobId": "test-job",
            "profileDigest": profile_digest,
            "deadlineUtc": "2999-01-01T00:00:00Z",
            "payload": json.dumps(value),
            "validationRequirements": {
                "profileId": profile.get("profileId", "test-profile"),
                "profileDigest": profile_digest,
                "requiredRules": profile.get("requiredChecks", []),
                "requiredPlugins": profile.get("requiredPlugins", []),
            },
        }

    def handshake_request(self):
        return {
            "operation": "handshake",
            "operationId": "test-handshake",
            "jobId": "test-job",
            "profileDigest": "-",
            "deadlineUtc": "2999-01-01T00:00:00Z",
            "payload": {"minimumProtocolVersion": arelle_worker.PROTOCOL_VERSION,
                        "maximumProtocolVersion": arelle_worker.PROTOCOL_VERSION},
        }

    def test_handshake_negotiates_supported_protocol(self):
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True):
            result = arelle_worker.handle(
                {
                    "operation": "handshake",
                    "operationId": "hello",
                    "jobId": "test",
                    "profileDigest": "-",
                    "deadlineUtc": "2999-01-01T00:00:00Z",
                    "payload": {"minimumProtocolVersion": arelle_worker.PROTOCOL_VERSION,
                                "maximumProtocolVersion": arelle_worker.PROTOCOL_VERSION},
                },
                self.args,
            )
        self.assertEqual("success", result["status"])
        self.assertEqual(2, result["result"]["protocolVersion"])
        self.assertIn("validate-report", result["result"]["capabilities"])

    def test_handshake_rejects_v1_without_validation_completion_contract(self):
        request = self.handshake_request()
        request["payload"] = {"minimumProtocolVersion": 1, "maximumProtocolVersion": 1}

        result = arelle_worker.handle(request, self.args)

        self.assertEqual("domainFailure", result["status"])
        self.assertIn("No mutually supported protocol version", result["error"])

    def test_request_rejects_oversized_operation_id_without_echoing_it(self):
        request = self.handshake_request()
        request["operationId"] = "x" * 129

        result = arelle_worker.handle(request, self.args)

        self.assertEqual("domainFailure", result["status"])
        self.assertEqual("-", result["operationId"])

    def test_log_diagnostics_caps_details_and_preserves_blocking_summary(self):
        count = arelle_worker.MAX_RESPONSE_DIAGNOSTICS + 7
        logs = [{"levelname": "error", "messageCode": "fixture:error", "msg": "finding"}
                for _ in range(count)]

        diagnostics = arelle_worker.log_diagnostics(logs, "bounded-op")

        self.assertEqual(arelle_worker.MAX_RESPONSE_DIAGNOSTICS + 1, len(diagnostics))
        self.assertTrue(all(item["isBlocking"] for item in diagnostics))
        self.assertEqual("arelle:diagnostic-limit", diagnostics[-1]["ruleIdentifier"])
        self.assertIn("bounded capture or response limits", diagnostics[-1]["message"])

        long_message = "x" * (arelle_worker.MAX_DIAGNOSTIC_MESSAGE_CHARS + 10)
        single = arelle_worker.log_diagnostics(
            [{"levelname": "warning", "messageCode": "fixture:warning", "msg": long_message}], "bounded-op")
        self.assertLessEqual(len(single[0]["message"]), arelle_worker.MAX_DIAGNOSTIC_MESSAGE_CHARS)
        self.assertTrue(single[0]["message"].endswith("…"))
        self.assertFalse(single[0]["isBlocking"])

    def test_log_diagnostics_preserves_a_bounded_exact_fact_locator(self):
        locator = "f" + "a" * 64

        diagnostics = arelle_worker.log_diagnostics([{
            "levelname": "error",
            "messageCode": "fixture:error",
            "msg": "fact finding",
            "factLocator": locator,
        }], "locator-op")
        oversized = arelle_worker.log_diagnostics([{
            "levelname": "error",
            "messageCode": "fixture:error",
            "msg": "fact finding",
            "factLocator": "f" * (arelle_worker.MAX_DIAGNOSTIC_FACT_LOCATOR_CHARS + 1),
        }], "locator-op")

        self.assertEqual(locator, diagnostics[0]["factLocator"])
        self.assertNotIn("factLocator", oversized[0])

    def test_arelle_fact_locator_enrichment_requires_one_exact_referenced_item_fact(self):
        first_locator = "f" + "1" * 64
        second_locator = "f" + "2" * 64
        first_fact = SimpleNamespace(isItem=True, id=first_locator)
        second_fact = SimpleNamespace(isItem=True, id=second_locator)
        concept = SimpleNamespace(isItem=False, id=first_locator)
        objects = {
            "_fact_1": first_fact,
            "_fact_2": second_fact,
            "_concept_3": concept,
        }
        model = SimpleNamespace(modelObject=lambda object_id: objects.get(object_id))
        logs = [
            {"refs": [{"objectId": "_fact_1"}]},
            {"refs": [{"objectId": "_fact_1"}, {"objectId": "_fact_2"}]},
            {"refs": [{"objectId": "_concept_3"}]},
            {"refs": [{"objectId": "_fact_1"}, "<additional-values-omitted>"]},
            {"refs": [{"objectId": "_unknown_4"}]},
            {"refs": [{"objectId": "_fact_1"}, {"objectId": "_unknown_4"}]},
        ]

        arelle_worker.attach_arelle_fact_locators(logs, [model])

        self.assertEqual(first_locator, logs[0]["factLocator"])
        self.assertTrue(all("factLocator" not in item for item in logs[1:]))

    def test_arelle_log_handler_bounds_raw_capture_and_preserves_omitted_severity(self):
        handler = arelle_worker.BoundedArelleLogHandler(max_records=2, max_bytes=16_384)
        for level, message_code in (
            (logging.INFO, "fixture:info"),
            (logging.WARNING, "fixture:warning"),
            (logging.ERROR, "fixture:error"),
        ):
            record = logging.LogRecord("arelle", level, "worker.py", 10, "finding", (), None)
            record.messageCode = message_code
            record.refs = []
            handler.emit(record)

        self.assertEqual(2, len(handler.messages))
        self.assertFalse(hasattr(handler, "logRecordBuffer"))
        self.assertEqual(1, handler.messages.suppressed_count)
        self.assertTrue(handler.messages.suppressed_blocking)
        diagnostics = arelle_worker.log_diagnostics(handler.messages, "bounded-op")
        self.assertTrue(diagnostics[-1]["isBlocking"])
        self.assertIn("1 additional Arelle log records", diagnostics[-1]["message"])

        warning_handler = arelle_worker.BoundedArelleLogHandler(max_records=1, max_bytes=16_384)
        for _ in range(2):
            record = logging.LogRecord("arelle", logging.WARNING, "worker.py", 10, "warning", (), None)
            warning_handler.emit(record)
        warning_diagnostics = arelle_worker.log_diagnostics(warning_handler.messages, "bounded-op")
        self.assertFalse(warning_diagnostics[-1]["isBlocking"])

    def test_arelle_custom_log_levels_keep_their_diagnostic_severity(self):
        expected = {
            "INFO-RESULT": ("information", False),
            "INFO-SEMANTIC": ("information", False),
            "WARNING-SEMANTIC": ("warning", False),
            "ASSERTION-SATISFIED": ("information", False),
            "INCONSISTENCY": ("warning", False),
            "ERROR-SEMANTIC": ("error", True),
            "ASSERTION-NOT-SATISFIED": ("error", True),
        }
        for level, (severity, blocking) in expected.items():
            with self.subTest(level=level):
                finding = arelle_worker.diagnostic("severity-op", "XBR009", level, "fixture")
                self.assertEqual(severity, finding["severity"])
                self.assertEqual(blocking, finding["isBlocking"])

    def test_arelle_log_handler_caps_message_values_and_total_capture_bytes(self):
        record = logging.LogRecord(
            "arelle", logging.WARNING, "worker.py", 10,
            "value %(payload)s", ({"payload": "x" * 1_000_000},), None)
        capped = arelle_worker.BoundedArelleLogHandler(max_records=4, max_bytes=512)
        capped.emit(record)
        self.assertLessEqual(capped.messages.bytes_retained, capped.max_bytes)
        self.assertEqual(1, capped.messages.suppressed_count)
        self.assertFalse(capped.messages.suppressed_blocking)

        bounded = arelle_worker.BoundedArelleLogHandler(max_records=4, max_bytes=16_384)
        bounded.emit(record)
        self.assertLessEqual(len(bounded.messages[0]["msg"]),
                             arelle_worker.MAX_DIAGNOSTIC_MESSAGE_CHARS)

        positional = logging.LogRecord(
            "arelle", logging.ERROR, "worker.py", 10, "finding %s", ("specific issue",), None)
        formatted = arelle_worker.BoundedArelleLogHandler()
        formatted.emit(positional)
        self.assertEqual("finding specific issue", formatted.messages[0]["msg"])

        large_integer = 1 << 100_000
        structured = logging.LogRecord(
            "arelle", logging.ERROR, "worker.py", 10, "finding", (), None)
        structured.args = {"args": {"value": large_integer}}
        bounded_integer = arelle_worker.BoundedArelleLogHandler()
        bounded_integer.emit(structured)
        self.assertEqual("<large-integer:100001-bits>",
                         bounded_integer.messages[0]["args"]["value"])

        decimal_record = logging.LogRecord(
            "arelle", logging.ERROR, "worker.py", 10,
            "value=%s", (Decimal("9" * 5_000),), None)
        bounded_decimal = arelle_worker.BoundedArelleLogHandler()
        bounded_decimal.emit(decimal_record)
        self.assertLessEqual(len(bounded_decimal.messages[0]["msg"]),
                             arelle_worker.MAX_DIAGNOSTIC_MESSAGE_CHARS)

        non_finite = logging.LogRecord(
            "arelle", logging.ERROR, "worker.py", 10, "finding", (), None)
        non_finite.args = {"args": {"value": float("nan")}}
        bounded_float = arelle_worker.BoundedArelleLogHandler()
        bounded_float.emit(non_finite)
        self.assertEqual("nan", bounded_float.messages[0]["args"]["value"])

    def test_emit_replaces_oversized_response_with_bounded_failure(self):
        output = io.StringIO()
        payload = arelle_worker.response("bounded-op", "success", {"value": "x" * 2_000})

        with mock.patch.object(arelle_worker, "MAX_MESSAGE_BYTES", 512), \
                mock.patch.object(arelle_worker.sys, "stdout", output):
            arelle_worker.emit(payload)

        encoded = output.getvalue()
        self.assertLessEqual(len(encoded.encode("utf-8")), 512)
        result = json.loads(encoded)
        self.assertEqual("bounded-op", result["operationId"])
        self.assertEqual("domainFailure", result["status"])
        self.assertIn("exceeds the 4 MiB limit", result["error"])

    def test_handshake_reports_exact_profile_entrypoint_and_plugins(self):
        self.args.profile_manifest_data = {
            "schemaVersion": 2,
            "profileId": "fixture-profile",
            "version": "2.0.0",
            "profileDigest": "sha256:fixture",
            "entryPoint": "taxonomy/entry.xsd",
            "requiredPlugins": ["validate/EFM"],
            "requiredChecks": ["rule:one"],
        }
        self.args.plugin = ["validate/EFM"]
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True):
            result = arelle_worker.handle(self.handshake_request(), self.args)

        self.assertEqual("success", result["status"])
        self.assertEqual("fixture-profile", result["result"]["profileId"])
        self.assertEqual("2.0.0", result["result"]["profileVersion"])
        self.assertEqual("taxonomy/entry.xsd", result["result"]["taxonomyEntryPoint"])
        self.assertEqual(["validate/EFM"], result["result"]["requiredPlugins"])
        self.assertEqual(["rule:one"], result["result"]["requiredChecks"])

    def test_validate_report_returns_profile_bound_completion_report(self):
        request = self.request(report())
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "run_arelle", return_value=(True, [], 1, ())):
            result = arelle_worker.handle(request, self.args)

        self.assertEqual("success", result["status"])
        completion = result["result"]["validationCompletion"]
        self.assertEqual("test-profile", completion["profileId"])
        self.assertEqual("test", completion["profileDigest"])
        self.assertEqual([], completion["requiredRules"])
        self.assertEqual([], completion["requiredPlugins"])

    def test_validate_report_does_not_infer_required_rule_pass_from_engine_success(self):
        request = self.request(report())
        request["validationRequirements"]["requiredRules"] = ["rule:mandatory"]
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "run_arelle", return_value=(True, [], 1, ())) as run_arelle:
            result = arelle_worker.handle(request, self.args)

        self.assertEqual("success", result["status"])
        completion = result["result"]["validationCompletion"]
        self.assertEqual([{"identifier": "rule:mandatory", "status": "unavailable",
                           "detail": "Arelle does not expose a machine-readable completion signal for this required rule."}],
                         completion["requiredRules"])
        self.assertFalse(any(item["isBlocking"] for item in result["diagnostics"]))
        run_arelle.assert_called_once()

    def test_validate_report_absent_required_plugin_is_incomplete_before_arelle(self):
        request = self.request(report())
        request["validationRequirements"]["requiredPlugins"] = ["validate/EFM"]
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "run_arelle") as run_arelle:
            result = arelle_worker.handle(request, self.args)

        self.assertEqual("incompleteValidation", result["status"])
        completion = result["result"]["validationCompletion"]
        self.assertEqual("unavailable", completion["requiredPlugins"][0]["status"])
        self.assertTrue(any(item["code"] == "XBR010" and item["isBlocking"]
                            for item in result["diagnostics"]))
        run_arelle.assert_not_called()

    def test_validate_report_does_not_infer_required_plugin_loaded_from_configuration(self):
        request = self.request(report())
        request["validationRequirements"]["requiredPlugins"] = ["validate/EFM"]
        self.args.plugin = ["validate/EFM"]
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "run_arelle", return_value=(True, [], 1, ())):
            result = arelle_worker.handle(request, self.args)

        self.assertEqual("incompleteValidation", result["status"])
        self.assertFalse(result["result"]["completed"])
        completion = result["result"]["validationCompletion"]
        self.assertEqual("unavailable", completion["requiredPlugins"][0]["status"])
        self.assertIn("loaded module registry", completion["requiredPlugins"][0]["detail"])
        self.assertTrue(any(item["code"] == "XBR010" and item["isBlocking"]
                            for item in result["diagnostics"]))

    def test_validate_report_marks_plugin_pass_only_when_runtime_reports_it_loaded(self):
        request = self.request(report())
        request["validationRequirements"]["requiredPlugins"] = ["validate/EFM"]
        self.args.plugin = ["validate/EFM"]
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "run_arelle",
                                  return_value=(True, [], 1, ("validate/EFM",))):
            result = arelle_worker.handle(request, self.args)

        self.assertEqual("success", result["status"])
        completion = result["result"]["validationCompletion"]
        self.assertEqual("pass", completion["requiredPlugins"][0]["status"])
        self.assertIn("runtime registry", completion["requiredPlugins"][0]["detail"])

    def test_validate_report_marks_explicit_plugin_load_error_as_failure(self):
        request = self.request(report())
        request["validationRequirements"]["requiredPlugins"] = ["validate/EFM"]
        self.args.plugin = ["validate/EFM"]
        logs = [{
            "levelname": "error",
            "messageCode": "arelle:pluginLoadingError",
            "msg": "Unable to load module validate/EFM",
        }]
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "run_arelle", return_value=(False, logs, 0, ())):
            result = arelle_worker.handle(request, self.args)

        self.assertEqual("incompleteValidation", result["status"])
        completion = result["result"]["validationCompletion"]
        self.assertEqual("fail", completion["requiredPlugins"][0]["status"])
        self.assertTrue(any(item["code"] == "XBR010" and item["isBlocking"]
                            for item in result["diagnostics"]))

    def test_validate_report_rejects_requirements_substituted_from_installed_profile(self):
        self.args.profile_manifest_data = {
            "profileId": "fixture-profile",
            "profileDigest": "sha256:fixture",
            "requiredChecks": ["rule:mandatory"],
            "requiredPlugins": ["validate/EFM"],
        }
        request = self.request(report())
        request["validationRequirements"]["requiredRules"] = []
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "run_arelle") as run_arelle:
            result = arelle_worker.handle(request, self.args)

        self.assertEqual("domainFailure", result["status"])
        self.assertIn("do not match the installed profile", result["error"])
        run_arelle.assert_not_called()

    def test_handshake_keeps_distinct_profile_identities(self):
        identities = [("profile-one", "sha256:one"), ("profile-two", "sha256:two")]
        observed = []
        for profile_id, digest in identities:
            self.args.profile_manifest_data = {
                "schemaVersion": 2,
                "profileId": profile_id,
                "version": "1.0.0",
                "profileDigest": digest,
                "entryPoint": "entry.xsd",
                "requiredPlugins": [],
            }
            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True):
                result = arelle_worker.handle(self.handshake_request(), self.args)
            self.assertEqual("success", result["status"])
            observed.append((result["result"]["profileId"], result["result"]["profileDigest"]))

        self.assertEqual(identities, observed)

    def test_handshake_reports_missing_arelle_as_incomplete(self):
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", False):
            result = arelle_worker.handle(self.handshake_request(), self.args)

        self.assertEqual("incompleteValidation", result["status"])
        self.assertTrue(any(item["code"] == "XBR007" for item in result["diagnostics"]))

    def test_handshake_reports_protocol_mismatch_as_domain_failure(self):
        request = self.handshake_request()
        request["payload"] = {"minimumProtocolVersion": 99, "maximumProtocolVersion": 100}

        result = arelle_worker.handle(request, self.args)

        self.assertEqual("domainFailure", result["status"])
        self.assertIn("No mutually supported protocol version", result["error"])

    def test_handshake_marks_missing_required_plugin_incomplete(self):
        self.args.profile_manifest_data = {"requiredPlugins": ["validate/EFM"]}
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True):
            result = arelle_worker.handle(self.handshake_request(), self.args)

        self.assertEqual("incompleteValidation", result["status"])
        self.assertTrue(any(item["code"] == "XBR010" for item in result["diagnostics"]))

    def test_extract_taxonomy_relationships_preserves_roles_and_arc_attributes(self):
        namespace = "urn:xms:relationship-fixture"
        concepts = ["Root", "Shared", "Axis", "Member"]
        by_name = {name: SimpleNamespace(qname=SimpleNamespace(
            namespaceURI=namespace, localName=name)) for name in concepts}
        presentation_role_one = "urn:xms:role:one"
        presentation_role_two = "urn:xms:role:two"
        dimension_arcrole = "http://xbrl.org/int/dim/arcrole/domain-member"
        presentation = [
            SimpleNamespace(linkQname=SimpleNamespace(namespaceURI=arelle_worker.LINK_NAMESPACE,
                                                       localName="presentationLink"),
                            arcrole=arelle_worker.PRESENTATION_ARCROLE,
                            linkrole=presentation_role_one,
                            fromModelObject=by_name["Root"], toModelObject=by_name["Shared"],
                            orderDecimal=Decimal("1")),
            SimpleNamespace(linkQname=SimpleNamespace(namespaceURI=arelle_worker.LINK_NAMESPACE,
                                                       localName="presentationLink"),
                            arcrole=arelle_worker.PRESENTATION_ARCROLE,
                            linkrole=presentation_role_two,
                            fromModelObject=by_name["Root"], toModelObject=by_name["Shared"],
                            orderDecimal=Decimal("2")),
        ]
        calculation = [SimpleNamespace(
            linkQname=SimpleNamespace(namespaceURI=arelle_worker.LINK_NAMESPACE,
                                      localName="calculationLink"),
            arcrole=arelle_worker.CALCULATION_ARCROLE,
            linkrole=presentation_role_one,
            fromModelObject=by_name["Root"], toModelObject=by_name["Shared"],
            weightDecimal=Decimal("-1"), orderDecimal=Decimal("3"), targetRole=None)]
        definition = [SimpleNamespace(
            linkQname=SimpleNamespace(namespaceURI=arelle_worker.LINK_NAMESPACE,
                                      localName="definitionLink"),
            arcrole=dimension_arcrole, linkrole=presentation_role_one,
            fromModelObject=by_name["Axis"], toModelObject=by_name["Member"],
            orderDecimal=Decimal("4"), targetRole="urn:xms:role:members",
            usable=False, closed=True, contextElement="segment")]
        link_qnames = {
            name: type("TestQName", (), {"namespaceURI": arelle_worker.LINK_NAMESPACE,
                                         "localName": name})()
            for name in ("presentationLink", "calculationLink", "definitionLink")
        }
        model = SimpleNamespace(
            baseSets={
                (arelle_worker.PRESENTATION_ARCROLE, presentation_role_one, None,
                 link_qnames["presentationLink"]): [],
                (arelle_worker.CALCULATION_ARCROLE, presentation_role_one, None,
                 link_qnames["calculationLink"]): [],
                (dimension_arcrole, presentation_role_one, None,
                 link_qnames["definitionLink"]): [],
            },
            relationshipSet=lambda arcrole: SimpleNamespace(modelRelationships={
                arelle_worker.PRESENTATION_ARCROLE: presentation,
                arelle_worker.CALCULATION_ARCROLE: calculation,
                dimension_arcrole: definition,
            }[arcrole]))
        profile = {
            "concepts": [{"namespace": namespace, "localName": name,
                          "qualifiedName": "ex:" + name} for name in concepts],
            "presentationLinks": [
                {"roleUri": presentation_role_one, "parentQName": "ex:Root",
                 "childQName": "ex:Shared", "order": 1},
                {"roleUri": presentation_role_two, "parentQName": "ex:Root",
                 "childQName": "ex:Shared", "order": 2},
            ],
            "calculationLinks": [{
                "roleUri": presentation_role_one, "parentQName": "ex:Root",
                "childQName": "ex:Shared", "weight": -1, "order": 3,
            }],
            "definitionLinks": [{
                "roleUri": presentation_role_one, "arcRoleUri": dimension_arcrole,
                "parentQName": "ex:Axis", "childQName": "ex:Member", "order": 4,
                "targetRoleUri": "urn:xms:role:members", "usable": False,
                "closed": True, "contextElement": "segment",
            }],
        }

        extracted = arelle_worker.extract_taxonomy_relationships([model], profile)

        self.assertEqual(2, len(extracted["presentationLinks"]))
        self.assertEqual({presentation_role_one, presentation_role_two},
                         {item["roleUri"] for item in extracted["presentationLinks"]})
        self.assertEqual(Decimal("-1"), extracted["calculationLinks"][0]["weight"])
        self.assertEqual("urn:xms:role:members",
                         extracted["definitionLinks"][0]["targetRoleUri"])
        arelle_worker._assert_relationship_metadata_matches_profile(extracted, profile)

        missing_second_role = {**profile, "presentationLinks": profile["presentationLinks"][:1]}
        with self.assertRaisesRegex(ValueError, "do not match Arelle's effective taxonomy"):
            arelle_worker._assert_relationship_metadata_matches_profile(extracted, missing_second_role)

    def test_load_taxonomy_returns_versioned_profile_capabilities(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            entry_digest = "sha256:" + hashlib.sha256(entry.read_bytes()).hexdigest()
            self.args.taxonomy_root = str(root)
            self.args.plugin = ["validate/EFM"]
            self.args.profile_manifest_data = {
                "schemaVersion": 2,
                "profileId": "fixture-profile",
                "version": "2.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "requiredPlugins": ["validate/EFM"],
                "requiredChecks": ["rule:taxonomy-check"],
                "packageFiles": {"entry.xsd": entry_digest},
            }
            request = self.handshake_request()
            request["operation"] = "load-taxonomy"
            request["profileDigest"] = "sha256:fixture"
            request["payload"] = {"taxonomyPath": "entry.xsd"}

            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                    mock.patch.object(arelle_worker, "run_arelle", return_value=(True, [], 42, ("validate/EFM",))):
                result = arelle_worker.handle(request, self.args)

        self.assertEqual("success", result["status"])
        self.assertTrue(result["result"]["completed"])
        self.assertEqual(2, result["result"]["profileSchemaVersion"])
        self.assertEqual("fixture-profile", result["result"]["profileId"])
        self.assertEqual("2.0.0", result["result"]["profileVersion"])
        self.assertEqual("sha256:fixture", result["result"]["profileDigest"])
        self.assertEqual("entry.xsd", result["result"]["taxonomyEntryPoint"])
        self.assertEqual(["validate/EFM"], result["result"]["requiredPlugins"])
        self.assertEqual(["validate/EFM"], result["result"]["loadedPlugins"])
        self.assertEqual(["rule:taxonomy-check"], result["result"]["requiredChecks"])
        self.assertIn("validate-report", result["result"]["capabilities"])
        self.assertEqual(42, result["result"]["conceptCount"])

    def test_load_taxonomy_reports_configured_but_unavailable_required_plugin(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            entry_digest = "sha256:" + hashlib.sha256(entry.read_bytes()).hexdigest()
            self.args.taxonomy_root = str(root)
            self.args.plugin = ["validate/EFM"]
            self.args.profile_manifest_data = {
                "schemaVersion": 2,
                "profileId": "fixture-profile",
                "version": "2.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "requiredPlugins": ["validate/EFM"],
                "packageFiles": {"entry.xsd": entry_digest},
            }
            request = self.handshake_request()
            request["operation"] = "load-taxonomy"
            request["profileDigest"] = "sha256:fixture"
            request["payload"] = {"taxonomyPath": "entry.xsd"}

            logs = [{
                "levelname": "error",
                "messageCode": "arelle:plugin",
                "msg": "Unable to load module validate/EFM",
            }]
            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                    mock.patch.object(arelle_worker, "run_arelle", return_value=(False, logs, 0, ())):
                result = arelle_worker.handle(request, self.args)

        self.assertEqual("incompleteValidation", result["status"])
        self.assertEqual([], result["result"]["loadedPlugins"])
        self.assertTrue(any(item["code"] == "XBR010" for item in result["diagnostics"]))
        self.assertIn("required Arelle plugin did not complete loading", result["error"])

    def test_load_taxonomy_requires_positive_required_plugin_load_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            entry_digest = "sha256:" + hashlib.sha256(entry.read_bytes()).hexdigest()
            self.args.taxonomy_root = str(root)
            self.args.plugin = ["validate/EFM"]
            self.args.profile_manifest_data = {
                "schemaVersion": 2,
                "profileId": "fixture-profile",
                "version": "2.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "requiredPlugins": ["validate/EFM"],
                "packageFiles": {"entry.xsd": entry_digest},
            }
            request = self.handshake_request()
            request["operation"] = "load-taxonomy"
            request["profileDigest"] = "sha256:fixture"
            request["payload"] = {"taxonomyPath": "entry.xsd"}

            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                    mock.patch.object(arelle_worker, "run_arelle", return_value=(True, [], 1, ())):
                result = arelle_worker.handle(request, self.args)

        self.assertEqual("incompleteValidation", result["status"])
        self.assertFalse(result["result"]["completed"])
        self.assertEqual([], result["result"]["loadedPlugins"])
        self.assertTrue(any(item["code"] == "XBR010" and item["isBlocking"]
                            for item in result["diagnostics"]))

    def test_taxonomy_dependency_closure_accepts_pinned_relative_xsd_and_xlink_edges(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            files = {
                "taxonomy/entry.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" '
                    'xmlns:link="http://www.xbrl.org/2003/linkbase" '
                    'xmlns:xlink="http://www.w3.org/1999/xlink">'
                    '<xs:include schemaLocation="common.xsd"/>'
                    '<xs:annotation><xs:appinfo>'
                    '<link:linkbaseRef xlink:href="labels.xml#labels"/>'
                    '<link:roleRef xlink:href="roles.xsd#role" roleURI="urn:role"/>'
                    '<link:arcroleRef xlink:href="arcroles.xsd#arcrole" arcroleURI="urn:arcrole"/>'
                    '</xs:appinfo></xs:annotation></xs:schema>'
                ),
                "taxonomy/common.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
                    '<xs:import namespace="urn:types" schemaLocation="nested/types.xsd"/>'
                    '</xs:schema>'
                ),
                "taxonomy/nested/types.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
                    '<xs:include schemaLocation="../common.xsd"/></xs:schema>'
                ),
                "taxonomy/labels.xml": '<link:linkbase xmlns:link="http://www.xbrl.org/2003/linkbase"/>',
                "taxonomy/roles.xsd": '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>',
                "taxonomy/arcroles.xsd": '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>',
            }
            package_files = write_taxonomy_files(root, files)

            first = arelle_worker.validate_taxonomy_dependency_closure(
                "taxonomy/entry.xsd", root, package_files)
            second = arelle_worker.validate_taxonomy_dependency_closure(
                "taxonomy/entry.xsd", root, package_files)

        self.assertEqual(first, second)
        self.assertEqual(6, len(first))
        self.assertEqual("taxonomy/entry.xsd", first[0])
        self.assertIn("taxonomy/nested/types.xsd", first)

    def test_taxonomy_dependency_rejects_present_but_unlisted_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_files(root, {
                "entry.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
                    '<xs:include schemaLocation="dependency.xsd"/></xs:schema>'
                ),
                "dependency.xsd": '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>',
            })
            package_files.pop("dependency.xsd")

            with self.assertRaisesRegex(ValueError, "not hash-pinned by packageFiles: dependency.xsd"):
                arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)

    def test_taxonomy_dependency_rejects_missing_or_tampered_pinned_targets(self):
        entry_xml = (
            '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
            '<xs:include schemaLocation="dependency.xsd"/></xs:schema>'
        )
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_files(root, {"entry.xsd": entry_xml})
            package_files["dependency.xsd"] = "sha256:" + "0" * 64

            with self.assertRaisesRegex(ValueError, "dependency is unavailable: dependency.xsd"):
                arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_files(root, {
                "entry.xsd": entry_xml,
                "dependency.xsd": '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>',
            })
            (root / "dependency.xsd").write_text(
                '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" tampered="yes"/>',
                encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "hash mismatch while scanning dependency: dependency.xsd"):
                arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)

    def test_taxonomy_dependency_cycles_are_deterministic_and_limits_fail_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_files(root, {
                "entry.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
                    '<xs:include schemaLocation="dependency.xsd"/></xs:schema>'
                ),
                "dependency.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
                    '<xs:include schemaLocation="entry.xsd"/></xs:schema>'
                ),
            })
            first = arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)
            second = arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)
            self.assertEqual(("entry.xsd", "dependency.xsd"), first)
            self.assertEqual(first, second)

            with mock.patch.object(arelle_worker, "MAX_TAXONOMY_GRAPH_FILES", 1):
                with self.assertRaisesRegex(ValueError, "file limit"):
                    arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)
            with mock.patch.object(arelle_worker, "MAX_TAXONOMY_GRAPH_DEPTH", 0):
                with self.assertRaisesRegex(ValueError, "depth limit"):
                    arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)
            with mock.patch.object(arelle_worker, "MAX_TAXONOMY_GRAPH_XML_BYTES", 1):
                with self.assertRaisesRegex(ValueError, "XML byte limit"):
                    arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)
            with mock.patch.object(arelle_worker, "MAX_TAXONOMY_XML_FILE_BYTES", 1):
                with self.assertRaisesRegex(ValueError, "XML file exceeds the size limit"):
                    arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)
            with mock.patch.object(arelle_worker, "MAX_TAXONOMY_GRAPH_EDGES", 0):
                with self.assertRaisesRegex(ValueError, "edge limit"):
                    arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)

    def test_taxonomy_dependency_rejects_dtd_and_entity_payloads(self):
        payloads = [
            '<!DOCTYPE schema [<!ENTITY local "expanded">]><schema>&local;</schema>',
            '<!DOCTYPE schema SYSTEM "https://example.invalid/external.dtd"><schema/>',
        ]
        for payload in payloads:
            with self.subTest(payload=payload), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                package_files = write_taxonomy_files(root, {"entry.xsd": payload})
                with self.assertRaisesRegex(ValueError, "DTD/entity declarations are forbidden"):
                    arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)

    def test_taxonomy_dependency_rejects_xml_base_resolution(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_files(root, {
                "entry.xsd": '<schema xmlns:xml="http://www.w3.org/XML/1998/namespace" xml:base="../"/>',
            })
            with self.assertRaisesRegex(ValueError, "xml:base resolution is unsupported"):
                arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)

    def test_taxonomy_dependency_checks_xsi_schema_location_edges(self):
        external_locations = [
            'xsi:schemaLocation="urn:link https://example.invalid/linkbase.xsd"',
            'xsi:noNamespaceSchemaLocation="https://example.invalid/schema.xsd"',
        ]
        for location in external_locations:
            with self.subTest(location=location), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                package_files = write_taxonomy_files(root, {
                    "entry.xsd": (
                        '<schema xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" ' + location + '/>'
                    ),
                })
                with self.assertRaisesRegex(ValueError, "no exact hash-pinned offlineReferences mapping"):
                    arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_files(root, {
                "entry.xsd": (
                    '<schema xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
                    'xsi:schemaLocation="urn:link schemas/linkbase.xsd"/>'
                ),
                "schemas/linkbase.xsd": '<schema/>',
            })
            closure = arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)
        self.assertEqual(("entry.xsd", "schemas/linkbase.xsd"), closure)

    def test_external_taxonomy_dependency_uses_exact_pinned_offline_mapping(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_files(root, {
                "entry.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
                    '<xs:import namespace="urn:remote" '
                    'schemaLocation="https://example.invalid/remote.xsd"/></xs:schema>'
                ),
                "mapped.xsd": '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>',
            })
            self.args.taxonomy_root = str(root)
            self.args.profile_manifest_data = {
                "schemaVersion": 3,
                "profileId": "fixture-profile",
                "version": "1.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "requiredPlugins": [],
                "packageFiles": package_files,
                "offlineReferences": {"https://example.invalid/remote.xsd": "mapped.xsd"},
            }
            request = self.handshake_request()
            request["operation"] = "load-taxonomy"
            request["profileDigest"] = "sha256:fixture"
            request["payload"] = {"taxonomyPath": "entry.xsd"}
            observed = {}

            def capture_arelle(snapshot_entry, run_args, relationship_metadata_consumer=None):
                observed["entry"] = Path(snapshot_entry)
                observed["mappings"] = runpy.run_path(run_args.plugin[-1])["MAPPINGS"]
                observed["mapped_content"] = (
                    Path(observed["mappings"]["https://example.invalid/"]) /
                    "remote.xsd").read_text(encoding="utf-8")
                if relationship_metadata_consumer is not None:
                    relationship_metadata_consumer({
                        "presentationLinks": [], "calculationLinks": [], "definitionLinks": []})
                return True, [], 1, ()

            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                    mock.patch.object(arelle_worker, "run_arelle", side_effect=capture_arelle) as run_arelle:
                result = arelle_worker.handle(request, self.args)
            dependency_closure = arelle_worker.validate_taxonomy_dependency_closure(
                "entry.xsd", root, package_files,
                {"https://example.invalid/remote.xsd": "mapped.xsd"})

        self.assertEqual("success", result["status"], result["error"])
        self.assertEqual(("entry.xsd", "mapped.xsd"), dependency_closure)
        self.assertEqual(1, len(observed["mappings"]))
        self.assertIn("https://example.invalid/", observed["mappings"])
        self.assertEqual('<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>',
                         observed["mapped_content"])
        self.assertFalse(observed["entry"].is_relative_to(root))
        run_arelle.assert_called_once()

    def test_load_taxonomy_uses_hash_verified_snapshot_after_installed_files_change(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            original_dependency = '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" id="pinned"/>'
            files = write_taxonomy_files(root, {
                "entry.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
                    '<xs:include schemaLocation="dependency.xsd"/></xs:schema>'
                ),
                "dependency.xsd": original_dependency,
            })
            self.args.taxonomy_root = str(root)
            self.args.profile_manifest_data = {
                "schemaVersion": 3,
                "profileId": "fixture-profile",
                "version": "1.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "requiredPlugins": [],
                "packageFiles": files,
            }
            request = self.handshake_request()
            request["operation"] = "load-taxonomy"
            request["profileDigest"] = "sha256:fixture"
            request["payload"] = {"taxonomyPath": "entry.xsd"}
            observed = {}

            def mutate_installed_file_then_load(snapshot_entry, _args,
                                                relationship_metadata_consumer=None):
                observed["snapshot_entry"] = Path(snapshot_entry)
                (root / "dependency.xsd").write_text(
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" id="changed"/>',
                    encoding="utf-8")
                observed["loaded_dependency"] = (
                    Path(snapshot_entry).parent / "dependency.xsd").read_text(encoding="utf-8")
                if relationship_metadata_consumer is not None:
                    relationship_metadata_consumer({
                        "presentationLinks": [], "calculationLinks": [], "definitionLinks": []})
                return True, [], 1, ()

            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                    mock.patch.object(arelle_worker, "run_arelle", side_effect=mutate_installed_file_then_load):
                result = arelle_worker.handle(request, self.args)

        self.assertEqual("success", result["status"])
        self.assertEqual("pinned", observed["loaded_dependency"].split('id="', 1)[1].split('"', 1)[0])
        self.assertFalse(observed["snapshot_entry"].is_relative_to(root))
        self.assertEqual("entry.xsd", result["result"]["taxonomyEntryPoint"])

    def test_load_taxonomy_rejects_file_changed_between_closure_scan_and_snapshot(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            files = write_taxonomy_files(root, {
                "entry.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
                    '<xs:include schemaLocation="dependency.xsd"/></xs:schema>'
                ),
                "dependency.xsd": '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" id="pinned"/>',
            })
            self.args.taxonomy_root = str(root)
            self.args.profile_manifest_data = {
                "schemaVersion": 3,
                "profileId": "fixture-profile",
                "version": "1.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "requiredPlugins": [],
                "packageFiles": files,
            }
            request = self.handshake_request()
            request["operation"] = "load-taxonomy"
            request["profileDigest"] = "sha256:fixture"
            request["payload"] = {"taxonomyPath": "entry.xsd"}
            validate = arelle_worker.validate_taxonomy_dependency_closure

            def scan_then_mutate(*args):
                closure = validate(*args)
                (root / "dependency.xsd").write_text(
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" id="changed"/>',
                    encoding="utf-8")
                return closure

            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                    mock.patch.object(arelle_worker, "validate_taxonomy_dependency_closure", side_effect=scan_then_mutate), \
                    mock.patch.object(arelle_worker, "run_arelle") as run_arelle:
                result = arelle_worker.handle(request, self.args)

        self.assertEqual("domainFailure", result["status"])
        self.assertIn("hash mismatch while snapshotting dependency: dependency.xsd", result["error"])
        run_arelle.assert_not_called()

    def test_taxonomy_dependency_resolves_hash_pinned_zip_catalog_offline(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_package(root)

            closure = arelle_worker.validate_taxonomy_dependency_closure(
                "SyntheticPackage/schemas/entry.xsd", root, package_files)

            self.assertEqual(("SyntheticPackage/schemas/dependency.xsd",
                              "SyntheticPackage/schemas/entry.xsd"), tuple(sorted(closure)))
            self.assertEqual(("taxonomy.zip",), closure.package_archives)
            self.assertEqual({}, closure.offline_references)
            with self.assertRaisesRegex(ValueError, "not declared by its pinned taxonomyPackage.xml"):
                arelle_worker.validate_taxonomy_dependency_closure(
                    "SyntheticPackage/schemas/dependency.xsd", root, package_files)

            observed = {}

            def load_snapshot(entry_point, args):
                observed["entry_point"] = Path(entry_point)
                observed["plugins"] = list(args.plugin)
                observed["mappings"] = runpy.run_path(args.plugin[-1])["MAPPINGS"]
                observed["mapped_entry"] = (
                    Path(observed["mappings"]["https://taxonomy.example/test/"]) /
                    "entry.xsd").read_text(encoding="utf-8")
                observed["mapped_dependency"] = (
                    Path(observed["mappings"]["https://taxonomy.example/test/"]) /
                    "dependency.xsd").read_text(encoding="utf-8")
                return True, [], 2, ()

            with mock.patch.object(arelle_worker, "run_arelle", side_effect=load_snapshot):
                result = arelle_worker.run_arelle_with_verified_taxonomy(
                    "SyntheticPackage/schemas/entry.xsd", closure, root, package_files, self.args)

            self.assertEqual((True, [], 2, ()), result)
            self.assertTrue(observed["plugins"][-1].endswith("arelle_offline_taxonomy_catalog.py"))
            self.assertIn("dependency.xsd", observed["mapped_entry"])
            self.assertIn("DependencyType", observed["mapped_dependency"])
            self.assertNotEqual(root, observed["entry_point"].parent.parent.parent)

    def test_taxonomy_package_archive_rejects_traversal_and_unpinned_members(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive_path = root / "unsafe.zip"
            with zipfile.ZipFile(archive_path, "w") as archive:
                archive.writestr("../escape.xsd", "unsafe")
            package_files = {
                "unsafe.zip": "sha256:" + hashlib.sha256(archive_path.read_bytes()).hexdigest(),
            }
            with self.assertRaisesRegex(ValueError, "escapes its package root"):
                arelle_worker.validate_taxonomy_package_archives(root, package_files)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_package(root)
            package_files.pop("SyntheticPackage/schemas/dependency.xsd")
            with self.assertRaisesRegex(ValueError, "not hash-pinned by packageFiles"):
                arelle_worker.validate_taxonomy_package_archives(root, package_files)

    def test_taxonomy_package_archive_rejects_escaping_catalog_and_case_colliding_names(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_package(root, rewrite_prefix="../../outside/")
            with self.assertRaisesRegex(ValueError, "rewritePrefix escapes its package root"):
                arelle_worker.validate_taxonomy_package_archives(root, package_files)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive_path = root / "taxonomy.zip"
            with zipfile.ZipFile(archive_path, "w") as archive:
                archive.writestr("SyntheticPackage/", b"")
                archive.writestr("syntheticpackage/", b"")
            package_files = {
                "taxonomy.zip": "sha256:" + hashlib.sha256(archive_path.read_bytes()).hexdigest(),
            }
            with self.assertRaisesRegex(ValueError, "duplicate or case-colliding"):
                arelle_worker.validate_taxonomy_package_archives(root, package_files)

    def test_profile_zip_taxonomy_package_is_used_for_json_report_validation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_package(root)
            self.args.taxonomy_root = str(root)
            self.args.profile_manifest_data = {
                "schemaVersion": 5,
                "profileId": "fixture-profile",
                "version": "1.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "SyntheticPackage/schemas/entry.xsd",
                "requiredPlugins": [],
                "requiredChecks": [],
                "packageFiles": package_files,
                "offlineReferences": {},
            }
            report_value = report()
            report_value["documentInfo"]["taxonomy"] = [
                "https://taxonomy.example/test/entry.xsd"]
            observed = {}

            def fake_arelle(entrypoint, args, temporary_path=None):
                observed["report"] = json.loads(Path(entrypoint).read_text(encoding="utf-8"))
                observed["plugins"] = list(args.plugin)
                observed["mappings"] = runpy.run_path(args.plugin[-1])["MAPPINGS"]
                observed["mapped_dependency"] = (
                    Path(observed["mappings"]["https://taxonomy.example/test/"]) /
                    "dependency.xsd").read_text(encoding="utf-8")
                return True, [], 2, ()

            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                    mock.patch.object(arelle_worker, "run_arelle", side_effect=fake_arelle):
                result = arelle_worker.handle(self.request(report_value), self.args)

        self.assertEqual("success", result["status"], result["error"])
        localized_entry = observed["report"]["documentInfo"]["taxonomy"][0].replace("\\", "/")
        self.assertTrue(localized_entry.endswith("SyntheticPackage/schemas/entry.xsd"))
        self.assertTrue(observed["plugins"][-1].endswith("arelle_offline_taxonomy_catalog.py"))
        self.assertEqual(1, len(observed["mappings"]))
        self.assertIn("DependencyType", observed["mapped_dependency"])

    def test_schema_location_rejects_query_or_fragment_but_xlink_fragment_only_is_local(self):
        invalid_schema_locations = ("dependency.xsd?version=1", "dependency.xsd#schema")
        for schema_location in invalid_schema_locations:
            with self.subTest(schema_location=schema_location), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                package_files = write_taxonomy_files(root, {
                    "entry.xsd": (
                        '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">'
                        '<xs:include schemaLocation="' + schema_location + '"/></xs:schema>'
                    ),
                })
                with self.assertRaisesRegex(ValueError, "schemaLocation (queries|fragments) are unsupported"):
                    arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_files(root, {
                "entry.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" '
                    'xmlns:link="http://www.xbrl.org/2003/linkbase" '
                    'xmlns:xlink="http://www.w3.org/1999/xlink">'
                    '<xs:annotation><xs:appinfo><link:roleRef xlink:href="#local-role" '
                    'roleURI="urn:role"/></xs:appinfo></xs:annotation></xs:schema>'
                ),
            })
            result = arelle_worker.validate_taxonomy_dependency_closure("entry.xsd", root, package_files)
        self.assertEqual(("entry.xsd",), result)

    def test_arelle_plugins_use_the_runtime_option_separator(self):
        self.args.plugin = ["plugin-one", "plugin-two"]
        session = mock.MagicMock()
        session.__enter__.return_value = session
        session.run.return_value = True
        session.get_log_messages.return_value = []
        session.get_models.return_value = []

        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "Session", return_value=session), \
                mock.patch.object(arelle_worker, "RuntimeOptions") as runtime_options:
            arelle_worker.run_arelle("entry.xsd", self.args)

        self.assertEqual("plugin-one|plugin-two", runtime_options.call_args.kwargs["plugins"])
        self.assertIsInstance(session.run.call_args.kwargs["logHandler"],
                              arelle_worker.BoundedArelleLogHandler)

    def test_arelle_run_links_single_fact_reference_before_closing_models(self):
        locator = "f" + "b" * 64
        fact = SimpleNamespace(isItem=True, id=locator)
        model = SimpleNamespace(
            modelObject=lambda object_id: fact if object_id == "_fact_1" else None,
            qnameConcepts={},
        )
        session = mock.MagicMock()
        session.__enter__.return_value = session
        session.get_models.return_value = [model]

        def emit_fact_finding(_options, logHandler):
            record = logging.LogRecord("arelle", logging.ERROR, "worker.py", 10, "fact issue", (), None)
            record.messageCode = "fixture:factIssue"
            record.refs = [{"objectId": "_fact_1"}]
            logHandler.emit(record)
            return False

        session.run.side_effect = emit_fact_finding
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "Session", return_value=session), \
                mock.patch.object(arelle_worker, "RuntimeOptions"):
            _ok, logs, _concept_count, _loaded_plugins = arelle_worker.run_arelle("report.json", self.args)

        self.assertEqual(locator, logs[0]["factLocator"])
        session._cntlr.modelManager.close.assert_called_once_with(model)

    def test_arelle_run_collects_only_modules_from_the_loaded_plugin_registry(self):
        session = mock.MagicMock()
        session.__enter__.return_value = session
        session.run.return_value = True
        session.get_log_messages.return_value = []
        session.get_models.return_value = []
        plugin_manager = SimpleNamespace(modulePluginInfos={
            "loaded-plugin": {"name": "loaded-plugin", "moduleURL": "profile/loaded-plugin"},
        }, pluginConfig={"modules": {
            "loaded-plugin": {
                "entryPoint": {"name": "profile/loaded-plugin", "module": "plugin.module"},
            },
            "configured-but-not-loaded": {
                "name": "configured-but-not-loaded", "moduleURL": "profile/unloaded-plugin",
            },
        }})
        plugin_manager_api = SimpleNamespace(getInstance=lambda: plugin_manager)

        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "PluginManager", plugin_manager_api), \
                mock.patch.object(arelle_worker, "Session", return_value=session), \
                mock.patch.object(arelle_worker, "RuntimeOptions"):
            result = arelle_worker.run_arelle("entry.xsd", self.args)

        self.assertEqual((True, [], 0,
                          ("loaded-plugin", "plugin.module", "profile/loaded-plugin")), result)

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Pinned Arelle runtime supplies the plugin loader for this integration fixture.")
    def test_arelle_run_reports_a_plugin_imported_by_the_pinned_runtime(self):
        with tempfile.TemporaryDirectory(prefix="xms-plugin-evidence-") as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text(
                '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" '
                'targetNamespace="urn:xms:plugin-evidence">'
                '<xs:element name="Entry" type="xs:string"/></xs:schema>',
                encoding="utf-8")
            plugin = root / "xms_evidence_plugin.py"
            plugin.write_text(
                "def add_options(_parser):\n"
                "    return None\n"
                "__pluginInfo__ = {\n"
                "    'name': 'xms-evidence-plugin',\n"
                "    'version': '1.0',\n"
                "    'CntlrCmdLine.Options': add_options,\n"
                "}\n",
                encoding="utf-8")
            self.args.plugin = [str(plugin)]

            ok, logs, _concept_count, loaded_plugins = arelle_worker.run_arelle(entry, self.args)

        self.assertTrue(ok, logs)
        self.assertIn(str(plugin), loaded_plugins)

    def test_arelle_runtime_options_enforce_exact_offline_mode(self):
        session = mock.MagicMock()
        session.__enter__.return_value = session
        session.run.return_value = True
        session.get_log_messages.return_value = []
        session.get_models.return_value = []

        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "Session", return_value=session), \
                mock.patch.object(arelle_worker, "RuntimeOptions") as runtime_options:
            arelle_worker.run_arelle("entry.xsd", self.args)

        options = runtime_options.call_args.kwargs
        self.assertIs(options["workOffline"], True)
        self.assertEqual("offline", options["internetConnectivity"])

    def test_worker_process_denies_dns_and_loopback_socket_operations(self):
        class ConnectionCanaryServer(http.server.ThreadingHTTPServer):
            def __init__(self, server_address, request_handler):
                self.accepted_connections = []
                super().__init__(server_address, request_handler)

            def get_request(self):
                request, address = super().get_request()
                self.accepted_connections.append(address)
                return request, address

        class SilentHandler(http.server.BaseHTTPRequestHandler):
            def log_message(self, _format, *_args):
                return

        canary = ConnectionCanaryServer(("127.0.0.1", 0), SilentHandler)
        canary_thread = threading.Thread(target=canary.serve_forever, daemon=True)
        canary_thread.start()
        datagram_canary = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        datagram_canary.bind(("127.0.0.1", 0))
        datagram_canary.settimeout(0.2)
        probe = """
import runpy
import socket
import sys

worker_path = sys.argv[1]
tcp_port = int(sys.argv[2])
udp_port = int(sys.argv[3])
sys.argv = [worker_path]
runpy.run_path(worker_path, run_name="__main__")

def expect_blocked(action, description):
    try:
        action()
    except PermissionError as error:
        assert "disabled" in str(error)
    else:
        raise SystemExit(description + " was allowed")

expect_blocked(lambda: socket.getaddrinfo("localhost", 80), "DNS resolution")

listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
try:
    expect_blocked(lambda: listener.bind(("127.0.0.1", 0)), "The loopback TCP bind")
finally:
    listener.close()

datagram_listener = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
try:
    expect_blocked(lambda: datagram_listener.bind(("127.0.0.1", 0)), "The loopback UDP bind")
finally:
    datagram_listener.close()

connection = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
try:
    address = ("127.0.0.1", tcp_port)
    expect_blocked(lambda: connection.connect(address), "The loopback connection")
    expect_blocked(lambda: connection.connect_ex(address), "The loopback connect_ex call")
finally:
    connection.close()

datagram = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
try:
    expect_blocked(lambda: datagram.sendto(b"offline probe", ("127.0.0.1", udp_port)),
                   "The loopback UDP send")
finally:
    datagram.close()

print("offline-network-denied")
        """
        datagram_received = False
        try:
            completed = subprocess.run(
                [sys.executable, "-c", probe, arelle_worker.__file__,
                 str(canary.server_address[1]),
                 str(datagram_canary.getsockname()[1])],
                cwd=WORKER_DIR,
                input="",
                capture_output=True,
                text=True,
                timeout=10,
                check=False,
            )
            try:
                datagram_canary.recvfrom(4096)
                datagram_received = True
            except socket.timeout:
                pass
        finally:
            canary.shutdown()
            canary.server_close()
            canary_thread.join(timeout=2)
            datagram_canary.close()

        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertIn("offline-network-denied", completed.stdout)
        self.assertEqual([], canary.accepted_connections,
                         "The worker process opened a loopback connection despite offline mode.")
        self.assertFalse(datagram_received,
                         "The worker process sent a loopback datagram despite offline mode.")

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_local_taxonomy_report_passes_arelle(self):
        result = arelle_worker.handle(self.request(report()), self.args)
        self.assertEqual("success", result["status"], result["error"])
        self.assertTrue(result["result"]["completed"])
        self.assertFalse(any(item["isBlocking"] for item in result["diagnostics"]))

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_profile_external_schema_loads_from_exact_hash_pinned_offline_reference_without_network_request(self):
        requests = []

        class RequestCanaryHandler(http.server.BaseHTTPRequestHandler):
            observed_requests = requests

            def parse_request(self):
                parsed = super().parse_request()
                if parsed:
                    self.observed_requests.append((self.command, self.path))
                return parsed

            def do_GET(self):
                self.send_response(404)
                self.end_headers()

            def log_message(self, _format, *_args):
                return

        canary = http.server.HTTPServer(("127.0.0.1", 0), RequestCanaryHandler)
        canary_thread = threading.Thread(target=canary.serve_forever, daemon=True)
        canary_thread.start()
        try:
            remote_uri = f"http://127.0.0.1:{canary.server_port}/remote%20schema.xsd"
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                package_files = write_taxonomy_files(root, {
                    "entry.xsd": (
                        '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" '
                        'xmlns:r="urn:remote" targetNamespace="urn:entry">'
                        '<xs:import namespace="urn:remote" '
                        f'schemaLocation="{remote_uri}"/>'
                        '<xs:element name="Entry" type="r:RemoteType"/></xs:schema>'
                    ),
                    "remote.xsd": (
                        '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" '
                        'targetNamespace="urn:remote"><xs:simpleType name="RemoteType">'
                        '<xs:restriction base="xs:string"/></xs:simpleType></xs:schema>'
                    ),
                })
                self.args.taxonomy_root = str(root)
                self.args.profile_manifest_data = {
                    "schemaVersion": 3,
                    "profileId": "fixture-profile",
                    "version": "1.0.0",
                    "profileDigest": "sha256:fixture",
                    "entryPoint": "entry.xsd",
                    "requiredPlugins": [],
                    "packageFiles": package_files,
                    "offlineReferences": {remote_uri: "remote.xsd"},
                }
                request = self.handshake_request()
                request["operation"] = "load-taxonomy"
                request["profileDigest"] = "sha256:fixture"
                request["payload"] = {"taxonomyPath": "entry.xsd"}

                result = arelle_worker.handle(request, self.args)
                repeat_request = {**request, "operationId": "offline-profile-repeat"}
                repeated = arelle_worker.handle(repeat_request, self.args)
        finally:
            canary.shutdown()
            canary.server_close()
            canary_thread.join(timeout=2)

        self.assertEqual("success", result["status"], result["error"])
        self.assertTrue(result["result"]["completed"])
        self.assertEqual(2, result["result"]["conceptCount"])
        self.assertFalse(any(item["isBlocking"] for item in result["diagnostics"]))
        self.assertEqual("success", repeated["status"], repeated["error"])
        self.assertEqual(result["result"], repeated["result"])
        comparable_diagnostics = lambda response: sorted(
            (item["code"], item["severity"], item["isBlocking"], item["message"])
            for item in response["diagnostics"])
        self.assertEqual(comparable_diagnostics(result), comparable_diagnostics(repeated))
        self.assertEqual([], requests, "Arelle attempted an HTTP request instead of using its pinned local file.")

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_load_taxonomy_checks_relationships_against_the_installed_profile(self):
        namespace = "urn:xms:relationship-fixture"
        role_one = "http://www.xbrl.org/2003/role/link"
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package_files = write_taxonomy_files(root, {
                "entry.xsd": (
                    '<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" '
                    'xmlns:link="http://www.xbrl.org/2003/linkbase" '
                    'xmlns:xlink="http://www.w3.org/1999/xlink" '
                    f'targetNamespace="{namespace}" xmlns:ex="{namespace}">'
                    '<xs:annotation><xs:appinfo>'
                    '<link:linkbaseRef xlink:type="simple" '
                    'xlink:arcrole="http://www.w3.org/1999/xlink/properties/linkbase" '
                    'xlink:href="relationships.xml"/>'
                    '</xs:appinfo></xs:annotation>'
                    '<xs:element name="Root" id="Root" type="xs:string"/>'
                    '<xs:element name="Shared" id="Shared" type="xs:string"/>'
                    '</xs:schema>'
                ),
                "relationships.xml": (
                    '<link:linkbase xmlns:link="http://www.xbrl.org/2003/linkbase" '
                    'xmlns:xlink="http://www.w3.org/1999/xlink">'
                    f'<link:presentationLink xlink:type="extended" xlink:role="{role_one}">'
                    '<link:loc xlink:type="locator" xlink:href="entry.xsd#Root" xlink:label="root"/>'
                    '<link:loc xlink:type="locator" xlink:href="entry.xsd#Shared" xlink:label="shared"/>'
                    '<link:presentationArc xlink:type="arc" '
                    'xlink:arcrole="http://www.xbrl.org/2003/arcrole/parent-child" '
                    'xlink:from="root" xlink:to="shared" order="1"/>'
                    '</link:presentationLink></link:linkbase>'
                ),
            })
            profile = {
                "schemaVersion": 4,
                "profileId": "fixture-profile",
                "version": "1.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "requiredPlugins": [],
                "packageFiles": package_files,
                "concepts": [
                    {"qualifiedName": "ex:Root", "namespace": namespace, "localName": "Root"},
                    {"qualifiedName": "ex:Shared", "namespace": namespace, "localName": "Shared"},
                ],
                "presentationLinks": [
                    {"roleUri": role_one, "parentQName": "ex:Root", "childQName": "ex:Shared", "order": 1},
                ],
                "calculationLinks": [],
                "definitionLinks": [],
            }
            self.args.taxonomy_root = str(root)
            self.args.profile_manifest_data = profile
            request = self.handshake_request()
            request["operation"] = "load-taxonomy"
            request["profileDigest"] = "sha256:fixture"
            request["payload"] = {"taxonomyPath": "entry.xsd"}

            matched = arelle_worker.handle(request, self.args)
            self.assertEqual("success", matched["status"], (matched["error"], matched["diagnostics"]))

            self.args.profile_manifest_data = {**profile, "presentationLinks": []}
            mismatched = arelle_worker.handle(request, self.args)

        self.assertEqual("domainFailure", mismatched["status"])
        self.assertIn("presentationLinks do not match", mismatched["error"])

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_profile_zip_taxonomy_package_catalog_resolves_pinned_schema_without_network_request(self):
        requests = []

        class RequestCanaryHandler(http.server.BaseHTTPRequestHandler):
            observed_requests = requests

            def parse_request(self):
                parsed = super().parse_request()
                if parsed:
                    self.observed_requests.append((self.command, self.path))
                return parsed

            def do_GET(self):
                self.send_response(404)
                self.end_headers()

            def log_message(self, _format, *_args):
                return

        canary = http.server.HTTPServer(("127.0.0.1", 0), RequestCanaryHandler)
        canary_thread = threading.Thread(target=canary.serve_forever, daemon=True)
        canary_thread.start()
        try:
            base_uri = f"http://127.0.0.1:{canary.server_port}/taxonomy/"
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                package_files = write_taxonomy_package(root, base_uri=base_uri)
                self.args.taxonomy_root = str(root)
                self.args.profile_manifest_data = {
                    "schemaVersion": 5,
                    "profileId": "fixture-profile",
                    "version": "1.0.0",
                    "profileDigest": "sha256:fixture",
                    "entryPoint": "SyntheticPackage/schemas/entry.xsd",
                    "requiredPlugins": [],
                    "packageFiles": package_files,
                    "offlineReferences": {},
                }
                request = self.handshake_request()
                request["operation"] = "load-taxonomy"
                request["profileDigest"] = "sha256:fixture"
                request["payload"] = {"taxonomyPath": "SyntheticPackage/schemas/entry.xsd"}

                result = arelle_worker.handle(request, self.args)
        finally:
            canary.shutdown()
            canary.server_close()
            canary_thread.join(timeout=2)

        self.assertEqual("success", result["status"], (result["error"], result["diagnostics"]))
        self.assertEqual(3, result["result"]["conceptCount"])
        self.assertFalse(any(item["isBlocking"] for item in result["diagnostics"]))
        self.assertEqual([], requests,
                         "Arelle attempted an HTTP request instead of using the taxonomy package catalog.")

    def test_large_path_payload_rehashes_and_streams_report_to_arelle_file_loader(self):
        temporary = tempfile.mkdtemp(prefix="xms-large-")
        directory = Path(temporary)
        if sys.platform != "win32":
            directory.chmod(0o700)
        path = directory / "report.json"
        raw = json.dumps(report(), ensure_ascii=False).encode("utf-8")
        path.write_bytes(raw)
        request = {
            "operation": "validate-report", "operationId": "large-op", "jobId": "large-job",
            "profileDigest": "test", "deadlineUtc": "2999-01-01T00:00:00Z",
            "payload": {"xbrlJsonPath": str(path), "sha256": "sha256:" + hashlib.sha256(raw).hexdigest()},
            "validationRequirements": {"profileId": "test-profile", "profileDigest": "test",
                                       "requiredRules": [], "requiredPlugins": []},
        }
        observed = {}

        def fake_arelle(entrypoint, args, temporary_path=None):
            observed["path"] = Path(entrypoint)
            observed["temporary_path"] = temporary_path
            observed["report"] = json.loads(Path(entrypoint).read_text(encoding="utf-8"))
            return True, [], 1, ()

        try:
            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                    mock.patch.object(arelle_worker, "run_arelle", side_effect=fake_arelle):
                result = arelle_worker.handle(request, self.args)
            self.assertEqual("success", result["status"], result["error"])
            self.assertNotEqual(path, observed["path"])
            sanitized = observed["report"]
            expected_taxonomy = str(TAXONOMY_ROOT / "arelle-test-taxonomy.xsd") \
                if sys.platform == "win32" else (TAXONOMY_ROOT / "arelle-test-taxonomy.xsd").as_uri()
            self.assertEqual([expected_taxonomy], sanitized["documentInfo"]["taxonomy"])
        finally:
            import shutil
            shutil.rmtree(directory, ignore_errors=True)

    def test_large_path_rejects_bad_digest_and_paths_outside_private_temp_directory(self):
        outside_directory = Path(tempfile.mkdtemp(prefix="xms-unqualified-"))
        outside = outside_directory / "report.json"
        outside_raw = json.dumps(report()).encode("utf-8")
        outside.write_bytes(outside_raw)
        payloads = [
            {"xbrlJsonPath": str(outside),
             "sha256": "sha256:" + hashlib.sha256(outside_raw).hexdigest()},
        ]
        private = Path(tempfile.mkdtemp(prefix="xms-large-"))
        if sys.platform != "win32":
            private.chmod(0o700)
        try:
            path = private / "report.json"
            raw = json.dumps(report()).encode("utf-8")
            path.write_bytes(raw)
            payloads.append({"xbrlJsonPath": str(path), "sha256": "sha256:" + "0" * 64})
            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True):
                for payload in payloads:
                    request = {
                        "operation": "validate-report", "operationId": "large-op", "jobId": "large-job",
                        "profileDigest": "test", "deadlineUtc": "2999-01-01T00:00:00Z",
                        "payload": payload,
                        "validationRequirements": {"profileId": "test-profile", "profileDigest": "test",
                                                   "requiredRules": [], "requiredPlugins": []},
                    }
                    result = arelle_worker.handle(request, self.args)
                    self.assertEqual("domainFailure", result["status"])
        finally:
            import shutil
            shutil.rmtree(private, ignore_errors=True)
            shutil.rmtree(outside_directory, ignore_errors=True)

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Pinned Arelle runtime supplies lxml for staged XML path tests.")
    def test_staged_xml_path_rehashes_localizes_taxonomy_and_loads_exact_instance(self):
        import shutil

        with tempfile.TemporaryDirectory(prefix="xms-staged-xml-") as temporary:
            directory = Path(temporary)
            if sys.platform != "win32":
                directory.chmod(0o700)
            path = directory / "report.xml"
            raw = (FIXTURES / "product-generated-pass.xbrl.xml").read_bytes()
            path.write_bytes(raw)
            request = {
                "operation": "validate-report", "operationId": "staged-xml-op", "jobId": "staged-xml-job",
                "profileDigest": "test", "deadlineUtc": "2999-01-01T00:00:00Z",
                "payload": {"xbrlXmlPath": str(path), "sha256": "sha256:" + hashlib.sha256(raw).hexdigest()},
                "validationRequirements": {"profileId": "test-profile", "profileDigest": "test",
                                           "requiredRules": [], "requiredPlugins": []},
            }
            observed = {}

            def fake_arelle(entrypoint, args, temporary_path=None):
                observed["path"] = Path(entrypoint)
                observed["root"] = ElementTree.parse(entrypoint).getroot()
                schema_ref = observed["root"].find("{http://www.xbrl.org/2003/linkbase}schemaRef")
                observed["taxonomy_exists"] = (Path(entrypoint).parent /
                    schema_ref.get("{http://www.w3.org/1999/xlink}href")).is_file()
                return True, [], 1, ()

            try:
                with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                        mock.patch.object(arelle_worker, "run_arelle", side_effect=fake_arelle):
                    result = arelle_worker.handle(request, self.args)
            finally:
                shutil.rmtree(directory, ignore_errors=True)

        self.assertEqual("success", result["status"], result["error"])
        self.assertNotEqual(path, observed["path"])
        self.assertTrue(observed["taxonomy_exists"])
        schema_ref = observed["root"].find("{http://www.xbrl.org/2003/linkbase}schemaRef")
        self.assertEqual("arelle-test-taxonomy.xsd",
                         schema_ref.get("{http://www.w3.org/1999/xlink}href"))

    def test_staged_xml_path_rejects_outside_directory_and_bad_digest(self):
        import shutil

        outside_directory = Path(tempfile.mkdtemp(prefix="xms-unqualified-xml-"))
        private = Path(tempfile.mkdtemp(prefix="xms-staged-xml-"))
        if sys.platform != "win32":
            private.chmod(0o700)
        try:
            raw = (FIXTURES / "product-generated-pass.xbrl.xml").read_bytes()
            outside = outside_directory / "report.xml"
            outside.write_bytes(raw)
            valid = private / "report.xml"
            valid.write_bytes(raw)
            payloads = [
                {"xbrlXmlPath": str(outside), "sha256": "sha256:" + hashlib.sha256(raw).hexdigest()},
                {"xbrlXmlPath": str(valid), "sha256": "sha256:" + "0" * 64},
            ]
            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True):
                for payload in payloads:
                    request = {
                        "operation": "validate-report", "operationId": "staged-xml-op", "jobId": "staged-xml-job",
                        "profileDigest": "test", "deadlineUtc": "2999-01-01T00:00:00Z",
                        "payload": payload,
                        "validationRequirements": {"profileId": "test-profile", "profileDigest": "test",
                                                   "requiredRules": [], "requiredPlugins": []},
                    }
                    result = arelle_worker.handle(request, self.args)
                    self.assertEqual("domainFailure", result["status"])
        finally:
            shutil.rmtree(private, ignore_errors=True)
            shutil.rmtree(outside_directory, ignore_errors=True)

    def test_large_path_rejects_duplicate_root_member_extra_member_and_trailing_data(self):
        valid = json.dumps(report(), separators=(",", ":"))
        document_info = json.dumps(report()["documentInfo"], separators=(",", ":"))
        malformed_reports = [
            "{" + json.dumps("documentInfo") + ":" + document_info + "," + json.dumps("documentInfo")
            + ":" + document_info + ',"facts":{}}',
            valid[:-1] + ',"unexpected":true}',
            valid + " trailing",
        ]
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                mock.patch.object(arelle_worker, "run_arelle", return_value=(True, [], 1, ())) as run_arelle:
            for raw_text in malformed_reports:
                directory = Path(tempfile.mkdtemp(prefix="xms-large-"))
                if sys.platform != "win32":
                    directory.chmod(0o700)
                try:
                    path = directory / "report.json"
                    raw = raw_text.encode("utf-8")
                    path.write_bytes(raw)
                    request = {
                        "operation": "validate-report", "operationId": "large-op", "jobId": "large-job",
                        "profileDigest": "test", "deadlineUtc": "2999-01-01T00:00:00Z",
                        "payload": {"xbrlJsonPath": str(path),
                                    "sha256": "sha256:" + hashlib.sha256(raw).hexdigest()},
                        "validationRequirements": {"profileId": "test-profile", "profileDigest": "test",
                                                   "requiredRules": [], "requiredPlugins": []},
                    }
                    result = arelle_worker.handle(request, self.args)
                    self.assertEqual("domainFailure", result["status"])
                finally:
                    import shutil
                    shutil.rmtree(directory, ignore_errors=True)
        run_arelle.assert_not_called()

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_product_generated_report_fixture_passes_arelle(self):
        with (FIXTURES / "product-generated-pass.xbrl.json").open(encoding="utf-8") as source:
            value = json.load(source)
        result = arelle_worker.handle(self.request(value), self.args)
        self.assertEqual("success", result["status"], result["error"])
        self.assertTrue(result["result"]["completed"])
        self.assertFalse(any(item["isBlocking"] for item in result["diagnostics"]))

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_arelle_fact_value_diagnostic_returns_the_exact_report_fact_locator(self):
        locator = "f" + "c" * 64
        value = report("synthetic:Revenue")
        fact = value["facts"].pop("f1")
        value["facts"][locator] = fact
        fact["value"] = "not-a-number"
        fact["dimensions"]["period"] = "2025-01-01T00:00:00/2026-01-01T00:00:00"

        result = arelle_worker.handle(self.request(value), self.args)

        self.assertEqual("success", result["status"], result["error"])
        value_finding = next(item for item in result["diagnostics"]
                             if item["ruleIdentifier"] == "xmlSchema:valueError")
        self.assertEqual(locator, value_finding["factLocator"])

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_product_generated_xml_fixture_passes_arelle(self):
        import shutil

        with tempfile.TemporaryDirectory(prefix="xms-arelle-xml-fixture-") as temporary:
            workspace = Path(temporary)
            report_path = workspace / "product-generated-pass.xbrl.xml"
            shutil.copyfile(FIXTURES / "product-generated-pass.xbrl.xml", report_path)
            shutil.copyfile(TAXONOMY_ROOT / "arelle-test-taxonomy.xsd",
                            workspace / "arelle-test-taxonomy.xsd")

            ok, logs, concept_count, _loaded_plugins = arelle_worker.run_arelle(report_path, self.args)

        self.assertTrue(ok, logs)
        self.assertGreater(concept_count, 0)
        self.assertFalse(any(item.get("levelname", "").lower() == "error" for item in logs))

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_product_json_and_xml_fixtures_have_identical_arelle_occurrences(self):
        import shutil

        with tempfile.TemporaryDirectory(prefix="xms-arelle-cross-format-") as temporary:
            workspace = Path(temporary)
            json_value = json.loads((FIXTURES / "product-generated-pass.xbrl.json").read_text(encoding="utf-8"))
            json_value = arelle_worker.safe_taxonomy_urls(json_value, str(TAXONOMY_ROOT))
            json_path = workspace / "product-generated-pass.xbrl.json"
            json_path.write_text(json.dumps(json_value, ensure_ascii=False), encoding="utf-8")

            xml_path = workspace / "product-generated-pass.xbrl.xml"
            shutil.copyfile(FIXTURES / "product-generated-pass.xbrl.xml", xml_path)
            shutil.copyfile(TAXONOMY_ROOT / "arelle-test-taxonomy.xsd",
                            workspace / "arelle-test-taxonomy.xsd")

            def occurrences(report_path):
                options = arelle_worker.RuntimeOptions(
                    entrypointFile=str(report_path), validate=True, workOffline=True,
                    internetConnectivity="offline", keepOpen=True, disablePersistentConfig=True,
                    logFile="logToStructuredMessage", logPropagate=False,
                )
                with arelle_worker.Session() as session:
                    ok = session.run(options)
                    logs = session.get_log_messages()
                    self.assertTrue(ok, logs)
                    facts = [fact for model in session.get_models() for fact in model.facts]
                    self.assertEqual(2, len(facts), logs)

                    def qname_key(qname):
                        return qname.namespaceURI, qname.localName

                    def fact_key(fact):
                        context = fact.context
                        if context.isInstantPeriod:
                            period = ("instant", context.instantDatetime.isoformat())
                        else:
                            period = ("duration", context.startDatetime.isoformat(),
                                      context.endDatetime.isoformat())
                        unit = None if fact.unit is None else tuple(
                            tuple(sorted(qname_key(measure) for measure in side))
                            for side in fact.unit.measures
                        )
                        dimensions = tuple(sorted(
                            (qname_key(name), qname_key(value.memberQname))
                            for name, value in context.qnameDims.items()
                        ))
                        return (
                            qname_key(fact.qname), context.entityIdentifier, period, unit,
                            fact.value, None if fact.decimals is None else str(fact.decimals),
                            bool(fact.isNil), fact.xmlLang, dimensions,
                        )

                    return sorted((fact_key(fact) for fact in facts), key=repr)

            json_occurrences = occurrences(json_path)
            xml_occurrences = occurrences(xml_path)

        self.assertEqual(json_occurrences, xml_occurrences)
        self.assertEqual([
            (("urn:synthetic:finance:1.0", "EntityRegistrantName"),
             ("http://standards.iso.org/iso/17442", "00EHHQ2ZHDCFXJCPCL49"),
             ("duration", "2025-01-01T00:00:00", "2026-01-01T00:00:00"), None,
             "ABC INC", None, False, "en", ()),
            (("urn:synthetic:finance:1.0", "Revenue"),
             ("http://standards.iso.org/iso/17442", "00EHHQ2ZHDCFXJCPCL49"),
             ("duration", "2025-01-01T00:00:00", "2026-01-01T00:00:00"),
             ((("http://www.xbrl.org/2003/iso4217", "USD"),), ()),
             "123.45", "2", False, None, ()),
        ], json_occurrences)

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_file_taxonomy_uri_passes_arelle(self):
        value = report()
        value["documentInfo"]["taxonomy"] = [(TAXONOMY_ROOT / "arelle-test-taxonomy.xsd").as_uri()]
        result = arelle_worker.handle(self.request(value), self.args)
        self.assertEqual("success", result["status"], result["error"])
        self.assertTrue(result["result"]["completed"])
        self.assertFalse(any(item["isBlocking"] for item in result["diagnostics"]))

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_unknown_concept_is_blocking_even_if_arelle_completes(self):
        result = arelle_worker.handle(self.request(report("synthetic:Missing")), self.args)
        self.assertEqual("success", result["status"])
        self.assertTrue(any(item["isBlocking"] and item["code"] == "XBR009" for item in result["diagnostics"]))

    def test_taxonomy_path_cannot_escape_root(self):
        with self.assertRaises(ValueError):
            arelle_worker.safe_taxonomy_urls(
                {"documentInfo": {"taxonomy": ["../../../README.md"]}},
                str(TAXONOMY_ROOT),
            )

    def test_external_taxonomy_uri_requires_exact_offline_mapping(self):
        value = {"documentInfo": {"taxonomy": ["https://example.invalid/taxonomy.xsd"]}}
        with self.assertRaisesRegex(ValueError, "no approved offline package mapping"):
            arelle_worker.safe_taxonomy_urls(value, str(TAXONOMY_ROOT))

    def test_external_taxonomy_uri_rejects_query_and_fragment_even_with_mapping(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            references = {
                "https://example.invalid/taxonomy.xsd?version=1",
                "https://example.invalid/taxonomy.xsd#schema",
            }
            for reference in references:
                with self.subTest(reference=reference), self.assertRaisesRegex(
                        ValueError, "cannot include a query or fragment"):
                    arelle_worker.safe_taxonomy_urls(
                        {"documentInfo": {"taxonomy": [reference]}}, str(root),
                        {"https://example.invalid/taxonomy.xsd": "entry.xsd"},
                        {"entry.xsd": "sha256:fixture"})

    def test_external_taxonomy_uri_maps_only_to_local_package_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "taxonomy").mkdir()
            schema = root / "taxonomy" / "entry.xsd"
            schema.write_text("<schema/>", encoding="utf-8")
            value = {"documentInfo": {"taxonomy": ["https://example.invalid/taxonomy.xsd"]}}

            result = arelle_worker.safe_taxonomy_urls(
                value, str(root),
                {"https://example.invalid/taxonomy.xsd": "taxonomy/entry.xsd"},
                {"taxonomy/entry.xsd": "sha256:fixture"},
            )

            expected = str(schema.resolve()) if sys.platform == "win32" else schema.resolve().as_uri()
            self.assertEqual(expected, result["documentInfo"]["taxonomy"][0])

    def test_profile_manifest_rejects_tampered_assets(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            profile = root / "fixture-profile" / "1.0.0"
            profile.mkdir(parents=True)
            entry = profile / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            digest = hashlib.sha256(entry.read_bytes()).hexdigest()
            manifest = {
                "schemaVersion": 2,
                "profileId": "fixture-profile",
                "version": "1.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "packageFiles": {"entry.xsd": "sha256:" + digest},
                "offlineReferences": {"https://example.invalid/taxonomy.xsd": "entry.xsd"},
            }
            path = profile / "profile.json"
            path.write_text(json.dumps(manifest), encoding="utf-8")
            entry.write_text("<schema tampered='true'/>", encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                arelle_worker.load_profile_manifest(path, profile)

    def test_profile_manifest_rejects_offline_reference_queries_and_fragments(self):
        for suffix in ("?version=1", "?", "#schema-fragment", "#"):
            with self.subTest(suffix=suffix), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                entry = root / "entry.xsd"
                entry.write_text("<schema/>", encoding="utf-8")
                digest = hashlib.sha256(entry.read_bytes()).hexdigest()
                manifest = {
                    "schemaVersion": 2,
                    "profileId": "fixture-profile",
                    "version": "1.0.0",
                    "profileDigest": "sha256:fixture",
                    "entryPoint": "entry.xsd",
                    "packageFiles": {"entry.xsd": "sha256:" + digest},
                    "offlineReferences": {
                        "https://example.invalid/taxonomy.xsd" + suffix: "entry.xsd"},
                }
                path = root / "profile.json"
                path.write_text(json.dumps(manifest), encoding="utf-8")

                with self.assertRaisesRegex(ValueError, r"canonical HTTP\(S\) file URIs"):
                    arelle_worker.load_profile_manifest(path, root)

    def test_profile_manifest_rejects_unsupported_schema_version(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest_path = root / "profile.json"
            manifest_path.write_text(json.dumps({"schemaVersion": 1}), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "unsupported schema version"):
                arelle_worker.load_profile_manifest(manifest_path, root)

    def test_profile_manifest_required_check_limits_match_application_catalog(self):
        cases = [
            (["rule:" + "😀" * 507], None),  # 512 Unicode code points, despite 1014 UTF-16 code units.
            (["x" * 513], "required check IDs"),
            (["rule:bad\u0085id"], "required check IDs"),
            ([f"rule:{index}" for index in range(2049)], "required check IDs"),
        ]
        for required_checks, expected_error in cases:
            with self.subTest(required_check_count=len(required_checks), expected_error=expected_error), \
                    tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                entry = root / "entry.xsd"
                entry.write_text("<schema/>", encoding="utf-8")
                digest = hashlib.sha256(entry.read_bytes()).hexdigest()
                manifest = {
                    "schemaVersion": 2,
                    "profileId": "fixture-profile",
                    "version": "1.0.0",
                    "profileDigest": "sha256:fixture",
                    "entryPoint": "entry.xsd",
                    "packageFiles": {"entry.xsd": "sha256:" + digest},
                    "offlineReferences": {},
                    "requiredChecks": required_checks,
                }
                path = root / "profile.json"
                path.write_text(json.dumps(manifest), encoding="utf-8")

                if expected_error is None:
                    loaded = arelle_worker.load_profile_manifest(path, root)
                    self.assertEqual(required_checks, loaded["requiredChecks"])
                else:
                    with self.assertRaisesRegex(ValueError, expected_error):
                        arelle_worker.load_profile_manifest(path, root)

    def test_profile_manifest_must_match_application_verified_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            digest = hashlib.sha256(entry.read_bytes()).hexdigest()
            manifest = {
                "schemaVersion": 2,
                "profileId": "fixture-profile",
                "version": "1.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "packageFiles": {"entry.xsd": "sha256:" + digest},
                "offlineReferences": {},
            }
            path = root / "profile.json"
            original = json.dumps(manifest).encode("utf-8")
            path.write_bytes(original)
            expected = "sha256:" + hashlib.sha256(original).hexdigest()

            arelle_worker.load_profile_manifest(path, root, expected)
            path.write_bytes(original + b" ")

            with self.assertRaisesRegex(ValueError, "changed after the application verified it"):
                arelle_worker.load_profile_manifest(path, root, expected)

    def test_profile_manifest_accepts_schema_v3_dimension_domains(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            digest = hashlib.sha256(entry.read_bytes()).hexdigest()
            manifest = {
                "schemaVersion": 3,
                "profileId": "fixture-profile",
                "version": "1.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "packageFiles": {"entry.xsd": "sha256:" + digest},
                "offlineReferences": {},
                "concepts": [
                    {"qualifiedName": "tax:Axis"},
                    {"qualifiedName": "tax:Member"},
                ],
                "dimensionDomains": {"tax:Axis": ["tax:Member"]},
            }
            path = root / "profile.json"
            path.write_text(json.dumps(manifest), encoding="utf-8")

            loaded = arelle_worker.load_profile_manifest(path, root)

        self.assertEqual(3, loaded["schemaVersion"])
        self.assertEqual({"tax:Axis": ["tax:Member"]}, loaded["dimensionDomains"])

    def test_profile_manifest_accepts_schema_v4_presentation_relationships(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            digest = hashlib.sha256(entry.read_bytes()).hexdigest()
            manifest = {
                "schemaVersion": 4,
                "profileId": "fixture-profile",
                "version": "1.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "packageFiles": {"entry.xsd": "sha256:" + digest},
                "offlineReferences": {},
                "concepts": [
                    {"qualifiedName": "tax:Statements"},
                    {"qualifiedName": "tax:Revenue"},
                ],
                "presentationLinks": [
                    {
                        "roleUri": "https://example.invalid/roles/statement",
                        "parentQName": "tax:Statements",
                        "childQName": "tax:Revenue",
                        "order": 1.25,
                    }
                ],
            }
            path = root / "profile.json"
            path.write_text(json.dumps(manifest), encoding="utf-8")

            loaded = arelle_worker.load_profile_manifest(path, root)

        self.assertEqual(4, loaded["schemaVersion"])
        self.assertEqual(manifest["presentationLinks"], loaded["presentationLinks"])

    def test_profile_manifest_accepts_schema_v5_calculation_and_definition_relationships(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            digest = hashlib.sha256(entry.read_bytes()).hexdigest()
            manifest = {
                "schemaVersion": 5,
                "profileId": "fixture-profile",
                "version": "1.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "packageFiles": {"entry.xsd": "sha256:" + digest},
                "offlineReferences": {},
                "concepts": [
                    {"qualifiedName": "tax:Total"},
                    {"qualifiedName": "tax:Member"},
                ],
                "calculationLinks": [
                    {
                        "roleUri": "https://example.invalid/roles/calculation",
                        "parentQName": "tax:Total",
                        "childQName": "tax:Member",
                        "weight": -1,
                        "order": 1.25,
                    }
                ],
                "definitionLinks": [
                    {
                        "roleUri": "https://example.invalid/roles/dimensions",
                        "arcRoleUri": "https://xbrl.org/int/dim/arcrole/domain-member",
                        "parentQName": "tax:Total",
                        "childQName": "tax:Member",
                        "usable": False,
                        "contextElement": "segment",
                    }
                ],
            }
            path = root / "profile.json"
            path.write_text(json.dumps(manifest), encoding="utf-8")

            loaded = arelle_worker.load_profile_manifest(path, root)

        self.assertEqual(5, loaded["schemaVersion"])
        self.assertEqual(manifest["calculationLinks"], loaded["calculationLinks"])
        self.assertEqual(manifest["definitionLinks"], loaded["definitionLinks"])

    def test_profile_manifest_rejects_unsupported_calculation_and_definition_links(self):
        calculation = {
            "roleUri": "https://example.invalid/roles/calculation",
            "parentQName": "tax:Total",
            "childQName": "tax:Member",
            "weight": 1,
        }
        definition = {
            "roleUri": "https://example.invalid/roles/dimensions",
            "arcRoleUri": "https://xbrl.org/int/dim/arcrole/domain-member",
            "parentQName": "tax:Total",
            "childQName": "tax:Member",
        }
        cases = [
            (4, [calculation], [], "require manifest schema version 5"),
            (5, [{**calculation, "weight": 0.5}], [], "weight"),
            (5, [calculation], [{**definition, "contextElement": "body"}], "supported attributes"),
            (5, [calculation, calculation], [], "duplicate arcs"),
        ]
        for schema_version, calculation_links, definition_links, expected_error in cases:
            with self.subTest(schema_version=schema_version, expected_error=expected_error), \
                    tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                entry = root / "entry.xsd"
                entry.write_text("<schema/>", encoding="utf-8")
                digest = hashlib.sha256(entry.read_bytes()).hexdigest()
                manifest = {
                    "schemaVersion": schema_version,
                    "profileId": "fixture-profile",
                    "version": "1.0.0",
                    "profileDigest": "sha256:fixture",
                    "entryPoint": "entry.xsd",
                    "packageFiles": {"entry.xsd": "sha256:" + digest},
                    "offlineReferences": {},
                    "concepts": [
                        {"qualifiedName": "tax:Total"},
                        {"qualifiedName": "tax:Member"},
                    ],
                    "calculationLinks": calculation_links,
                    "definitionLinks": definition_links,
                }
                path = root / "profile.json"
                path.write_text(json.dumps(manifest), encoding="utf-8")

                with self.assertRaisesRegex(ValueError, expected_error):
                    arelle_worker.load_profile_manifest(path, root)

    def test_profile_manifest_rejects_malformed_presentation_relationships(self):
        valid = {
            "roleUri": "https://example.invalid/roles/statement",
            "parentQName": "tax:Statements",
            "childQName": "tax:Revenue",
            "order": 1,
        }
        cases = [
            (3, [valid], "require manifest schema version 4"),
            (4, [{**valid, "roleUri": "relative/role"}], "absolute role URI"),
            (4, [{**valid, "roleUri": "https://example.invalid/\u009frole"}], "absolute role URI"),
            (4, [{**valid, "parentQName": "tax:Missing"}], "absolute role URI"),
            (4, [{**valid, "order": "first"}], "numeric order"),
            (4, [valid, valid], "duplicate arcs"),
        ]
        for schema_version, presentation_links, expected_error in cases:
            with self.subTest(schema_version=schema_version, expected_error=expected_error), \
                    tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                entry = root / "entry.xsd"
                entry.write_text("<schema/>", encoding="utf-8")
                digest = hashlib.sha256(entry.read_bytes()).hexdigest()
                manifest = {
                    "schemaVersion": schema_version,
                    "profileId": "fixture-profile",
                    "version": "1.0.0",
                    "profileDigest": "sha256:fixture",
                    "entryPoint": "entry.xsd",
                    "packageFiles": {"entry.xsd": "sha256:" + digest},
                    "offlineReferences": {},
                    "concepts": [
                        {"qualifiedName": "tax:Statements"},
                        {"qualifiedName": "tax:Revenue"},
                    ],
                    "presentationLinks": presentation_links,
                }
                path = root / "profile.json"
                path.write_text(json.dumps(manifest), encoding="utf-8")

                with self.assertRaisesRegex(ValueError, expected_error):
                    arelle_worker.load_profile_manifest(path, root)

    def test_profile_manifest_validates_v3_dimension_domain_shape(self):
        invalid_cases = [
            (3, ["tax:Axis"], "dimensionDomains must be an object"),
            (2, {"tax:Axis": ["tax:Member"]}, "require manifest schema version 3"),
            (3, {"tax:Unknown": ["tax:Member"]}, "must name indexed taxonomy concepts"),
            (3, {"tax:Axis": []}, "unique, non-empty member arrays"),
            (3, {"tax:Axis": ["tax:Member", "tax:Member"]}, "unique, non-empty member arrays"),
            (3, {"tax:Axis": ["tax:Missing"]}, "unindexed taxonomy members"),
        ]
        for schema_version, dimension_domains, expected_error in invalid_cases:
            with self.subTest(schema_version=schema_version, dimension_domains=dimension_domains), \
                    tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                entry = root / "entry.xsd"
                entry.write_text("<schema/>", encoding="utf-8")
                digest = hashlib.sha256(entry.read_bytes()).hexdigest()
                manifest = {
                    "schemaVersion": schema_version,
                    "profileId": "fixture-profile",
                    "version": "1.0.0",
                    "profileDigest": "sha256:fixture",
                    "entryPoint": "entry.xsd",
                    "packageFiles": {"entry.xsd": "sha256:" + digest},
                    "offlineReferences": {},
                    "concepts": [
                        {"qualifiedName": "tax:Axis"},
                        {"qualifiedName": "tax:Member"},
                    ],
                    "dimensionDomains": dimension_domains,
                }
                path = root / "profile.json"
                path.write_text(json.dumps(manifest), encoding="utf-8")

                with self.assertRaisesRegex(ValueError, expected_error):
                    arelle_worker.load_profile_manifest(path, root)

    def test_profile_package_symlinks_are_rejected_when_supported(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            target = root / "target.xsd"
            link = root / "link.xsd"
            target.write_text("<schema/>", encoding="utf-8")
            try:
                link.symlink_to(target)
            except (OSError, NotImplementedError) as ex:
                self.skipTest("Symbolic links are unavailable in this environment: " + str(ex))

            with self.assertRaisesRegex(ValueError, "symbolic links"):
                arelle_worker.safe_package_path("link.xsd", root)


if __name__ == "__main__":
    unittest.main()
