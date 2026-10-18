# Architecture and technology-stack options for a self-hosted web "AD admin control center"

Scope: a custom web control center for a Windows AD domain. It lists computers and users, shows logged-on sessions, launches RDP shadow, RDP and SSH, manages AD users, runs diagnostics and keeps an audit log. The developer works on macOS and the targets are Windows. Research date: 2026-10-18.

## 1. Stack options and how well each fits Windows/AD integration

### Takeaway
.NET (ASP.NET Core and Blazor) hosted on a domain-joined Windows Server fits best. It is the only stack with first-class Windows auth on both Windows and Linux, it can host PowerShell in-process and it is the only one that supports impersonation (Windows only). PowerShell Universal is the fastest path to a working tool, but production now needs a paid per-server annual license (Devolutions owns it since 2026-01-01), and Windows auth and RBAC permissions are licensed features. Python and Go on Linux can do LDAP and WinRM well, but they cannot call the WTS API natively, so sessions and shadow have to go through WinRM/PowerShell or `quser`/`qwinsta` on Windows.

### Cited findings

**C# / .NET + ASP.NET Core + Blazor**
- Windows Authentication (Negotiate/Kerberos/NTLM) can be configured for ASP.NET Core apps hosted on IIS, Kestrel or HTTP.sys. The doc's default version is ASP.NET Core 10.0 and it was updated 2026-09 — [MS Learn: Configure Windows Authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- The `Microsoft.AspNetCore.Authentication.Negotiate` package lets Kestrel do Windows auth "using Negotiate and Kerberos on Windows, Linux, and macOS" — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- On Linux/macOS, Kerberos "doesn't provide any role information". To get groups you must call `options.EnableLdap("contoso.com")` so the handler reads roles from an LDAP domain — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- Linux/macOS setup steps: join the machine to the domain, run `setspn -S HTTP/mywebservice.mydomain.com mymachine`, generate a keytab with `ktpass ... -crypto AES256-SHA1`, copy it over, and set `KRB5_KTNAME`. The doc warns that "A keytab file contains domain access credentials and must be protected" — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- A maintainer in a dotnet/aspnetcore issue confirmed that SPNs and a keytab are needed on Linux even for development, and suggested `KRB5_TRACE` for debugging — [GitHub dotnet/aspnetcore#44681](https://github.com/dotnet/aspnetcore/issues/44681)
- "Impersonation is only supported on Windows". ASP.NET Core doesn't impersonate by default, so to act as the user you must use `WindowsIdentity.RunImpersonated(Async)` — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- Windows auth "isn't supported with HTTP/2": the client must downgrade to HTTP/1.1 for the handshake — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- Proxy caveat: "Negotiate authentication must not be used with proxies unless the proxy maintains a 1:1 connection affinity (a persistent connection) with Kestrel". Otherwise the proxy itself must do the authentication and pass the identity in a header. MS suggests ADFS + OIDC as an alternative — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth). A real-world failure (HAProxy in front of Kestrel on Debian returns 401) is reported at [HAProxy discourse](https://discourse.haproxy.org/t/client-negotiate-haproxy-kestrel-debian-401/5751)
- SPN rules differ by server: for Kestrel/Negotiate (user mode) the SPN goes on the service *user* account. HTTP.sys (kernel mode) uses the *machine* account SPN — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- MS has a dedicated doc on a Blazor Web App with Windows authentication — [MS Learn: Blazor Web App with Windows Authentication](https://learn.microsoft.com/aspnet/core/blazor/security/blazor-web-app-with-windows-authentication)
- PowerShell can be hosted in-process with the `Microsoft.PowerShell.SDK` NuGet package through `System.Management.Automation.PowerShell` (`PowerShell.Create().AddCommand(...)`). Constrained runspaces built from `InitialSessionState.Create()` restrict which cmdlets can run, which is useful for a least-privilege action catalog. The docs cover PS 7.4–7.7 — [MS Learn: PowerShell Host Quickstart](https://learn.microsoft.com/en-us/powershell/scripting/developer/hosting/windows-powershell-host-quickstart)
- WTS API: `WTSEnumerateSessions` needs query-information permission on the session and the buffer must be freed with `WTSFreeMemory` — [MS Learn WTSEnumerateSessions](https://msdn.microsoft.com/en-us/library/aa383833.aspx). The [Cassia](https://github.com/danports/cassia) .NET wrapper (sessions, logoff, disconnect, message, processes) is on NuGet, but its README lists platforms only up to Windows 10 / Server 2016, so it may be old

**PowerShell Universal (PSU)**
- PSU was acquired from Ironman Software by **Devolutions on 2026-01-01**. Docs now live at docs.devolutions.net, and ironmansoftware.com/pricing redirects to store.devolutions.net — [ComponentSource brand page](https://www.componentsource.com/brand/ironman-software); [Devolutions PSU docs: Licensing](https://docs.devolutions.net/powershell-universal/licensing)
- Licensing model: per server, where one server is one running PSU instance. Each container instance needs its own license (10 instances = 10 licenses), and each IIS website running PSU needs its own license — [Devolutions docs: Licensing](https://docs.devolutions.net/powershell-universal/licensing)
- Licensed-only features include **Windows Authentication**, OIDC, SAML2, WS-Fed, client certs and app tokens (all "Enterprise Authentication"). Also licensed: permissions and custom authorization scripts ("Enterprise Authorization"), Git, SQL/PostgreSQL, computer groups, branding, OpenAPI, triggers, **terminals**, workflows, and the App Page/Function editors — [Devolutions docs: Licensing](https://docs.devolutions.net/powershell-universal/licensing)
- Free **Developer edition** (announced Feb 2026): full feature set at no cost, but only on a single-user workstation with a Devolutions account login. It "cannot operate on servers, cloud infrastructure, or shared environments", and production needs a paid Server license — [Devolutions blog, 2026-02](https://devolutions.net/blog/2026/02/powershell-universal-developer-edition-unlock-full-features-at-no-cost)
- Pricing: reseller listings say PSU is licensed "per server, per year" with renewal required, and that perpetual licenses are no longer sold. Tiers are Business (1 server) and Site (one physical location), on 1-year or 3-year terms. Prices start at **US$489.99**, and one listing shows $1,322.98 for a 3-year Business license (v5.x line) — [ComponentSource](https://www.componentsource.com/product/powershell-universal/licensing); [ComponentSource buy page](https://www.componentsource.com/product/powershell-universal/buy/1623978). Treat this as unverified for 2026: Devolutions' own store page did not render and the ComponentSource prices page returned 403
- Features: interactive web apps written in PowerShell with "over 70" controls, plus REST APIs, scripts, jobs and scheduling — [Devolutions PSU landing](https://devolutions.net/landing/powershell-universal/)

**Python (FastAPI/Django) + ldap3 + pypsrp/pywinrm**
- pypsrp is a Python client for PSRP and WinRM with four API levels (simple client, WSMan, WinRS, PSRP runspace pools/pipelines). Kerberos on Linux needs system GSSAPI/krb5 headers plus `pip install pypsrp[kerberos]` (python-gssapi, pykrb5). Newer versions add an SSH transport — [PyPI pypsrp](https://pypi.org/project/pypsrp/); [GitHub jborean93/pypsrp](https://redirect.github.com/jborean93/pypsrp)
- Existing Python AD web UIs follow this pattern: ad-webmanager uses Flask + python-ldap — [GitHub gombee/ad-webmanager](https://github.com/gombee/ad-webmanager)

**Go (go-ldap + WinRM) single binary**
- masterzen/winrm (Go) supports Basic (local accounts only), NTLM (with optional message encryption), **Kerberos** (`ClientKerberos`, for domain accounts), CredSSP and HTTPS with client certs. It has about 479 stars, and its README does not mention PSRP — [GitHub masterzen/winrm](https://github.com/masterzen/winrm)

**Node/TypeScript + React/Vue**
- No Node-specific sources were collected this session (see Gaps).

**Desktop app (WPF/WinUI/Avalonia)**
- OpenRSAT (Tranquil IT) is a GPL-3.0 cross-platform desktop RSAT alternative that works with Samba AD. It shows that a cross-platform desktop AD admin tool is feasible — [AlternativeTo: OpenRSAT](https://alternativeto.net/software/openrsat/about/); [oosmetrics: tranquilit/OpenRSAT](https://oosmetrics.com/repo/tranquilit/OpenRSAT)

### Inferences
- **Recommended primary stack:** ASP.NET Core (.NET 8 LTS or 10 LTS) with Blazor Server or Interactive Server, hosted on a domain-joined Windows Server VM under a gMSA or service account, with Kestrel/HTTP.sys behind IIS or no proxy. Reasons: SSO without a keytab, groups/roles out of the box, optional impersonation, in-process PowerShell (ActiveDirectory module, `quser`, CIM), direct WTS P/Invoke against remote servers, and the developer can write C# on macOS (cross-platform SDK) and test Windows-specific parts on a Windows VM.
- Blazor Server keeps the AD logic and credentials server-side, which suits an admin tool better than WASM, where every privileged operation would need a separate API anyway. *(This is reasoning, not a sourced fact.)*
- On Linux/Docker, .NET still works with Negotiate + keytab + `EnableLdap`, but impersonation and the WTS API are lost. Session queries then have to go through WinRM/PSRP to a Windows box, which puts the Linux .NET option at roughly the same capability as Python or Go.
- **PSU** is the best fit for a "mostly PowerShell scripts with a UI" approach and gives RBAC, audit-ish job history, scheduling and terminals out of the box. But the features this project needs most (Windows auth, granular permissions, terminals) are licensed, and the license is a recurring per-instance subscription. Budget at least about $500/yr per server, to be confirmed with Devolutions. The free Developer edition is fine for prototyping on a laptop but not for a shared server.
- **Python and Go** fit well for LDAP and for remote commands over WinRM. For logged-on sessions they would run `quser /server:X`, `qwinsta` or `Get-CimInstance Win32_LogonSession` remotely and parse the output, which is more fragile than WTS. Shadow cannot be performed server-side at all, because it is an interactive `mstsc /shadow` client feature (see section 2).
- **Node/TS** has no native Kerberos SSO comparable to ASP.NET (the `kerberos` npm package exists but this is unverified). A Node backend makes the most sense only as a thin BFF in front of a .NET/PowerShell worker.
- **Desktop app:** WPF/WinUI is Windows-only, but it can launch `mstsc.exe /shadow:<id> /v:<host> /control` directly and use WTS APIs locally. That is the main advantage over the web, because a browser cannot launch mstsc without a custom URL handler or a downloaded .rdp file. Avalonia gives macOS support, but on macOS there is no mstsc shadow. A hybrid design works well: a web UI for inventory, AD and audit, plus a small Windows "launcher" (protocol handler, e.g. `adcc://shadow?host=..&id=..`) or generated .rdp files for RDP and shadow.

### Gaps
- No official 2026 Devolutions PSU price list was retrievable: store.devolutions.net rendered empty and ComponentSource returned 403. The $489.99 "from" price may be v5.x-era.
- The PSU licensing page lists only licensed features. Whether a fully unlicensed (non-Developer) PSU install still runs basic apps in production in 2026 is ambiguous, because older docs implied a free tier and the 2026 Devolutions page implies the Developer license only.
- The status of Cassia on Server 2019/2022/2025 was not verified, and no actively maintained .NET WTS wrapper was found.
- No sources were collected for Node/TS AD/Kerberos libraries (e.g. `ldapts`, `kerberos` npm), go-ldap specifics, or Avalonia macOS details. The System.DirectoryServices (Windows-only) vs System.DirectoryServices.Protocols (cross-platform) distinction is from general knowledge and was not verified this session.

## 2. Which components can be reused (Guacamole, xterm.js, Homer, Homarr/Dashy)

### Takeaway
Apache Guacamole (Apache-2.0, v1.6.0) can serve as the browser RDP/SSH gateway. Its encrypted-JSON auth lets the control center generate one-click session URLs without a second login, and guacamole-common-js can be embedded in a custom UI. However, **Guacamole has no RDP shadow parameter**, so shadow still needs native `mstsc /shadow` on a Windows admin workstation. Homer is a good model for a static YAML-driven launcher UX but has no backend.

### Cited findings
- The latest Guacamole manual is **1.6.0**. It includes developer chapters on guacamole-common-js and "Writing your own Guacamole application", and auth extensions for LDAP/AD, HTTP header auth and **Encrypted, signed JSON** auth — [Guacamole manual](https://guacamole.apache.org/doc/gug/)
- Encrypted JSON auth: an external system builds a signed, AES-128-encrypted JSON describing connections (protocol + parameters). A key shared through `json-secret-key` lets Guacamole open sessions without its own login screen — [Guacamole: Encrypted JSON auth](https://guacamole.apache.org/doc/gug/json-auth.html)
- The RDP parameters include `console` (admin/console session), `preconnection-id`/`preconnection-blob` (Hyper-V) and `load-balance-info` (broker). **There is no shadow or session-ID parameter.** SSH supports key/password auth, host-key checks, SFTP and typescript/graphical session recording — [Guacamole: Configuring](https://guacamole.apache.org/doc/gug/configuring-guacamole.html)
- No documentation was found showing Guacamole supporting mstsc-style shadowing. Native shadow is `quser /server:RDSH01` then `mstsc /shadow:<ID> /v:RDSH01 /control`, and it cannot shadow disconnected or initializing sessions — [TruGrid help](https://help.trugrid.com/en/article/how-to-shadow-rdp-sessions-for-remote-support-without-third-party-tools-1ej3etx/); [LizardSystems](https://lizardsystems.com/terminal-services-manager/how-to/how-to-shadow-a-remote-desktop-session/)
- With `/shadow`, mstsc ignores credentials saved via cmdkey, so scripted shadow with stored credentials is unreliable — [Royal Apps support thread](https://support.royalapplications.com/support/discussions/topics/17000022823)
- Security research on RDS shadowing internals — [PT Swarm: RDS Shadowing](https://swarm.ptsecurity.com/remote-desktop-services-shadowing/)
- guacamole-client is Apache-2.0 — [GitHub apache/guacamole-client](https://github.com/apache/guacamole-client)
- Projects that embed Guacamole:
  - **guacamole-sharp**: a .NET backend paired with guacamole-common-js on the frontend, and the closest match to an ASP.NET stack — [GitHub m-expunged/guacamole-sharp](https://github.com/m-expunged/guacamole-sharp)
  - **guacamole-easy-tunnel**: strips the Guacamole UI and leaves websocket/HTTP tunnelling driven by an AES-encrypted JSON query-string payload — [GitHub mindcollapse/guacamole-easy-tunnel](https://github.com/mindcollapse/guacamole-easy-tunnel)
  - Russian walkthrough of embedding Guacamole RDP into a 1C web field through its API (Docker Compose, dedicated user) — [Infostart](https://infostart.ru/public/1922815/)
- **Homer**: a static HTML/JS homepage driven by YAML config, with no backend. Features: fuzzy search, multi-page grouping, themes, PWA, keyboard shortcuts and "smart cards". Docker image `b4bz/homer`, Apache-2.0, about 11.7k stars — [GitHub bastienwirtz/homer](https://github.com/bastienwirtz/homer)

### Inferences
- **Suggested reuse:**
  - Run `guacd` + guacamole-client in Docker next to the app. The control center generates encrypted-JSON tokens per user and per target, and opens an iframe or new tab. This gives browser RDP/SSH from macOS with Guacamole's session recording, so audit comes for free.
  - Alternatively, use guacamole-common-js directly in Blazor through JS interop (the guacamole-sharp pattern) for tighter UI integration.
- **Shadow:** offer a "Shadow" button that generates an `.rdp`-less command or custom URI handled by a small Windows helper, or show a copyable `mstsc /shadow:ID /v:HOST /control /noConsentPrompt` command. From macOS there is no native shadow client, so shadow requires a Windows jump host reached over Guacamole RDP, where mstsc /shadow runs inside that session.
- **xterm.js** is the standard browser terminal if SSH or PowerShell terminals are built without Guacamole. Guacamole's SSH already renders its own terminal, so xterm.js is only needed for a custom WebSocket-based PowerShell console.
- Homer, Homarr and Dashy are UX references (tile and launcher layout, YAML-defined services), not integration targets. None of them understand AD, so copying their card and search UX into Blazor is simpler than embedding them.

### Gaps
- No Guacamole REST API documentation was captured, though the web app is known to use `/api/tokens` (unverified here).
- Homarr and Dashy were not researched (no fetches).
- FreeRDP client support for RDS shadow was not confirmed. It would matter for a non-Windows shadow path.
- xterm.js details (license, current version) were not fetched.

## 3. Existing open-source AD admin and self-service web portals

### Takeaway
The OSS landscape is thin and mostly Python or PHP on Linux:
- LDAP Account Manager (PHP, GPL, with a commercial Pro edition)
- ad-webmanager (Flask + python-ldap, MIT, forked from samba4-manager)
- GNU-ADMW (Django)
- OpenRSAT (a desktop app)

None of them covers sessions, shadow or RDP, so they are references for the AD-CRUD part only.

### Cited findings
- **LDAP Account Manager (LAM):** GPL base edition plus a commercial **LAM Pro**. It runs on PHP 8.1+ and supports OpenLDAP, **Active Directory**, Samba 4, 389 DS and others, with Windows (Samba 4/AD) user, group and host modules. Pro-only features include user self-service, password self-reset and AD LDS — [Wikipedia: LAM](https://en.wikipedia.org/wiki/LDAP_Account_Manager); [LAM manual](https://ldap-account-manager.org/static/doc/manual/pr01.html)
- **ad-webmanager:** a Flask + python-ldap web UI for AD and Samba4 that started as a fork of samba4-manager. Features:
  - Tree view with a container blacklist
  - Configurable search attributes and extra columns
  - Users log in with their own directory credentials; an admin group (default Domain Admins) gets read/write
  - Only file/console logging, no audit feature
  - MIT license, Dockerfile on port 8080
  - The fetched repo is a fork with 0 stars; the upstream is VicentGJ/AD-webmanager

  [GitHub gombee/ad-webmanager](https://github.com/gombee/ad-webmanager)
- **GNU-ADMW:** a Django + `ms_active_directory` web interface, GPL, with Samba4 support — [GNU-ADMW (proxied GitHub link)](https://proxy-ga.blitzz.co/proxy/123456/github.com/Tiozao-do-Linux/GNU-ADMW)
- **S-RCS:** a Dockerized portal for users, groups and OUs with audit logging, announced only on Dev.to. No repo or license could be confirmed — [DEV Community](https://dev.to/ali_zeynalli_460a35b6c666/s-rcs-a9)
- **OpenRSAT** (Tranquil IT): a GPL-3.0 cross-platform desktop RSAT alternative — [AlternativeTo](https://alternativeto.net/software/openrsat/about/)

### Inferences
- Patterns worth borrowing:
  - The ad-webmanager admin-group gate (AD group mapped to app role)
  - LAM's modular, per-objectClass account editors
  - Configurable visible attributes and columns
- Their main weakness for this project is missing audit and session features. A custom build should design an append-only audit log from day one (who, what, target, before/after, result), stored in SQL, ideally also forwarded to the Windows Event Log or a SIEM.
- Using an existing OSS portal as the base is not recommended: the stack mismatch (PHP/Python on Linux) and the missing session/RDP features outweigh the CRUD code saved.

### Gaps
- Commercial comparators were not researched: ManageEngine ADManager/ADSelfService and Adaxes.
- Star counts and last-commit dates for GNU-ADMW and upstream AD-webmanager were not verified.
- Samba-specific tools (samba-tool web front-ends) were not found.
- r/sysadmin and r/PowerShell discussions were not retrieved this session.

## 4. Deployment: domain-joined Windows Server VM vs Docker on Linux with a keytab; reverse proxy, HTTPS, internal CA

### Takeaway
A domain-joined Windows Server VM gives Kerberos SSO with the least friction, plus impersonation, WTS and the PowerShell AD module. Linux Docker needs an SPN, a protected keytab and `EnableLdap` for groups, loses impersonation and WTS, and cannot sit behind a typical reverse proxy unless the proxy keeps 1:1 connection affinity or does the auth itself.

### Cited findings
- Kestrel/Negotiate on Linux/macOS requires domain join, `setspn`, a keytab from `ktpass` with AES256 crypto and `KRB5_KTNAME`. The keytab must be protected as a credential — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- Group/role claims on Linux require `EnableLdap` — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- Proxy rules: either 1:1 connection affinity, or the proxy handles authentication and passes identity in a header. ADFS + OIDC is the suggested alternative behind load balancers — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- Negotiate doesn't work over HTTP/2 for the handshake — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- On IIS the ASP.NET Core Module forwards the Windows auth token to the app by default (configured in web.config) — [MS Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth)
- If PSU runs in containers, licensing is per container instance — [Devolutions docs](https://docs.devolutions.net/powershell-universal/licensing)

### Inferences
- **Recommended topology:**
  - Windows Server 2022/2025 VM, domain-joined.
  - App runs as a **gMSA** (or a dedicated service account) with delegated AD permissions on specific OUs, not Domain Admin.
  - Hosting is either IIS with Windows auth, or Kestrel/HTTP.sys as a Windows service.
  - TLS certificate comes from the internal AD CS (an enterprise CA is already trusted by domain-joined clients).
  - Set the `HTTP/adcc.corp.local` SPN on the right account (service account for Kestrel/IIS with useAppPoolCredentials; machine account for HTTP.sys).
  - A Guacamole Docker host (Linux) sits alongside it, as a separate service behind its own TLS.
- If Docker on Linux is preferred, simplify auth by putting the app behind an OIDC provider and using cookie auth instead of raw Negotiate. Options are ADFS, Keycloak with LDAP/Kerberos federation, or Authentik. Remote Windows operations then go over WinRM-HTTPS with Kerberos from the container, which needs krb5.conf and a keytab or gMSA-equivalent credentials.
- macOS dev loop:
  - Build and run on macOS with mocked AD/WTS interfaces, or with `System.DirectoryServices.Protocols` against a lab DC (verify it is cross-platform).
  - Deploy to a Windows test VM in a lab domain (e.g. Windows Server eval in UTM/Parallels) for Kerberos/WTS/PowerShell integration tests.
  - Safari and Chrome on macOS can do Kerberos SSO only if the Mac has a TGT (`kinit` or Kerberos SSO extension); otherwise the browser falls back to a password prompt or fails. *(Unverified this session.)*

### Gaps
- Specific guidance on gMSA with ASP.NET Core on IIS (useAppPoolCredentials, SPN placement) was not fetched.
- Browser Kerberos configuration on macOS (Chrome `AuthServerAllowlist`, Safari) was not sourced.
- No official doc on running WinRM/PSRP from Linux containers with gMSA was collected.

## 5. Effort estimates: realistic MVP scope for a single developer

### Takeaway
No sourced effort benchmarks were found, so the estimates below are reasoned inferences. The PSU route can deliver a usable internal MVP in about 1–3 weeks. A custom ASP.NET Core + Blazor MVP with Guacamole likely takes about 6–10 weeks of focused work for one experienced developer.

### Cited findings
- The PSU Developer edition gives the full feature set for local prototyping at no cost, and production then needs a Server license — [Devolutions blog](https://devolutions.net/blog/2026/02/powershell-universal-developer-edition-unlock-full-features-at-no-cost)
- PSU apps ship with about 70+ UI controls plus REST APIs, jobs and scheduling, which cuts UI work — [Devolutions PSU landing](https://devolutions.net/landing/powershell-universal/)
- Guacamole's encrypted-JSON auth removes the need for a second login for RDP/SSH sessions — [Guacamole docs](https://guacamole.apache.org/doc/gug/json-auth.html)

### Inferences
These are estimates, not sourced facts.

**Custom .NET MVP, about 6–10 weeks:**

| Week | Work |
|---|---|
| 1 | Scaffold: Blazor Server, Negotiate auth, AD group → role policies, SQL/SQLite audit table, CI to Windows VM |
| 2 | AD inventory: computers and users via LDAP; search, filter, OU tree; paging; caching |
| 3 | Sessions: WTS P/Invoke or `quser`/CIM across servers with parallelism and timeouts; logoff, disconnect, message |
| 4 | Launch actions: .rdp file generation, shadow command/URI helper, Guacamole JSON-token integration (RDP/SSH in browser) |
| 5–6 | AD user management: create, disable/enable, reset password, unlock, group membership, move OU. Every action is audited, with confirmation dialogs |
| 7 | Diagnostics: ping, DNS, port checks, WinRM check, uptime, disk, services, event-log snippets via PowerShell runspaces |
| 8+ | Hardening: least-privilege delegation, CSRF/anti-forgery, rate limiting, error handling, packaging, docs |

**PSU MVP, about 1–3 weeks:** reuse existing PowerShell (ActiveDirectory module, `quser`), PSU Apps for the UI, built-in RBAC and job history as the audit log. The trade-offs are a recurring license and less UI control.

The biggest schedule risks are:
- Kerberos/SPN/delegation debugging (double-hop when the web app acts on remote servers as the user, which needs constrained delegation or a service account)
- Shadow UX (client-side only, needs Group Policy for shadow consent/permissions)
- Testing Windows-only code from macOS

### Gaps
- No published case studies or time-to-MVP figures for comparable custom AD admin portals were found.
- No r/sysadmin or r/PowerShell threads comparing PSU vs custom builds were retrieved this session.
