#!/usr/bin/env python3
"""Focused, no-provider tests for the sample canary result gate."""

import subprocess
import sys
import unittest
from pathlib import Path


CHECKER = Path(__file__).with_name("sample_canary_result.py")


class SampleCanaryResultTests(unittest.TestCase):
    def run_checker(
        self, status: int, markers: str, output: str
    ) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(CHECKER), str(status), "MAF/Example", markers],
            input=output,
            capture_output=True,
            check=False,
            text=True,
        )

    def test_requires_final_completion_marker_after_early_progress(self) -> None:
        result = self.run_checker(
            0,
            "Scenario 4: Tool calls disabled|PerToolActivities sample complete.",
            "Scenario 4: Tool calls disabled\n",
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("PerToolActivities sample complete.", result.stderr)

    def test_passes_when_successful_process_prints_all_markers(self) -> None:
        result = self.run_checker(
            0,
            "Parallel responses:|MultiAgentRouting sample complete.",
            "Parallel responses:\nMultiAgentRouting sample complete.\n",
        )

        self.assertEqual(0, result.returncode)
        self.assertEqual("", result.stderr)

    def test_process_failure_cannot_pass_even_when_markers_exist(self) -> None:
        result = self.run_checker(
            1,
            "WorkflowRouting sample complete.",
            "WorkflowRouting sample complete.\n",
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("exited with status 1", result.stderr)

    def test_timeout_related_statuses_are_not_reported_as_deadlocks(self) -> None:
        for status in (124, 137, 143):
            with self.subTest(status=status):
                result = self.run_checker(status, "complete", "complete\n")

                self.assertEqual(2, result.returncode)
                self.assertIn("does not establish a deadlock", result.stderr)

    def test_success_without_configured_markers_fails_closed(self) -> None:
        result = self.run_checker(0, "", "sample exited\n")

        self.assertEqual(1, result.returncode)
        self.assertIn("no completion markers configured", result.stderr)

    def test_configurable_agent_gate_matches_actual_agent_activity_logs(self) -> None:
        justfile = CHECKER.parent.parent / "justfile"
        line = next(
            line for line in justfile.read_text().splitlines()
            if '"ConfigurableAgent:samples/MAF/ConfigurableAgent:' in line
        )
        entry = line.strip().removesuffix("\\").strip().strip('"')
        markers = entry.split(":", 3)[3]
        result = self.run_checker(
            0,
            markers,
            "[triageagent/ta-123] Agent activity started\n"
            "[escalationagent/ta-456] Agent activity started\n"
            "ConfigurableAgent sample complete.\n",
        )
        self.assertEqual(0, result.returncode)
        missing_escalation = self.run_checker(
            0, markers, "[triageagent/ta-123] Agent activity started\n"
            "ConfigurableAgent sample complete.\n"
        )
        self.assertEqual(1, missing_escalation.returncode)


if __name__ == "__main__":
    unittest.main()
