# Agent logos

One JSON file per built-in agent, named after the agent id (`AgentIcon.AgentId`). Format:

```json
{ "viewBox": [x, y, w, h], "paths": [ { "d": "M…Z", "fill": "#D97757 | currentColor", "fillRule": "nonzero | evenodd" } ] }
```

`currentColor` follows the theme foreground (black-and-white logos); a hex value is a fixed brand color. The view box is
trimmed to the artwork plus about 6% margin. Agents without a file here (and custom agents) get the letter tile.

| File | Agent | Source | License of the artwork file |
| --- | --- | --- | --- |
| claude.json | Claude Code | `@lobehub/icons-static-svg` 1.102.0, `icons/claude-color.svg` | MIT (package) |
| codex.json | Codex CLI | `@lobehub/icons-static-svg`, `icons/openai.svg` (mono) | MIT (package) |
| grok.json | Grok | `@lobehub/icons-static-svg`, `icons/grok.svg` (mono) | MIT (package) |
| opencode.json | OpenCode | `@lobehub/icons-static-svg`, `icons/opencode.svg` (mono) | MIT (package) |
| cursor.json | Cursor | `@lobehub/icons-static-svg`, `icons/cursor.svg` (mono) | MIT (package) |
| muse.json | Muse | `@lobehub/icons-static-svg`, `icons/muse-color.svg` (gradient reduced to #0064E0) | MIT (package) |
| gemini.json | Gemini CLI | `@lobehub/icons-static-svg`, `icons/gemini-color.svg` (solid base shape, gradient overlays dropped) | MIT (package) |
| copilot.json | GitHub Copilot | `@lobehub/icons-static-svg`, `icons/githubcopilot.svg` (mono) | MIT (package) |
| amp.json | Amp | `@lobehub/icons-static-svg`, `icons/amp-color.svg` | MIT (package) |
| droid.json | Droid (Factory) | Orca `src/renderer/src/components/status-bar/icons.tsx` (`DroidIcon`; white glyph without its black tile, drawn in currentColor) | MIT (Orca, github.com/stablyai/orca) |
| aider.json | Aider | Orca `agent/icons/agent-icon-glyphs.tsx` (`AiderIcon`), from aider.chat `assets/icons/safari-pinned-tab.svg` | Apache-2.0 (Aider repository) |
| qwen.json | Qwen Code | `@lobehub/icons-static-svg`, `icons/qwen-color.svg` (fill #082DFF replaced by Qwen purple #615CED for contrast on dark) | MIT (package) |
| kiro.json | Kiro | `@lobehub/icons-static-svg`, `icons/kiro-color.svg` | MIT (package) |
| goose.json | Goose | `@lobehub/icons-static-svg`, `icons/goose.svg` (mono) | MIT (package) |
| kimi.json | Kimi CLI | `@lobehub/icons-static-svg`, `icons/kimi-color.svg` | MIT (package) |
| (none) | Crush | no reliable SVG found (the repository only ships raster art) | letter tile fallback |

All logos are trademarks of their respective owners and are used here only to identify the corresponding product.
