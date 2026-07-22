# Copilot instructions — ProductivityTools

A collection of small, **self-contained** tools that smooth over rough edges in day-to-day
workflows. Each tool stands on its own; there is no shared application framework to learn.

## Repository layout

- Every tool lives in its **own folder under `src/`** (e.g. `src/WcfPrTriage`, `src/OutlookLinkHandler`)
  and is fully self-contained: its own `.csproj`, `.slnx`, `README.md`, and code.
- **Keep tools independent.** Do not add cross-tool project references or introduce shared/common
  projects. Duplication across tools is preferred over coupling them.
- Every tool has its own `README.md`. When you add a new tool, also add a row to the project table in
  the root [`README.md`](../README.md).

## Build & quality gate

- **Every build must finish with 0 warnings and 0 errors.** Treat warnings as failures — this includes
  analyzer and NuGet warnings (e.g. `NU1510`). Do not leave a tool in a warning-producing state.
- Standard C# project settings: `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`.
- Build a specific tool with, e.g.,
  `dotnet build src/WcfPrTriage/WcfPrTriage.csproj -c Release`.
- **Verify your change actually works** before considering it done. Rebuild; for an app, confirm it
  launches and stays responsive; where practical, prefer a concrete runtime check (e.g. a round-trip
  test) over assuming correctness.

## Dependency policy

- **Prefer framework / built-in APIs over hand-rolled code.** For example, use the managed
  `System.Security.Cryptography.ProtectedData` API rather than hand-written `crypt32` P/Invoke.
- NuGet packages are acceptable **only when trustworthy and actively maintained** — in practice, that
  means **Microsoft-published** packages, so security issues are fixed in a timely manner.
- **Before adding a package, check whether the target framework already provides it.** On
  `net10.0-windows` with `UseWPF`, the Windows Desktop shared framework already includes many
  assemblies (e.g. `System.Security.Cryptography.ProtectedData`); a redundant `PackageReference` will
  trigger `NU1510` and must be removed.

## Documentation & style

- Keep each tool's `README.md` in sync with code changes (features, dependencies, file map).
- **Comment only where clarification is genuinely needed.** Do not narrate self-explanatory code.

## Workflow & safety

- **Never run state-changing `git` or `gh` operations without an explicit instruction** naming the
  change (no commit, push, branch delete, tag, PR/issue creation, or repo/settings mutation on your
  own initiative). Read-only commands (`git status`, `git log`, file reads) are fine. Propose the exact
  command and wait for approval.
- Make **surgical, targeted changes** that fully address the request; avoid unrelated churn.

---

## Tool-specific: `src/WcfPrTriage`

A WPF (.NET 10, `net10.0-windows`) app that lists open **dotnet/wcf** PRs and drills straight to the
real CI failure (Helix queue → failing test → exception/assert + stack) from public GitHub, Azure
DevOps (`dnceng-public`), and Helix APIs. When working in this tool, preserve these patterns:

- **Threading:** all mutable view-model / PR-row state is written **only on the UI thread**. Background
  I/O uses `ConfigureAwait(false)` and applies results back via the dispatcher (`RunOnUi`). Data passed
  across threads is **immutable records**. Push heavy work (e.g. building the failure tree) off the UI
  thread above a size threshold, and re-check selection guards after each `await`.
- **API access is anonymous-first with optional escalation:** PAT → `gh` CLI token → anonymous. Keep it
  working with **no token required**. Always **respect rate-limit response headers** and back off rather
  than hammering the APIs.
- **Caching:** the on-disk triage cache must refresh when a PR produces new results (e.g. a re-run) and
  prune entries once a PR is green or closed. Keep stored console logs trimmed.
- **Single instance:** a mutex enforces one running copy; a second launch restores/foregrounds the
  existing window instead of starting a new process.
- **Secrets at rest:** the optional GitHub token is DPAPI-encrypted (CurrentUser) via managed
  `ProtectedData`. Do not weaken this or log token values.
- **Theming:** colours are referenced through fixed brush keys via `DynamicResource`, with swappable
  light/dark palettes selected from the OS theme. Add new colours as palette entries, not inline values.
