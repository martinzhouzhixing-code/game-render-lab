# SPDX-License-Identifier: MIT
"""Compile Vulkan workset selection and exercise rotating SR outputs without a GPU."""

import argparse
from pathlib import Path
import subprocess
import tempfile

from test_present_slot import SOURCE


HARNESS = r"""
#include <algorithm>
#include <atomic>
#include <cassert>
#include <cstdint>
#include <set>
#include <sstream>
#include <vector>

namespace stages { enum class Point { Upscaled, Present }; }
namespace codec { struct SourceUnits { bool absolute = false; }; }
namespace reshade::log { enum class level { info }; }
void Log(reshade::log::level, const std::string&) {}
unsigned V6EncodingConstant(codec::SourceUnits) { return 1; }

struct Workset {
    int output;
    uint32_t width, height;
    int output_format;
    uint32_t nr_width = 0, nr_height = 0, source_width = 0, source_height = 0;
    uint8_t hdr_mode = 0;
    uint32_t id = 0;
    uint64_t use_id = 0, last_used = 0;
    codec::SourceUnits units;
};
struct Stage { std::vector<Workset> worksets, retired_worksets; };
std::atomic_uint32_t max_worksets{4}, workset_free_policy{0};
std::atomic_uint64_t upscaled_workset_reuses{0};
uint32_t next_workset_id = 100;
uint64_t next_use_id = 100, lifecycle_events = 0;
std::set<uint64_t> in_use;
bool RecordedUseComplete(uint64_t use) { return in_use.count(use) == 0; }
unsigned destroyed = 0;
void DestroyWorkset(int, const Workset&) { ++destroyed; }

uint32_t Choose(Stage& feature, int output, stages::Point stage = stages::Point::Upscaled) {
    const struct { int Image; } out{output};
    const struct { struct { int format; } texture; } output_desc{{8}};
    constexpr uint32_t width = 2560, height = 1440, nr_width = 1280, nr_height = 720;
    constexpr uint32_t resource_width = width, resource_height = height, hdr_mode = 1;
    const codec::SourceUnits current_units{};
    const int api_device = 1;
PRODUCTION_SELECTION
    return workset->id;
}

int main() {
    Stage rotates;
    const uint32_t first = Choose(rotates, 1);
    assert(rotates.worksets.size() == 1 && lifecycle_events == 1);
    for (int output = 2; output <= 9; ++output) {
        assert(Choose(rotates, output) == first);
        assert(rotates.worksets[0].output == output);
    }
    assert(rotates.worksets.size() == 1 && lifecycle_events == 1 && upscaled_workset_reuses == 8);

    in_use.insert(rotates.worksets[0].use_id);
    assert(Choose(rotates, 9) == first);  // Existing output-specific match.
    assert(Choose(rotates, 10) != first && rotates.worksets.size() == 2);
    const uint32_t second = rotates.worksets[1].id;
    rotates.worksets[1].nr_width = 960;
    const uint32_t third = Choose(rotates, 11);
    assert(third != first && third != second && rotates.worksets.size() == 3);

    Stage present;
    const uint32_t present_first = Choose(present, 1, stages::Point::Present);
    assert(Choose(present, 2, stages::Point::Present) != present_first && present.worksets.size() == 2);

    max_worksets.store(1);
    Stage full;
    Choose(full, 1);
    in_use.insert(full.worksets[0].use_id);
    Choose(full, 2);
    assert(full.worksets.size() == 1 && full.retired_worksets.size() == 1 && destroyed == 0);
}
"""


def build_fixture(code):
    start = code.index("  auto workset = std::find_if(feature.worksets.begin()")
    end = code.index("\n  if (workset->units.encoding", start)
    return HARNESS.replace("PRODUCTION_SELECTION", code[start:end])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("compiler", nargs="+", help="C++ compiler command, e.g. clang++ or zig c++")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory() as tmp:
        source = Path(tmp) / "worksets.cpp"
        executable = Path(tmp) / "worksets.exe"
        source.write_text(build_fixture(SOURCE.read_text(encoding="utf-8")), encoding="utf-8")
        subprocess.run([*args.compiler, "-std=c++17", str(source), "-o", str(executable)], check=True)
        subprocess.run([str(executable)], check=True)
    print("Vulkan workset regression passed: same-contract reuse, live recording, grid, present, retirement")


if __name__ == "__main__":
    main()
