# 《明日方舟：终末地》一键安装器

Windows x64 桌面程序。自动查找启动器注册表、活动游戏路径及常见游戏目录，并验证 `Endfield.exe` 的发行者签名和 Unity 游戏文件。找不到时可以手动选择 `Endfield.exe`。

1. 在启动器取消勾选 **DirectX 11 启动**，退出游戏。
2. 打开安装器，等待自动查找，点击 **一键安装**。
3. 批准 Windows 管理员提示，等待安装完成。游戏通过官方启动器启动，按 Home 查看面板。

首版面向 GeForce RTX 50 系列和 NVIDIA 615.00 或更新驱动。游戏的神经渲染依赖游戏自身的 DLSS 超分辨率调用。安装器保留原游戏画质选项和驱动设置。已有终末地使用本套件时，配置文件会先备份，已有选项继续保留；新配置采用简体中文及 50% 工作分辨率的超分后单层。帧生成覆盖默认关闭。

## 构建

在 Windows PowerShell 或 PowerShell 7 中运行。`dist/endfield-candidate` 是 CI 的原生插件输出目录，需包含同一候选版的 `renodx-dlss5.addon64` 和 `renodx-mfgunlock.addon64`。

```powershell
./games/endfield/installer/Build-Installer.ps1 `
  -CandidateDirectory ./dist/endfield-candidate `
  -OutputPath ./dist/Endfield-Installer.exe
```

构建使用 Windows 自带的 .NET Framework x64 编译器。生成的 EXE 内嵌汉化组件及其 SHA-256。默认按固定 SHA-256 从官方 ReShade 下载页和上游发布页获取其余组件，不从压缩包安装旧 UI 或修改版 NVIDIA 运行库。个人有权使用这些文件且希望本地离线构建时，可另外传入 `-RuntimeArchive` 和 `-ReShadeInstaller`，输入仍必须匹配固定摘要。

`--self-test <report>` 在临时测试目录验证设置、回滚及嵌入的 x64 组件。可追加 `--self-test <report> <runtime.zip>` 校验上游压缩包和签名运行库。`--find <report>` 仅输出识别出的安装目录，便于验证而不运行界面。

## 安装边界与回滚

- ReShade 使用其官方 Vulkan 安装程序。完全相同的既有 6.8.0 完整加载项版本会复用。需要执行官方安装时，会拒绝可能被它覆盖的旧用户级、32 位或不同版本的注册。
- 程序只安装汉化 Generic、配套 MFG 及经 NVIDIA 签名的 RTX 50 NR 运行库。确切匹配的旧上游 Bridge 可备份后退役。未知 Bridge 或会冲突的旧渲染代理会中止安装。
- 安装前把管理范围内的文件备份到 `%ProgramFiles%\GameRenderLabData`，写入事务记录和预期文件摘要。点击 **回滚安装** 恢复这些文件，原有用户设置也可恢复。回滚会移除安装器新增的游戏白名单项，供其他游戏共享的 ReShade Vulkan 层会保留。
- 安装器依照正常的 Windows 管理员授权和 ReShade Vulkan 加载流程工作。游戏、反作弊组件和显卡配置由各自的官方程序管理。

[ReShade 6.8.0 官方安装程序源码](https://github.com/crosire/reshade/blob/v6.8.0/setup/MainWindow.xaml.cs) · [终末地上游固定版本](https://github.com/Yukikaze20170315/endfield-dlss5-vulkan-plugin/releases/tag/unified-20261103l)
