#!/usr/bin/env python3
"""Capture reproducible, explicitly scoped process-tree benchmark records.

The harness records measurements; it does not decide whether a product threshold
has passed. Run `self-test` to validate clock, process sampling, and output schema.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
import platform
import re
import signal
import sqlite3
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 1
RESULT_SCHEMA = Path(__file__).with_name("benchmark_result.schema.json")


@dataclass(frozen=True)
class ProcessSample:
    parent_pid: int
    working_set_bytes: int | None
    private_bytes: int | None
    cpu_seconds: float | None
    process_identity: int | None = None


@dataclass(frozen=True)
class TreeSample:
    elapsed_seconds: float
    process_count: int
    working_set_bytes: int | None
    private_bytes: int | None
    cpu_seconds: float | None
    complete: bool


class ProcessTreeSampler:
    """Sample the launched process and descendants using OS process metadata."""

    def __init__(self) -> None:
        # Keep identities for processes that were descendants in an earlier
        # sample. This captures children that outlive a parent after reparenting
        # without following a later process that reused the same PID.
        self._known_processes: dict[int, int | None] = {}

    def sample(self, root_pid: int, elapsed_seconds: float) -> TreeSample:
        if sys.platform == "win32":
            process_map = self._windows_processes(root_pid, self._known_processes)
        elif sys.platform.startswith("linux"):
            process_map = self._linux_processes(root_pid, self._known_processes)
        else:
            raise OSError("Process-tree sampling is supported on Windows and Linux.")

        roots: set[int] = set()
        candidates = set(self._known_processes) | {root_pid}
        for pid in candidates:
            current = process_map.get(pid)
            previous_identity = self._known_processes.get(pid)
            if current is not None and (
                pid not in self._known_processes
                or previous_identity is None
                or current.process_identity is None
                or current.process_identity == previous_identity
            ):
                roots.add(pid)
        pids = self._descendants_from_roots(process_map, roots)
        observed = [process_map[pid] for pid in pids if pid in process_map]
        self._known_processes = {pid: process_map[pid].process_identity for pid in pids if pid in process_map}
        complete = bool(observed) and all(
            item.working_set_bytes is not None
            and item.private_bytes is not None
            and item.cpu_seconds is not None
            for item in observed
        )
        working = [item.working_set_bytes for item in observed]
        private = [item.private_bytes for item in observed]
        cpu = [item.cpu_seconds for item in observed]
        return TreeSample(
            elapsed_seconds=elapsed_seconds,
            process_count=len(observed),
            working_set_bytes=sum(working) if complete and all(v is not None for v in working) else None,
            private_bytes=sum(private) if complete and all(v is not None for v in private) else None,
            cpu_seconds=sum(cpu) if complete and all(v is not None for v in cpu) else None,
            complete=complete,
        )

    @staticmethod
    def _descendants(processes: dict[int, ProcessSample], root_pid: int) -> set[int]:
        return ProcessTreeSampler._descendants_from_roots(processes, {root_pid})

    @staticmethod
    def _descendants_from_roots(processes: dict[int, ProcessSample], roots: set[int]) -> set[int]:
        result = set(roots)
        changed = True
        while changed:
            changed = False
            for pid, process in processes.items():
                if process.parent_pid in result and pid not in result:
                    result.add(pid)
                    changed = True
        return result

    @staticmethod
    def _linux_processes(root_pid: int, known_processes: dict[int, int | None]) -> dict[int, ProcessSample]:
        clock_ticks = os.sysconf("SC_CLK_TCK")
        page_size = os.sysconf("SC_PAGE_SIZE")
        process_details: dict[int, tuple[int, int, int, float, Path]] = {}
        for stat_path in Path("/proc").glob("[0-9]*/stat"):
            try:
                text = stat_path.read_text(encoding="utf-8")
                close_paren = text.rfind(")")
                fields = text[close_paren + 2 :].split()
                if not fields or fields[0] in {"Z", "X"}:
                    continue
                pid = int(stat_path.parent.name)
                parent_pid = int(fields[1])
                process_identity = int(fields[19])
                cpu_seconds = (int(fields[11]) + int(fields[12])) / clock_ticks
                resident_bytes = int(fields[21]) * page_size
                process_details[pid] = (parent_pid, process_identity, resident_bytes, cpu_seconds, stat_path.parent)
            except (OSError, ValueError, IndexError):
                # Process exit during enumeration is expected and is reflected by
                # `complete=false` in the aggregate sample.
                continue

        parent_map = {
            pid: ProcessSample(parent_pid, None, None, None, identity)
            for pid, (parent_pid, identity, _, _, _) in process_details.items()
        }
        roots: set[int] = set()
        if root_pid in process_details:
            previous_identity = known_processes.get(root_pid)
            if root_pid not in known_processes or previous_identity is None or previous_identity == process_details[root_pid][1]:
                roots.add(root_pid)
        roots.update(
            pid for pid, (_, identity, _, _, _) in process_details.items()
            if pid in known_processes and (known_processes[pid] is None or known_processes[pid] == identity)
        )
        candidate_pids = ProcessTreeSampler._descendants_from_roots(parent_map, roots)
        result: dict[int, ProcessSample] = {}
        for pid in candidate_pids:
            details = process_details.get(pid)
            if details is None:
                continue
            parent_pid, identity, resident_bytes, cpu_seconds, process_directory = details
            private_bytes = ProcessTreeSampler._linux_private_bytes(process_directory)
            result[pid] = ProcessSample(parent_pid, resident_bytes, private_bytes, cpu_seconds, identity)
        return result

    @staticmethod
    def _linux_private_bytes(process_directory: Path) -> int | None:
        try:
            total_kib = 0
            found = False
            for line in (process_directory / "smaps_rollup").read_text(encoding="ascii").splitlines():
                name, separator, value = line.partition(":")
                if separator and name in {"Private_Clean", "Private_Dirty", "Private_Hugetlb"}:
                    total_kib += int(value.strip().split()[0])
                    found = True
            return total_kib * 1024 if found else None
        except (OSError, ValueError, IndexError):
            return None

    @staticmethod
    def _windows_processes(root_pid: int, known_processes: dict[int, int | None]) -> dict[int, ProcessSample]:
        from ctypes import wintypes

        class ProcessEntry32W(ctypes.Structure):
            _fields_ = [
                ("dwSize", wintypes.DWORD),
                ("cntUsage", wintypes.DWORD),
                ("th32ProcessID", wintypes.DWORD),
                ("th32DefaultHeapID", ctypes.c_size_t),
                ("th32ModuleID", wintypes.DWORD),
                ("cntThreads", wintypes.DWORD),
                ("th32ParentProcessID", wintypes.DWORD),
                ("pcPriClassBase", wintypes.LONG),
                ("dwFlags", wintypes.DWORD),
                ("szExeFile", wintypes.WCHAR * 260),
            ]

        class FileTime(ctypes.Structure):
            _fields_ = [("low", wintypes.DWORD), ("high", wintypes.DWORD)]

        size_t = ctypes.c_size_t

        class ProcessMemoryCountersEx(ctypes.Structure):
            _fields_ = [
                ("cb", wintypes.DWORD),
                ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", size_t),
                ("WorkingSetSize", size_t),
                ("QuotaPeakPagedPoolUsage", size_t),
                ("QuotaPagedPoolUsage", size_t),
                ("QuotaPeakNonPagedPoolUsage", size_t),
                ("QuotaNonPagedPoolUsage", size_t),
                ("PagefileUsage", size_t),
                ("PeakPagefileUsage", size_t),
                ("PrivateUsage", size_t),
            ]

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
        kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
        kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry32W)]
        kernel32.Process32FirstW.restype = wintypes.BOOL
        kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry32W)]
        kernel32.Process32NextW.restype = wintypes.BOOL
        kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(FileTime)] * 4
        kernel32.GetProcessTimes.restype = wintypes.BOOL
        kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel32.CloseHandle.restype = wintypes.BOOL
        psapi.GetProcessMemoryInfo.argtypes = [
            wintypes.HANDLE,
            ctypes.POINTER(ProcessMemoryCountersEx),
            wintypes.DWORD,
        ]
        psapi.GetProcessMemoryInfo.restype = wintypes.BOOL

        snapshot = kernel32.CreateToolhelp32Snapshot(0x00000002, 0)
        if snapshot == wintypes.HANDLE(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            entry = ProcessEntry32W()
            entry.dwSize = ctypes.sizeof(entry)
            process_parents: dict[int, int] = {}
            if kernel32.Process32FirstW(snapshot, ctypes.byref(entry)):
                while True:
                    process_parents[int(entry.th32ProcessID)] = int(entry.th32ParentProcessID)
                    if not kernel32.Process32NextW(snapshot, ctypes.byref(entry)):
                        break
        finally:
            kernel32.CloseHandle(snapshot)

        query_rights = 0x1000 | 0x0010  # PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ
        parent_map = {
            pid: ProcessSample(parent_pid, None, None, None)
            for pid, parent_pid in process_parents.items()
        }
        roots = {root_pid} if root_pid in process_parents else set()
        roots.update(pid for pid in known_processes if pid in process_parents)
        candidate_pids = ProcessTreeSampler._descendants_from_roots(parent_map, roots)

        result: dict[int, ProcessSample] = {}
        for pid in candidate_pids:
            parent_pid = process_parents[pid]
            handle = kernel32.OpenProcess(query_rights, False, pid)
            if not handle:
                result[pid] = ProcessSample(parent_pid, None, None, None)
                continue
            try:
                counters = ProcessMemoryCountersEx()
                counters.cb = ctypes.sizeof(counters)
                working_set: int | None = None
                private_bytes: int | None = None
                cpu_seconds: float | None = None
                process_identity: int | None = None
                if psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
                    working_set = int(counters.WorkingSetSize)
                    private_bytes = int(counters.PrivateUsage)

                creation, exit_time, kernel_time, user_time = FileTime(), FileTime(), FileTime(), FileTime()
                if kernel32.GetProcessTimes(handle, ctypes.byref(creation), ctypes.byref(exit_time),
                                            ctypes.byref(kernel_time), ctypes.byref(user_time)):
                    process_identity = (creation.high << 32) | creation.low
                    kernel_ticks = (kernel_time.high << 32) | kernel_time.low
                    user_ticks = (user_time.high << 32) | user_time.low
                    cpu_seconds = (kernel_ticks + user_ticks) / 10_000_000
                result[pid] = ProcessSample(parent_pid, working_set, private_bytes, cpu_seconds, process_identity)
            finally:
                kernel32.CloseHandle(handle)
        return result


def _hardware_record() -> dict[str, Any]:
    total_memory: int | None = None
    if sys.platform.startswith("linux"):
        try:
            for line in Path("/proc/meminfo").read_text(encoding="ascii").splitlines():
                if line.startswith("MemTotal:"):
                    total_memory = int(line.split()[1]) * 1024
                    break
        except (OSError, ValueError, IndexError):
            pass
    elif sys.platform == "win32":
        from ctypes import wintypes

        class MemoryStatusEx(ctypes.Structure):
            _fields_ = [
                ("dwLength", wintypes.DWORD),
                ("dwMemoryLoad", wintypes.DWORD),
                ("ullTotalPhys", ctypes.c_ulonglong),
                ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong),
                ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong),
                ("ullAvailVirtual", ctypes.c_ulonglong),
                ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
            ]

        memory = MemoryStatusEx()
        memory.dwLength = ctypes.sizeof(memory)
        global_memory_status = ctypes.WinDLL("kernel32", use_last_error=True).GlobalMemoryStatusEx
        global_memory_status.argtypes = [ctypes.POINTER(MemoryStatusEx)]
        global_memory_status.restype = wintypes.BOOL
        if global_memory_status(ctypes.byref(memory)):
            total_memory = int(memory.ullTotalPhys)

    return {
        "os": platform.platform(),
        "architecture": platform.machine(),
        "cpu_model": platform.processor() or os.environ.get("PROCESSOR_IDENTIFIER") or "unavailable",
        "logical_cpu_count": os.cpu_count(),
        "total_memory_bytes": total_memory,
    }


def _clock_record() -> dict[str, Any]:
    info = time.get_clock_info("perf_counter")
    previous = time.perf_counter()
    minimum_tick: float | None = None
    for _ in range(1_000):
        current = time.perf_counter()
        if current < previous:
            raise RuntimeError("Monotonic clock moved backwards during calibration.")
        if current > previous:
            delta = current - previous
            minimum_tick = delta if minimum_tick is None else min(minimum_tick, delta)
        previous = current
    return {
        "implementation": info.implementation,
        "resolution_seconds": info.resolution,
        "monotonic": info.monotonic,
        "adjustable": info.adjustable,
        "observed_minimum_tick_seconds": minimum_tick,
    }


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _git_revision(repo_root: Path) -> str | None:
    try:
        return subprocess.run(
            ["git", "rev-parse", "--verify", "HEAD"], cwd=repo_root,
            check=True, capture_output=True, text=True, timeout=5,
        ).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return None


def _percentile(values: list[float], percentage: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, int((percentage / 100) * (len(ordered) - 1) + 0.5)))
    return ordered[index]


def _terminate_launched_process_tree(process: subprocess.Popen[Any], sampler: ProcessTreeSampler) -> None:
    """Stop the command and its still-identifiable descendants after timeout/error."""
    if sys.platform == "win32":
        current: dict[int, ProcessSample] = {}
        try:
            current = sampler._windows_processes(0, sampler._known_processes)
        except OSError:
            pass
        targets = set(sampler._known_processes)
        if process.poll() is None:
            targets.add(process.pid)
        for pid in sorted(targets):
            observed = current.get(pid)
            expected_identity = sampler._known_processes.get(pid)
            if pid != process.pid:
                if observed is None or observed.process_identity is None:
                    continue
                if expected_identity is not None and observed.process_identity != expected_identity:
                    continue
            try:
                subprocess.run(
                    ["taskkill", "/PID", str(pid), "/T", "/F"],
                    stdin=subprocess.DEVNULL,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                    timeout=5,
                )
            except (OSError, subprocess.TimeoutExpired):
                pass
        if process.poll() is None:
            try:
                process.kill()
            except OSError:
                pass
    elif sys.platform.startswith("linux"):
        def signal_known_processes(signal_number: int) -> None:
            try:
                current = sampler._linux_processes(0, sampler._known_processes)
            except OSError:
                return
            for pid, expected_identity in sampler._known_processes.items():
                observed = current.get(pid)
                if observed is None or expected_identity is None or observed.process_identity != expected_identity:
                    continue
                try:
                    os.kill(pid, signal_number)
                except ProcessLookupError:
                    pass

        if process.poll() is None:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        signal_known_processes(signal.SIGTERM)
        try:
            process.wait(timeout=0.5)
        except subprocess.TimeoutExpired:
            pass
        if process.poll() is None:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        signal_known_processes(signal.SIGKILL)

    if process.poll() is None:
        try:
            process.kill()
        except OSError:
            pass
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()


def run_process(
    command: list[str],
    *,
    scenario: str,
    command_id: str,
    repo_root: Path,
    build_configuration: str,
    artifact_path: Path | None,
    fixture_path: Path | None,
    sample_interval_seconds: float,
    timeout_seconds: float | None,
) -> dict[str, Any]:
    if not command or any(not item for item in command):
        raise ValueError("A non-empty command is required.")
    if sample_interval_seconds < 0.05 or sample_interval_seconds > 10:
        raise ValueError("Sample interval must be between 0.05 and 10 seconds.")
    if timeout_seconds is not None and timeout_seconds <= 0:
        raise ValueError("Timeout must be positive.")

    clock = _clock_record()
    started_utc = datetime.now(timezone.utc).isoformat()
    start = time.perf_counter()
    spawn_options: dict[str, Any] = {}
    if sys.platform == "win32":
        spawn_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    elif sys.platform.startswith("linux"):
        spawn_options["start_new_session"] = True
    process = subprocess.Popen(
        command,
        cwd=repo_root,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        **spawn_options,
    )
    sampler = ProcessTreeSampler()
    samples: list[TreeSample] = []
    timed_out = False
    try:
        while True:
            elapsed = time.perf_counter() - start
            samples.append(sampler.sample(process.pid, elapsed))
            return_code = process.poll()
            if return_code is not None and samples[-1].process_count == 0:
                break
            if timeout_seconds is not None and elapsed >= timeout_seconds:
                timed_out = True
                _terminate_launched_process_tree(process, sampler)
                # Confirm that tracked descendants exited after termination.
                deadline = time.perf_counter() + 2
                while time.perf_counter() < deadline:
                    sample_elapsed = time.perf_counter() - start
                    samples.append(sampler.sample(process.pid, sample_elapsed))
                    if samples[-1].process_count == 0:
                        break
                    time.sleep(sample_interval_seconds)
                break
            time.sleep(sample_interval_seconds)
    except BaseException:
        _terminate_launched_process_tree(process, sampler)
        raise

    return_code = process.wait()
    duration = time.perf_counter() - start
    complete_samples = [sample for sample in samples if sample.complete]
    working_values = [float(s.working_set_bytes) for s in complete_samples if s.working_set_bytes is not None]
    private_values = [float(s.private_bytes) for s in complete_samples if s.private_bytes is not None]
    cpu_values = [s.cpu_seconds for s in complete_samples if s.cpu_seconds is not None]
    private_kind = "private_commit_bytes" if sys.platform == "win32" else "private_resident_bytes"
    return {
        "schema_version": SCHEMA_VERSION,
        "measurement_status": "measured",
        "captured_utc": started_utc,
        "scenario_id": scenario,
        "command_id": command_id,
        "executable_name": Path(command[0]).name,
        "repository_revision": _git_revision(repo_root),
        "build_configuration": build_configuration,
        "build_configuration_is_declared": build_configuration != "unspecified",
        "artifact_sha256": _sha256_file(artifact_path) if artifact_path else None,
        "fixture_sha256": _sha256_file(fixture_path) if fixture_path else None,
        "hardware": _hardware_record(),
        "clock": clock,
        "measurement_policy": {
            "sample_interval_seconds": sample_interval_seconds,
            "working_set_definition": "Resident working set; shared pages may be included.",
            "private_memory_kind": private_kind,
            "cpu_measurement_definition": "Cumulative CPU seconds summed across observed processes.",
            "command_arguments_recorded": False,
            "threshold_pass_claimed": False,
        },
        "execution": {
            "duration_seconds": duration,
            "exit_code": return_code,
            "timed_out": timed_out,
            "sample_count": len(samples),
        },
        "summary": {
            "working_set_peak_bytes": max(working_values) if working_values else None,
            "working_set_p95_bytes": _percentile(working_values, 95),
            "private_peak_bytes": max(private_values) if private_values else None,
            "private_p95_bytes": _percentile(private_values, 95),
            "cpu_seconds_peak": max(cpu_values) if cpu_values else None,
            "process_count_peak": max((s.process_count for s in samples), default=0),
            "complete_sample_count": sum(1 for s in samples if s.complete),
        },
        "samples": [
            {
                "elapsed_seconds": sample.elapsed_seconds,
                "process_count": sample.process_count,
                "working_set_bytes": sample.working_set_bytes,
                "private_bytes": sample.private_bytes,
                "cpu_seconds": sample.cpu_seconds,
                "complete": sample.complete,
            }
            for sample in samples
        ],
    }


def create_sqlite_fixture(path: Path, rows: int) -> dict[str, Any]:
    if rows < 1 or rows > 2_000_000:
        raise ValueError("Fixture row count must be between 1 and 2,000,000.")
    path = path.resolve()
    if path.exists():
        raise FileExistsError(f"Refusing to overwrite existing fixture: {path.name}")
    path.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(path)
    try:
        connection.execute(
            "CREATE TABLE financials (Id INTEGER PRIMARY KEY, Line TEXT NOT NULL, Current TEXT NOT NULL, Prior TEXT NOT NULL)"
        )
        with connection:
            for first in range(1, rows + 1, 5_000):
                batch = [
                    (row, "Revenue", str(1_250 + row % 1_000), str(1_100 + row % 500))
                    for row in range(first, min(first + 5_000, rows + 1))
                ]
                connection.executemany("INSERT INTO financials VALUES (?, ?, ?, ?)", batch)
    finally:
        connection.close()
    return {
        "schema_version": 1,
        "fixture_kind": "synthetic-financials-sqlite-v1",
        "rows": rows,
        "sha256": _sha256_file(path),
        "seed_policy": "deterministic arithmetic pattern; contains no customer data",
    }


def write_new_json(path: Path, value: dict[str, Any]) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    serialized = json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(serialized)


def validate_record(record: dict[str, Any]) -> None:
    schema = json.loads(RESULT_SCHEMA.read_text(encoding="utf-8"))
    _validate_schema_value(record, schema, "record")


def _validate_schema_value(value: Any, schema: dict[str, Any], path: str) -> None:
    expected_type = schema.get("type")
    accepted_types = expected_type if isinstance(expected_type, list) else [expected_type]
    if expected_type is not None:
        type_matches = any(
            (kind == "null" and value is None)
            or (kind == "object" and isinstance(value, dict))
            or (kind == "array" and isinstance(value, list))
            or (kind == "string" and isinstance(value, str))
            or (kind == "boolean" and isinstance(value, bool))
            or (kind == "integer" and isinstance(value, int) and not isinstance(value, bool))
            or (kind == "number" and isinstance(value, (int, float)) and not isinstance(value, bool))
            for kind in accepted_types
        )
        if not type_matches:
            raise ValueError(f"{path} does not match JSON Schema type {expected_type}.")

    if "const" in schema and value != schema["const"]:
        raise ValueError(f"{path} does not match its JSON Schema constant.")
    if "enum" in schema and value not in schema["enum"]:
        raise ValueError(f"{path} is outside its JSON Schema enum.")

    if isinstance(value, dict):
        missing = set(schema.get("required", [])) - set(value)
        if missing:
            raise ValueError(f"{path} is missing fields: {sorted(missing)}")
        properties = schema.get("properties", {})
        unknown = set(value) - set(properties)
        if schema.get("additionalProperties") is False and unknown:
            raise ValueError(f"{path} contains unknown fields: {sorted(unknown)}")
        for name, item in value.items():
            if name in properties:
                _validate_schema_value(item, properties[name], f"{path}.{name}")
    elif isinstance(value, list):
        if len(value) < schema.get("minItems", 0):
            raise ValueError(f"{path} contains fewer items than the JSON Schema minimum.")
        item_schema = schema.get("items")
        if item_schema is not None:
            for index, item in enumerate(value):
                _validate_schema_value(item, item_schema, f"{path}[{index}]")
    elif isinstance(value, str):
        if len(value) < schema.get("minLength", 0):
            raise ValueError(f"{path} is shorter than the JSON Schema minimum.")
        if "pattern" in schema and re.search(schema["pattern"], value) is None:
            raise ValueError(f"{path} does not match its JSON Schema pattern.")
        if schema.get("format") == "date-time":
            try:
                parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
            except ValueError as error:
                raise ValueError(f"{path} is not a valid date-time.") from error
            if parsed.tzinfo is None or parsed.utcoffset() is None:
                raise ValueError(f"{path} must include a timezone.")
    elif isinstance(value, (int, float)) and not isinstance(value, bool):
        if isinstance(value, float) and not math.isfinite(value):
            raise ValueError(f"{path} must be finite JSON data.")
        if value < schema.get("minimum", float("-inf")):
            raise ValueError(f"{path} is below its JSON Schema minimum.")
        if value <= schema.get("exclusiveMinimum", float("-inf")):
            raise ValueError(f"{path} is not above its JSON Schema exclusive minimum.")


def self_test() -> None:
    clock = _clock_record()
    monotonic_start = time.perf_counter()
    time.sleep(0.02)
    if time.perf_counter() <= monotonic_start:
        raise RuntimeError("Monotonic clock calibration failed.")
    if not clock["monotonic"] or clock["resolution_seconds"] <= 0 or clock["observed_minimum_tick_seconds"] is None:
        raise RuntimeError("High-resolution clock calibration was incomplete.")
    if sys.platform not in {"win32", "linux"}:
        raise RuntimeError("Self-test requires Windows or Linux process-tree support.")

    with tempfile.TemporaryDirectory(prefix="xms-benchmark-self-test-") as temp:
        root = Path(temp)
        fixture = create_sqlite_fixture(root / "fixture.db", rows=5)
        repeated_fixture = create_sqlite_fixture(root / "fixture-repeat.db", rows=5)
        if len(fixture["sha256"]) != 64:
            raise RuntimeError("Synthetic fixture digest is malformed.")
        if fixture["sha256"] != repeated_fixture["sha256"]:
            raise RuntimeError("Synthetic fixture generation is not deterministic.")
        record = run_process(
            [sys.executable, "-c", "import time; time.sleep(0.08)"],
            scenario="harness-self-test",
            command_id="python-short-process-v1",
            repo_root=root,
            build_configuration="self-test",
            artifact_path=None,
            fixture_path=root / "fixture.db",
            sample_interval_seconds=0.05,
            timeout_seconds=5,
        )
        validate_record(record)
        schema = json.loads(RESULT_SCHEMA.read_text(encoding="utf-8"))
        if not set(schema["required"]).issubset(record):
            raise RuntimeError("Tracked benchmark output schema does not match the record.")
        if record["execution"]["exit_code"] != 0 or record["execution"]["sample_count"] < 1:
            raise RuntimeError("Process-tree measurement self-test failed.")
        json.loads(json.dumps(record, allow_nan=False))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    commands.add_parser("self-test", help="check monotonic clock, fixture creation, sampler and record schema")

    fixture = commands.add_parser("create-sqlite-fixture", help="create deterministic synthetic financial rows")
    fixture.add_argument("--path", type=Path, required=True)
    fixture.add_argument("--rows", type=int, default=100_000)

    run = commands.add_parser("run", help="launch one scenario command and record process-tree samples")
    run.add_argument("--scenario", required=True, help="stable scenario ID from the tracked scenario catalog")
    run.add_argument("--command-id", required=True, help="non-secret command template ID")
    run.add_argument("--repo-root", type=Path, default=Path.cwd())
    run.add_argument("--build-configuration", choices=("Release", "Debug", "unspecified"), default="unspecified")
    run.add_argument("--artifact", type=Path, help="optional built artifact whose SHA-256 should be recorded")
    run.add_argument("--fixture", type=Path, help="optional synthetic input whose SHA-256 should be recorded")
    run.add_argument("--sample-interval", type=float, default=0.5)
    run.add_argument("--timeout", type=float, default=600)
    run.add_argument("--output", type=Path, required=True, help="new JSON output file; existing files are never overwritten")
    run.add_argument("command", nargs=argparse.REMAINDER, help="command to launch after `--`; arguments are not persisted")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.action == "self-test":
        self_test()
        print("Benchmark harness self-test passed; no product performance threshold was measured.")
        return 0
    if args.action == "create-sqlite-fixture":
        print(json.dumps(create_sqlite_fixture(args.path, args.rows), indent=2))
        return 0

    command = list(args.command)
    if command and command[0] == "--":
        command.pop(0)
    if not command:
        raise SystemExit("Provide a command after `--`.")
    record = run_process(
        command,
        scenario=args.scenario,
        command_id=args.command_id,
        repo_root=args.repo_root.resolve(),
        build_configuration=args.build_configuration,
        artifact_path=args.artifact,
        fixture_path=args.fixture,
        sample_interval_seconds=args.sample_interval,
        timeout_seconds=args.timeout,
    )
    validate_record(record)
    write_new_json(args.output, record)
    print(json.dumps({
        "output": args.output.name,
        "exit_code": record["execution"]["exit_code"],
        "timed_out": record["execution"]["timed_out"],
        "sample_count": record["execution"]["sample_count"],
        "threshold_pass_claimed": False,
    }, indent=2))
    return 1 if record["execution"]["exit_code"] != 0 or record["execution"]["timed_out"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
