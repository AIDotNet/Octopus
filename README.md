# Octopus

终端管理 + 项目管理。按项目组织终端和编码智能体（智能体就是预先配置好启动参数的终端）。

.NET 11 · Avalonia 12.1.5 · [TKCode.Arc](https://docs.tkcode.si) 1.1.0-preview.4

## 运行

```bash
dotnet run --project src/Octopus.App
```

## 测试

```bash
dotnet build Octopus.slnx
dotnet test Octopus.slnx
```

终端层的真机冒烟（会短暂弹出一个窗口）：

```bash
dotnet run --project tests/Octopus.Terminal.Smoke -- --script
```

## CI 与发布

- `.github/workflows/ci.yml`：push 到 main 和 PR 时，在 Linux / macOS / Windows 上构建并跑全部测试。
- `.github/workflows/release.yml`：推 `v*` 标签（如 `v0.1.0`）后构建 macOS（arm64、x64 的 `.dmg`）、Windows x64（`.zip`）、Linux x64（`.tar.gz`），并发布 GitHub Release。在 Actions 页手动运行是 dry run，只出产物、不发布。
- macOS 签名与公证用仓库 Secrets：`APPLE_ID`、`APPLE_TEAM_ID`、`APPLE_APP_SPECIFIC_PASSWORD`，以及 Developer ID Application 证书 `APPLE_CERTIFICATE`（`.p12` 的 base64）和 `APPLE_CERTIFICATE_PASSWORD`。没有证书时 dmg 只做 ad-hoc 签名、不公证。
- 本地打包：`tools/package-macos.sh osx-arm64 0.1.0 dist`。

## 结构

```
src/
  Octopus.Core               领域模型
  Octopus.Storage            projects.json、settings.json
  Octopus.Platform(.MacOS/.Linux/.Windows)   shell、环境、路径等平台差异
  Octopus.Agents             智能体目录（JSON）、安装扫描、命令行编译
  Octopus.FileSystem         文件树、监听、搜索
  Octopus.Git                只读 git 状态
  Octopus.Terminal(.MiniPty/.Avalonia)       PTY 与终端控件
  Octopus.App                Avalonia 界面
tests/                       各层测试
design/                      设计文档与调研，从 design/README.md 开始
```

数据目录：macOS `~/Library/Application Support/Octopus/`，Linux `~/.config/octopus/`，Windows `%AppData%\Octopus\`。
