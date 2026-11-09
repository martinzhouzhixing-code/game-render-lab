# Game Render Lab

面向 Windows 游戏的渲染实验。每款游戏有独立的实现、安装入口和验证流程。当前首个项目是《明日方舟：终末地》的 DLSS 5 Vulkan 适配。

| 项目 | 说明 |
| --- | --- |
| [终末地安装器](games/endfield/installer/) | 自动查找游戏、一键部署经过校验的组件、记录备份 |
| [终末地源码](games/endfield/plugin/) | 汉化控制台、外观预设、层数滑条、帧生成页和 Vulkan 稳定性改动 |
| [原理与开发](games/endfield/DEVELOPMENT.md) | 工作分辨率、帧时间、安装事务、构建与验证 |

## 安装

下载 GitHub Releases 中的 Windows 安装器。打开后选择「查找终末地」，确认安装路径，再选择「安装」。ReShade 的 Vulkan 层由官方安装程序注册，Windows 可能显示一次管理员确认。游戏关闭时执行安装，启动器使用默认 Vulkan 模式。

首版针对配备 RTX 50 系列及受支持 NVIDIA 驱动的 Windows x64。启动后的 NR 依赖游戏本身输出 DLSS 超分画面。安装器保留现有游戏画质。游戏的超分选项已设为 DLSS 时，重启后即可从 Home 打开面板。

发布包包含本项目构建的界面组件。ReShade 安装程序和 NVIDIA 运行库通过固定的上游来源获取并校验。需要联网完成首次安装。

## 源码与许可

本仓库新增安装器、脚本和文档采用 MIT 许可。终末地插件基于上游 `unified-20261103l` 的 `e4f52e7c32e53359823105435e6335e59d2ca5b3`，上游及第三方文件保留各自许可。 [上游版本与致谢](https://github.com/Yukikaze20170315/endfield-dlss5-vulkan-plugin/releases/tag/unified-20261103l)
