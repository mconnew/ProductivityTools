# Productivity Tools

A collection of small, self-contained tools to help with everyday productivity and to smooth
over rough edges in day-to-day workflows.

## Projects

| Project | Description |
|---------|-------------|
| [OutlookLinkHandler](src/OutlookLinkHandler) | A custom `outlookitem:` URL-scheme handler that opens a specific email in **Outlook Classic (desktop)** by Internet Message-ID — instead of being sent to OWA in a browser. Built for automations that email you a list of messages needing attention. |
| [EdgeProfileRouter](src/EdgeProfileRouter) | A WPF (.NET 10) app you set as your **default browser** that opens each link in the right **Microsoft Edge profile** based on rules that inspect the whole URL (host + path prefix / regex), e.g. `github.com/CoreWCF/*` → personal, `github.com/dotnet/*` → work. Any link no rule matches is handed to Edge to pick the profile, so it's a transparent pass-through otherwise. Includes a settings GUI, per-user (no-admin) registration, and a URL dry-run tester. |
| [WcfPrTriage](src/WcfPrTriage) | A WPF (.NET 10) app that lists open **[dotnet/wcf](https://github.com/dotnet/wcf)** pull requests and, on selecting one, drills straight to the real CI failure — which Helix queue failed, which test, and its exception / assert message + stack trace — from public GitHub, Azure DevOps, and Helix APIs (no PAT required). Collapses the many manual click-throughs normally needed to find a failing test. |
