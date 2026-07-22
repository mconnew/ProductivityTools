# WCF PR Triage

A WPF (.NET 10) desktop app that lists the open pull requests for **[dotnet/wcf](https://github.com/dotnet/wcf)**
and, when you select one, shows **exactly which Helix queue run failed and what the actual test
failure was** — the test name, the exception / xUnit assert message, and the stack trace — all in
one panel.

It exists to collapse the long manual drill-down you otherwise have to do to find a CI failure:

```
GitHub PR  →  Azure Pipelines check  →  pipeline build  →  "Run Helix Tests" leg
   →  Helix summary  →  the failing queue  →  the failing work item  →  Artifacts tab
   →  download console log  →  scroll to the [FAIL] block  →  finally read the stack trace
```

The app does every one of those hops for you and drops you straight on the last step.

## What it looks like

Three panes, master → detail:

1. **Open pull requests** (left) — every open PR with author, "updated N ago", and a coloured CI
   status dot (green = passing, red = failing, grey = pending/none). Failing PRs are what you care
   about; the app auto-selects the first interesting one on load. Each row has a **⟳** button that
   force-refreshes just that PR (bypassing the cache) — handy right after you push a fix or hit
   *Re-run failed checks*.
2. **Failures** (middle) — a tree for the selected PR:

   ```
   net-wcf-ci #20260720.3            (the pipeline build)
   └─ OSX.15.Amd64.Open  MacOS Release        (Helix queue + AzDO build config)
      └─ Binding.Http.IntegrationTests.dll  exit 1, 19 tests   (Helix work item)
         └─ CustomBindingTests.DefaultSettings_Http_Text_Echo_RoundTrips_String   (failed test)
   ```

   Non-test build breaks (e.g. a compile failure) appear as build-error nodes with the log tail.
3. **Detail** (right) — for the selected test: the **Message** and **Stack Trace** parsed out of
   the Helix console log, with **Copy** and **Open** (jump to the source in the browser) buttons.

## How it works — 100% anonymous, no PAT required

Everything the app reads is public, so it works with **no sign-in and no Personal Access Token**.
A GitHub token is *optional* and only raises the API rate limit. If you don't set one, the app
automatically borrows a token from the **GitHub CLI** (`gh auth token`) when you're logged in — so
you get the 5000/hr authenticated budget without creating or rotating a PAT (see
[Settings](#settings)).

```
1. GitHub REST (public)
   GET /repos/{owner}/{repo}/pulls?state=open         → the open PR list
   GET /repos/{owner}/{repo}/commits/{sha}/check-runs  → the azure-pipelines check per PR
        the check's details_url carries buildId=NNNN   → the Azure DevOps build to inspect

2. Azure DevOps  (org "dnceng-public", project "public" — anonymous read)
   GET .../_apis/build/builds/{id}                     → build number + result
   GET .../_apis/build/builds/{id}/timeline            → the failed task records;
                                                          the "Run Helix Tests" task log
   GET .../_apis/build/builds/{id}/logs/{logId}        → that task's log text
        the log contains the Helix job GUIDs

3. Helix API  (helix.dot.net — anonymous)
   GET /api/jobs/{jobId}/details                       → QueueId (the distro) + Source (PR #)
   GET /api/jobs/{jobId}/workitems                     → work items with ExitCode + ConsoleOutputUri
        a work item failed when ExitCode != 0 (State stays "Finished" even on failure)
   GET {ConsoleOutputUri}                              → the raw xUnit console log
        parsed for "  <Test> [FAIL]" blocks → message + "Stack Trace:" + the run summary
```

The Azure DevOps **test-management** APIs (`_apis/test/...`) require auth, so they are deliberately
**not** used — the same failure detail is recovered from the public Helix console logs instead.

## Caching, refresh & rate-limit backoff

The app is built to get you a fresh answer with the **fewest possible GitHub calls**, so it stays
comfortably under the anonymous 60/hr budget.

**Per-PR cache.** The full triage result for a PR (its failure tree + detail) is cached on that PR's
row and survives selecting other PRs, so re-clicking a PR you already looked at is instant and costs
zero calls. The cache is keyed on a **signature** built from the head commit SHA *and* the set of
Azure DevOps build IDs and their pass/fail states — so if you **push a fix or re-run the checks**,
the signature changes and the app automatically re-triages instead of showing stale results.

**Persistent across restarts.** The per-PR cache is also written to disk at
`%APPDATA%\WcfPrTriage\triage-cache.json`, so results survive closing and reopening the app — a PR
you triaged yesterday renders instantly (marked `· cached`) on the next launch, with **no** Azure
DevOps or Helix calls, as long as its head commit is unchanged. On startup the cache is matched to
each PR by head SHA; if the PR has moved to a new commit the stale entry is ignored and the PR is
re-triaged. The on-disk cache is also actively reconciled on every refresh: entries for PRs that
have been **closed or merged** (no longer in the open-PR list) and for PRs that have gone **green**
(their failing result was invalidated by a new build signature) are dropped from the file, not just
left to age out. Entries older than 14 days (or beyond a 400-PR cap) are additionally pruned. Writes
are skipped entirely when the reconciled contents are unchanged, so a large (tens-of-MB) cache is not
re-serialized on every periodic refresh. To keep the file small, the **persisted copy stores only what
the detail pane can actually show**: a test's full raw console block is dropped when a parsed message
or stack trace is present (that block is only a fallback for the rare test with neither), and a work
item's console tail is dropped when it has parsed test failures (the tail is only shown for crashed or
timed-out items with no parsed tests). This is display-lossless yet roughly halves the on-disk size of
a large PR (e.g. a 19-failure PR shrinks from ~300 KB to ~100 KB; a 4000-test PR from ~16 MB to ~7.5 MB).
Delete the file any time to reset — it is a pure cache.

**Cheap freshness check.** Before doing a deep (AzDO + Helix) triage, the app does one cheap GitHub
call to fetch the commit's check-runs and compare the signature. If nothing changed, the cached
result is reused; only a changed signature triggers the expensive drill-down. The builds fetched by
this check are also reused by the deep triage, so selecting a freshly-scanned failing PR costs **no
extra GitHub calls** (the AzDO and Helix APIs are not GitHub-rate-limited).

**Per-row ⟳ refresh.** Each PR row has a refresh button that force-bypasses the cache for just that
PR — use it right after you push or hit *Re-run failed checks* and want to poll for the new result.

**Auto-refresh.** Optionally (see [Settings](#settings)) the app re-checks PR statuses on a timer.
It re-checks only a handful of PRs per tick, refreshes the full list every few minutes, and — most
importantly — **throttles itself as the budget shrinks**: below ~12 remaining calls it only refreshes
the selected PR, and below ~4 it skips the tick entirely, always leaving headroom for your clicks.

**Rate-limit backoff.** Every response's `X-RateLimit-Remaining` / `X-RateLimit-Reset` headers are
read; if the budget is spent the app **stops issuing requests until the window resets** rather than
firing calls that would just come back `403`. A `403`/`429` (including secondary "abuse" limits)
honours `Retry-After`. When limited, the top bar shows e.g. `GitHub: 0/60 (anon) · resets 17:02` and
the status bar explains when it resets and suggests adding a token. The initial status scan also
stops early while leaving a few calls in reserve for the PR you actually open.

## Requirements

- Windows with the **.NET 10 Desktop Runtime** (WPF). To build you need the **.NET 10 SDK**
  (`dotnet`) or Visual Studio 2026.
- Network access to `api.github.com`, `dev.azure.com` / `dnceng-public`, and `helix.dot.net`.
- **No NuGet dependencies** — the tool uses only framework APIs: `HttpClient`, `System.Text.Json`,
  and Windows DPAPI via the managed `System.Security.Cryptography.ProtectedData` API (part of the
  .NET Windows Desktop runtime), keeping it self-contained like its sibling tools.
- *(Optional)* the **GitHub CLI** (`gh`) on your PATH and signed in — if present, the app borrows
  its token for the higher rate limit automatically (see [Settings](#settings)).

## Build

```powershell
dotnet build src\WcfPrTriage\WcfPrTriage.csproj -c Release
```

Or open `src\WcfPrTriage\WcfPrTriage.slnx` (or the `.csproj`) in Visual Studio and build.

Output: `src\WcfPrTriage\bin\Release\net10.0-windows\WcfPrTriage.exe`

## Run

```powershell
dotnet run --project src\WcfPrTriage\WcfPrTriage.csproj -c Release
```

or launch `WcfPrTriage.exe` directly. On start it loads the open PRs, scans each PR's CI status,
and auto-selects the first failing one. Click any PR to triage it; the failure tree and detail pane
populate as the data comes back.

**Single instance.** Only one copy runs at a time. Launching the app again while it's already open
(or its taskbar/pinned shortcut) doesn't start a second window — it restores the existing window if
minimized and brings it to the front.

## Settings

Open **Settings** (top-right) to change:

| Setting | Default | Notes |
|---------|---------|-------|
| **Owner / Repo** | `dotnet` / `wcf` | Point it at any GitHub repo that uses the arcade + Helix CI (dnceng-public). |
| **GitHub token** | *(none)* | Optional, read-only is enough. Raises the API rate limit from **60/hr** (anonymous) to **5000/hr**. Leave it blank to auto-borrow a token from the **GitHub CLI** (`gh auth login`) instead. Stored **DPAPI-encrypted** (see below). |
| **Include drafts** | on | Show or hide draft PRs in the list. |
| **Auto-refresh every** | `120` seconds | How often the app re-checks PR statuses in the background. Set to **0** to turn auto-refresh off. Auto-refresh throttles itself as the rate-limit budget shrinks (see [Caching, refresh & rate-limit backoff](#caching-refresh--rate-limit-backoff)). |

### GitHub token — three ways to authenticate

The token is used **only** to raise the GitHub rate limit; it is never used for Azure DevOps or
Helix (those are read anonymously) and the app never writes anything. Because dotnet/wcf is public,
the token needs **no scopes at all** — a bare classic PAT with nothing ticked, or a fine-grained one
with just *Public repositories: read*, is enough. The app resolves a token in this order:

1. **Explicit PAT** — whatever you type in the Settings field (takes precedence).
2. **GitHub CLI** — if the field is blank, the app runs `gh auth token --hostname github.com` once
   at startup and reuses your existing `gh` login. This is the recommended path in environments
   where PATs are locked down or short-lived: `gh` is OAuth-backed and handles SSO and refresh for
   you, so there's no token to create or rotate. The gh token is read once per launch (restart the
   app to pick up a fresh `gh auth login`).
3. **Anonymous** — if neither is available, the app runs at the 60/hr anonymous limit.

The top-bar indicator shows which one is active: `(gh cli)`, `(anon)`, or no suffix for an explicit
PAT.

Settings live in `%APPDATA%\WcfPrTriage\settings.json`. The optional GitHub token is **never**
written in plaintext — it is encrypted with **Windows DPAPI** (per-user) and only the ciphertext is
stored. If the file is copied to another machine or user, the token is silently ignored and simply
falls back to the gh CLI or anonymous access.

The rate-limit indicator (`GitHub: 4988/5000 (gh cli)`) in the top bar shows remaining calls, and
switches to e.g. `GitHub: 0/60 (anon) · resets HH:MM` once an anonymous budget is spent. Thanks to
the per-PR cache and the cheap freshness check, a full status scan uses roughly one call per open PR
and re-triaging an unchanged PR uses none — so even the anonymous 60/hr budget is usually enough for
a repo the size of dotnet/wcf. See
[Caching, refresh & rate-limit backoff](#caching-refresh--rate-limit-backoff) for the full behaviour.

## Project layout

```
src/WcfPrTriage/
  App.xaml(.cs)              WPF entry point + global exception handler
  MainWindow.xaml(.cs)       3-pane master/detail UI
  app.manifest               per-monitor DPI awareness (must keep manifestVersion="1.0")
  Assets/Theme.xaml          dark theme brushes + control styles (ListBox, TreeView, etc.)
  Converters/                CI-state → brush, tree-depth → indent margin
  Models/                    CiState, PullRequestInfo, TriageModels (records)
  Services/
    GitHubService.cs         open PRs + check-runs
    GitHubTokenSource.cs     resolves the rate-limit token: PAT → gh CLI → anonymous
    AzureDevOpsService.cs    build / timeline / task logs
    HelixService.cs          job details / work items / console log
    ConsoleLogParser.cs      xUnit console log → TestFailure (message + stack)
    TriageService.cs         orchestrates GitHub → AzDO → Helix into a PrTriageResult
    SettingsStore.cs         load/save AppSettings (%APPDATA%), DPAPI-protecting the token
    Dpapi.cs                 DPAPI wrapper over ProtectedData (managed)
  ViewModels/                MVVM (MainViewModel + PR / tree-node VMs, commands)
  Views/SettingsWindow.xaml  settings dialog
```

## Notes & limitations

- A Helix work item's `State` is always `Finished`, even for failures — the failure signal is
  `ExitCode != 0`. The app relies on the exit code, not the state.
- Containerised queues report an ugly name in the task log
  (e.g. `(Fedora.41.Amd64.Open)ubuntu.2204...@mcr.microsoft.com/...`); the app extracts the friendly
  parenthesised distro for display.
- Console-log parsing targets the standard xUnit console format
  (`    Class.Method [FAIL]` → message → `Stack Trace:` → the `=== TEST EXECUTION SUMMARY ===`
  block). A radically different test runner's output would need parser changes.
- All data is read-only; the app never writes to GitHub, Azure DevOps, or Helix.
