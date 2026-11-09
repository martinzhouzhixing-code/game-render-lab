# SPDX-License-Identifier: MIT
import csv
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from validate import analyze, preflight


class PreflightTests(unittest.TestCase):
    def test_known_game_with_supported_driver_is_ready_for_manual_check(self):
        with tempfile.TemporaryDirectory() as temporary:
            game = Path(temporary)
            (game / "Endfield.exe").touch()
            (game / "sl.interposer.dll").touch()
            driver = subprocess.CompletedProcess([], 0, "NVIDIA RTX, 615.00\n", "")
            fingerprint = "fabb19bb6495d743182c1bc51bcbbdc4481acc3dcaf445403f0b304a31a56ccc"
            with patch("validate.subprocess.run", return_value=driver), patch("validate.sha256", return_value=fingerprint):
                self.assertEqual(preflight(game)["status"], "ready_for_manual_vulkan_check")
                (game / "dlss5-feed.addon64").touch()
                self.assertEqual(preflight(game)["status"], "blocked")


class CaptureTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.capture = Path(temporary.name) / "capture.csv"

    def write(self, *frames):
        with self.capture.open("w", newline="", encoding="utf-8") as stream:
            writer = csv.writer(stream)
            writer.writerow(("Application", "ProcessID", "SwapChainAddress", "FrameType",
                             "CPUStartQPCTime", "MsClickToPhotonLatency", "MsPCLatency"))
            writer.writerows(frames)

    def test_real_frame_intervals_and_missing_latency(self):
        self.write(("Endfield.exe", 3, "0x01", "Application", 1000, "NA", 20),
                   ("Endfield.exe", 3, "0x01", "Application", 1010, "NA", 30),
                   ("Endfield.exe", 3, "0x01", "Application", 1030, "NA", 0),
                   ("launcher.exe", 7, "0x04", "Application", 1035, 5, 7))
        report = analyze(self.capture)
        self.assertEqual(report["real_fps"], 66.667)
        self.assertEqual(report["frame_interval"]["p99_ms"], 20)
        self.assertIsNone(report["measured_latency_and_gpu"]["MsClickToPhotonLatency"])
        self.assertEqual(report["measured_latency_and_gpu"]["MsPCLatency"]["p50_ms"], 25)

    def test_unmarked_frames_require_operator_confirmation(self):
        self.write(("Endfield.exe", 3, "0x01", "Not Set", 1000, "NA", "NA"),
                   ("Endfield.exe", 3, "0x01", "Not Set", 1010, "NA", "NA"))
        with self.assertRaisesRegex(ValueError, "unmarked"):
            analyze(self.capture)
        self.assertFalse(analyze(self.capture, confirm_fg_off=True)["frame_type_verified"])

    def test_generated_frames_are_rejected_even_if_operator_confirms(self):
        self.write(("Endfield.exe", 3, "0x01", "Application", 1000, "NA", "NA"),
                   ("Endfield.exe", 3, "0x01", "NVIDIA DLSS", 1005, "NA", "NA"),
                   ("Endfield.exe", 3, "0x01", "Application", 1010, "NA", "NA"))
        with self.assertRaisesRegex(ValueError, "non-application"):
            analyze(self.capture, confirm_fg_off=True)

    def test_swap_chain_switch_does_not_mix_frame_times(self):
        self.write(("Endfield.exe", 3, "0x01", "Application", 1000, "NA", "NA"),
                   ("Endfield.exe", 3, "0x02", "Application", 1005, "NA", "NA"),
                   ("Endfield.exe", 3, "0x01", "Application", 1010, "NA", "NA"))
        with self.assertRaisesRegex(ValueError, "one Endfield process and swap chain"):
            analyze(self.capture)
        self.assertEqual(analyze(self.capture, swap_chain="0x01")["real_fps"], 100)


if __name__ == "__main__":
    unittest.main()
