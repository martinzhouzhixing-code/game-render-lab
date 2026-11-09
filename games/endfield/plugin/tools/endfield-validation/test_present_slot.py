# SPDX-License-Identifier: MIT
"""Compile the production slot admission block against a fake Vulkan fence."""

import argparse
from pathlib import Path
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/unified-20261011/generic/src/addons/dlss5/vulkan_path.hpp"

HARNESS = r"""
#include <atomic>
#include <cassert>
#include <cstdint>

struct PrePresentSlot { bool submitted; int fence = 91; };
constexpr uint32_t kPrePresentSlots = 4;
struct Present {
    PrePresentSlot slots[kPrePresentSlots]{};
    uint32_t next = 0;
    int wait_result = 0;
    int waits = 0;
    int resets = 0;
    uint64_t last_timeout = UINT64_MAX;

    int wait_fences(int device, int count, const int* fence, unsigned all, uint64_t timeout) {
        assert(device == 7 && count == 1 && *fence == 91 && all == 1);
        ++waits;
        last_timeout = timeout;
        return wait_result;
    }
    int reset_fences(int, int, const int*) { ++resets; return 0; }
};
std::atomic_uint64_t prepresent_declines{0};
std::atomic_uint64_t prepresent_slots_unavailable{0};

int Admit(Present& p) {
    constexpr int device = 7;
    PRODUCTION_ADMISSION
    return 1;
}

int main() {
    Present idle;
    assert(Admit(idle) == 1 && idle.waits == 0 && idle.resets == 1);

    Present done;
    done.slots[0].submitted = true;
    assert(Admit(done) == 1 && done.waits == 1 && done.resets == 1);
    assert(done.last_timeout == 0 && !done.slots[0].submitted);

    Present busy;
    busy.slots[0].submitted = true;
    busy.wait_result = 2;  // VK_TIMEOUT
    assert(Admit(busy) == 0 && busy.waits == 1 && busy.resets == 0);
    assert(busy.last_timeout == 0 && busy.slots[0].submitted);
    assert(prepresent_slots_unavailable.load() == 1 && prepresent_declines.load() == 1);

    Present error;
    error.slots[0].submitted = true;
    error.wait_result = -4;  // VK_ERROR_DEVICE_LOST
    assert(Admit(error) == 0 && error.waits == 1 && error.resets == 0);
    assert(error.last_timeout == 0 && error.slots[0].submitted);
    assert(prepresent_slots_unavailable.load() == 2 && prepresent_declines.load() == 2);
}
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("compiler", nargs="+", help="C++ compiler command, e.g. clang++ or zig c++")
    args = parser.parse_args()
    code = SOURCE.read_text(encoding="utf-8")
    start = code.index("  PrePresentSlot& slot = p.slots[p.next % kPrePresentSlots];",
                       code.index("inline uint32_t PrePresentNr("))
    end = code.index("\n  // VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT.", start)
    with tempfile.TemporaryDirectory() as tmp:
        source = Path(tmp) / "slot.cpp"
        executable = Path(tmp) / "slot.exe"
        source.write_text(HARNESS.replace("PRODUCTION_ADMISSION", code[start:end]), encoding="utf-8")
        subprocess.run([*args.compiler, "-std=c++17", str(source), "-o", str(executable)], check=True)
        subprocess.run([str(executable)], check=True)
    print("Present slot regression passed: idle, signaled, pending, device error")


if __name__ == "__main__":
    main()
