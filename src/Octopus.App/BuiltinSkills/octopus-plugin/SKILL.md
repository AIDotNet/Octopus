---
name: octopus-plugin
description: Design, build, test, package (.octoplugin), and publish a plugin for Octopus (the cross-platform terminal & AI coding agent workbench). Use when the user wants to create a new Octopus plugin, update an existing plugin, write a theme/editor/indicator/usage provider, or package and upload to the Octopus Plugin Marketplace.
---

# Writing an Octopus Plugin

An Octopus plugin is a folder containing `plugin.json` and an optional ES module entry (`main.js`). Code plugins run inside Octopus's sandboxed **Jaunt** JavaScript runtime and can only access capabilities declared in `plugin.json` and granted by the user. Pure declarative plugins (themes, icon themes, agent definitions, prompts, keybindings) need no `main.js` and never spawn a JS engine.

Read `references/permissions.md` and `references/api.md` before writing code. Start from `templates/basic-command/` or `templates/theme-pack/`.

## Package Layout

```text
<publisher>.<name>/
  plugin.json          # Required manifest (schema: 1)
  main.js              # Optional ES module exporting activate(ctx) / deactivate()
  themes/*.json        # Optional color themes
  agents/*.json        # Optional agent definitions
  assets/*             # Optional icons / images
  README.md            # Displayed in the Octopus Marketplace
```

Packaged archives are standard `.zip` files with extension `.octoplugin` (max 16 MiB upload, 64 MiB unpacked, 5,000 files, no symlinks, no `..` path entries).

## Step-by-Step Workflow

1. **Choose an ID**: `<publisher>.<name>` — lowercase letters, digits, hyphens, at least one dot (e.g. `acme.csv-viewer`). Never use the reserved `octopus.*` prefix.
2. **Declare minimal permissions**: Request only what the plugin genuinely needs in `permissions` (see `references/permissions.md`). Mark non-essential permissions `"optional": true` and include a clear `reason` (with `localized["zh-CN"].reason`).
3. **Implement `main.js`**: Export `activate(ctx)` (sync or async) and push disposables into `ctx.subscriptions`. Keep startup fast (30s activation deadline).
4. **Test locally**: Set `OCTOPUS_DEV_PLUGINS=/path/to/your-plugin` when starting Octopus, or zip into `<id>-<version>.octoplugin` and install via **Settings → Plugins → Install from file…**.
5. **Package cleanly**: Zip the plugin directory (exclude `.git`, `node_modules`, `.DS_Store`, `*.map`) into `<id>-<version>.octoplugin`. Verify `plugin.json` is at the zip root or inside a single top-level directory.
6. **Publish to Marketplace**:
   - Web Console: Sign in at `https://octoterm.si/account?view=plugins`, claim your publisher slug, drag-and-drop the `.octoplugin` archive, review the automated scan report, and submit for approval.
   - CI / CLI: Create a Personal Access Token (PAT) in the console and upload via:
     ```bash
     curl -X POST https://octoterm.si/api/developer/plugins/upload?submit=1 \
       -H "Authorization: Bearer octo_pat_..." \
       -H "X-Content-Sha256: $(shasum -a 256 my-plugin.octoplugin | awk '{print $1}')" \
       --data-binary @my-plugin.octoplugin
     ```

## Manifest Essentials (`plugin.json`)

```json
{
  "schema": 1,
  "id": "acme.hello",
  "name": "Hello Workbench",
  "version": "1.0.0",
  "description": "Adds a sample command and view to Octopus.",
  "publisher": "acme",
  "category": "workflow",
  "license": "MIT",
  "engines": { "api": 0 },
  "main": "main.js",
  "permissions": {
    "notifications": { "optional": true }
  },
  "contributes": {
    "commands": [
      {
        "id": "acme.hello.greet",
        "title": "Say Hello",
        "localized": { "zh-CN": { "title": "打招呼" } }
      }
    ]
  },
  "localized": {
    "zh-CN": {
      "name": "你好工作台",
      "description": "向 Octopus 添加示例命令和视图。"
    }
  }
}
```

> **Important**: During preview, `engines.api` MUST be `0`. Every contributed item `id` and configuration key MUST be prefixed with `<plugin-id>.` (or equal `<plugin-id>`). Never use `eval()` or `new Function()` unless strictly justified and declared with `"unsafeEval": true` (triggers mandatory high-risk manual audit).
