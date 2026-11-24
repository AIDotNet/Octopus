# File icons

Built-in VS Code style colored file and folder icons (`file-icons.json`), generated from [`material-icon-theme`](https://github.com/material-extensions/vscode-material-icon-theme) (v5.40.0, MIT license — see `LICENSE-material-icon-theme.txt`).

## Format

A single compact JSON bundle loaded once at run time by `FileIconStore` and rendered directly through `DrawingContext.DrawGeometry` in `FileIconView`:

- `defaultFile`, `defaultFolder`, `defaultFolderOpen`: fallback icon ids.
- `fileNames`, `fileExtensions`, `folderNames`: lowercase lookup tables matching exact names, compound/single extensions, and semantic folder names (with `-open` variants).
- `lightVariants`: dark-to-light icon id overrides for icons that need higher contrast on light themes.
- `icons`: `{ "<id>": { "vb": [x, y, w, h], "p": [ { "d": "…", "f": "#…", "s": "#…", "sw": 1.5, "eo": true, "o": 0.5, "m": [a,b,c,d,e,f] } ] } }`.

## Regenerating

```bash
python3 tools/generate-file-icons.py <path/to/material-icon-theme/package>
```
