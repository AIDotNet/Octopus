# Octopus Plugin Permissions & Marketplace Risk Grades

Marketplace automated scanning grades every uploaded `.octoplugin` into **Low**, **Medium**, or **High** risk based on declared permissions and JS analysis:

| Permission | Risk | Notes |
|---|---|---|
| Pure declarative (`themes`, `prompts`, `agents`) | **Low** | No JS engine started; fastest approval path. |
| `projects`, `settings`, `notifications`, `theme` | **Low** | Read-only UI metadata or notifications. |
| `fs.project` (`read`), `git` (`read`), `workspace` | **Medium** | Scoped to active project; sensitive files (`.env`, `*.pem`) blocked by default. |
| `net.hosts` (explicit domains) | **Medium** | HTTPS only; undeclared domains referenced in JS trigger scanner warning. |
| `agents.signals` / `agents.probes` | **Medium** | Structured read-only agent telemetry. |
| `fs.project` (`readwrite`), `fs.external`, `fs.agentData` | **High** | Requires explicit justification and manual reviewer diff check. |
| `secrets`, `terminal.read/write`, `process.run`, `clipboard.read` | **High** | Mandatory manual code review. |
| `net.hosts` with wildcard (`*.example.com`) | **High** | Prefer exact hostnames whenever possible. |
| `unsafeEval: true` | **High** | Strongly discouraged; `eval()`/`new Function()` without `unsafeEval: true` is rejected immediately. |
