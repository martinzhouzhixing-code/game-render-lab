# 《终末地》DLSS 5 实机验收

源码底座为上游 `unified-20261103l`，提交 `e4f52e7c32e53359823105435e6335e59d2ca5b3`。本目录的验收工具采用 MIT 许可，原有源码沿用各自许可证。NVIDIA 运行库和游戏文件由测试环境单独提供。

## Home 面板

| 页签 | 插件源码中的功能 |
| --- | --- |
| 常用 | 神经渲染开关、工作分辨率、层数和「稳定优先」 |
| 画面 | 强度、模型风格和「保留原色与暗部」，可调整局部色调、结构和皮肤细节 |
| 高级 | 完整设置、开发者视图、渲染／超分后／呈现层数滑条 |
| 诊断 | 最近一次 NR 评估结果、成功次数、主要跳过原因、支持报告和日志入口 |
| 帧生成 | 游戏默认或固定 2×–6×，显示运行库接受状态与倍率上限 |

沿用 ReShade 的字体图集和 ImGui 控件，面板只在打开时绘制当前页，不新增界面着色器、纹理或持续性能采样。面板顶部保留语言选项，默认跟随 ReShade 语言，后者选用简体中文时能加载对应字形。桥接仍由「DLSS 5 Bridge」管理。新帧生成页复用「MFG Unlock」的设置，两个组件需要用同一候选源码构建。配合旧 MFG 时，新页会指向原入口。

「保留原色与暗部」开启已有的外观合成，将色彩、色相偏移与压暗调到零。它保留原始画面的色度，抑制模型新增的压暗，作用于全画面。当前 Vulkan 路径没有可供合成单独选中人物脸部的遮罩。预设保留已有细节参数，本次会话可撤销。原先关闭外观合成时，开启它会增加合成工作。

层数滑条拖动时只预览，松手才提交。提交沿用原有的高层数确认、计时和回退逻辑。改变层数可能触发一次资源调整。

游戏内帧生成开启固定档位后，MFG 才可申请 2×–6×，可选范围取决于显卡、Streamline 和 DLSS-G 运行库。要使用 1×，在游戏内关闭帧生成。NVIDIA App「AI 插帧」（Smooth Motion）是另一条路径，上游将其列为不支持的组合。更高倍率增加呈现帧，真实渲染速率和操作延迟仍应单独测量。

「稳定优先」仅在关闭神经渲染时可应用。预设保留超分后 1 层，关闭渲染前与呈现层，工作分辨率的宽、高各取输出的 50%。例如输出 2560 × 1440 时，模型处理约 1280 × 720，实际尺寸还受运行库的对齐要求约束。这样减少模型工作像素，仍按原输出分辨率呈现。预设由用户显式选择，沿用原配置格式，不改变游戏插帧或驱动设置。


本包提供候选源码。原生插件构建沿用上游 MSVC 和 Windows SDK 环境。界面交互和渲染同步回归可以用提取代码离线验证，色彩观感、倍率的实际输出和帧率仍以游戏内对照为准。

## Vulkan 修复候选

Vulkan 呈现层复用 NR 命令槽时，通过零超时轮询判断 GPU 是否已经完成之前的工作。忙碌或返回错误时保留槽位状态并让原有呈现链路继续。`prepresent[... slots_unavailable=...]` 记录这种情况。上游 DLSS-G 回退路径继续生效。这项修复处理原先最多等待 500 ms 的槽位检查。首次创建 NR 模型的成本和实际操作延迟仍需要实机测量。

回归脚本会提取并编译生产代码中这段槽位处理逻辑，使用模拟 Vulkan fence 验证空闲、已完成、未完成及设备错误四种情况。可以传入支持 C++17 的 Clang 或 GCC，也可以使用 Zig。

呈现层首次创建模型时，提交包含初始化的命令缓冲区并沿用当前游戏画面。后续匹配的帧生成回调会跳过同一图像的重复处理，首个 NR 评估留到后续帧。提交失败时会退回原路径，设备丢失时保留可能仍在使用的命令槽。查看 `prepresent[... warmup_submissions=...]` 可区分模型预热和成功处理的帧。

超分后路径在输出图像句柄轮换时优先复用已经确认完成、尺寸和工作规格完全一致的暂存资源。这样可以减少短期内重复分配和显存压力。实际用量取决于游戏的图像轮换方式与资源完成状态，可通过日志中的 `upscaled_workset_reuses` 验证是否命中。

