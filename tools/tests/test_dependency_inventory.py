import base64
import hashlib
import json
import sys
import tempfile
import unittest
from email.message import Message
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import dependency_inventory as inventory


class InstalledDistribution:
    def __init__(
        self,
        version: str,
        license_expression: str = "",
        legacy_license: str = "",
        license_file: Path | None = None,
    ) -> None:
        self.version = version
        self.metadata = Message()
        self.metadata["Name"] = "worker-root"
        if license_expression:
            self.metadata["License-Expression"] = license_expression
        if legacy_license:
            self.metadata["License"] = legacy_license
        self.files = [Path("worker-root.dist-info/licenses/LICENSE.txt")] if license_file else []
        self.license_file = license_file

    def locate_file(self, _: Path) -> Path:
        assert self.license_file is not None
        return self.license_file


class DependencyInventoryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.cache = self.root / "nuget-cache"
        self.cache.mkdir()
        self.worker_lock = Path("tools/worker/requirements.lock")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _write_lock(self, relative_path: str, graph: dict) -> None:
        path = self.root / relative_path
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps({"version": 1, "dependencies": {"net10.0/win-x64": graph}}), encoding="utf-8")

    def _write_nuspec(self, name: str, version: str, license_xml: str) -> Path:
        package_dir = self.cache / name.lower() / version
        package_dir.mkdir(parents=True)
        nuspec = package_dir / f"{name}.nuspec"
        nuspec.write_text(
            f"<package><metadata><id>{name}</id><version>{version}</version>{license_xml}</metadata></package>",
            encoding="utf-8",
        )
        return package_dir

    def _write_worker_lock(self, content: str) -> None:
        path = self.root / self.worker_lock
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def test_spdx_captures_locked_graph_hashes_licenses_and_relationships(self) -> None:
        package_hash = base64.b64encode(bytes(range(64))).decode("ascii")
        graph = {
            "Example.Root": {
                "type": "Direct",
                "resolved": "1.0.0",
                "contentHash": package_hash,
                "dependencies": {"Example.Child": "[2.0.0, 3.0.0)"},
            },
            "Example.Child": {
                "type": "Transitive",
                "resolved": "2.1.0",
                "contentHash": package_hash,
            },
        }
        self._write_lock("src/XBRLMappingStudio.CLI/packages.lock.json", graph)
        self._write_lock("src/XBRLMappingStudio.Desktop/packages.Release.lock.json", graph)
        root_dir = self._write_nuspec("Example.Root", "1.0.0", '<license type="expression">MIT</license>')
        child_dir = self._write_nuspec("Example.Child", "2.1.0", '<license type="file">LICENSE.txt</license>')
        license_text = "Example license text\n"
        (child_dir / "LICENSE.txt").write_text(license_text, encoding="utf-8")
        del root_dir

        root_hash = "a" * 64
        child_hash = "b" * 64
        python_license_file = self.root / "python-license.txt"
        python_license_file.write_text("Python worker license text\n", encoding="utf-8")
        self._write_worker_lock(
            "worker-root==1.0.0 \\\n"
            f"    --hash=sha256:{root_hash}\n"
            "    # via -r tools/worker/requirements.in\n"
            "worker-child==2.0.0 \\\n"
            f"    --hash=sha256:{child_hash}\n"
            "    # via worker-root\n"
        )
        document = inventory.build_spdx_document(
            self.root,
            self.cache,
            self.worker_lock,
            {
                "worker-root": InstalledDistribution("1.0.0", "Apache-2.0"),
                "worker-child": InstalledDistribution(
                    "2.0.0", legacy_license="MIT License", license_file=python_license_file
                ),
            },
        )
        packages = {package["name"]: package for package in document["packages"]}

        self.assertEqual("SPDX-2.3", document["spdxVersion"])
        self.assertEqual("MIT", packages["Example.Root"]["licenseDeclared"])
        self.assertTrue(packages["Example.Child"]["licenseDeclared"].startswith("LicenseRef-XMS-"))
        self.assertIn(
            hashlib.sha256((child_dir / "LICENSE.txt").read_bytes()).hexdigest(),
            packages["Example.Child"]["licenseComments"],
        )
        extracted = {row["licenseId"]: row for row in document["hasExtractedLicensingInfo"]}
        self.assertEqual(
            (child_dir / "LICENSE.txt").read_bytes().decode("utf-8-sig"),
            extracted[packages["Example.Child"]["licenseDeclared"]]["extractedText"],
        )
        self.assertEqual(bytes(range(64)).hex(), packages["Example.Root"]["checksums"][0]["checksumValue"])
        self.assertEqual("Apache-2.0", packages["worker-root"]["licenseDeclared"])
        self.assertTrue(packages["worker-child"]["licenseDeclared"].startswith("LicenseRef-XMS-"))
        self.assertIn(root_hash, packages["worker-root"]["comment"])

        relationship_pairs = {
            (row["spdxElementId"], row["relationshipType"], row["relatedSpdxElement"])
            for row in document["relationships"]
        }
        self.assertIn(
            (packages["Example.Root"]["SPDXID"], "DEPENDS_ON", packages["Example.Child"]["SPDXID"]),
            relationship_pairs,
        )
        self.assertIn(
            (packages["worker-root"]["SPDXID"], "DEPENDS_ON", packages["worker-child"]["SPDXID"]),
            relationship_pairs,
        )
        self.assertEqual(
            python_license_file.read_bytes().decode("utf-8-sig"),
            extracted[packages["worker-child"]["licenseDeclared"]]["extractedText"],
        )

    def test_legacy_license_url_does_not_become_an_assertion(self) -> None:
        package_dir = self._write_nuspec(
            "Legacy.Package", "1.0.0", '<licenseUrl>https://licenses.example.test/Unknown</licenseUrl>'
        )
        (package_dir / "LICENSE.txt").write_text("Legacy package license text\n", encoding="utf-8")
        declared, comment, extracted = inventory.read_nuget_license(self.cache, "Legacy.Package", "1.0.0")
        self.assertEqual("NOASSERTION", declared)
        self.assertIn("not dereferenced", comment)
        self.assertEqual(1, len(extracted))
        self.assertIn("Legacy package license text", extracted[0]["extractedText"])

    def test_nuspec_license_file_path_cannot_escape_package_directory(self) -> None:
        for name, value in (
            ("ParentTraversal.Package", "../outside.txt"),
            ("WindowsDrive.Package", "C:\\outside.txt"),
        ):
            with self.subTest(name=name):
                self._write_nuspec(name, "1.0.0", f'<license type="file">{value}</license>')
                declared, comment, extracted = inventory.read_nuget_license(self.cache, name, "1.0.0")
                self.assertEqual("NOASSERTION", declared)
                self.assertIn("unsafe license-file path", comment)
                self.assertEqual([], extracted)

    def test_license_capture_is_bounded_to_one_mebibyte(self) -> None:
        path = self.root / "oversize-license.txt"
        path.write_bytes(b"x" * (inventory.MAX_LICENSE_FILE_BYTES + 1))
        license_id, evidence, comment = inventory._capture_license_file(
            path, "Test", "Oversized.Package", "1.0.0", path.name
        )
        self.assertIsNone(license_id)
        self.assertIsNone(evidence)
        self.assertIn("exceeds the 1 MiB capture limit", comment)

    def test_license_capture_preserves_utf8_bom_in_extracted_text(self) -> None:
        path = self.root / "bom-license.txt"
        path.write_bytes(b"\xef\xbb\xbfLicense terms\n")
        license_id, evidence, _ = inventory._capture_license_file(
            path, "Test", "Bom.Package", "1.0.0", path.name
        )
        self.assertIsNotNone(license_id)
        self.assertIsNotNone(evidence)
        self.assertEqual("\ufeffLicense terms\n", evidence["extractedText"])

    def test_worker_lock_requires_hashes_for_every_package(self) -> None:
        self._write_worker_lock("unsafe-package==1.2.3\n")
        with self.assertRaisesRegex(ValueError, "no SHA-256 lock hashes"):
            inventory.read_worker_lock(self.root / self.worker_lock)

    def test_license_expression_is_only_asserted_when_spdx_syntax_is_valid(self) -> None:
        self.assertTrue(inventory.is_spdx_expression("MIT OR Apache-2.0"))
        self.assertTrue(inventory.is_spdx_expression("GPL-2.0-only WITH Classpath-exception-2.0"))
        self.assertFalse(inventory.is_spdx_expression("(MIT OR Apache-2.0) WITH Classpath-exception-2.0"))
        self.assertFalse(inventory.is_spdx_expression("Dual License"))
        self.assertFalse(inventory.is_spdx_expression("MIT License"))
        self.assertFalse(inventory.is_spdx_expression("MIT / Apache-2.0"))
        self.assertFalse(inventory.is_spdx_expression("MIT AND"))


if __name__ == "__main__":
    unittest.main()
