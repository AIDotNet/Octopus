<div align="center">

<img src="src/Octopus.App/Assets/Icon/logo.png" alt="Octopus Logo" width="116" />

# Octopus

**跨平台终端管理与 AI 编码智能体工作台**

按项目组织终端、61 款 AI 编码智能体、文件、Git 工作树、SSH 隧道与远程设备 —— NativeAOT 原生编译，端到端加密。

[English](README.md) · **简体中文**

[![GitHub Release](https://img.shields.io/github/v/release/AIDotNet/Octopus?include_prereleases&style=flat-square)](https://github.com/AIDotNet/Octopus/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/AIDotNet/Octopus/ci.yml?branch=main&style=flat-square&label=CI)](https://github.com/AIDotNet/Octopus/actions/workflows/ci.yml)
[![GitHub Stars](https://img.shields.io/github/stars/AIDotNet/Octopus?style=flat-square)](https://github.com/AIDotNet/Octopus/stargazers)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![Avalonia 12.2](https://img.shields.io/badge/Avalonia-12.2.0-8B44AC?style=flat-square)](https://avaloniaui.net/)
[![TKCode.Arc 1.3](https://img.shields.io/badge/TKCode.Arc-1.3.1-0EA5E9?style=flat-square)](https://docs.tkcode.si)

[官网](https://octoterm.si/) · [下载安装包](https://github.com/AIDotNet/Octopus/releases) · [更新日志](CHANGELOG.md) · [反馈问题](https://github.com/AIDotNet/Octopus/issues) · [交流群](#交流群)

</div>

---

## 核心一览

| 维度 | Octopus 提供的能力 |
| --- | --- |
| **原生高性能** | 基于 .NET 10、Avalonia 12 与 NativeAOT 原生编译，覆盖 macOS / Windows / Linux —— 启动快、内存占用低，无浏览器内核开销。 |
| **61 款智能体 · 双模式** | 自动探测 61 款主流编码智能体 CLI；其中 **49 款支持 ACP 图形界面**，可在真终端与图形对话间无缝切换，支持多订阅账户隔离。 |
| **零知识远程接管** | 通过局域网直连、官方密文中转或自托管服务，在另一台电脑或 Android 手机端继续操控主机 —— 私钥永不出设备。 |
| **插件与透明安全** | 内置 `Jaunt` 独立 JS 插件引擎与细粒度授权、92 天多账户用量账单导出，以及 ECDSA P-256 签名校验的自动更新。 |

---

## 核心特性

### 项目化工作台
- **项目与分组管理**：按项目与项目组收纳终端、编码智能体与打开的文件，切换项目时后台会话持续运行；支持 Lucide 图标、像素图标与自定义图片，以及拖放排序。
- **项目组共享工作目录**：在项目组右键开启「合并工作目录」后，启动支持附加目录的智能体时会自动携带同组其他本地项目路径。
- **拖拽分屏与分支工作区**：支持左右 / 上下自由分屏并按项目记忆布局；内置 **Git Worktree 分支工作区**，可让多个智能体在相互隔离的分支中并行开发。
- **精致桌面体验**：支持**紧凑 / 宽松**界面风格切换、明暗主题与中英双语界面；支持系统托盘 / 菜单栏常驻、重启后自动恢复标签与智能体会话，并在 `~/.octopus/logs/` 记录异常日志保障主程序稳定运行。

### 61 款内置 AI 编码智能体与 ACP 图形界面
- **双模式工作流（终端 ⇄ ACP GUI）**：
  - 既可在原生终端中运行任意智能体 CLI，也可一键切换至 **ACP（Agent Client Protocol）图形界面**（内置 **49 款**智能体支持，涵盖原生 ACP 与官方适配器，另支持自定义智能体定义与可选 ACP Registry）。
  - 提供统一工作时间线与停靠式活动托盘、可视化权限确认卡片与表单问答、可折叠工具调用与内联代码 Diff、提示词队列与**立即发送**、内置模型 / 服务商品牌图标、第三方 API 模型自动发现与切换，以及一键**在终端中继续**无缝接力。
- **多订阅账户与会话管理**：
  - 支持为同一智能体配置多个订阅账户（按环境变量隔离独立目录并发运行，或按系统钥匙串保存 / 切换登录快照），可绑定项目专属账户或启动时按需选用。
  - 右侧面板「会话」标签支持浏览历史会话、按 ID 恢复、跨账户复制以及跨智能体交接。
- **实时状态与后台任务追踪**：
  - 跨轮次追踪活跃后台工作流与异步子智能体状态；通过智能体钩子与内置轻量可执行文件 `octopus-signal`（仅限当前用户的本地通道）实时显示运行状态与等待回复提醒。
  - 默认自动向命令行及 ACP 子进程注入系统代理（`HTTP_PROXY` / `HTTPS_PROXY`），且不会覆盖用户显式设置的代理变量。

<details>
<summary><strong>查看全部 61 款内置编码智能体（28 款原生 ACP · 21 款 ACP 适配器 · 12 款终端 CLI）</strong></summary>

#### 原生 ACP + 终端模式（28）

| | | | |
| --- | --- | --- | --- |
| Antigravity CLI (`agy`) | Augment Code (Auggie) | Autohand Code | Cline |
| CodeBuddy Code | Cursor Agent | Devin CLI | Factory Droid |
| Gemini CLI | GitHub Copilot CLI | Goose | Grok CLI |
| Hermes Agent | IBM Bob | Junie | Kilo Code |
| Kimi Code CLI | Kiro | MiMo Code | Muse Code |
| omp | OpenCode | OpenHands CLI | Qoder CLI |
| Qoder CLI CN | Qwen Code | Stakpak | VT Code |

#### ACP 适配器 + 终端模式（21）

| | | | |
| --- | --- | --- | --- |
| Agoragentic | Amp | Claude Code | Codex CLI |
| Cortex Code | Corust Agent | crow-cli | DeepAgents |
| DimCode | Dirac | fast-agent | GLM Agent |
| Harn | Kimchi | MiniMax Code | Minion Code |
| Mistral Vibe | Nova | Pi | Poolside |
| siGit Code | | | |

#### 仅终端 CLI 模式（12）

| | | | |
| --- | --- | --- | --- |
| Aider | Codebuff | Comate CLI | Continue CLI |
| Crush | ForgeCode | Jules Tools | Letta Code |
| Nanocoder | OpenClaw | Trae Agent | Warp Oz CLI |

> 你也可以在「设置 → 智能体」中自由新增自定义智能体，或覆写启动命令、参数与环境变量。

</details>

### 高性能原生终端
- 基于 `MiniPty` 与 `TKCode.Arc.Terminal` 构建，支持同步渲染（`RenderHold`）以减少画面撕裂与闪烁。
- 支持历史滚动条（回滚行数最高可配至 100,000 行）、可点击的 **OSC 8 超链接**（悬停预览真实地址）、按住 `Ctrl` / `Cmd` 点击文件路径（含 `:行:列`）直达工作区查看器、剪贴板图片自动转存临时 PNG 并粘贴路径、Kitty 扩展键盘协议、完整中文输入法支持以及贴合各平台习惯的快捷键。

### 文件、查看器与 Git 集成
- **按需加载的右侧工作区**：面板关闭时不读文件树、不监听变更、不执行 Git；展开后呈现彩色 VS Code 风格文件图标、完整文件树增删改（支持回收站）以及全局文件名快速搜索。
- **内置多格式查看器**：代码语法高亮查看器、左右分栏 **Markdown 编辑器与实时预览（原生支持 Mermaid 图表渲染，无需浏览器）**、图片查看器（支持 SVG 与动图）以及高亮 Diff 视图。
- **Git 工作流**：内置改动列表、暂存 / 取消暂存、提交（完整透传 Git 原始提示）、分支与提交记录浏览，以及工作树（Worktree）管理。

### SSH 主机与可视化端口隧道
- **SSH 配置管理**：在设置中直接新增、编辑、删除 `~/.ssh/config` 主机并测试连通性，支持 **SSH / Mosh / Telnet** 协议，并自动兼容中文等非 ASCII 主机别名。
- **远程项目与端口转发**：SSH 项目提供独立远程终端、远端目录浏览器与文件树、本机与远程文件复制，以及可视化**本地（`-L`）/ 远程（`-R`）端口转发隧道**管理与实时日志查看。

### 远程访问、手机端与端到端安全
无需账号即可完整使用本地桌面端与局域网直连；需要跨网远程接管时，可登录官方中转或自建服务。

| 连接方式 | 需要账号 | 说明 |
| --- | --- | --- |
| **局域网直连** | 否 | 同一网络内通过 6 位验证码或配对链接直接配对连接，全程端到端加密。 |
| **官方密文中转（`octoterm.si`）** | 是 | 登录同一账号即可看到账号下全部在线 / 离线设备，跨网络经零知识中转仅转发密文。 |
| **自托管服务** | 自定义 | 自行部署 Octopus Server 并在登录处填写服务器地址，体验与加密强度完全一致。 |

- **零知识端到端加密**：每台设备本地生成 **ECDSA P-256** 身份密钥（私钥永不上传）；每次连接通过临时 **ECDH P-256** + **HKDF-SHA256** 协商会话密钥，所有消息以 **AES-256-GCM** 加密传输，中转服务无法读取任何明文。
- **8 位设备访问密码**：已登录主机在本地钥匙串生成（或自定义）8 位访问密码；同账号设备首次接入时仅在两端之间通过 **PBKDF2-SHA256 + HMAC-SHA256** 完成零知识验证，密码不经过也不存放在中转服务，已授权设备随时可撤销。
- **手机配套端（`Octopus.Mobile`）**：基于 `TKCode.Arc.Mobile` 打造的触控优先客户端，支持远程 **ACP 图形对话**（统一工作区与滑动实时状态条）、远程交互式终端、远程文件浏览与 Git Diff 查看、项目搜索与状态筛选、工作区分组、图片上传以及 Android 应用内自动更新。

### 插件系统、多账户用量账单与签名更新
- **沙盒化 JS 插件引擎**：插件以目录包形式运行在独立的 **`Jaunt`** JavaScript 运行时中，通过声明式清单与细粒度权限授予机制控制能力边界（智能体信号 / 探针、限定范围文件读取、白名单域名网络访问、系统钥匙串）。
- **内置用量与账单插件（`octopus.agent-usage`）**：汇总多账户订阅配额窗口（Claude Code、Codex、Antigravity CLI、Copilot、Grok、Cursor、OpenCode）与第三方 API 余额，保留 **92 天**本地 token 与估算费用历史，支持阈值提醒以及导出可打印的 **HTML 小票账单**与 **CSV** 明细。
- **ECDSA 签名自动更新**：更新清单（`update.json`）通过内置受信任 **ECDSA P-256** 公钥验签，并校验安装包大小与 **SHA-256**；支持正式版 / 预览版通道切换与 Markdown 更新说明预览。

---

## 下载与安装

前往 [GitHub Releases](https://github.com/AIDotNet/Octopus/releases) 或 [官网](https://octoterm.si/) 下载对应平台安装包（桌面端均采用 NativeAOT 编译）：

| 平台 | 发行产物 | 系统要求与说明 |
| --- | --- | --- |
| **macOS** | `Octopus-<版本>-osx-arm64.dmg`<br>`Octopus-<版本>-osx-x64.dmg` | **macOS 13+**（Apple 芯片 / Intel）。拖入应用程序目录即可；另提供 `.zip` 供自动更新。卸载前可运行 `/Applications/Octopus.app/Contents/MacOS/Octopus --cleanup-hooks` 清理外部智能体钩子。 |
| **Windows** | `Octopus-<版本>-win-x64-setup.exe`<br>`Octopus-<版本>-win-x64.zip` | **Windows 10/11（64 位）**。Inno Setup 安装向导支持自定义目录与所有用户安装，卸载时自动清理外部智能体钩子；另提供免安装 `.zip` 便携包。 |
| **Linux** | `Octopus-<版本>-linux-x64.run`<br>`Octopus-<版本>-linux-x64.tar.gz` | **Linux x64**。`.run` 支持交互指定目录或 `--prefix DIR`（默认 root 装 `/opt/octopus`、普通用户装 `~/.local/share/octopus`），自带 `uninstall.sh` 并在卸载时自动清理外部智能体钩子；另提供便携 `.tar.gz`。 |
| **Android** | `Octopus-<版本>-android.apk` | **Android 7.0+（API 24+）**。正式发布包由发布密钥签名并纳入 `update.json`，支持直接覆盖升级与应用内自动更新。*（未配置签名密钥的自定义构建会生成 `*-android-debug.apk`。）* |

> **iOS**：iPhone 客户端源码（iOS 15+，真机采用 NativeAOT 编译）已包含在 [`Octopus.Mobile.slnx`](Octopus.Mobile.slnx) 中，暂未上架 App Store。

### 数据、缓存与日志目录

桌面端数据现统一存放在用户主目录下的 `.octopus`（旧版本数据会在首次启动时自动一次性迁移）：

| 平台 | 应用数据与崩溃日志 | 可清理缓存 |
| --- | --- | --- |
| **macOS** | `~/.octopus/` *（日志位于 `~/.octopus/logs/`）* | `~/Library/Caches/Octopus/` |
| **Linux** | `~/.octopus/` *（日志位于 `~/.octopus/logs/`）* | `~/.cache/octopus/` *（或 `$XDG_CACHE_HOME/octopus`）* |
| **Windows** | `%UserProfile%\.octopus\` *（日志位于 `logs\`）* | `%LocalAppData%\Octopus\Cache\` |

---

## 终端使用提示与平台说明

<details>
<summary><strong>快捷键、历史滚动、路径跳转与 Windows ConPTY 说明</strong></summary>

### 常用交互与快捷键
- **历史滚动**：存在历史输出时，终端右侧自动显示滚动条；支持滚轮、`Shift+PageUp` / `Shift+PageDown` 翻页及 `Shift+Home` / `Shift+End` 跳至顶部 / 底部。当程序开启鼠标上报时，普通滚轮交由程序处理，按住 `Shift` 滚轮可翻看终端历史。全屏备用屏（`vim`、`less` 等）由程序自行管理滚动。
- **链接与文件路径跳转**：终端中的 OSC 8 超链接可直接点击打开（悬停显示真实目标地址）；按住 `Ctrl`（macOS 为 `Cmd`）悬停真实存在的文件路径（含 `:行:列`）可点击在工作区新标签页打开，文件夹则在系统文件管理器中显示*（暂仅限本地终端）*。
- **编辑、复制粘贴与图片**：
  - `Shift+Enter` 与 `Ctrl+J` 可在智能体命令行中插入换行而不直接提交。
  - 剪贴板仅有图片时粘贴会自动存为临时 PNG 并输入路径（临时文件超过一天自动清理）。
  - **macOS**：支持 `Cmd+←/→`、`Cmd+Backspace`、`Option+←/→` 等原生行编辑习惯，不影响全屏程序与 Kitty 协议程序。
  - **Windows / Linux**：选中文字后按 `Ctrl+C` 复制（取消选区后再次按下仍发送中断）；右键有选区时复制、无选区时粘贴；全平台支持 `Shift+Insert` 粘贴，Windows 另支持直接 `Ctrl+V` 粘贴。

### Windows 平台说明
- 使用系统原生 **ConPTY**，无需安装 Bash 或 WSL；默认按 **PowerShell 7 → Windows PowerShell → cmd** 顺序选择 Shell，也可在「设置 → 终端」切换 Git Bash 等本机已安装的其他 Shell。
- 终端启动时自动合并注册表中最新的用户 / 系统 `PATH`，新安装的命令行工具无需重启 Octopus 即可直接使用。
- npm 等工具安装的 `.cmd` / `.bat` 智能体通过 `cmd.exe` 执行探测（保留输出捕获与超时控制），ACP 原生适配器也会自动解析 Windows 批处理包装脚本。
- 为兼容商店版 PowerShell，终端沿用 ConPTY 控制台会话生命周期，不强制把子进程放入 Job Object；关闭终端会结束控制台会话，但不会强行终止已独立拉起的 GUI 程序。

</details>

---

## 开发与架构

### 环境要求
- **.NET 10 SDK**（具体版本见 [`global.json`](global.json)）；macOS 13 及以上可运行。
- 可选：构建移动端原生包需安装 `android` / `ios` 工作负载（见 [`Octopus.Mobile.slnx`](Octopus.Mobile.slnx)）。

### 快速运行与测试

```bash
# 运行桌面端
dotnet run --project src/Octopus.App

# 运行手机端桌面预览壳
dotnet run --project src/Octopus.Mobile.Desktop

# 构建并运行全部测试
dotnet build Octopus.slnx
dotnet test Octopus.slnx
```

### 项目结构

```text
src/
├── Octopus.Core                              领域模型、设置与服务端点定义
├── Octopus.Storage                           本地持久化（projects.json、settings.json 等）
├── Octopus.Platform(.MacOS/.Linux/.Windows)  Shell 发现、环境、路径、系统钥匙串与数据迁移
├── Octopus.Terminal(.MiniPty/.Avalonia)      PTY 抽象、MiniPty 后端与 Avalonia 终端控件
├── Octopus.Agents                            61 款智能体定义、扫描、多账户、会话与状态信号
├── Octopus.Acp                               ACP JSON-RPC 引擎、19 种方言适配与适配器托管
├── Octopus.SignalForwarder                   随附的 NativeAOT 本地信号转发器（octopus-signal）
├── Octopus.FileSystem                        文件树、监听、搜索与目录浏览
├── Octopus.Git                               Git 状态、Diff、暂存提交与 Worktree 管理
├── Octopus.Ssh                               SSH / Mosh / Telnet 配置、远端文件树与端口隧道
├── Octopus.Plugins(.Jaunt)                   插件清单、权限策略与 Jaunt JS 运行时
├── Octopus.Updates                           ECDSA 签名更新清单校验与自动更新状态机
├── Octopus.Remote(.Host/.Client)             P-256 设备身份、端到端安全通道、局域网与中转传输
├── Octopus.App                               跨平台 Avalonia 桌面端主程序与内置插件
└── Octopus.Mobile(.Desktop/.Android/.iOS)    手机端界面与各平台壳（Android/iOS 位于 Octopus.Mobile.slnx）
tests/                                        各模块单元测试与无头 UI 测试
packaging/                                    macOS 配置、Windows（Inno Setup）与 Linux 安装器资源
tools/                                        打包、图标生成、更新签名与发布辅助脚本
```

### CI、打包与发布

| 工作流 | 触发条件 | 用途 |
| --- | --- | --- |
| [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | 推送 `main` 与 PR | 在 Linux / macOS / Windows 上自动构建并运行全部测试。 |
| [`.github/workflows/mobile.yml`](.github/workflows/mobile.yml) | 移动端及共享模块变更 | 构建 Android Release APK 与 iOS 模拟器 / 未签名 NativeAOT 真机包。 |
| [`.github/workflows/release.yml`](.github/workflows/release.yml) | 标签 `vX.Y.Z` / `vX.Y.Z-preview.N` | 自动构建 macOS (arm64/x64)、Windows (x64)、Linux (x64) 与 Android APK，签名 `update.json` 并发布至 GitHub Release 与 Octopus Server*（手动触发为 dry run，仅出产物不发版）*。 |

```bash
# 本地打包脚本
tools/package-macos.sh osx-arm64 1.0.0 dist
tools/package-linux.sh 1.0.0 <linux-x64 发布目录> dist
```

<details>
<summary><strong>发布签名密钥配置与发版核对流程</strong></summary>

- **自动更新清单签名**：发布时由 `tools/make-update-manifest.py` 生成 `update.json`，使用仓库 Secret `UPDATE_SIGNING_KEY`（ECDSA P-256 PEM 私钥，可由 `tools/make-update-key.sh` 生成，受信任公钥位于 [`src/Octopus.Updates/UpdateEndpoints.cs`](src/Octopus.Updates/UpdateEndpoints.cs)）签名；配置 `OCTOPUS_RELEASE_TOKEN` 后还会同步发布至 Octopus Server（默认 `https://octoterm.si`）。
- **macOS 签名与公证**：通过仓库 Secrets `APPLE_CERTIFICATE`（`.p12` base64）、`APPLE_CERTIFICATE_PASSWORD`、`APPLE_ID`、`APPLE_TEAM_ID`、`APPLE_APP_SPECIFIC_PASSWORD` 启用；未配置时仅做 ad-hoc 签名、不公证。Windows 安装包当前无代码签名。
- **Android 正式签名**：通过仓库 Secrets `ANDROID_KEYSTORE`（由 `tools/make-android-keystore.sh` 生成的 base64 keystore）、`ANDROID_KEYSTORE_PASSWORD` 与 `ANDROID_KEY_ALIAS` 启用；未配置时产出 `*-android-debug.apk` 且不纳入 `update.json`。
- **完整发版流程**：见 [`.agents/skills/publish-release/SKILL.md`](.agents/skills/publish-release/SKILL.md)。

</details>

---

## 交流群

欢迎扫码加入 Octopus 微信交流群：

<img src="images/wechat.png" alt="Octopus 微信交流群" width="260" />

---

## Star 进展

<a href="https://www.star-history.com/#AIDotNet/Octopus&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=AIDotNet/Octopus&type=Date&theme=dark" />
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=AIDotNet/Octopus&type=Date" />
    <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=AIDotNet/Octopus&type=Date" />
  </picture>
</a>
