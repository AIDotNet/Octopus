---
name: publish-release
description: 发布 Octopus 的新版本（正式版或预览版）：定版本号、检查发布前提、打 v 标签触发 Release 工作流、盯完构建、核对产物和签名的更新清单。用户说"发版""发布""出个预览版""打 tag""release""版本号该用多少"时使用。
---

# 发布 Octopus

发布会公开推送给所有已安装的程序（自动更新，见 `design/10-自动更新.md`）。**推标签之前必须让用户确认版本号**，推出去就收不回来。

辅助脚本：`.agents/skills/publish-release/scripts/release_version.py`（下文简称 `rv`），用 `python3 -I <脚本> <命令>` 运行。

## 版本号规则

格式 `主.次.构建[-preview.N]`，标签前加 `v`：`v0.2.1112`、`v0.2.1112-preview.3`。CI 只接受这两种形状。

| 段 | 规则 |
|---|---|
| 主 | 只在用户明确要求时加 1，次归 0 |
| 次 | 跨年时加 1（构建号随日期重新开始，保证版本只增不减）；用户要求时也可以加 |
| 构建 | 发布当天的 `月×100+日`：11 月 12 日是 `1112`，1 月 5 日是 `105`（数字不能有前导零）。同一天要开第二条线时用前一个构建号加 1 |
| `-preview.N` | 预览版，N 从 1 开始，同一条线每发一次加 1 |

比较规则同 SemVer：三段数字逐段比；数字相同时预览版低于正式版（`1.2.1112-preview.3 < 1.2.1112`）。所以：

- **预览转正**：预览线 `0.2.1112-preview.3` 测好后，正式版就发 `0.2.1112`，装着预览版的人也会升上来。
- 正式版发出后，下一个预览版必须开新线（更大的构建号），不能再发 `0.2.1112-preview.4`。
- 已经发出去的版本号**永远不复用**，哪怕那次发布有问题，也发一个更大的版本号修正。
- 装的是预览版的程序默认收预览版，装的是正式版的只收正式版（`design/10-自动更新.md` 第 4 节）。

`rv status` 列出已有版本和下一个可选的版本号；`rv check <版本>` 校验格式、是否已存在、是否高于所有已有版本（本地和 origin 上的标签都算）。

## 流程

### 1. 搞清楚要发什么

- 用户没说正式版还是预览版时，问一句；没说具体版本号时，跑 `rv status`，按上面的规则给出建议（通常是"下一个预览版"或"预览转正"）。
- 列出上个版本以来的提交，帮用户确认要发的内容：
  `git log --oneline <上一个标签>..origin/main`（还没有标签时用 `git log --oneline origin/main | head -30`）。

### 2. 检查发布前提

逐项检查，有不满足的先告诉用户，不要绕过：

```bash
git fetch origin --tags
git status --short                       # 工作区要干净；有未提交改动就问用户要不要先提交
git rev-parse HEAD origin/main           # 要发的提交必须已经在 origin/main 上
gh run list --workflow ci.yml --branch main --limit 3   # 这个提交的 CI 要是绿的
gh secret list                           # 必须有 UPDATE_SIGNING_KEY
python3 -I .agents/skills/publish-release/scripts/release_version.py check <版本>
```

- 缺 `UPDATE_SIGNING_KEY`：发布会成功，但不会生成更新清单，已安装的程序收不到这个版本。告诉用户，让用户决定先补 Secret 还是照发。
- 缺 `APPLE_CERTIFICATE` 等 macOS 证书 Secret：dmg 只做 ad-hoc 签名、不公证，用户首次打开要右键"打开"。照发可以，但要说明。
- 标签默认打在 `origin/main` 的最新提交上。用户要发别的提交时，那个提交也必须在 `origin/main` 上。

### 3. 确认并打标签

把下面这些一次性给用户确认：版本号、正式版还是预览版、要发的提交（短哈希和标题）、上面检查里的警告。**用户确认后**再执行：

```bash
git tag -a v<版本> <提交> -m "Octopus <版本>"
git push origin v<版本>
```

只推这一个标签，不要 `git push --tags`。

想先试构建、不发布时，用 dry run（只出产物，不建 Release，不需要标签）：
`gh workflow run release.yml -f version=<版本>`。

### 4. 盯构建

```bash
gh run list --workflow release.yml --limit 1          # 拿到刚触发的运行 id
gh run watch <id> --exit-status                       # 要等一阵，macOS 公证最慢
```

构建期间可以去做别的，结束后再看。失败时：

- `gh run view <id> --log-failed` 找原因，告诉用户。
- **Release 还没建出来**（失败在 macos / windows / linux 任务里）：修好代码合进 main 后，删掉标签重打。删除标签前先问用户：
  `git push --delete origin v<版本> && git tag -d v<版本>`
- **Release 已经建出来了**：不要删除或覆盖已发布的 Release（已经有程序可能下载了它）。发一个新的、更大的版本号修正。

### 5. 核对发布结果

```bash
python3 -I .agents/skills/publish-release/scripts/release_version.py verify v<版本>
```

它会检查：

- 是不是草稿、"预发布"标记和版本类型对不对；
- 10 个产物都在：两个 dmg、两个 macOS zip、Windows 安装包和 zip、Linux `.run` 和 tar.gz、`update.json`、`update.json.sig`；
- `update.json` 由 `src/Octopus.Updates/UpdateEndpoints.cs` 里的公钥签名，版本号与标签一致，四个平台都有包，大小与 Release 上的文件一致。

全部通过后，把 Release 链接和结果告诉用户。有问题按第 4 步的办法处理。

## 注意

- 发布说明由工作流的 `--generate-notes` 自动生成。用户想改说明，发布后用 `gh release edit v<版本> --notes-file <文件>`，只改说明，不动产物。
- 第一个带自动更新的版本之前发布的程序没有更新器，用户要手动安装一次，之后才会自动更新。发第一个带清单的版本时提醒用户这一点。
- 签名私钥只存在于 Secret `UPDATE_SIGNING_KEY` 和用户自己的备份里，不要读取、打印或提交它。要换密钥看 `tools/make-update-key.sh` 的说明。
- 不确定的地方先问用户。推标签、删标签、改 Release 都要用户点头。
