<div align="center">

<img src="src/Octopus.App/Assets/Icon/logo.png" alt="Octopus Logo" width="120" />

# Octopus

**跨平台终端管理与 AI 编码智能体工作台**

按项目组织终端、编码智能体、文件、Git、SSH 主机与远程设备。

[![GitHub Release](https://img.shields.io/github/v/release/AIDotNet/Octopus?include_prereleases&style=flat-square)](https://github.com/AIDotNet/Octopus/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/AIDotNet/Octopus/ci.yml?branch=main&style=flat-square&label=CI)](https://github.com/AIDotNet/Octopus/actions/workflows/ci.yml)
[![GitHub Stars](https://img.shields.io/github/stars/AIDotNet/Octopus?style=flat-square)](https://github.com/AIDotNet/Octopus/stargazers)
[![.NET 11](https://img.shields.io/badge/.NET-11-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![Avalonia 12.2](https://img.shields.io/badge/Avalonia-12.2.0-8B44AC?style=flat-square)](https://avaloniaui.net/)
[![TKCode.Arc 1.3](https://img.shields.io/badge/TKCode.Arc-1.3.1-0EA5E9?style=flat-square)](https://docs.tkcode.si)

[官网](https://octoterm.si/) · [下载安装包](https://github.com/AIDotNet/Octopus/releases) · [更新日志](CHANGELOG.md) · [反馈问题](https://github.com/AIDotNet/Octopus/issues)

</div>

---

## 核心特性

- **项目化工作台**：按项目与项目组管理多个终端和智能体会话，支持自定义图标（Lucide / 像素图标 / 自定义图片）、分组与拖放排序、项目组共享工作目录、Git Worktree 分支工作区、紧凑 / 宽松界面风格以及系统托盘常驻。
- **40 款内置 AI 编码智能体 & ACP 图形界面**：
  - 自动扫描并识别主流命令行编码智能体，也可在设置中自由新增或覆盖启动参数。
  - 支持**终端模式**与 **ACP（Agent Client Protocol）图形界面模式**双界面切换，提供权限确认卡片、实时工具调用展开、提示词队列与立即发送等体验。
  - 支持**多订阅账户管理**、**会话历史浏览 / 恢复 / 跨智能体交接**、**子智能体追踪**、**系统代理自动注入**，以及基于钩子与本地信号通道的实时运行状态和等待回复提醒。
- **高性能原生终端**：基于 `MiniPty` 与 `TKCode.Arc.Terminal`，发布包采用 NativeAOT 原生编译，启动快、内存占用低；支持历史滚动条、OSC 8 超链接、点击文件路径直达工作区查看器、剪贴板图片粘贴、扩展键盘协议（Kitty 协议）以及贴合各平台习惯的快捷键。
- **文件、查看器与 Git 集成**：右侧工作区面板按需加载，内置文件树操作、全局文件搜索、代码 / Markdown / 图片（含 SVG 与动图）/ Diff 查看器，以及 Git 暂存、提交、分支与工作树管理。
- **SSH 主机与端口隧道**：在设置中直接管理 `~/.ssh/config`（支持 SSH / Mosh / Telnet，兼容中文别名），远程项目提供独立文件树、远程终端、跨机文件复制与可视化端口转发隧道。
- **远程访问与移动端**：支持多台电脑间通过局域网直连或加密中转互访（配对授权、随时撤销），在远端主机上运行项目、终端与智能体；配套 `Octopus.Mobile` 手机端。
- **插件系统与用量账单**：基于独立 JavaScript 引擎（`Jaunt`）与权限授予机制的插件架构；内置 `octopus.agent-usage` 插件，提供多账户配额汇总、92 天用量历史、费用估算以及 HTML / CSV 账单导出。
- **安全自动更新**：更新清单（`update.json`）经 ECDSA P-256 签名并校验安装包大小与 SHA-256，支持正式版 / 预览版通道切换与更新日志预览。

<details>
<summary>查看全部 40 款内置支持的编码智能体</summary>

| | | | |
| --- | --- | --- | --- |
| Antigravity CLI (`agy`) | Aider | Amp | Augment Code (Auggie) |
| Autohand Code | IBM Bob | Claude Code | Cline |
| CodeBuddy Code | Codebuff | Codex CLI | Continue CLI |
| GitHub Copilot CLI | Crush | Cursor Agent | Devin CLI |
| Factory Droid | Gemini CLI | Goose | Grok CLI |
| Hermes Agent | Jules Tools | Junie | Kilo Code |
| Kimi Code CLI | Kiro | Letta Code | MiMo Code |
| MiniMax Code | Muse Code | Nanocoder | omp |
| OpenCode | OpenHands CLI | Pi | Qoder CLI |
| Qwen Code | Stakpak | Mistral Vibe | VT Code |

</details>

---

## 下载与安装

前往 [GitHub Releases](https://github.com/AIDotNet/Octopus/releases) 下载对应平台的安装包（桌面端均采用 NativeAOT 编译）：

| 平台 | 发行产物 | 说明 |
| --- | --- | --- |
| **macOS** | `Octopus-<版本>-osx-arm64.dmg` / `osx-x64.dmg` | 拖入应用程序目录即可；另提供 `.zip` 供自动更新使用 |
| **Windows** | `Octopus-<版本>-win-x64-setup.exe` / `.zip` | Inno Setup 安装向导（支持自定义目录及所有用户安装），另提供免安装便携包 |
| **Linux** | `Octopus-<版本>-linux-x64.run` / `.tar.gz` | `.run` 支持交互式指定目录或 `--prefix DIR`（默认 root 装 `/opt/octopus`、普通用户装 `~/.local/share/octopus`，含 `uninstall.sh`），另提供便携归档 |
| **Android** | `Octopus-<版本>-android.apk` | 手机端手动安装；当前使用 Android SDK 默认调试密钥签名，升级前需先卸载旧版，不纳入自动更新清单 |

> iOS 客户端源码已包含在仓库中，暂未发布安装包。

### 数据目录

| 平台 | 默认路径 |
| --- | --- |
| macOS | `~/Library/Application Support/Octopus/` |
| Linux | `~/.config/octopus/` |
| Windows | `%AppData%\Octopus\` |

---

## 终端使用提示

- **历史滚动**：存在历史输出时，终端右侧自动显示滚动条；支持滚轮、`Shift+PageUp` / `Shift+PageDown` 翻页以及 `Shift+Home` / `Shift+End` 跳至顶部 / 底部。当智能体或程序开启鼠标上报时，普通滚轮交由程序处理，按住 `Shift` 滚动滚轮可查看终端历史。全屏备用屏（如 `vim`、`less`）由程序自行管理滚动，不显示主屏滚动条。
- **链接与文件跳转**：终端中输出的超链接（OSC 8）可直接点击打开；按住 `Ctrl`（macOS 为 `Cmd`）悬停真实存在的文件路径（含 `:行:列`）可点击在工作区新标签页中打开（SSH 终端暂不支持）。
- **快捷键与图片粘贴**：支持 `Shift+Enter` 与 `Ctrl+J` 换行；剪贴板仅有图片时粘贴会自动存为临时 PNG 并输入路径；macOS 支持 `Cmd+←/→`、`Cmd+Backspace`、`Option+←/→` 等原生行编辑习惯；Windows / Linux 选中文字后 `Ctrl+C` 复制（取消选区后再次按下仍发送中断），右键复制选区或无选区时粘贴。
- **Windows 平台说明**：
  - 使用系统原生 ConPTY，默认按 **PowerShell 7 → Windows PowerShell → cmd** 顺序选择 Shell，无需安装 Bash 或 WSL（也可在设置中切换本机已安装的其他 Shell）。
  - 终端启动时自动合并最新注册表 `PATH`，新安装的工具无需重启 Octopus 即可使用；npm 等工具安装的 `.cmd` / `.bat` 智能体会通过 `cmd.exe` 执行探测，保留输出捕获与超时控制。
  - 为兼容商店版 PowerShell，终端沿用 ConPTY 控制台会话生命周期，不强制把子进程放入 Job Object；关闭终端会结束控制台会话，但不会强行终止已独立拉起的 GUI 程序。

---

## 开发与测试

### 环境要求

- **.NET 11 SDK**（具体版本见 [`global.json`](global.json)）
- 移动端构建另需安装 `android` / `ios` 工作负载（见 [`Octopus.Mobile.slnx`](Octopus.Mobile.slnx)）

### 运行

```bash
# 运行桌面端
dotnet run --project src/Octopus.App

# 运行手机端桌面预览壳
dotnet run --project src/Octopus.Mobile.Desktop
```

### 构建与测试

```bash
# 构建与单元测试
dotnet build Octopus.slnx
dotnet test Octopus.slnx

# 终端层真机冒烟测试（会短暂弹出窗口）
dotnet run --project tests/Octopus.Terminal.Smoke -- --script
```

> 终端冒烟测试在 Windows 上启动本机默认 PowerShell，检查输入输出、窗口缩放、`Ctrl+C`、退出码与重启；在 macOS / Linux 上使用登录 Shell，并额外检查设备应答、`vim` 与 `terminfo`。

---

## 项目结构

```text
src/
├── Octopus.Core                    领域模型、设置与服务端点定义
├── Octopus.Storage                 本地持久化（projects.json、settings.json 等）
├── Octopus.Platform(.MacOS/.Linux/.Windows)
│                                   Shell 解析、环境、路径、钥匙串与平台集成
├── Octopus.Terminal(.MiniPty/.Avalonia)
│                                   PTY 抽象、MiniPty 后端与 Avalonia 终端控件
├── Octopus.Agents                  40 款智能体目录、扫描、多账户、会话与状态信号
├── Octopus.Acp                     ACP 协议引擎（JSON-RPC、多方言适配与会话宿主）
├── Octopus.SignalForwarder         随附的轻量信号转发可执行文件（octopus-signal）
├── Octopus.FileSystem              文件树、监听、搜索与目录浏览
├── Octopus.Git                     Git 状态、Diff、提交与 Worktree 管理
├── Octopus.Ssh                     SSH / Mosh / Telnet 配置管理、远程文件树与端口隧道
├── Octopus.Plugins(.Jaunt)         插件清单、权限策略与 Jaunt JS 运行时
├── Octopus.Updates                 签名更新清单校验、下载与自动更新状态机
├── Octopus.Remote(.Host/.Client)   设备身份、安全通道、局域网与加密中转远程访问
├── Octopus.App                     跨平台 Avalonia 桌面端主程序与内置插件
└── Octopus.Mobile(.Desktop/.Android/.iOS)
                                    手机端界面与各平台壳（Android/iOS 位于 Octopus.Mobile.slnx）
tests/                              各模块单元测试与终端冒烟测试
packaging/                          macOS、Windows（Inno Setup）、Linux 打包配置
tools/                              打包、图标生成、更新签名与发布辅助脚本
```

---

## CI 与发布

- **持续集成**：
  - [`.github/workflows/ci.yml`](.github/workflows/ci.yml)：在 `main` 分支 push 与 PR 时，于 Linux / macOS / Windows 上自动构建并运行全部测试。
  - [`.github/workflows/mobile.yml`](.github/workflows/mobile.yml)：当移动端相关代码变更时，构建 Android APK 与 iOS（模拟器及未签名设备包）。
- **版本发布**：
  - [`.github/workflows/release.yml`](.github/workflows/release.yml)：推送 `vX.Y.Z` 或 `vX.Y.Z-preview.N` 标签后自动构建 macOS / Windows / Linux / Android 产物并发布 GitHub Release（在 Actions 页面手动触发为 dry run，仅产出构建产物不发版）。
  - **自动更新清单**：发布时由 `tools/make-update-manifest.py` 生成 `update.json`，使用仓库 Secret `UPDATE_SIGNING_KEY`（ECDSA P-256 PEM 私钥，可由 `tools/make-update-key.sh` 生成，对应公钥位于 `src/Octopus.Updates/UpdateEndpoints.cs`）签名；配置 `OCTOPUS_RELEASE_TOKEN` 后还会同步发布至 Octopus Server（默认 `https://octoterm.si`）。
  - **macOS 签名与公证**：通过仓库 Secrets `APPLE_CERTIFICATE`（`.p12` base64）、`APPLE_CERTIFICATE_PASSWORD`、`APPLE_ID`、`APPLE_TEAM_ID`、`APPLE_APP_SPECIFIC_PASSWORD` 启用；未配置时仅做 ad-hoc 签名、不公证。Windows 安装包当前无代码签名。
  - **本地打包脚本**：
    - macOS：`tools/package-macos.sh osx-arm64 0.1.0 dist`
    - Linux：`tools/package-linux.sh 0.1.0 <linux-x64 发布目录> dist`
  - 完整发版核对流程见 [`.agents/skills/publish-release/SKILL.md`](.agents/skills/publish-release/SKILL.md)。

---

## Star 进展

<a href="https://www.star-history.com/#AIDotNet/Octopus&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=AIDotNet/Octopus&type=Date&theme=dark" />
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=AIDotNet/Octopus&type=Date" />
    <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=AIDotNet/Octopus&type=Date" />
  </picture>
</a>
