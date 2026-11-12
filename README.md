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
