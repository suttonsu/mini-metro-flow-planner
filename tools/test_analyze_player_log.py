import unittest
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze_player_log import analyze_lines


class AnalyzePlayerLogTests(unittest.TestCase):
    def test_healthy_session_passes(self):
        lines = [
            "[Auto Planner][Session] START city=london mode=CLASSIC",
            "[Auto Planner][Action] Built line through 3 stations | city=london mode=CLASSIC unconnected=0 routes=L1[s3]",
            "[Auto Planner][Session] END actions=1 built=1 extended=0 capacity=0 interchange=0 transfers=0 replans=0 reallocations=0 rollbacks=0 noCrossing=0 | city=london mode=CLASSIC",
        ]
        sessions = analyze_lines(lines)
        self.assertEqual(1, len(sessions))
        self.assertTrue(sessions[0].completed)
        self.assertEqual([], sessions[0].findings)

    def test_churn_and_duplicate_routes_are_flagged(self):
        lines = ["[Auto Planner][Session] START city=berlin mode=CLASSIC"]
        lines.extend(
            "[Auto Planner][Blocked] NoCrossing operation=transfer unconnected=0"
            for _ in range(12)
        )
        lines.append(
            "[Auto Planner][Action] Added transfer L1-L2 | city=berlin mode=CLASSIC "
            "routes=L1[s3],L1[s4]"
        )
        lines.append(
            "[Auto Planner][Session] END actions=5 built=0 extended=0 capacity=0 "
            "interchange=0 transfers=5 replans=0 reallocations=0 rollbacks=0 "
            "noCrossing=12 | city=berlin mode=CLASSIC"
        )
        findings = analyze_lines(lines)[0].findings
        self.assertTrue(any("NoCrossing churn" in item for item in findings))
        self.assertTrue(any("transfer churn" in item for item in findings))
        self.assertTrue(any("duplicate route labels" in item for item in findings))


if __name__ == "__main__":
    unittest.main()
