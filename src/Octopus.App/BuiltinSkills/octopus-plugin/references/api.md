# Octopus Plugin Host API Reference (API v0)

Your `main.js` ES module exports:

```js
export async function activate(ctx) {
  // Register commands, views, editors, indicators, usageProviders, etc.
  // Push returned disposables onto ctx.subscriptions.
}

export function deactivate() {
  // Optional cleanup; host also disposes ctx.subscriptions in reverse order.
}
```

## Key Context Objects (`ctx`)

- `ctx.subscriptions`: `Array<{ dispose(): void }>` — auto-disposed on disable/unload.
- `ctx.globalState`: `get(key)`, `set(key, value)`, `delete(key)` — persisted in `~/.octopus/plugin-data/<id>/state.json`.
- `ctx.projectState`: project-scoped key-value store.
- `ctx.config`: `get(key)`, `onDidChange(cb)` — reads `contributes.configuration` values.
- `ctx.commands.register(id, handler)` — handles a declared command.
- `ctx.views.register(viewId, provider)` — renders a declarative view tab.
- `ctx.editors.register(editorId, provider)` — renders custom file editors (e.g. `*.csv`).
- `ctx.indicators.register(indicatorId, provider)` — top-right status indicator + popover.
- `ctx.usage.registerProvider(id, provider)` — supplies coding agent quota/usage windows.
- `ctx.fs`: scoped filesystem API (`data`, `tmp`, `project`, `external`, `agentData`, `picked`).
- `ctx.net.fetch(url, init)` — HTTPS fetch restricted to `permissions.net.hosts`.
- `ctx.secrets`: `get(key)`, `set(key, value)`, `delete(key)` — OS keychain storage.
- `ctx.git`: read-only Git status/branch/diff helpers (`permissions.git`).
- `ctx.projects`: current project & project list (`permissions.projects`).
- `ctx.notifications.show({ title, body, tone })` — desktop notifications.
- `ctx.log.info / warn / error(msg)` — visible in Settings → Plugins → Log.

## Contribution Points (`contributes`)

- **Declarative (no `main.js` required)**: `themes`, `iconThemes`, `agents`, `agentFeatures`, `prompts`, `terminalProfiles`, `terminalLinks`, `keybindings`, `menus`, `configuration`, `languages`.
- **Code-backed (require `main.js`)**: `commands`, `views`, `editors`, `panels`, `sidebarSections`, `indicators`, `usageProviders`, `taskProviders`, `statusProviders`.
