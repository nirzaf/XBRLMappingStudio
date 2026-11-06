import io
import json
import sqlite3
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import performance.benchmark_harness as harness


class PerformanceHarnessTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def test_fixture_is_repeatable_and_has_expected_rows(self) -> None:
        first_path = self.root / "first.db"
        second_path = self.root / "second.db"

        first = harness.create_sqlite_fixture(first_path, rows=13)
        second = harness.create_sqlite_fixture(second_path, rows=13)

        self.assertEqual(first["sha256"], second["sha256"])
        connection = sqlite3.connect(first_path)
        try:
            self.assertEqual(13, connection.execute("SELECT COUNT(*) FROM financials").fetchone()[0])
            self.assertEqual(
                ("Revenue", "1251", "1101"),
                connection.execute("SELECT Line, Current, Prior FROM financials WHERE Id=1").fetchone(),
            )
        finally:
            connection.close()

    def test_fixture_refuses_to_overwrite_existing_path(self) -> None:
        path = self.root / "existing.db"
        path.write_bytes(b"preserve this")

        with self.assertRaises(FileExistsError):
            harness.create_sqlite_fixture(path, rows=1)

        self.assertEqual(b"preserve this", path.read_bytes())

    def test_clock_calibration_reports_a_monotonic_high_resolution_clock(self) -> None:
        clock = harness._clock_record()

        self.assertTrue(clock["monotonic"])
        self.assertGreater(clock["resolution_seconds"], 0)
        self.assertIsNotNone(clock["observed_minimum_tick_seconds"])
        self.assertGreater(clock["observed_minimum_tick_seconds"], 0)

    def test_process_tree_record_includes_child_and_obeys_schema(self) -> None:
        record = harness.run_process(
            [sys.executable, "-c", "import subprocess,sys; subprocess.run([sys.executable,'-c','import time;time.sleep(0.35)'])"],
            scenario="test-process-tree",
            command_id="python-parent-with-child-v1",
            repo_root=self.root,
            build_configuration="self-test",
            artifact_path=None,
            fixture_path=None,
            sample_interval_seconds=0.05,
            timeout_seconds=5,
        )

        harness.validate_record(record)
        self.assertEqual(0, record["execution"]["exit_code"])
        self.assertGreaterEqual(record["summary"]["process_count_peak"], 2)
        self.assertGreater(record["summary"]["complete_sample_count"], 0)
        self.assertFalse(record["measurement_policy"]["command_arguments_recorded"])
        self.assertNotIn("time.sleep", json.dumps(record))

    def test_schema_validation_rejects_extra_properties_and_missing_fields(self) -> None:
        record = harness.run_process(
            [sys.executable, "-c", "pass"],
            scenario="test-schema",
            command_id="python-noop-v1",
            repo_root=self.root,
            build_configuration="self-test",
            artifact_path=None,
            fixture_path=None,
            sample_interval_seconds=0.05,
            timeout_seconds=5,
        )

        record["untracked"] = True
        with self.assertRaisesRegex(ValueError, "unknown fields"):
            harness.validate_record(record)

        del record["untracked"]
        del record["clock"]["resolution_seconds"]
        with self.assertRaisesRegex(ValueError, "missing fields"):
            harness.validate_record(record)

    def test_output_is_created_without_overwriting_existing_file(self) -> None:
        path = self.root / "record.json"
        harness.write_new_json(path, {"safe": True})
        before = path.read_bytes()

        with self.assertRaises(FileExistsError):
            harness.write_new_json(path, {"safe": False})

        self.assertEqual(before, path.read_bytes())
        self.assertEqual({"safe": True}, json.loads(path.read_text(encoding="utf-8")))

    def test_run_command_writes_a_valid_raw_record_without_arguments(self) -> None:
        output = self.root / "run.json"
        captured = io.StringIO()
        with redirect_stdout(captured):
            exit_code = harness.main([
                "run",
                "--scenario", "test-cli-run",
                "--command-id", "python-cli-smoke-v1",
                "--repo-root", str(self.root),
                "--build-configuration", "Release",
                "--sample-interval", "0.05",
                "--timeout", "5",
                "--output", str(output),
                "--", sys.executable, "-c", "print('not persisted')",
            ])

        self.assertEqual(0, exit_code)
        record = json.loads(output.read_text(encoding="utf-8"))
        harness.validate_record(record)
        self.assertNotIn("not persisted", output.read_text(encoding="utf-8"))
        self.assertIn('"threshold_pass_claimed": false', captured.getvalue())

    def test_timeout_terminates_the_launched_process_tree(self) -> None:
        # Leave enough time for Windows to start the parent and its child under
        # load; this test measures tree cleanup, not interpreter startup latency.
        record = harness.run_process(
            [
                sys.executable,
                "-c",
                "import subprocess,sys,time; subprocess.Popen([sys.executable,'-c','import time;time.sleep(30)']); time.sleep(30)",
            ],
            scenario="test-timeout-tree",
            command_id="python-parent-and-child-v1",
            repo_root=self.root,
            build_configuration="self-test",
            artifact_path=None,
            fixture_path=None,
            sample_interval_seconds=0.05,
            timeout_seconds=2.0,
        )

        harness.validate_record(record)
        self.assertTrue(record["execution"]["timed_out"])
        self.assertGreaterEqual(record["summary"]["process_count_peak"], 2)
        self.assertEqual(0, record["samples"][-1]["process_count"])


if __name__ == "__main__":
    unittest.main()
