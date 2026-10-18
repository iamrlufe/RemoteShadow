# Open-source / free self-hosted platforms as a base for a unified AD sysadmin control center

Research date: 2026-10-18. Repo activity numbers come from the GitHub REST API, queried on 2026-10-18. Example call: `https://api.github.com/repos/<owner>/<repo>` and `/releases/latest`. Treat statements from older sources (pre-2025) as possibly outdated; they are flagged inline.

---

## Q0. Platform profiles: what each one does, architecture, protocols, and extensibility

### Takeaway
The candidates fall into two groups:
- **Agent-based remote-support/RMM tools** (MeshCentral, Tactical RMM, which uses MeshCentral, RustDesk, Remotely, Aspia). The agent captures the screen of the session the user is already in, so they "see what the user sees" by design.
- **Gateways / bastions / PAM** (Apache Guacamole, Teleport, JumpServer, Next Terminal, Warpgate, ShellHub, Windows Admin Center). These broker RDP/SSH/VNC from the browser. Over RDP they open a *new* session; they do not natively shadow.

### Cited Findings

**Apache Guacamole** (Apache-2.0, clientless HTML5 gateway: guacd + Java web app)
- 1.6.0 is the latest tag. Tags: 1.6.0, 1.6.0-RC3/RC2/RC1, 1.5.5. The 1.6.0 tag commit is dated 2025-06-19. — [GitHub API tags](https://api.github.com/repos/apache/guacamole-server/tags)
- 1.6.0 changes:
  - FreeRDP 3 support (GUACAMOLE-1026).
  - Batch import of connections from CSV/JSON/YAML.
  - Session-recording player with activity and keyboard histograms.
  - LDAP: TLS level, proxy, and a domain parameter token.
  - TOTP can be disabled per group and combined with SAML.
  - Signed/encrypted SAML requests, and OIDC claims mapped to parameter tokens.
  - Source: [Guacamole 1.6.0 release notes](https://guacamole.apache.org/releases/1.6.0/)
- The 1.6.0 release notes do not mention RDP shadowing or joining existing Windows sessions. — [Guacamole 1.6.0 release notes](https://guacamole.apache.org/releases/1.6.0/)
- Repos are active. guacamole-server was last pushed 2026-10-17 (~4.0k stars); guacamole-client was last pushed 2026-10-14. — [GitHub API](https://api.github.com/repos/apache/guacamole-server)
- Guacamole's own "connection sharing" (view-only share links) shares a *Guacamole* session. It does not shadow an existing Windows console session. — [edtechirl blog](https://www.edtechirl.com/p/apache-guacamole-how-to-set-up-and)

**MeshCentral** (Apache-2.0; Node.js server; MeshAgent on endpoints; web UI; optional Intel AMT agentless)
- Latest release is 1.2.6, published 2026-09-24 (~7.4k stars). — [GitHub API](https://api.github.com/repos/Ylianst/MeshCentral)
- How remote desktop works: the MeshAgent takes screenshots, cuts them into compressed JPEG tiles, and sends them to the browser over a websocket relay or WebRTC. The developer confirmed it is "not starting a different remote session... just looking at the local session the user is using". This is from issue #36, 2018. — [MeshCentral issue #36](https://github.com/Ylianst/MeshCentral/issues/36)
- Desktop control methods: Mesh Agent, RDP, Intel AMT.
- For local device groups behind a relay agent, `"ssh": true` in the domain config enables SSH, SFTP, RDP and VNC.
- The `ClickOnce` option adds native RDP launch options on Windows browsers.
- Source: [MeshCentral user guide](https://docs.meshcentral.com/meshcentral/)
- Extensibility: the docs' table of contents lists MeshCentral Router (TCP port mapping) and web relay (reach internal websites without the Router). — [MeshCentral docs](https://docs.meshcentral.com/meshcentral/)

**Tactical RMM** (Django/Vue/Go agent; remote control delegated to a bundled MeshCentral)
- Remote access comes from the bundled MeshCentral, alongside monitoring, Windows patching and scripting. — [openmsp.ai review (third-party)](https://www.openmsp.ai/blog/tactical-rmm-review); [Tactical RMM sponsor docs](https://docs.tacticalrmm.com/sponsor/)
- Latest release is v1.5.2 (2026-08-11); last push 2026-09-30 (~4.5k stars). GitHub reports the license as "NOASSERTION" (a custom license). — [GitHub API](https://api.github.com/repos/amidaware/tacticalrmm)

**RustDesk** (AGPL-3.0 client and OSS server; Rust/Flutter; own protocol with ID/relay servers hbbs/hbbr)
- Client 1.5.0 was released 2026-09-30 (~126k stars). Server OSS 1.1.16 was released 2026-07-20. — [GitHub API rustdesk](https://api.github.com/repos/rustdesk/rustdesk); [GitHub API rustdesk-server](https://api.github.com/repos/rustdesk/rustdesk-server)
- 1.2.6 (2024-06-23) added "support of connecting to specific Windows session". — [RustDesk PR #7184](https://github.com/rustdesk/rustdesk/pull/7184), from [release notes](https://api.github.com/repos/rustdesk/rustdesk/releases)
- 1.4.7 (2026-06-02): "Fix Windows session-based logon and lock-screen detection". — [PR #14620](https://github.com/rustdesk/rustdesk/pull/14620)
- 1.5.0: "recognize Hyper-V Enhanced Sessions by protocol" and "allow existing session to foreground". — [PR #16223](https://github.com/rustdesk/rustdesk/pull/16223); [PR #16349](https://github.com/rustdesk/rustdesk/pull/16349)
- Server Pro adds a web console, 2FA, audit log, access control, address book, centralized settings and distributed relays. LDAP, OIDC and the custom client generator require Basic or higher. — [RustDesk pricing](https://rustdesk.com/pricing/)

**Teleport** (Go; Auth/Proxy services plus a Windows Desktop Service; web UI and Teleport Connect; source on GitHub under AGPL-3.0, binaries under a separate license)
- How Windows access works:
  - The Desktop Service sits between the Proxy and Windows hosts and translates the Teleport Desktop Protocol to RDP.
  - The host must trust the Teleport CA, and Teleport authenticates with certificates (passwordless).
  - Setup disables NLA and enables RemoteFX.
  - Sessions are recorded automatically and viewable under Audit → Session Recordings.
  - Source: [Teleport desktop access docs](https://goteleport.com/docs/enroll-resources/desktop-access/getting-started/)
- From 18.11.1, Teleport uses a Microsoft-signed auth package for LSA protection. Earlier versions require LSA protection to be disabled. — [Teleport desktop access docs](https://goteleport.com/docs/enroll-resources/desktop-access/getting-started/)
- The latest GitHub "release" is v18.10.0 (2026-07-09); the docs reference 18.11.1. Last push was 2026-09-17. — [GitHub API](https://api.github.com/repos/gravitational/teleport)

**Windows Admin Center (WAC)** (Microsoft, free, gateway-based web console; not open source)
- Tool list includes:
  - Remote Desktop: start an interactive session, disconnect, send Ctrl+Alt+Del.
  - PowerShell, Processes, Services, Registry, Events, Files, Firewall, Updates, Local users & groups, Certificates, DNS, DHCP, Storage, Hyper-V VMs.
  - Active Directory, as an extension-feed preview: create users/groups, search users/computers/groups, reset passwords, manage group membership, configure delegation.
  - Settings, which includes RBAC settings.
  - Source: [Microsoft Learn – Manage servers with WAC](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/use/manage-servers) (updated 2025-08-05)
- The documented Remote Desktop features do not include shadowing an existing session. — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/use/manage-servers)
- A recent article notes that VMConnect default changes apply to anything that launches VMConnect, including WAC. — [4sysops](https://4sysops.com/archives/hyper-v-enhanced-session-mode-vmconnect-no-longer-redirects-smart-cards-webauthn-and-printers-by-default/)

**Remotely** (GPL-3.0; .NET/Blazor server plus agent; remote control and scripting)
- Status:
  - The repo is at immense/Remotely and is not archived.
  - Last push was **2024-12-17**; last release was **v2024.08.07.0819** (2024-08-12).
  - It has 237 open issues and 11 open PRs.
  - Source: [GitHub API](https://api.github.com/repos/immense/Remotely); [GitHub repo](https://github.com/immense/Remotely)
- Server config options:
  - `RemoteControlNotifyUsers` shows a notification to the end user when unattended control starts.
  - `EnforceAttendedAccess` prompts clients to allow unattended remote control.
  - Source: [GitHub repo](https://github.com/immense/Remotely)

**JumpServer** (GPL-3.0 PAM/bastion; multi-component; Community and paid Enterprise editions)
- Latest release is v5.1.0 (2026-10-15); last push 2026-10-16 (~31.8k stars). — [GitHub API](https://api.github.com/repos/jumpserver/jumpserver)
- Brokers SSH, RDP, Kubernetes, databases, websites, RemoteApp and VirtualApp; records sessions; has a credential vault. — [JumpServer GitHub](https://github.com/jumpserver/jumpserver); [madalin.me comparison](https://madalin.me/warpgate-vs-jumpserver-zero-trust-access-without-an-enterprise-budget)
- Heavier footprint: multi-component, with a 4 vCPU / 8 GB floor according to a third-party comparison. — [madalin.me](https://madalin.me/warpgate-vs-jumpserver-zero-trust-access-without-an-enterprise-budget)

**Next Terminal** (bastion with RDP/SSH/VNC/Telnet/HTTP, session recording and replay; Guacamole-based)
- Latest release is v3.10.3 (2026-10-18); very active. — [GitHub API](https://api.github.com/repos/next-terminal/next-terminal)
- From v2.0.0 the backend is no longer open source; releases up to v1.3.9 remain Apache-2.0. The free edition allows unlimited users and 100 assets. — [Next Terminal GitHub](https://github.com/next-terminal/next-terminal); [next-terminal.com](https://www.next-terminal.com/)
- Runs as a single container from 1 vCPU / 512 MB. — [next-terminal.com](https://www.next-terminal.com/)

**Warpgate** (Apache-2.0; Rust; single binary plus SQLite; clientless bastion)
- Protocols listed: SSH, HTTPS, RDP, VNC, Kubernetes, PostgreSQL, MySQL, plus a browser terminal.
- Recordings are replayable for SSH, Kubernetes, RDP and VNC, with storage in S3.
- Auth: OIDC SSO, TOTP; LDAP is used only for SSH-key sync.
- Source: [warpgate.null.page](https://warpgate.null.page/)
- Latest release is v0.29.2 (2026-10-07). — [GitHub API](https://api.github.com/repos/warp-tech/warpgate)

**ShellHub** (Apache-2.0; SSH gateway for Linux, IoT and containers)
- Latest release is v0.26.0 (2026-07-06). — [GitHub API](https://api.github.com/repos/shellhub-io/shellhub)
- Described as a "centralized SSH gateway for ... Linux servers, IoT devices, and containers". — [opensourcesoftware.io](https://opensourcesoftware.io/opensource-ssh-access-management-software/)

**Other options found**
- **Aspia** (GPL-3.0; agent-based remote desktop and management with its own router/relay): v3.0.37 (2026-10-15), active. — [GitHub API](https://api.github.com/repos/dchapyshev/aspia); [AlternativeTo](https://alternativeto.net/software/remotely/?p=3)
- **DWService**: free and open-source remote support; agent can be permanent or on-demand. — [helpwire.app](https://www.helpwire.app/blog/remote-support-software-open-source/)

### Inferences
- No single open-source product does everything on its own (shadowing a workstation session, browser RDP/SSH to servers, and an AD-integrated inventory). The realistic architecture is a combination:
  1. An agent-based tool (MeshCentral, or Tactical RMM + MeshCentral, or RustDesk Pro) for workstation "see the user's screen".
  2. A gateway (Guacamole, JumpServer, Warpgate or Teleport) for RDP/SSH to servers.
  3. WAC for server management tooling.
- MeshCentral alone covers the most of the requirement list: agent desktop, web RDP/SSH/VNC through relay, SSPI/LDAP, 2FA, consent prompts, and Apache-2.0.
- Guacamole is the most mature *framework* to build a custom UI on. It is embeddable (guacamole-common-js), has a REST API and auth extensions, and Next Terminal is itself built on it.

### Gaps
- Exact list of Guacamole REST API and extension points, and a Kerberos/NLA status table for 1.6.0, were not fetched.
- JumpServer Enterprise feature split and pricing: not found on a primary source.
- Whether the Teleport Desktop Service supports Windows 11 and Server 2025: the docs page did not list versions.
- ShellHub, JumpServer and Next Terminal API details were not fetched.

---

## Q1. Which tools can attach to an EXISTING logged-on user session on a Windows workstation, and with or without consent?

### Takeaway
Only the **agent-based tools** (MeshCentral, Tactical RMM via MeshCentral, RustDesk, Remotely, Aspia, DWService) show and control the user's current interactive session. They do it by capturing the screen of the console session (and, in MeshCentral and RustDesk, a selectable RDP session); this is not RDP shadow. Consent is configurable in MeshCentral and Remotely. No gateway product here documents native `mstsc /shadow`-style RDP shadow: not Guacamole, Teleport, WAC, JumpServer, Warpgate or Next Terminal.

### Cited Findings
- **MeshCentral**:
  - Remote desktop views the local session the user is using; it does not start a new session. — [issue #36](https://github.com/Ylianst/MeshCentral/issues/36)
  - When an RDP session is open, MeshCentral asks whether to connect to the console or the RDP session.
  - With user consent enabled, the consent prompt goes to the chosen session for desktop. A reported bug (issue #6710) sends file-transfer consent to the console session instead.
  - Source: [issue #6710](https://github.com/Ylianst/MeshCentral/issues/6710)
- **MeshCentral consent flags**: DesktopNotifyUser, DesktopPromptUser and FilesNotifyUser, configurable at multiple levels (server/domain, device group, user). — [DeepWiki MeshCentral security](https://deepwiki.com/Ylianst/MeshCentral/8-security-and-authentication) (third-party code summary; verify against config schema)
- **MeshCentral, open issue (Oct 2025)**: a delegated non-admin with Remote Control rights can still seize or request control of an existing RDP session even when mstsc is disabled. The request asks for a prompt and timeout for the existing user. — [issue #7397](https://github.com/Ylianst/MeshCentral/issues/7397)
- **MeshCentral, known problem**: a viewed active RDP session gets kicked to the login screen after some time when accessed via MeshRouter/web UI. — [issue #6651](https://github.com/Ylianst/MeshCentral/issues/6651)
- **RustDesk**:
  - Supports connecting to a specific Windows session since 1.2.6 (June 2024).
  - 1.5.0 (Sept 2026) adds "allow existing session to foreground" and Hyper-V Enhanced Session recognition.
  - Sources: [PR #7184](https://github.com/rustdesk/rustdesk/pull/7184); [PR #16349](https://github.com/rustdesk/rustdesk/pull/16349); [releases](https://api.github.com/repos/rustdesk/rustdesk/releases)
  - For login-screen and UAC access, the RustDesk service must be installed as admin, which enables unattended access. — [AnyViewer guide (third-party)](https://www.anyviewer.com/fr/how-to/rtdk-unattended-access-2578.html)
- **Remotely**: unattended control with optional end-user notification (`RemoteControlNotifyUsers`) or enforced attended access (`EnforceAttendedAccess`). — [Remotely repo](https://github.com/immense/Remotely)
- **Apache Guacamole**:
  - No native RDP shadow.
  - A 2019 Apache mailing-list reply said RDP shadow "would likely be possible" but asked about feasibility. Nothing shows it was implemented.
  - 1.6.0 release notes don't mention it.
  - Sources: [Apache mail archive "RDP Shadow option"](https://lists.apache.org/thread/wq4x9g8zvdwn1c61yfpy09443zd6q2c8); [1.6.0 release notes](https://guacamole.apache.org/releases/1.6.0/)
  - FreeRDP's own `freerdp-shadow-cli` server does not work with the Windows RDP client or Guacamole. — [FreeRDP issue #8744](https://github.com/FreeRDP/FreeRDP/issues/8744)
- **Windows Admin Center Remote Desktop**: documented features are start session, disconnect and Ctrl+Alt+Del; no shadow. — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/use/manage-servers)
- **Teleport Windows desktop access**: the docs describe opening a remote desktop connection (RDP via the Desktop Service) and recording it. They do not say whether users can join an existing Windows session. — [Teleport docs](https://goteleport.com/docs/enroll-resources/desktop-access/getting-started/)
- **Native Windows shadow, for reference**:
  - Controlling another session requires Full Control or the Remote Control special permission.
  - The user is warned before monitoring unless the warning is disabled by policy.
  - Sources: [Microsoft `shadow` command](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/shadow); [woshub guide](https://woshub.com/rdp-session-shadow-to-windows-10-user/)
  - Shadow pauses while the user has a UAC prompt on the secure desktop. — [RDPSoft KB](https://www.rdpsoft.com/knowledge-base/troubleshooting-shadowing-with-supershadow/)

### Inferences
- If "shadow" must literally mean the RDP shadow protocol (agentless, using built-in Windows), none of the open-source web gateways provides it. You would have to build it, for example a small Windows-side service or jump host that runs `mstsc /shadow:<id> /v:<host> /control [/noConsentPrompt]` and is published through Guacamole RemoteApp or a Windows jump server. This is the custom part of a "remote_Shadow" project.
- If agent screen capture is acceptable, MeshCentral (free, Apache-2.0) is the closest ready-made fit:
  - It sees the console session, can choose an RDP session, and has prompt/notify consent flags per device group.
  - "Without consent" means simply not setting the prompt flags.
- RustDesk can also do this, but centralized policy and LDAP need Pro.
- Consent behavior differs:
  - Native shadow consent is enforced on the endpoint by GPO ("Set rules for remote control of RDS user sessions").
  - Agent tools enforce consent in the server or agent config. That is weaker if a server admin can flip it, so audit logs matter.

### Gaps
- No primary source fetched on RustDesk's consent model ("accept via click / password / both") or on whether the 1.2.6 session selection works for workstation (non-RDS) fast-user-switching sessions.
- Tactical RMM: whether it exposes MeshCentral's consent flags in its own UI was not verified.
- Aspia and DWService session/consent behavior not verified.

---

## Q2. Which support browser-based RDP and SSH to servers?

### Takeaway
Browser RDP + SSH (+ VNC) is available in:
- Apache Guacamole
- Next Terminal
- JumpServer
- Warpgate (per its site; RDP/VNC are relatively recent)
- Teleport (SSH plus Windows RDP through its Desktop Service)
- MeshCentral (RDP/SSH/VNC through agent or relay)

WAC provides browser RDP and PowerShell to Windows servers only. ShellHub is SSH only. RustDesk and Remotely are their own protocol, not RDP/SSH brokers.

### Cited Findings
- Guacamole 1.6.0 uses FreeRDP 3 for RDP; it also supports VNC/SSH/Telnet/Kubernetes connections. Connection import is from CSV/JSON/YAML. — [1.6.0 notes](https://guacamole.apache.org/releases/1.6.0/)
- Next Terminal: RDP, SSH, VNC, Telnet, HTTP, with recording and replay. — [GitHub](https://github.com/next-terminal/next-terminal)
- JumpServer: SSH, RDP, Kubernetes, databases, websites, RemoteApp. — [GitHub](https://github.com/jumpserver/jumpserver)
- Warpgate: SSH, HTTPS, RDP, VNC, Kubernetes, PostgreSQL, MySQL; browser terminal. — [warpgate.null.page](https://warpgate.null.page/)
- Teleport: Windows desktops through RDP with certificate auth and recording. — [Teleport docs](https://goteleport.com/docs/enroll-resources/desktop-access/getting-started/)
- MeshCentral: control methods are Mesh Agent, RDP and AMT. With `"ssh": true`, SSH, SFTP, RDP and VNC work through a relay agent. — [MeshCentral docs](https://docs.meshcentral.com/meshcentral/)
- WAC: Remote Desktop and PowerShell tools in the browser. — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/use/manage-servers)
- ShellHub: SSH gateway only. — [opensourcesoftware.io](https://opensourcesoftware.io/opensource-ssh-access-management-software/)

### Inferences
- For an admin working from macOS and Windows, any of the HTML5 gateways (Guacamole, Next Terminal, JumpServer, Warpgate) avoids native client differences.
- Guacamole and its derivatives are the most proven for RDP rendering quality in the browser.
- Warpgate's RDP/VNC support should be checked against its changelog before relying on it.

### Gaps
- Warpgate: the exact version that introduced RDP/VNC was not verified (could be recent and less mature).
- Whether Teleport Community supports AD-joined desktops without the 5-host limit is only claimed on an unofficial forum (see Q3).

---

## Q3. AD integration: LDAP/Kerberos login and auto-importing computers from AD

### Takeaway
- **LDAP login** is common: Guacamole, MeshCentral, Tactical RMM (via SSO, which is paid), RustDesk Pro (Basic+), JumpServer, Teleport (Enterprise SSO).
- **Windows integrated SSO** (SSPI/Kerberos) to the web console is documented for MeshCentral when its server runs on Windows.
- **Auto-discovery of computers from AD**: Teleport (AD discovery for desktops) and WAC (domain-joined, AD tool). The agent tools populate inventory by agent install (e.g. GPO deploy), not by an AD query.

### Cited Findings
- MeshCentral:
  - With `"auth": "sspi"` on a Windows server, the site authenticates the browser with domain credentials and shows no login screen. — [MeshCentral docs](https://docs.meshcentral.com/meshcentral/)
  - LDAP group membership syncs to MeshCentral user groups at each login, and login can be restricted to specific LDAP groups (2022 blog). — [MeshCentral blog](https://meshcentral2.blogspot.com/2022/08/meshcentral-ldap-scripts-drag-drop.html)
  - 2FA: TOTP, YubiKey OTP, Duo. — [MeshCentral docs](https://docs.meshcentral.com/meshcentral/)
- Guacamole 1.6.0: LDAP extension with TLS-level and proxy settings plus a domain parameter token; TOTP; SAML and OIDC. — [1.6.0 notes](https://guacamole.apache.org/releases/1.6.0/)
- RustDesk Server Pro: LDAP integration and OIDC SSO are in Basic, Customized and Customized V2, not Individual. — [RustDesk pricing](https://rustdesk.com/pricing/)
- Tactical RMM: SSO requires sponsorship Tier 1. — [TRMM sponsor](https://docs.tacticalrmm.com/sponsor/)
- Teleport:
  - "Teleport can't automatically discover Windows desktops without Active Directory." Hosts can be registered with `ad: true` or `ad: false`.
  - Community Edition limits non-AD local-user access to 5 desktops; above 5 it blocks connections to all of them.
  - Source: [Teleport docs](https://goteleport.com/docs/enroll-resources/desktop-access/getting-started/)
  - Teleport 16 removed the AD install/config wizard. — [Teleport 16 blog](https://goteleport.com/blog/teleport-16/)
  - A forum user claims the 5-desktop cap does not apply to AD-joined hosts; this is unofficial. — [GitHub discussion #62086](https://github.com/gravitational/teleport/discussions/62086)
- Warpgate: OIDC SSO plus TOTP; LDAP is only used for SSH-key sync, not as a login method. — [warpgate.null.page](https://warpgate.null.page/)
- WAC: Active Directory tool (preview) can search users, computers and groups, reset passwords, and manage membership; Settings includes RBAC. — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/use/manage-servers)

### Inferences
- For AD-centric single sign-on with no extra IdP, MeshCentral (SSPI or LDAP) and Guacamole (LDAP, or SAML/OIDC through ADFS or Keycloak) are the strongest free options.
- For Warpgate or Teleport Community, plan to put an OIDC IdP (e.g. Keycloak or ADFS federated to AD) in front.
- No tool in the list was confirmed to auto-import *workstation* objects from AD into a connection list out of the box, except Teleport's AD desktop discovery.
- A custom sync (LDAP query of OU=Computers, then the Guacamole REST API or MeshCentral group) is a likely glue component.

### Gaps
- JumpServer LDAP/AD and MFA details, and the Community vs Enterprise split, were not fetched from a primary source.
- Guacamole Kerberos/NLA SSO pass-through for RDP was not verified for 1.6.0.
- RustDesk Pro LDAP group-to-device-group mapping depth was not verified.

---

## Q4. License terms and costs for about 50–500 endpoints

### Takeaway
- **Fully free:** MeshCentral, Guacamole, Warpgate, ShellHub, Aspia, RustDesk OSS server, and Remotely (stale).
- **Free with gated features:** Tactical RMM (SSO and signed agents need about $600/yr), Next Terminal (free up to 100 assets; backend closed since v2), JumpServer (GPL Community plus a paid EE).
- **RustDesk Pro:** roughly $287/yr (Basic: 10 users / 100 devices) and up.
- **Teleport Community:** free only for companies with <100 employees and <$10M revenue.
- **WAC:** a free Microsoft download.

### Cited Findings
- RustDesk Server Pro, billed annually in USD; self-hosted, not SaaS:
  - Individual: $11.88/mo, 1 user, 20 devices.
  - Basic: $23.88/mo, 10 users, 100 devices, unlimited concurrent connections, includes LDAP and OIDC.
  - Customized: base plus $1.20 per extra user per month and $0.12 per extra device per month.
  - Customized V2: concurrency-limited, +$24 per extra connection.
  - The web client is enabled only when users × 10 + devices ≥ 400.
  - Free OSS server is $0.
  - Source: [RustDesk pricing](https://rustdesk.com/pricing/)
  - Third-party sites quote different (older) prices, e.g. $9.90/$19.90. — [mspcompared](https://mspcompared.com/rustdesk/pricing/), versus the official page above.
- Tactical RMM sponsorship (agent counts are suggested, not enforced) — [TRMM sponsor](https://docs.tacticalrmm.com/sponsor/):

  | Tier | Price | Suggested agents | Notes |
  |---|---|---|---|
  | 1 | $55/mo or $600/yr | <200 | Required for code-signed agents and SSO |
  | 2 | $80/mo or $900/yr | <400 | Required for Reporting |
  | 3 | $105/mo or $1,200/yr | <600 | Email support, 72 h |
  | 4–7 | $155–$850/mo | Up to 10,000 | |

  - TRMM is "source available". Offering it as a hosted service needs written approval from AmidaWare. — [TRMM sponsor](https://docs.tacticalrmm.com/sponsor/); [openmsp.ai](https://www.openmsp.ai/blog/tactical-rmm-review)
- Teleport:
  - From v16, Community Edition binaries are free only for companies with <100 employees and <$10M annual revenue; others must buy Enterprise.
  - Pre-16 versions remain Apache-2.0.
  - Source: [Teleport license blog](https://goteleport.com/blog/teleport-community-license/); [GitHub discussion #39158](https://github.com/gravitational/teleport/discussions/39158)
- Next Terminal: the free edition offers unlimited users and 100 assets; the backend has been closed source since v2.0.0. — [next-terminal.com](https://www.next-terminal.com/); [GitHub](https://github.com/next-terminal/next-terminal)
  - Conflict: the GitHub API still reports an Apache-2.0 license file on the repo. — [GitHub API](https://api.github.com/repos/next-terminal/next-terminal)
- JumpServer: GPL-3.0 core plus a paid Enterprise edition. — [startwithidentity.com](https://startwithidentity.com/articles/top-7-open-source-pam-solutions/); [GitHub](https://github.com/jumpserver/jumpserver)
- Licenses per the GitHub API:
  - MeshCentral: Apache-2.0
  - Guacamole: Apache-2.0
  - Warpgate: Apache-2.0
  - ShellHub: Apache-2.0
  - RustDesk: AGPL-3.0
  - Remotely: GPL-3.0
  - Aspia: GPL-3.0
  - JumpServer: GPL-3.0
  - Source: [GitHub API](https://api.github.com/repos/Ylianst/MeshCentral) (and the corresponding repo endpoints)

### Inferences
Cost scenarios for 50–500 endpoints, software only (excluding server and VM costs):
- MeshCentral + Guacamole: $0.
- Tactical RMM:
  - $0 without SSO or signed agents. Unsigned agents are often flagged by AV.
  - $600/yr for under 200 agents.
  - $900–1,200/yr for 400–600 agents.
- RustDesk Pro:
  - Basic covers up to 100 devices for about $287/yr.
  - 500 devices plus a few admins means Customized. Roughly $23.88 base plus 400 × $0.12 = $48/mo extra, assuming base allowances equal Basic, which the page does not state. That is about $860/yr.
  - The 400-point web-client threshold would be met at 500 devices.
- Teleport: if the organization has ≥100 employees, Community Edition is not licensed for it, so Enterprise pricing (not public) applies.

### Gaps
- Teleport Enterprise pricing is not public; not found.
- JumpServer EE pricing was not found on a primary source.
- RustDesk "Customized" base allowance was not stated on the pricing page.

---

## Q5. Which are actively maintained in 2025–2026?

### Takeaway
All candidates except Remotely show 2026 releases or commits. Remotely has had no push since December 2024 and should be treated as dormant. The most active by release cadence are JumpServer, Next Terminal, Warpgate, RustDesk, MeshCentral and Aspia, all with releases in Sept–Oct 2026.

### Cited Findings
Last push and latest release, from the GitHub API on 2026-10-18 ([api.github.com/repos/…](https://api.github.com/repos/Ylianst/MeshCentral)):

| Project | Last push | Latest release | Stars |
|---|---|---|---|
| Apache Guacamole (server / client) | 2026-10-17 / 2026-10-14 | 1.6.0 (tag commit 2025-06-19) | ~4.0k (server) |
| MeshCentral | 2026-09-24 | 1.2.6 (2026-09-24) | ~7.4k |
| Tactical RMM | 2026-09-30 | v1.5.2 (2026-08-11) | ~4.5k |
| RustDesk client | 2026-10-16 | 1.5.0 (2026-09-30) | ~126k |
| RustDesk server | 2026-10-10 | 1.1.16 (2026-07-20) | ~10.6k |
| Teleport | 2026-09-17 | v18.10.0 (2026-07-09); docs mention 18.11.1 | ~21k |
| JumpServer | 2026-10-16 | v5.1.0 (2026-10-15) | ~31.8k |
| Next Terminal | 2026-10-18 | v3.10.3 (2026-10-18) | ~5.7k |
| Warpgate | 2026-10-17 | v0.29.2 (2026-10-07) | ~8.1k |
| ShellHub | 2026-10-17 | v0.26.0 (2026-07-06) | ~2.1k |
| Aspia | 2026-10-16 | v3.0.37 (2026-10-15) | ~2.0k |
| **Remotely** | **2024-12-17** | **v2024.08.07.0819 (2024-08-12)** | ~5.1k |

- WAC docs page was updated 2025-08-05. — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/use/manage-servers)

### Inferences
- Guacamole has had no new feature release since 1.6.0 (June 2025), but commits are ongoing, so a 1.6.x or 1.7 release is likely pending. Mature but slow-cadence.
- MeshCentral is maintained mostly by a small core team (historically Ylian Saint-Hilaire plus community maintainers). The bus factor is worth noting, but releases continue in 2026.
- Remotely should not be chosen as a base for new work.

### Gaps
- Not verified whether Remotely's maintainers formally announced end-of-life; only commit and release dates were checked.
- The WAC release cadence and current version (v2410 and successors) were not fetched.
