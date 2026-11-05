import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

WORKER_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKER_DIR))
import arelle_worker  # noqa: E402


FIXTURES = Path(__file__).resolve().parents[3] / "tests" / "XBRLMappingStudio.XBRL.Tests" / "fixtures"
TAXONOMY_ROOT = WORKER_DIR / "taxonomy"


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
        return {
            "operation": "validate-report",
            "operationId": "test-op",
            "jobId": "test-job",
            "profileDigest": "test",
            "deadlineUtc": "2999-01-01T00:00:00Z",
            "payload": json.dumps(value),
        }

    def handshake_request(self):
        return {
            "operation": "handshake",
            "operationId": "test-handshake",
            "jobId": "test-job",
            "profileDigest": "-",
            "deadlineUtc": "2999-01-01T00:00:00Z",
            "payload": {"minimumProtocolVersion": 1, "maximumProtocolVersion": 1},
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
                    "payload": {"minimumProtocolVersion": 1, "maximumProtocolVersion": 1},
                },
                self.args,
            )
        self.assertEqual("success", result["status"])
        self.assertIn("validate-report", result["result"]["capabilities"])

    def test_handshake_reports_exact_profile_entrypoint_and_plugins(self):
        self.args.profile_manifest_data = {
            "schemaVersion": 2,
            "profileId": "fixture-profile",
            "version": "2.0.0",
            "profileDigest": "sha256:fixture",
            "entryPoint": "taxonomy/entry.xsd",
            "requiredPlugins": ["validate/EFM"],
        }
        self.args.plugin = ["validate/EFM"]
        with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True):
            result = arelle_worker.handle(self.handshake_request(), self.args)

        self.assertEqual("success", result["status"])
        self.assertEqual("fixture-profile", result["result"]["profileId"])
        self.assertEqual("2.0.0", result["result"]["profileVersion"])
        self.assertEqual("taxonomy/entry.xsd", result["result"]["taxonomyEntryPoint"])
        self.assertEqual(["validate/EFM"], result["result"]["requiredPlugins"])

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

    def test_load_taxonomy_returns_versioned_profile_capabilities(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            self.args.taxonomy_root = str(root)
            self.args.plugin = ["validate/EFM"]
            self.args.profile_manifest_data = {
                "schemaVersion": 2,
                "profileId": "fixture-profile",
                "version": "2.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "requiredPlugins": ["validate/EFM"],
                "packageFiles": {"entry.xsd": "sha256:entry"},
            }
            request = self.handshake_request()
            request["operation"] = "load-taxonomy"
            request["profileDigest"] = "sha256:fixture"
            request["payload"] = {"taxonomyPath": "entry.xsd"}

            with mock.patch.object(arelle_worker, "ARELLE_AVAILABLE", True), \
                    mock.patch.object(arelle_worker, "run_arelle", return_value=(True, [], 42)):
                result = arelle_worker.handle(request, self.args)

        self.assertEqual("success", result["status"])
        self.assertTrue(result["result"]["completed"])
        self.assertEqual(2, result["result"]["profileSchemaVersion"])
        self.assertEqual("fixture-profile", result["result"]["profileId"])
        self.assertEqual("2.0.0", result["result"]["profileVersion"])
        self.assertEqual("sha256:fixture", result["result"]["profileDigest"])
        self.assertEqual("entry.xsd", result["result"]["taxonomyEntryPoint"])
        self.assertEqual(["validate/EFM"], result["result"]["requiredPlugins"])
        self.assertIn("validate-report", result["result"]["capabilities"])
        self.assertEqual(42, result["result"]["conceptCount"])

    def test_load_taxonomy_reports_configured_but_unavailable_required_plugin(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "entry.xsd").write_text("<schema/>", encoding="utf-8")
            self.args.taxonomy_root = str(root)
            self.args.plugin = ["validate/EFM"]
            self.args.profile_manifest_data = {
                "schemaVersion": 2,
                "profileId": "fixture-profile",
                "version": "2.0.0",
                "profileDigest": "sha256:fixture",
                "entryPoint": "entry.xsd",
                "requiredPlugins": ["validate/EFM"],
                "packageFiles": {"entry.xsd": "sha256:entry"},
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
                    mock.patch.object(arelle_worker, "run_arelle", return_value=(False, logs, 0)):
                result = arelle_worker.handle(request, self.args)

        self.assertEqual("incompleteValidation", result["status"])
        self.assertTrue(any(item["code"] == "XBR010" for item in result["diagnostics"]))
        self.assertIn("required Arelle plugin is unavailable", result["error"])

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

    @unittest.skipUnless(arelle_worker.ARELLE_AVAILABLE,
                         "Arelle integration runtime is not installed; pinned CI installs it.")
    def test_local_taxonomy_report_passes_arelle(self):
        result = arelle_worker.handle(self.request(report()), self.args)
        self.assertEqual("success", result["status"], result["error"])
        self.assertTrue(result["result"]["completed"])
        self.assertFalse(any(item["isBlocking"] for item in result["diagnostics"]))

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
        }
        observed = {}

        def fake_arelle(entrypoint, args, temporary_path=None):
            observed["path"] = Path(entrypoint)
            observed["temporary_path"] = temporary_path
            observed["report"] = json.loads(Path(entrypoint).read_text(encoding="utf-8"))
            return True, [], 1

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
                    }
                    result = arelle_worker.handle(request, self.args)
                    self.assertEqual("domainFailure", result["status"])
        finally:
            import shutil
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
                mock.patch.object(arelle_worker, "run_arelle", return_value=(True, [], 1)) as run_arelle:
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

    def test_profile_manifest_rejects_unsupported_schema_version(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest_path = root / "profile.json"
            manifest_path.write_text(json.dumps({"schemaVersion": 1}), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "unsupported schema version"):
                arelle_worker.load_profile_manifest(manifest_path, root)

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
