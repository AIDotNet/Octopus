# Octopus

终端管理 + 项目管理。按项目组织终端和编码智能体（智能体就是预先配置好启动参数的终端）。

.NET 11 · Avalonia 12.1.5 · [TKCode.Arc](https://docs.tkcode.si) 1.1.0-preview.4

## 运行

需要 .NET 11 SDK（版本见 `global.json`）。Windows 终端使用系统的 ConPTY；默认优先启动 PowerShell 7，未安装时使用 Windows PowerShell，再退到 cmd。不需要安装 Bash 或 WSL。

```bash
dotnet run --project src/Octopus.App
```

终端右侧在有历史内容时显示滚动条，也可用滚轮和 `Shift+PageUp` / `Shift+PageDown` 翻页、`Shift+Home` / `Shift+End` 跳到历史顶部 / 底部。智能体接管鼠标时，普通滚轮交给智能体，按住 `Shift` 滚轮可查看终端历史。全屏备用屏由程序自己管理滚动，不显示主屏历史滚动条；已被程序清除或超过回滚上限的内容无法从终端恢复。

## 测试

```bash
dotnet build Octopus.slnx
dotnet test Octopus.slnx
```

终端层的真机冒烟（会短暂弹出一个窗口）：

```bash
dotnet run --project tests/Octopus.Terminal.Smoke -- --script
```

Windows 的冒烟会启动本机默认 PowerShell，检查命令输入与输出、窗口缩放、Ctrl+C、退出码和重启；macOS / Linux 使用登录 shell，另检查设备应答、vim 和 terminfo。

Windows 上通过 npm 等工具安装的 `.cmd` / `.bat` 智能体会通过系统 `cmd.exe` 执行版本和账号 / 用量探测，保留输出捕获与超时控制。终端启动时也会合并最新的注册表 PATH，避免扫描能找到智能体、终端却找不到其运行时。

为兼容商店版 PowerShell，终端使用 ConPTY 的控制台会话生命周期，不强制把子进程放入 Job Object。关闭终端会结束控制台会话，但不会强制关闭已独立启动的 GUI 程序。

## CI 与发布

- `.github/workflows/ci.yml`：push 到 main 和 PR 时，在 Linux / macOS / Windows 上构建并跑全部测试。
- `.github/workflows/release.yml`：推 `v*` 标签（如 `v0.1.0`）后构建并发布 GitHub Release。在 Actions 页手动运行是 dry run，只出产物、不发布。
  - macOS：arm64、x64 的 `.dmg`（拖进任意目录即可）。
  - Windows：`Octopus-<版本>-win-x64-setup.exe`（Inno Setup，安装向导里可选安装目录，也可切换为所有用户安装），另有免安装的 `.zip`。
  - Linux：`Octopus-<版本>-linux-x64.run`（运行时询问安装目录，或 `--prefix DIR`；默认 root 装 `/opt/octopus`、普通用户装 `~/.local/share/octopus`；安装目录里有 `uninstall.sh`），另有 `.tar.gz`。
- 发版步骤（版本号怎么定、打标签、核对产物和更新清单）写在 `.agents/skills/publish-release/SKILL.md`。
- 自动更新（[design/10-自动更新.md](design/10-自动更新.md)）：标签必须是 `vX.Y.Z` 或 `vX.Y.Z-preview.N`。发布时 `tools/make-update-manifest.py` 生成 `update.json`，并用仓库 Secret `UPDATE_SIGNING_KEY`（ECDSA P-256 私钥的 PEM，`tools/make-update-key.sh` 生成，对应公钥在 `src/Octopus.Updates/UpdateEndpoints.cs`）签名。没有这个 Secret 时不生成清单，已安装的程序不会更新到这个版本。macOS 另出一个 `.app` 的 zip，供自动更新使用。
- macOS 签名与公证用仓库 Secrets：`APPLE_ID`、`APPLE_TEAM_ID`、`APPLE_APP_SPECIFIC_PASSWORD`，以及 Developer ID Application 证书 `APPLE_CERTIFICATE`（`.p12` 的 base64）和 `APPLE_CERTIFICATE_PASSWORD`。没有证书时 dmg 只做 ad-hoc 签名、不公证。Windows 安装包目前没有代码签名。
- 本地打包：`tools/package-macos.sh osx-arm64 0.1.0 dist`；Linux 安装器 `tools/package-linux.sh 0.1.0 <linux-x64 发布目录> dist`。

## 结构

```
src/
  Octopus.Core               领域模型
  Octopus.Storage            projects.json、settings.json
  Octopus.Platform(.MacOS/.Linux/.Windows)   shell、环境、路径等平台差异
  Octopus.Agents             智能体目录（JSON）、安装扫描、命令行编译
  Octopus.FileSystem         文件树、监听、搜索
  Octopus.Git                只读 git 状态
  Octopus.Updates            自动更新：签名清单、下载、状态机（安装步骤在各平台工程里）
  Octopus.Terminal(.MiniPty/.Avalonia)       PTY 与终端控件
  Octopus.App                Avalonia 界面
  Octopus.Remote             远程操作两端共用的部分：设备身份、安全通道、WebSocket 传输、消息（design/12-远程操作.md）
  Octopus.Remote.Host        主机一侧：谁能连进来、配对、把工作区交给已连接的设备
  Octopus.Remote.Client      远程端一侧：连接主机、调用它的工作区、把它的终端接成本地的终端连接
  Octopus.Mobile             手机端的界面与视图模型（设计稿见 design/12-远程操作.md 第 9 节；目前跑在示例数据上）
  Octopus.Mobile.Desktop     手机端的桌面预览壳：dotnet run --project src/Octopus.Mobile.Desktop
  Octopus.Mobile.Android/.iOS  手机端的安卓、iOS 壳（只在 Octopus.Mobile.slnx 里，需要 android / ios 工作负载；由 .github/workflows/mobile.yml 构建）
tests/                       各层测试
design/                      设计文档与调研，从 design/README.md 开始
```

数据目录：macOS `~/Library/Application Support/Octopus/`，Linux `~/.config/octopus/`，Windows `%AppData%\Octopus\`。
