# Threat model

plan-guard runs inside CI with a database login and, for PR comments, a GitHub token. It reads SQL from the
repository and Markdown it writes back to GitHub. This document lists what it protects, against whom, and how.

## Assets

| Asset | Why it matters |
|---|---|
| The CI database and its login | `EXPLAIN ANALYZE` executes statements; a privileged login can change or destroy data |
| The GitHub token (`pull-requests: write`) | Can post and edit comments on the repository |
| The build verdict | A regression that passes, or a forged "pass", defeats the purpose |
| Snapshot and report files | Contain SQL text and plans: table, index and column names of the schema |

## Actors

- **Contributor opening a pull request** (possibly from a fork): controls catalog files, migrations, branch names
  and comment text on the PR.
- **Repository maintainers**: control the workflow, `plan-guard.json` on the base branch and secrets.
- **Anyone who can comment on the PR.**

## Threats and mitigations (STRIDE)

| # | Threat | Category | Mitigation |
|---|---|---|---|
| T1 | A catalog file appends a second statement (`SELECT 1; DROP TABLE orders`) that runs outside EXPLAIN | Tampering | Lexer-based single-statement check (quotes, dollar quotes, nested comments); file refused. FsCheck properties + unit cases (ADR 0004) |
| T2 | A catalog `UPDATE`/`DELETE` changes data during capture | Tampering | Every EXPLAIN in its own transaction, always rolled back; integration test proves the row is unchanged |
| T3 | A slow or malicious query hangs the job or exhausts the runner | Denial of service | `statement_timeout` on every capture session (default 30 s); runaway query recorded as an error. CI job timeouts |
| T4 | Injection through config values (setting names, schema name) | Tampering / elevation | Settings applied with `set_config($1,$2,false)`; names validated by regex; store schema name validated; all store SQL parameterised |
| T5 | Injection through action inputs (label, paths, branch names) into the shell | Elevation | Inputs passed to the composite action's script only via environment variables, never `${{ }}` inside `run:`; CodeQL `actions` analysis |
| T6 | Someone plants the report marker in their own comment so the action overwrites it with the token | Tampering / spoofing | The commenter updates only marked comments authored by the workflow token's own account, matched by user id 41898282 (configurable with `--author-id`); unit test with a planted comment |
| T7 | Markdown/HTML injection in the PR comment via query ids or plan text | Tampering | Table cells escape `|`, `<`, `>` and newlines; plan text goes into code fences longer than any backtick run inside it |
| T8 | The token leaks through logs or the command line | Information disclosure | `comment` reads `GITHUB_TOKEN` from the environment only (no CLI flag); error messages truncate API bodies; token never logged |
| T9 | A fork PR forges a passing verdict by editing `plan-guard.json` thresholds or the ignore list | Tampering | Thresholds and ignores are code: review them like CI configuration. Teams can load the config from the base branch (`git show origin/main:plan-guard.json`) in the workflow. Documented, not enforced by the tool |
| T10 | Hostile snapshot JSON (deep nesting, huge files) crashes the compare step | Denial of service | JSON depth capped (512), plan tree depth capped (256), strict format version and duplicate-id checks |
| T11 | Schema details or real data leak through artifacts and comments | Information disclosure | Reports contain SQL and plan structure, never result rows. Use synthetic seed data (this repository's data is fully synthetic). Private repositories keep artifacts private |
| T12 | Production database used as the capture target | Tampering / DoS | Documentation: capture against a disposable CI database; rolled-back writes still create WAL and dead tuples |

## Residual risks

- A single statement can still call a function with side effects outside the transaction (`dblink`, sequence
  `nextval`, `pg_terminate_backend` with privileges). The catalog is trusted code; use a least-privilege login if
  untrusted contributors can change it.
- `pull_request_target` workflows that check out fork code and run plan-guard with secrets are unsafe, as for any
  tool; the example workflow uses `pull_request`.
- Discovery plans whatever statements the test suite ran; it never executes them (GENERIC_PLAN), so the residual
  risk there is information in the report, not data change.
