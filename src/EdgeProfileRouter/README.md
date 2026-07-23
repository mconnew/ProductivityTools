# Edge Profile Router

Open every link in the **right Microsoft Edge profile** based on rules that can inspect the
whole URL — not just the host. Set this app as your default browser and, for each clicked link,
it decides which Edge profile to use and launches Edge with `--profile-directory=<dir>`. Any URL
that no rule matches is handed straight to Edge **with no profile flag**, so Edge's own
last-used / automatic-profile behaviour applies — exactly as if this app were not installed.

The motivating example: everything on `github.com/CoreWCF/*` opens in a **personal** profile,
everything on `github.com/dotnet/*` opens in a **work** profile, and every other link is left to
Edge to decide.

## How it works

```
You click a link anywhere in Windows
        │  Windows invokes the default browser with the URL as %1
        ▼
EdgeProfileRouter.exe  ──►  load rules (%APPDATA%\EdgeProfileRouter\config.json)
        │
        ├─ first matching rule?  ──►  msedge.exe --profile-directory="<Dir>" -- "<url>"
        │
        └─ no rule matches       ──►  msedge.exe -- "<url>"      (no profile flag → Edge decides)
```

Rules are evaluated top-to-bottom and the **first** match wins, so put more specific rules above
broader ones. Routing shows no window and exits immediately, so it feels as fast as launching
Edge directly.

### Why match on the profile *directory*, not the friendly name

Edge stores each profile in a fixed folder (`Default`, `Profile 1`, `Profile 2`, …) but shows a
friendly name ("Profile 1", an account e-mail, or whatever you renamed it to). Those friendly
names can be reordered and don't line up with the folder numbers — e.g. the folder `Default`
might display as *"Profile 1"* for your work account while the folder `Profile 1` displays as
*"Profile 2"* for your personal account. Rules therefore store the **stable directory name**, and
the settings window shows the friendly name + account so you can pick the right one.

Run `--list-profiles` to see the mapping on your machine:

```
Edge profiles (use the Directory value in rules):
  Directory="Default   "  Name="Profile 1"  Account="you@work.com"      [last used]
  Directory="Profile 1 "  Name="Profile 2"  Account="you@outlook.com"
```

## Requirements

- Windows 10/11 with **Microsoft Edge** installed.
- **.NET 10 Desktop Runtime** (this is a WPF app). Build with Visual Studio 2026 or the .NET 10 SDK.

## Build

```powershell
dotnet build src\EdgeProfileRouter\EdgeProfileRouter.csproj -c Release
```

Output: `src\EdgeProfileRouter\bin\Release\net10.0-windows\EdgeProfileRouter.exe`

