# Model icons

Built-in AI model and provider brand icons (`model-icons.json`), generated from [`@lobehub/icons-static-svg`](https://icons.lobehub.com/) (v1.106.0, MIT license — see `LICENSE-lobehub-icons.txt`).

## Format

A single compact JSON bundle loaded once at run time by `ModelIconStore` and rendered directly through `DrawingContext.DrawGeometry` in `ModelIconView`:

- `icons`: `{ "<id>": { "vb": [x, y, w, h], "p": [ { "d": "…", "f": "#… | currentColor", "g": { "k": "l | r", "u": true/false, "pts": [...], "st": [ { "t": 0.0, "c": "#…", "o": 0.0 } ] }, "s": "#…", "sw": 1.5, "eo": true, "o": 0.5, "m": [a,b,c,d,e,f] } ] } }`.

`currentColor` follows the active theme foreground so monochrome logos (OpenAI, Anthropic, Grok, Ollama, Cursor, Z.ai, etc.) adapt automatically between light and dark themes.

## Regenerating

```bash
python3 tools/generate-model-icons.py <path/to/@lobehub/icons-static-svg/package>
```

All logos are trademarks of their respective owners and are used here solely to identify the corresponding AI models and providers.
