# Connector icons

Built-in authentic product brand icons (`connector-icons.json`) for the Bot Connector Catalog and connected-app lists.

## Sources

- `github`, `vercel`, `intercom`: Simple Icons (CC0)
- `cloudflare`, `supabase`, `neon`, `netlify`, `gitlab`, `slack`, `sentry`, `datadog`, `linear`, `notion`: svgl (MIT)
- `jira`, `pagerduty`: gilbarbara/logos (CC0)
- `google`: `@lobehub/icons-static-svg` (MIT)
- `context7`: `upstash/context7` (`public/context7-icon-green.svg`, MIT)

## Format

Same compact NativeAOT-safe JSON bundle schema as `Assets/ModelIcons/model-icons.json`, loaded once by `ConnectorIconStore` and rendered via `DrawingContext.DrawGeometry` in `ConnectorIconView`.

All logos are trademarks of their respective owners and are used here solely to identify the corresponding products.