> Tip: copy the `net10.0-windows` output to a **stable folder** (e.g.
> `%LOCALAPPDATA%\EdgeProfileRouter\`) before registering, because registration records the exe's
> current path. If you move the exe later, just run `--register` again from the new location.

## Install & make it your default browser — no admin required

1. **Register** the app as a candidate browser (writes only to `HKCU`):

   ```powershell
   EdgeProfileRouter.exe --register
   ```

2. **Set it as default.** Windows 10/11 deliberately does **not** let any app set itself as the
   default browser (the choice is protected by a signed hash), so you finish this in Settings:
   open **Settings → Apps → Default apps → Edge Profile Router** and set it for **HTTP** and
   **HTTPS** (and optionally `.htm`/`.html`). The settings window's **"Set as default…"** button
   opens that page for you.

Remove it any time with:

```powershell
EdgeProfileRouter.exe --unregister
```

(If it was your default, pick another browser in Default apps afterwards.)

## Managing rules (settings window)

Run the exe with **no arguments** (or `--settings`) to open the settings window. On first run it
seeds the two GitHub example rules so the feature is discoverable — review them and click **Save**.

The window lets you:

- See registration status, the resolved Edge path, and the current HTTPS default.
- **Register / Unregister** and open **Default apps** to finish setting the default.
- Edit rules in a grid — **Add**, **Remove**, and reorder with **Move Up / Move Down** (order is
  priority). Each rule has a checkbox to enable/disable it and a profile picker populated from your
  detected Edge profiles.
- Optionally override the Edge executable path (blank = auto-detect).
- **Test URL** — type a URL and see exactly which rule would match and which profile it would open
  in, without launching anything.

Rules are stored as indented JSON at `%APPDATA%\EdgeProfileRouter\config.json`.

### Rule fields

| Field | Meaning |
|-------|---------|
| `name` | Friendly description (display only). |
| `enabled` | `false` skips the rule during matching. |
| `hostPattern` | Host to match. A plain host (`github.com`) matches that host **and any sub-domain** (`api.github.com`). A pattern containing `*` or `?` is treated as a glob over the whole host. |
| `pathPrefix` | Case-insensitive path prefix matched on **segment boundaries**, so `/dotnet/` matches `/dotnet` and `/dotnet/wcf` but **not** `/dotnetfoundation`. |
| `urlRegex` | Optional regular expression matched (case-insensitively, 1-second timeout) against the **whole URL**, for cases that need deeper inspection than host + path. |
| `profileDirectory` | Edge profile **directory** to open matches in (e.g. `Default`, `Profile 1`). |
| `profileLabel` | Remembered friendly label for readability in the JSON (not used for matching). |

A rule matches only when **every** criterion it specifies is satisfied; criteria left blank are
ignored. A rule with **no** criteria never matches, so a half-filled row can't accidentally become
a catch-all that swallows every link. If a matched rule has no `profileDirectory`, the URL is
handed to Edge to decide.

### Example configuration

```json
{
  "rules": [
    {
      "name": "GitHub: CoreWCF → personal",
      "enabled": true,
      "hostPattern": "github.com",
      "pathPrefix": "/CoreWCF/",
      "profileDirectory": "Profile 1",
      "profileLabel": "Profile 2 — you@outlook.com"
    },
    {
      "name": "GitHub: dotnet → work",
      "enabled": true,
      "hostPattern": "github.com",
      "pathPrefix": "/dotnet/",
      "profileDirectory": "Default",
      "profileLabel": "Profile 1 — you@work.com"
    }
  ]
}
```

With this config:

- `https://github.com/CoreWCF/CoreWCF` → opens in profile `Profile 1` (personal).
- `https://github.com/dotnet/wcf` → opens in profile `Default` (work).
- `https://github.com/microsoft/vscode`, `https://example.com/`, `mailto:…` → handed to Edge to
  decide (no profile forced).

## CLI reference

| Command | Purpose |
|---------|---------|
| *(no args)* / `--settings` | Open the settings window |
| `--register` | Register as a candidate browser (`HKCU`, no admin) |
| `--unregister` | Remove the registration |
| `--list-profiles` | Show detected Edge profiles, the resolved Edge path, and registration status |
| `--route <url>` | Route a URL now (opens Edge) |
| `--dry-run <url>` | Show which rule/profile a URL would use, **without** launching Edge |
| `--browser` | Open Edge itself with no forced profile (used by the "launch browser" verb) |
| `--enable-logging` | Turn on diagnostic logging (reports the log file path) |
| `--disable-logging` | Turn off diagnostic logging (default) |
| `--quiet` / `--silent` | Suppress all dialogs / console output (combine with any command) |
| `--version` | Print the version |
| `--help` | Show usage (also reports the current logging state) |
| `<url>` | Route the URL (this is what Windows invokes for a clicked link) |

CLI verbs print to the parent terminal when run from a console, and fall back to a message box
when launched from Explorer.

## Troubleshooting / logging

Diagnostic logging is **off** by default. Turn it on to see how each URL was routed:

```powershell
EdgeProfileRouter.exe --enable-logging     # prints the log path
EdgeProfileRouter.exe --disable-logging    # turn it back off
```

- Log file: `%TEMP%\EdgeProfileRouter.log`. Each launch records the URL, which rule matched (or
  that none did), and the exact Edge command used.
- The logging preference is stored per-user at `HKCU\Software\EdgeProfileRouter`
  (DWORD `LoggingEnabled`), so it persists across runs and survives `--unregister`.
- **Links keep opening in the wrong profile:** run `--list-profiles` and confirm each rule's
  `profileDirectory` is the **directory** (left column), not the friendly name.
- **A link that should hand off is being routed (or vice-versa):** use `--dry-run <url>` or the
  Test URL box to see which rule matched, then reorder/adjust rules (first match wins).

## Security notes

Every clicked link is treated as untrusted input, so the app is designed so that a hostile link
can neither run a privileged command nor smuggle extra switches into Edge:

- **Argument-injection is blocked.** The URL is passed to Edge as a single, isolated argument
  after a `--` end-of-switches marker (via `ProcessStartInfo.ArgumentList`), so a crafted link
  cannot append its own Edge flags.
- **A link can't trigger the app's own verbs.** When the first argument is a URL (as Windows
  passes it), only that URL is honoured and all other arguments are ignored — a link can't sneak
  in `--register`, `--unregister`, or `--enable-logging`. Those work only when typed on the command
  line.
- **Only web URLs are rule-matched.** `http`/`https` URLs are evaluated against the rules; any
  other scheme (`mailto:`, `tel:`, …) and any non-absolute input is handed straight to Edge.
- **Regexes are sandboxed.** Rule regular expressions run case-insensitively with a 1-second
  timeout; an invalid or catastrophically slow pattern simply fails to match (and is logged) rather
  than hanging routing.
- **Registration is per-user (`HKCU`)** — no elevation, and fully reversible with `--unregister`.
```
