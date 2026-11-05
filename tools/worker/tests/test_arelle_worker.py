import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

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
            taxonomy_root=str(TAXONOMY_ROOT), package=[], plugin=[], disclosure_system=None
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

    def test_handshake_negotiates_supported_protocol(self):
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

    def test_local_taxonomy_report_passes_arelle(self):
        result = arelle_worker.handle(self.request(report()), self.args)
        self.assertEqual("success", result["status"], result["error"])
        self.assertTrue(result["result"]["completed"])
        self.assertFalse(any(item["isBlocking"] for item in result["diagnostics"]))

    def test_product_generated_report_fixture_passes_arelle(self):
        with (FIXTURES / "product-generated-pass.xbrl.json").open(encoding="utf-8") as source:
            value = json.load(source)
        result = arelle_worker.handle(self.request(value), self.args)
        self.assertEqual("success", result["status"], result["error"])
        self.assertTrue(result["result"]["completed"])
        self.assertFalse(any(item["isBlocking"] for item in result["diagnostics"]))

    def test_file_taxonomy_uri_passes_arelle(self):
        value = report()
        value["documentInfo"]["taxonomy"] = [(TAXONOMY_ROOT / "arelle-test-taxonomy.xsd").as_uri()]
        result = arelle_worker.handle(self.request(value), self.args)
        self.assertEqual("success", result["status"], result["error"])
        self.assertTrue(result["result"]["completed"])
        self.assertFalse(any(item["isBlocking"] for item in result["diagnostics"]))

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

    def test_profile_manifest_must_match_application_verified_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            entry = root / "entry.xsd"
            entry.write_text("<schema/>", encoding="utf-8")
            digest = hashlib.sha256(entry.read_bytes()).hexdigest()
            manifest = {
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
