# Outlook Classic deep-link handler (`outlookitem:`)

Open a **specific email in Outlook Classic (desktop)** by clicking a link — instead of being
sent to OWA in a browser. Built for automations that email you a list of messages needing
attention, where each item links to the real message.

## How it works

```
Automation email  ──►  <a href="outlookitem:open?mid=...">Open in Outlook</a>
        │  user clicks the link
        ▼
Outlook asks the Windows shell to invoke the "outlookitem:" verb (registered in HKCU)
        │  the verb is a DelegateExecute COM handler, so the shell hands the URL to COM/RPCSS
        ▼
RPCSS (svchost) starts OutlookLinkHandler.exe as a COM LocalServer  ◄── NOT a child of Outlook
        │  the exe implements IExecuteCommand and receives the URL from the shell
        ▼
OutlookLinkHandler.exe  ──►  connects to Outlook via COM  ──►  finds the message
        │                    (by immutable Internet Message-ID)
        ▼
Outlook opens the exact email in an inspector window
```

This is a **standalone protocol-handler executable**, not a VSTO add-in. Windows URL-protocol
handlers must be OS-launchable executables, so a DLL loaded inside Outlook cannot serve that
role. The exe uses Outlook's COM object model as an automation client (and can even start
Outlook if it is closed).

### Why a DelegateExecute COM handler (and not a plain `command` line)

The obvious registration — `shell\open\command = "<exe>" "%1"` — makes **Outlook itself** launch
the handler as a **child process**. On managed/enterprise machines that trips the Microsoft
Defender **Attack Surface Reduction (ASR)** rule *"Block Office communication application from
creating child processes"* (GUID `26190899-1602-49E8-8B27-EB1D0A1CE869`). Defender blocks the
launch and Outlook shows **"Access is denied."**

The fix is to register the `open` verb as a **`DelegateExecute` COM handler** backed by a
`LocalServer32` CLSID. With this shape the shell activates the verb through **COM**, so the
handler is started by **RPCSS (`svchost.exe`)** — Outlook is never the parent process, so the
ASR rule does not apply. The exe detects the `-Embedding` argument COM appends and runs as a
single-use COM server implementing the shell's `IExecuteCommand` verb interfaces; it reads the
clicked URL from the shell selection, opens the email, then exits.

> **Implementation gotcha (documented so it is never re-broken):** the real IID of
> `IExecuteCommand` is **`7F9185B0-CB92-43C5-80A9-92277A4F7B54`**. The shell QIs for exactly this
> IID as the final step of activation; declaring the interface under any other GUID makes
> activation abort with `E_NOINTERFACE` *after* the shell has already QI'd ~40 other interfaces,
> and the verb never runs. The handler also implements
> `IExecuteCommandApplicationHostEnvironment` (returns `AHE_DESKTOP`) and `IForegroundTransfer`,
> mirroring how browsers register their URL verbs.

## Why the Internet Message-ID

Each email carries an immutable `Message-ID` header (globally unique). Microsoft Graph exposes
it as `internetMessageId`. It survives the message being moved between folders, unlike the MAPI
`EntryID`. The handler searches your mailbox for the item whose `PR_INTERNET_MESSAGE_ID` matches.
An optional `EntryID` fast-path is also supported.

## Requirements

- Windows with **Outlook Classic** (desktop) installed and configured.
- **.NET 10 Runtime** (`Microsoft.NETCore.App`) on the machine that runs the handler. This build
  is framework-dependent; the base .NET 10 Runtime is enough (the Desktop Runtime is *not*
  required, since no WinForms/WPF is used).
- To build: Visual Studio 2026 **or** the .NET 10 SDK (`dotnet`).

## Build

Using the .NET SDK:

```powershell
dotnet build src\OutlookLinkHandler\OutlookLinkHandler.csproj -c Release
```

Or open `OutlookEmailLink.slnx` (or the `.csproj`) in Visual Studio and build.

Output: `src\OutlookLinkHandler\bin\Release\net10.0-windows\OutlookLinkHandler.exe`

