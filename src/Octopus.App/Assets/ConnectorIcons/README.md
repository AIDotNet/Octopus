# Connector icons

Built-in authentic product brand icons (`connector-icons.json`) for the Bot Connector Catalog and connected-app lists.

## Sources

- `github`, `vercel`, `intercom`, `gitee`: Simple Icons (CC0)
- `cloudflare`, `supabase`, `neon`, `netlify`, `gitlab`, `jihulab`, `slack`, `sentry`, `datadog`, `linear`, `notion`: svgl (MIT)
- `jira`, `pagerduty`: gilbarbara/logos (CC0)
- `google`, `yunxiao` (Alibaba Cloud): `@lobehub/icons-static-svg` (MIT)
- `dingtalk`: Ant Design Icons (`ant-design:dingtalk`, MIT)
- `context7`: `upstash/context7` (`public/context7-icon-green.svg`, MIT)
- `feishu`, `tencentdocs`, `amap`, `baidumap`, `tencentmap`: crafted official-style multi-path 24x24 SVGs using standard `M/L/C/Z` path geometry for NativeAOT-safe Avalonia rendering

## Format

Same compact NativeAOT-safe JSON bundle schema as `Assets/ModelIcons/model-icons.json`, loaded once by `ConnectorIconStore` and rendered via `DrawingContext.DrawGeometry` in `ConnectorIconView`.

All logos are trademarks of their respective owners and are used here solely to identify the corresponding products.
