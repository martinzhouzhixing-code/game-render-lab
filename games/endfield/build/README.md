# 构建《终末地》组件

CI 使用 GitHub 托管的 Windows 2022 环境。它从当前源码编译中文 NR 面板和配套 MFG Unlock，再交给安装器打包。测试只运行脱离游戏的策略及配置测试。

## 本机复现

需要 Python 3、Visual Studio 2022 的 x64 C++ 工具集及 Windows 10/11 SDK。可从仓库根目录运行下面的命令。

```powershell
./games/endfield/build/Build-Plugin.ps1
./games/endfield/installer/Build-Installer.ps1 `
  -CandidateDirectory ./dist/endfield-candidate `
  -OutputPath ./dist/Endfield-DLSS5-Setup.exe
```

第一步为已钉住的第三方头文件逐个核对 SHA-256，并检查产物的 x64 PE 头和公开导出。NR 和 MFG 的二进制文件以及源码提交与哈希清单保存在 `dist/endfield-candidate`。构建中间文件和头文件缓存写到 `.build`。

仓库保留上游源码及其许可。SDK 头文件由公开来源按锁定提交下载，并仅用来编译。SDK 头文件、NVIDIA 运行库和 ReShade 安装包不纳入 Git 或默认 CI 安装器的内置文件。使用 SDK 时适用原始许可。 [NVIDIA DLSS SDK 许可](https://github.com/NVIDIA/DLSS/blob/a291cc7d2cc642a51566f3dfd5376f635cd1b284/LICENSE.txt)

本地或 CI 构建通过只表明程序可编译，游戏内加载和性能需要在已安装游戏的 Windows 主机上另行验证。