```powershell
python tools/endfield-validation/test_present_slot.py clang++
python tools/endfield-validation/test_present_warmup.py clang++
python tools/endfield-validation/test_upscaled_worksets.py clang++
```

## 20–30 FPS 的排查

检查到的 Vulkan 主路径没有固定 20–30 FPS 的限制。上游 `Throttle` 属于另一条旧的 D3D12 转接路径。需要用同场景遥测分别定位 GPU 时间、显存压力与呈现节奏。

候选代码没有按奇偶帧跳过 NR，也没有改动着色器和帧生成倍率。保留同一配置下的时序处理是为了减少画面交替的风险。离线回归覆盖命令提交、回退和资源复用，当前版本尚无实机的帧率、操作延迟或闪烁结果。

## 1. 预检

```powershell
python tools/endfield-validation/validate.py preflight 'C:\Program Files\Hypergryph Launcher\games\Arknights Endfield'
```

预检需要 Python 3.11+ 和 `nvidia-smi`，检查上游要求的最低驱动、已知 Streamline 哈希和常见的旧 D3D12 插件文件。`ready_for_manual_vulkan_check` 之后还要实机确认 Vulkan 渲染器、上游插件版本、当前活动的 NR 层和插帧状态。若出现反作弊拦截，记录当次结果并结束该测试。

## 2. 稳定性基线

先按上游文档建立单独的 Vulkan 配置，保留现有 D3D12 配置的恢复记录。首轮固定相同输出分辨率和场景，关闭游戏帧生成和 NVIDIA Smooth Motion。使用上游允许的超分后单层 `U1/P0`，关闭呈现层，固定 NR 工作分辨率，记录在配置中。通过插件日志确认实际在 Vulkan 路径评估 NR，并记录真实的进程会话。

| 场景 | 验收条件 |
| --- | --- |
| 三次冷启动 | 每次均进入同一存档场景并确认 Vulkan 和 NR 生效 |
| 持续游玩 30 分钟 | 全程可交互，无崩溃、黑屏、驱动重置或客户端拦截 |
| 切场景、切窗口、切分辨率、开关 NR | 画面可恢复，NR 状态与设置一致，记录各次明显停顿 |
| 正常退出与回退 | 进程正常结束，恢复到原配置后能再次启动 |

上游离线测试独立于实机门槛。部署和回退夹具推荐用 PowerShell 7，深层目录在 Windows PowerShell 5.1 下可能触发路径长度限制。

```powershell
python -m unittest discover -s tools/endfield-validation -v
pwsh -NoProfile -File tools/unified-20261011/test-deploy.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File tools/unified-20261011/test-clean-recovery.ps1
```

## 3. 真实帧率与延迟对照

稳定性基线通过后，在同一台电脑、相同驱动、同一游戏版本和可重复场景采样。每组预热后录制三次，每次 90 秒。顺序为原版 NR 关、上游 NR 开、候选 NR 开。保持同一渲染 API、画质、视角、刷新率、功耗和 Reflex 设置。针对操作延迟安排相同的点击与镜头操作。

```powershell
PresentMon.exe --process_name Endfield.exe --v2_metrics --qpc_time_ms --track_frame_type --timed 90 --terminate_after_timed --output_file upstream-nr-1.csv
python tools/endfield-validation/validate.py analyze upstream-nr-1.csv
```

如进程有多个交换链，指定已确认的主交换链 `--swap-chain`。若没有帧类型标记，在人工确认所有插帧关闭后使用 `--confirm-fg-off`。存在明确的非应用帧时脚本会拒绝。分析输出提供真实应用帧率、P50/P95/P99 应用帧间隔、长帧次数以及可用的输入延迟数据。输入延迟空值代表缺少对应样本。需要另用可重复的输入到显示测量补齐。Windows 若拒绝启动 ETW 采样，需要从具有相应权限的终端启动 PresentMon。支持 `--track_pc_latency` 的版本可另外启用该项。

候选版本至少要改善同场景的真实帧率和慢帧，并用独立的输入延迟样本验证响应。对照快速运动、光照切换和 UI 叠加画面，记录画质折损。实际数据与原始 CSV 一并保留。

[上游发布说明](https://github.com/Yukikaze20170315/endfield-dlss5-vulkan-plugin/releases/tag/unified-20261103l) · [PresentMon 指标定义](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md#csv-columns)