> Tip: copy the `net10.0-windows` output to a **stable folder** (e.g.
> `%LOCALAPPDATA%\OutlookLinkHandler\`) before registering, because registration records the
> exe's current path. If you move the exe later, just run `--register` again from the new
> location.

## Install (register the protocol) — no admin required

Run once, from the exe's final location:

```powershell
OutlookLinkHandler.exe --register
```

This writes two things under `HKEY_CURRENT_USER\Software\Classes`:

```
outlookitem\
    (Default)               = URL:Outlook Item Protocol
    URL Protocol            = ""
    DefaultIcon             = <exe>,0
    shell\open\command\
        (Default)           = "<exe>" "%1"                     ← down-level fallback only
        DelegateExecute     = {86840CF6-1991-45E9-8974-625B5F40D759}

CLSID\{86840CF6-1991-45E9-8974-625B5F40D759}\
    (Default)               = Outlook Item Link Handler
    LocalServer32\
        (Default)           = "<exe>"
```

The `DelegateExecute` value is what routes activation through COM/RPCSS (see
*Why a DelegateExecute COM handler* above). The plain `command` line is kept only as a
fallback for callers that don't honour `DelegateExecute`.

Add `--quiet` to suppress the confirmation dialog (for silent/scripted installs):

```powershell
OutlookLinkHandler.exe --register --quiet
```

Remove it with:

```powershell
OutlookLinkHandler.exe --unregister
```

## Optional: suppress Outlook's "unsafe location" warning (elevated)

Because `outlookitem:` is a custom scheme, Outlook shows its standard *"Microsoft Office has
identified a potential security concern — This location may be unsafe"* prompt every time you
click such a link (see [First-click prompt](#first-click-prompt)). You can suppress it by adding
the scheme to Office's **Trusted Protocols** policy — and `--register` will do this for you
**automatically when it is run elevated**:

```powershell
# From an elevated prompt (right-click Windows Terminal / PowerShell > Run as administrator)
OutlookLinkHandler.exe --register
```

- **Run normally (no admin):** only the per-user protocol is registered. `--register` prints a
  note reminding you that, to silence Outlook's warning, you can re-run it elevated. Everything
  still works — you just click **Yes** on the warning each time you follow a link.
- **Run elevated:** in addition to the protocol, `--register` writes

  ```
  HKCU\Software\Policies\Microsoft\Office\16.0\Common\Security\Trusted Protocols\All Applications\outlookitem:
  ```

  after which Outlook no longer warns for `outlookitem:` links. (If the entry is already present,
  a normal run detects it and reports that the warning is already suppressed.)
- **Removal:** `--unregister` also removes that Trusted Protocols entry **when run elevated**. Run
  non-elevated, it removes only the per-user protocol and leaves the policy entry in place (it
  tells you so, and how to remove it).

**Why elevation is required:** the `…\Software\Policies\…` hive is a protected Group Policy
location whose ACL grants write access to Administrators/SYSTEM only. Office honours the setting
*only* there — the equivalent non-policy (`Software\Microsoft\Office\…`) key is ignored — so the
warning genuinely cannot be suppressed without an elevated write. The value is `16.0` for Office
2016 / 2019 / 2021 / Microsoft 365. On a managed/enterprise device this is the same mechanism IT
uses to trust internal protocols; if a Group Policy refresh ever removes the entry, re-run
`--register` elevated, or ask IT to add `outlookitem:` to the Trusted Protocols list fleet-wide.

## Link formats

| Form | Example |
|------|---------|
| Query (recommended) | `outlookitem:open?mid=<url-encoded Message-ID>` |
| Shorthand | `outlookitem:<url-encoded Message-ID>` |
| EntryID fast-path | `outlookitem:open?eid=<EntryID>&sid=<StoreID>` |

`mid` = Internet Message-ID (with or without the surrounding `<...>`; both are handled).
If both `mid` and `eid` are supplied, the EntryID is tried first and falls back to the
Message-ID search if the item has moved.

**Always URL-encode the values** — Message-IDs contain `<`, `>`, `@`, and `.`.

## Generating links in your automation

You need each message's `internetMessageId`.

**Microsoft Graph** — select it on the message:

```
GET /me/messages/{id}?$select=internetMessageId,subject
```

**Build the link (PowerShell):**

```powershell
$mid = '<PH0PR...@...outlook.com>'          # from Graph internetMessageId
$enc = [uri]::EscapeDataString($mid)
$href = "outlookitem:open?mid=$enc"
$html = "<a href=""$href"">Open in Outlook</a>"
```

**Build the link (C#):**

```csharp
string mid  = message.InternetMessageId;                 // "<...@...>"
string href = "outlookitem:open?mid=" + Uri.EscapeDataString(mid);
string html = $"<a href=\"{href}\">Open in Outlook</a>";
```

Embed that `<a>` element in the HTML body of the attention-list email. When you click it in
Outlook, the referenced message opens in Outlook.

## First-click prompt

The first time a custom-scheme link is clicked, Windows/Outlook may show a one-time
"allow this website to open a program?" confirmation. Approve it (optionally tick "always
allow") and subsequent clicks open silently.

Because `outlookitem:` is a non-standard scheme, Outlook also shows its normal hyperlink
warning — *"Microsoft Office has identified a potential security concern … This location may be
unsafe"* — displaying the URL. This is Outlook's standard behaviour for any custom scheme (it is
not specific to this handler); click **Yes** to continue. The URL shown may appear decoded (with
literal `<`, `>`), which is expected — the handler accepts the Message-ID with or without those
characters and with or without URL-encoding.

To stop this warning from appearing at all, register once from an **elevated** prompt — see
[Optional: suppress Outlook's "unsafe location" warning](#optional-suppress-outlooks-unsafe-location-warning-elevated).

## Troubleshooting

- **Diagnostic logging is OFF by default.** Turn it on when you need to diagnose an issue:
  ```powershell
  OutlookLinkHandler.exe --enable-logging    # prints the log file path
  OutlookLinkHandler.exe --disable-logging   # turn it back off
  ```
  The setting is stored per-user at `HKCU\Software\OutlookLinkHandler` (DWORD `LoggingEnabled`),
  so it persists across runs and survives `--unregister`.
- **Diagnostics log:** when enabled, `%TEMP%\OutlookItemHandler.log` records every invocation —
  the parsed ids, whether it connected to Outlook, which folder/filter matched, and the found
  subject.
- **"Could not find that message":** the item may have been deleted, or the running Outlook
  profile is a different account than the one that received it. The handler searches the Inbox
  first, then every folder in every connected store.
- **"Access is denied." right after clicking a link (Defender ASR):** if a Windows Defender
  "action blocked" toast appears citing *"Block Office communication application from creating
  child processes"*, an older direct-`command` registration is in effect. Re-run
  `OutlookLinkHandler.exe --register` to install the `DelegateExecute` COM shape (which routes
  the launch through RPCSS instead of Outlook) and confirm the registry matches the shape shown
  under *Install*. You can check for the block with:
  ```powershell
  Get-WinEvent -LogName "Microsoft-Windows-Windows Defender/Operational" -MaxEvents 40 |
    Where-Object Id -eq 1121 | Format-List TimeCreated, Message
  ```
- **Test resolution without opening a window:**
  ```powershell
  OutlookLinkHandler.exe --find --quiet "outlookitem:open?mid=<encoded>"
  # exit code 0 = found, 2 = not found, 1 = error
  ```
  (Enable logging first if you also want the matched subject/folder recorded.)

## CLI reference

| Command | Purpose |
|---------|---------|
| `--register` | Register the `outlookitem:` protocol for the current user (run **elevated** to also add the Trusted Protocols policy that suppresses Outlook's "unsafe location" warning) |
| `--unregister` | Remove the registration (run **elevated** to also remove the Trusted Protocols policy entry) |
| `--enable-logging` | Turn on diagnostic logging (reports the log file path) |
| `--disable-logging` | Turn off diagnostic logging (default) |
| `--quiet` / `--silent` | Suppress all dialogs (combine with any command) |
| `--find <url>` | Resolve the message and report via exit code + log, without opening it |
| `--help` | Show usage (also reports the current logging state) |
| `<outlookitem: url>` | Open the referenced email |

## Changing the scheme name

The scheme is defined once, at the top of `Program.cs`:

```csharp
private const string Scheme = "outlookitem";
```

Change it, rebuild, and re-run `--register`. Update the links your automation generates to
match.

## Security notes

The handler treats every link as untrusted input (a malicious `outlookitem:` link could be
emailed to you), so it is designed so that **no link can ever cause a destructive action**:

- **It only opens, never mutates.** The sole Outlook methods invoked on a matched item are
  `Display` and `Inspector.Activate` — the handler cannot forward, reply, send, delete, move, or
  modify anything, regardless of what a link contains. There is no code path that calls a
  mutating Outlook API.
- **Input allowlist.** Before any query runs, the message id is validated as printable ASCII
  with no spaces, control characters, double quotes, or non‑ASCII bytes (and bounded in length);
  EntryID/StoreID must be hex. Anything else is rejected (`mid`) or ignored (`eid`/`sid`).
- **Query-injection is blocked and inert.** Single quotes are doubled before building the DASL
  restriction, so a crafted value cannot break out of the string literal. DASL is only a *filter*
  language — even a hypothetical injection could not invoke a method, only (at most) select a
  different item.
- **Post-match verification.** After a search matches, the handler independently re-reads the
  item's real `PR_INTERNET_MESSAGE_ID` and requires an exact match before opening it. This closes
  the gap where a manipulated query might otherwise select the wrong message.
- **Protocol launches carry no flags.** When invoked from a clicked link (first argument is an
  `outlookitem:` URL), only that URL is honoured — all other arguments are ignored. This defeats
  command-line/argument-injection tricks: a crafted link cannot smuggle in `--register`,
  `--unregister`, `--enable-logging`, or even `--quiet`. Those commands work only when typed
  directly on the command line.
- Registration is per-user (`HKCU`) — no elevation needed.
- The logging preference lives in a separate per-user key (`HKCU\Software\OutlookLinkHandler`)
  so it is kept even if you `--unregister` the protocol. Logging is off unless you opt in.
