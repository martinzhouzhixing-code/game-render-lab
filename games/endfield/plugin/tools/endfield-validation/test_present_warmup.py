# SPDX-License-Identifier: MIT
"""Compile the production Vulkan warmup submission and fallback with fake API calls."""

import argparse
from pathlib import Path
import subprocess
import tempfile

from test_present_slot import SOURCE


HARNESS = r"""
#include <atomic>
#include <cassert>
#include <cstdint>
#include <map>
#include <string>

enum class NrDeclineReason { kCount, kSlotWarmupMaturation, kFailureBackoff };
struct Stage {};
struct Feature { Stage stages[1]; };
namespace stages { enum class Point { Present }; }
int StageIndex(stages::Point) { return 0; }
int runtime_mutex;
struct RuntimeLock { explicit RuntimeLock(int&) {} };
struct PreFg { int Handle() { return 123; } } prefg;
std::map<int, Feature> features{{123, {}}};
unsigned retired_stages = 0, retired_images = 0;
void RetireStagePasses(Stage*, unsigned first, bool replacement) {
    assert(first == 0 && replacement); ++retired_stages;
}
void RetirePreFgImages() { ++retired_images; }
namespace reshade::log { enum class level { info, warning }; }
void Log(reshade::log::level, const std::string&) {}
int64_t SteadyNowNs() { return 17; }

enum class VkStructureType {};
using VkResult = int;
using VkPipelineStageFlags = uint32_t;
struct VkSubmitInfo {
    VkStructureType sType; void* next;
    uint32_t wait_count; const int* waits; const VkPipelineStageFlags* stages;
    uint32_t buffer_count; const int* buffers;
    uint32_t signal_count; void* const* signals;
};
struct Slot { int cb = 5, fence = 6; void* semaphore = reinterpret_cast<void*>(7); bool submitted = false; };
struct Present {
    bool failed = false, pending = false;
    int pending_image = 0, next = 0, ends = 0, submits = 0, abandons = 0;
    int end_result = 0, submit_result = 0;
    int end(int cb) { assert(cb == 5); ++ends; return end_result; }
    int submit(int queue, int count, const VkSubmitInfo* submit, int fence) {
        assert(queue == 3 && count == 1 && fence == 6);
        assert(submit->wait_count == 1 && *submit->waits == 42);
        assert(*submit->stages == 0x10000u);  // VK_PIPELINE_STAGE_ALL_COMMANDS_BIT
        assert(submit->buffer_count == 1 && *submit->buffers == 5);
        assert(submit->signal_count == 1 && *submit->signals == reinterpret_cast<void*>(7));
        ++submits;
        return submit_result;
    }
} prepresent;
struct Info { uint32_t waitSemaphoreCount = 1; const int* pWaitSemaphores; };
std::atomic_uint64_t prepresent_frames{0}, prepresent_warmup_submissions{0}, prepresent_declines{0},
    prepresent_skipped_evaluates{0}, prepresent_mismatches{0};
std::atomic_int64_t prepresent_last_ns{0};
int prefg_miss_streak = 10;

uint32_t Submit(Present& p, Slot& slot, NrDeclineReason decline, uint64_t* out_wait_semaphore) {
    const auto abandon = [&]() { ++p.abandons; return 0u; };
    int semaphore = 42;
    const Info original{1, &semaphore};
    const auto* info = &original;
    const int queue = 3, image = 11;
    [[maybe_unused]] bool served = false;
    const struct { bool with_hud; } serve{false};
PRODUCTION_SUBMISSION
}

NrDeclineReason EvaluateFallback(int backbuffer) {
    const struct { struct { int Image; } view; } back{{backbuffer}};
PRODUCTION_FALLBACK
    return NrDeclineReason::kFailureBackoff;
}

int main() {
    Slot slot;
    uint64_t semaphore = 0;
    assert(Submit(prepresent, slot, NrDeclineReason::kSlotWarmupMaturation, &semaphore) == 1);
    assert(slot.submitted && prepresent.ends == 1 && prepresent.submits == 1 && semaphore == 7);
    assert(prepresent_warmup_submissions == 1 && prepresent_frames == 0 && prepresent_last_ns == 0);
    assert(EvaluateFallback(11) == NrDeclineReason::kCount && prefg_miss_streak == 0);
    assert(EvaluateFallback(11) == NrDeclineReason::kFailureBackoff);

    assert(Submit(prepresent, slot, NrDeclineReason::kCount, &semaphore) == 1);
    assert(prepresent_frames == 1 && prepresent_last_ns == 17);
    assert(EvaluateFallback(99) == NrDeclineReason::kFailureBackoff && !prepresent.pending);

    Slot failed;
    semaphore = 99;
    Present end_error;
    end_error.end_result = -4;
    assert(Submit(end_error, failed, NrDeclineReason::kSlotWarmupMaturation, &semaphore) == 0);
    assert(end_error.failed && !end_error.pending && !failed.submitted && end_error.submits == 0 && semaphore == 99);
    assert(retired_stages == 1 && retired_images == 1);

    Present submit_error;
    submit_error.submit_result = -2;  // VK_ERROR_OUT_OF_DEVICE_MEMORY
    assert(Submit(submit_error, failed, NrDeclineReason::kSlotWarmupMaturation, &semaphore) == 0);
    assert(submit_error.failed && !submit_error.pending && !failed.submitted && submit_error.submits == 1 && semaphore == 99);
    assert(retired_stages == 2 && retired_images == 2);

    Slot lost_slot;
    Present device_lost;
    device_lost.submit_result = -4;  // VK_ERROR_DEVICE_LOST
    assert(Submit(device_lost, lost_slot, NrDeclineReason::kSlotWarmupMaturation, &semaphore) == 0);
    assert(device_lost.failed && !device_lost.pending && lost_slot.submitted && semaphore == 99);
    assert(retired_stages == 3 && retired_images == 3);

    Present ordinary_decline;
    assert(Submit(ordinary_decline, failed, NrDeclineReason::kFailureBackoff, &semaphore) == 0);
    assert(ordinary_decline.abandons == 1 && ordinary_decline.submits == 0 && !ordinary_decline.pending);
}
"""


def build_fixture(code):
    present = code.index("inline uint32_t PrePresentNr(")
    start = code.index("\n  }\n", code.index("decline = ServePreFgFrame(slot.cb", present)) + 5
    end = code.index("\n}\n", start)
    fallback_start = code.index("    if (prepresent.pending) {")
    fallback_end = code.index("\n  }\n\n  // The frame can be served.", fallback_start)
    return HARNESS.replace("PRODUCTION_SUBMISSION", code[start:end]).replace(
        "PRODUCTION_FALLBACK", code[fallback_start:fallback_end]
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("compiler", nargs="+", help="C++ compiler command, e.g. clang++ or zig c++")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory() as tmp:
        source = Path(tmp) / "warmup.cpp"
        executable = Path(tmp) / "warmup.exe"
        source.write_text(build_fixture(SOURCE.read_text(encoding="utf-8")), encoding="utf-8")
        subprocess.run([*args.compiler, "-std=c++17", str(source), "-o", str(executable)], check=True)
        subprocess.run([str(executable)], check=True)
    print("Present warmup regression passed: submit, fallback, end/submit errors, ordinary decline")


if __name__ == "__main__":
    main()
