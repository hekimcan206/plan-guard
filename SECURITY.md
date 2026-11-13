# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting
(**Security → Report a vulnerability**). Expect an acknowledgement within 5 working days and a fix or mitigation
plan within 30 days of confirmation.

Most valuable reports: a catalog file that gets a second statement past the single-statement check, a capture
that leaves data changed, a way to make the action overwrite a comment it did not write, or input that injects
commands into the composite action.

## Data

This project contains **synthetic data only**. Never submit real personal data, production query results or
production schemas in issues, pull requests, fixtures or snapshots. Reports contain SQL text and plan structure
(table, index and column names), never result rows; treat them as you would treat your schema.

## Security-relevant defaults

| Area | Default |
|---|---|
| Execution | Every EXPLAIN in its own transaction, always rolled back; one statement per catalog file (lexer-checked); `statement_timeout` 30 s; JIT off |
| SQL construction | Session settings via `set_config($1, $2, false)`; setting and schema names regex-validated; store queries parameterised |
| Discovery | Statements are planned with `EXPLAIN (GENERIC_PLAN)`, never executed |
| GitHub | Token only from `GITHUB_TOKEN`; only comments by the workflow token's own account (user id 41898282) carrying the marker are edited; action inputs reach shell only via environment variables |
| Parsing | Snapshot JSON depth ≤ 512, plan depth ≤ 256, strict format version |
| Container | Chiseled runtime image, non-root UID 1654, no shell |
| Supply chain | Central, pinned package versions updated by hand; CI fails on known-vulnerable packages (`dotnet list package --vulnerable --include-transitive`); CodeQL `security-and-quality` for C# and Actions |
| Secrets | None in the repository; compose password is a development default, overridable via `.env` |

See `docs/threat-model.md` for the full analysis.
