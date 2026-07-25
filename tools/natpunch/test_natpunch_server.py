import importlib.util
import pathlib
import sys
import unittest

MODULE_PATH = pathlib.Path(__file__).with_name("natpunch-server.py")
SPEC = importlib.util.spec_from_file_location("natpunch_server", MODULE_PATH)
natpunch_server = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = natpunch_server
SPEC.loader.exec_module(natpunch_server)
Coordinator = natpunch_server.Coordinator


class CoordinatorTests(unittest.TestCase):
    def setUp(self):
        self.coordinator = Coordinator()
        self.sent_a = []
        self.sent_b = []

    def handle(self, text, addr=("198.51.100.1", 40000), label="A"):
        here = self.sent_a.append if label == "A" else self.sent_b.append

        def wrap(target):
            return lambda message, destination: target.append((message, destination))

        self.coordinator.handle(
            text.encode(),
            addr,
            label,
            wrap(self.sent_a if label == "A" else self.sent_b),
            wrap(self.sent_a),
            wrap(self.sent_b),
        )

    def test_probe7_reports_exact_source(self):
        self.handle("PROBE7 abc123 7", ("198.51.100.4", 45678))
        self.assertTrue(self.sent_a[0][0].startswith("PROBED7 abc123 7 198.51.100.4 45678 A "))

    def test_two_reports_issue_synchronized_go(self):
        self.handle("JOIN7 room1 clientA", ("198.51.100.1", 40001))
        self.handle("JOIN7 room1 clientB", ("203.0.113.2", 40002))
        self.handle(
            "REPORT7 room1 clientA 1 sym 1 800 0 12000 13000 30 1 25",
            ("198.51.100.1", 40001),
        )
        self.handle(
            "REPORT7 room1 clientB 1 sym 1 750 0 22000 23000 40 1 25",
            ("203.0.113.2", 40002),
        )
        messages = [message for message, _ in self.sent_a]
        self.assertEqual(sum(message.startswith("GO7 ") for message in messages), 6)
        self.assertEqual(sum(message.startswith("PEERREPORT7 ") for message in messages), 6)

    def test_new_report_is_not_paired_with_consumed_peer_report(self):
        self.handle("JOIN7 room2 clientA", ("198.51.100.1", 41001))
        self.handle("JOIN7 room2 clientB", ("203.0.113.2", 41002))
        report_a = "sym 1 800 0 12000 13000 30 1 25"
        report_b = "sym 1 750 0 22000 23000 40 1 25"
        self.handle(f"REPORT7 room2 clientA 1 {report_a}", ("198.51.100.1", 41001))
        self.handle(f"REPORT7 room2 clientB 1 {report_b}", ("203.0.113.2", 41002))
        first_go_count = sum(message.startswith("GO7 ") for message, _ in self.sent_a)
        self.handle(f"REPORT7 room2 clientA 2 {report_a}", ("198.51.100.1", 41001))
        self.assertEqual(
            sum(message.startswith("GO7 ") for message, _ in self.sent_a),
            first_go_count,
        )
        self.handle(f"REPORT7 room2 clientB 2 {report_b}", ("203.0.113.2", 41002))
        self.assertEqual(
            sum(message.startswith("GO7 ") for message, _ in self.sent_a),
            first_go_count + 6,
        )

    def test_legacy_probe_still_works(self):
        self.handle("PROBE", ("192.0.2.1", 33333))
        self.assertEqual(self.sent_a[0], ("PROBED 192.0.2.1 33333 A", ("192.0.2.1", 33333)))


if __name__ == "__main__":
    unittest.main()
