# Technical building blocks: RDP shadow, RDP and SSH from a custom web admin control center (Windows AD)

Research date: 2026-10-18. Notes: "Confirmed" = stated by a primary/authoritative source cited inline. "Unconfirmed" = single secondary source, community comment, or inference. The PT Swarm article (swarm.ptsecurity.com/remote-desktop-services-shadowing/) could not be fetched directly (TLS error); facts attributed to it below come only from search-result snippets and are marked as such.

## 1. How Windows RDP shadowing works under the hood

### Takeaway
Modern shadowing (Windows 8.1 / Server 2012 R2 and later) is NOT a normal RDP connection on 3389: mstsc calls an RPC function (`RpcShadow2`, interface UUID `1257b580-ce2f-4109-82d6-a9459d0bf6bc`) over the SMB named pipe `\pipe\SessEnvPublicRpc` (TCP 445) hosted by the SessEnv service; the target then launches `RdpSa.exe` in the user's session, which serves the shadow stream on a dynamically chosen high TCP port that mstsc connects to. The RPC interface is not publicly documented by Microsoft (it was identified by reverse engineering); the only client that implements it is mstsc.exe.

### Cited Findings
**Command line (Microsoft-documented)**
- Syntax: `mstsc.exe ... [/prompt] [/shadow:<sessionID> [/control] [/noConsentPrompt]]`. `/shadow:<sessionID>` = ID of session to shadow; `/control` = allows control of the session when shadowing; `/noConsentPrompt` = allows shadowing without user consent; `/prompt` = prompts for credentials. — [Microsoft Learn: mstsc](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc)
- Without `/control` the shadow is view-only. — [woshub: Shadow to Windows 10 user](https://woshub.com/rdp-session-shadow-to-windows-10-user)

**Versions / requirements**
- Old implementation (`shadow.exe`) existed in Windows 7 / Server 2008 R2 and earlier; Windows 8 and Server 2012 (non-R2) support NEITHER implementation; new `mstsc /shadow` implementation from Windows 8.1 / Server 2012 R2 onward. — [BITSADMIN blog](https://blog.bitsadmin.com/spying-on-users-using-rdp-shadowing)
- Supported targets listed: Windows 11, 10, 8.1; Windows Server 2022, 2019, 2016, 2012 R2. — [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user)
- `TermService` (Remote Desktop Services) must be running; per BITSADMIN, Remote Desktop itself does not need to be enabled in SystemPropertiesRemote.exe. If TermService is disabled, connection fails with "The version of Windows running on this server does not support user shadowing". — [BITSADMIN](https://blog.bitsadmin.com/spying-on-users-using-rdp-shadowing); [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user). Note: woshub says "Enable RDP on the target computer" — mild conflict with BITSADMIN; treat as "TermService required, RDP-enable likely not strictly required" (unconfirmed).
- The `SessionEnv` service (Remote Desktop Configuration) hosts the RPC interface used for shadowing. — [BITSADMIN](https://blog.bitsadmin.com/spying-on-users-using-rdp-shadowing)
- Admin rights: by default the shadowing account must be a local administrator on the target (or be granted Remote Control permission on the RDP-Tcp listener). — [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user); [Lizard Systems TSM handbook](https://lizardsystems.com/terminal-services-manager/help/handbook/managing-user-sessions/shadowing-a-user/)
- WinStation permissions: shadow requires `WINSTATION_SHADOW` (0x00010); querying sessions requires `WINSTATION_QUERY` (0x00001). Default Administrators ACE = 0xF03BF (all except `WINSTATION_RESET`). Query held by default by Administrators, Remote Desktop Users, INTERACTIVE. ACL stored in `Security` (REG_BINARY) under `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp` or `DefaultSecurity` under `...\WinStations`. — [BITSADMIN](https://blog.bitsadmin.com/spying-on-users-using-rdp-shadowing)

**Process / protocol mechanics**
- Client opens SMB named pipe `SessEnvPublicRpc` on TCP 445, DCE/RPC bind to interface UUID `1257b580-ce2f-4109-82d6-a9459d0bf6bc` (in SessEnv.dll), single function opnum 0 `RpcShadow2`. This allows shadowing even when 3389 is blocked but SMB is open. — [BITSADMIN](https://blog.bitsadmin.com/spying-on-users-using-rdp-shadowing); same UUID/pipe/function independently stated by a contributor in [FreeRDP issue #5656](https://github.com/FreeRDP/FreeRDP/issues/5656)
- On session setup TermService spawns `RdpSaUacHelper.exe`, `RdpSaProxy.exe`, `RdpSa.exe`; RdpSaProxy spawns RdpSa.exe, which runs inside the shadowed user's session. — [BITSADMIN](https://blog.bitsadmin.com/spying-on-users-using-rdp-shadowing); detection rules watch the same binaries — [Elastic: Potential Remote Desktop Shadowing Activity](https://www.elastic.co/guide/en/security/current/potential-remote-desktop-shadowing-activity.html)
- Shadow connections do not use 3389 and negotiate a high TCP port. — [FreeRDP issue #5656 (reporter)](https://github.com/FreeRDP/FreeRDP/issues/5656)
- Ports: 139/TCP, 445/TCP and dynamic RPC range 49152–65535 instead of 3389; enable predefined firewall rules "File and Printer Sharing (SMB-In)" and "Remote Desktop - Shadow (TCP-In)" (the latter allows the RdpSa.exe program). — [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user)
- (Unconfirmed, search snippet attributed to PT Swarm/related write-up) mstsc first contacts the host, the server starts RdpSa.exe and tells mstsc which ports/IPs it is listening on, mstsc then connects to one of them; also "if the shadow appears to connect but no window opens, the firewall (dynamic ports / Shadow rule) is the cause". — [PT Swarm (snippet only)](https://swarm.ptsecurity.com/remote-desktop-services-shadowing/)
- (Unconfirmed) A write-up mentions RPC (SessEnvPublicRpc) returning a "Windows Desktop Sharing API invitation" for the target session. — search snippet, source [c3r3br4t3 ShadowRDP](https://red.c3r3br4t3.com/red-team-operations/lateral-movement/shadowrdp) / [PT Swarm](https://ptswarm.com/blog/remote-desktop-services-shadowing/)
- `AllowRemoteRPC` (HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server, 1=allow) is cited in error-1722 shadow troubleshooting; one source says shadowing works with remote RPC off — conflicting/unsettled. — [Microsoft Q&A: RDS farm error 1722 when shadowing](https://learn.microsoft.com/en-us/answers/questions/118448/rds-farm-error-1722-when-shadowing-session); [TechNet: Allowing Remote RPC](https://technet.microsoft.com/library/ff710472(v=ws.10))

**Shadow policy (registry / GPO)**
- GPO: Computer Configuration > Administrative Templates > Windows Components > Remote Desktop Services > Remote Session Host > Connections > "Set rules for remote control of Remote Desktop Services user sessions". Registry: `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services`, DWORD `Shadow`. — [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user)
- Values: 0 = disabled (no remote control); 1 = full control with user permission; 2 = full control without permission; 3 = view with permission; 4 = view without permission. Not set = full control with consent (= 1). — [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user); [BITSADMIN](https://blog.bitsadmin.com/spying-on-users-using-rdp-shadowing)
- If policy requires consent, `/noConsentPrompt` fails with error that Group Policy requires user consent. — [woshub (search summary)](https://woshub.com/rds-shadow-how-to-connect-to-a-user-session-in-windows-server-2012-r2)

**Console session / behavior**
- Shadowing attaches to the console session (typically session ID 1 on a workstation) without disconnecting the local user — this is the key difference from a normal RDP logon on client Windows. — [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user)
- Locked session or UAC prompt (without /control) shows black screen with pause symbol; UAC on Secure Desktop pauses the shadow until the user responds. — [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user)
- Some sources say a disconnected/idle session cannot be shadowed (target session must be active). — [Experts Exchange / search summary](https://www.experts-exchange.com/articles/33768/Using-RDP-Shadowing-For-Convenient-User-Support-and-Remote-Control.html) (unconfirmed)
- Hotkeys: Alt+* (Ctrl+* on RDS) ends shadow; Ctrl+Alt+Break fits window. — [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user)
- mstsc cannot accept username/password programmatically for shadow (only /prompt or current credentials). — [Royal Apps forum (user comment)](https://support.royalapplications.com/support/discussions/topics/17000022823) (unconfirmed)

**Auditing**
- Log `Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational`: 20508 (shadow view permission granted), 20503 (shadow view session started), 20504 (stopped). — [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user)

**Is it documented?**
- MS-RDPEMC (Multiparty Virtual Channel Extension) is an official open spec running over static virtual channel "encomsp" in MS-RDPBCGR. — [MS-RDPEMC 2.1 Transport](https://msdn.microsoft.com/en-us/library/cc242029.aspx). No source found linking it explicitly to mstsc /shadow.
- FreeRDP maintainer pointed to MS-TSTS as the starting point for implementing client-side shadow ("references many other specifications"); RpcShadow2/SessEnvPublicRpc was found by contributors, not in MS docs. — [FreeRDP #5656](https://github.com/FreeRDP/FreeRDP/issues/5656)

### Inferences
- Flow (assembled from sources, partly inferred): mstsc → SMB 445 → `\pipe\SessEnvPublicRpc` → `RpcShadow2(sessionId, control/consent flags)` → SessEnv/TermService checks Shadow policy + WINSTATION_SHADOW → consent prompt in user session (if required) → RdpSa.exe in user session starts listening on a dynamic port and returns a connection string/ticket → mstsc connects to RdpSa's port with an RDP-family (Desktop Sharing–style) protocol. The second stage is plausibly the same stack as Windows Desktop Sharing / Remote Assistance (MS-RDPEMC-related), but this is not confirmed.
- Firewall for the admin-to-endpoint path: TCP 445 + "Remote Desktop - Shadow (TCP-In)" (program rule for RdpSa.exe, any high port). If the control center relays shadow traffic, it cannot be pinned to a single port without host firewall program rules.
- For a domain fleet the practical baseline GPO: Shadow=2 (or 1 if consent required by policy/law), firewall rules enabled, TermService running, admin group with local admin or explicit Remote Control permission.

### Gaps
- No Microsoft open spec for `RpcShadow2` or the RdpSa wire protocol was found; whether MS-RDPEMC/MS-RAI structures are reused is unconfirmed.
- Exact RPC parameters and the format of what RpcShadow2 returns: not found in accessible sources (PT Swarm article likely covers it; could not fetch).
- Windows Server 2025 / Windows 11 24H2+ behavior changes for shadowing: no source found.

## 2. Can FreeRDP / Guacamole / IronRDP / other open-source stacks initiate a shadow connection? Browser or macOS?

### Takeaway
No. As of late 2025/2026 no open-source RDP client (FreeRDP, Remmina, rdesktop, Guacamole, IronRDP, Myrtille) implements the mstsc `/shadow` client side; the FreeRDP feature request has been open since 2019 with no PR. `freerdp-shadow-server/cli` is a different thing (it is a server that mirrors the local X11/desktop). The only viable non-mstsc path to "join an existing session" with open-source clients is Windows Remote Assistance (FreeRDP supports `/assistance` with .msrcIncident invitations), which itself has 2026 breakage reports.

### Cited Findings
- FreeRDP issue #5656 "Unable to shadow a Windows 10 console user session", opened 14 Oct 2019, still Open, labels feature/help-wanted, no linked PRs. Maintainer akallabeth: RPC mechanisms are "a bit of overkill" (would pull dependencies like Samba), would merge an implementation, pointed to MS-TSTS. Contributor hardening: needs Windows RPC calls FreeRDP lacks (dup of #3889). Comments: "no RDP client with /shadow support" (2021); mstsc appears to be the only client and doesn't work well under Wine (Feb 2025); request for Guacamole integration (Oct 2025). Workaround mentioned: Windows VM running mstsc /shadow, exposed via VNC. — [FreeRDP #5656](https://github.com/FreeRDP/FreeRDP/issues/5656)
- FreeRDP Discussion #6747 (Jan 2021): maintainer answer — `freerdp-shadow` shadows the session of the user it's started from (X11 mirroring; auth = PAM or none); Windows Remote Assistance works with FreeRDP as client. Question whether FreeRDP can use mstsc's shadow flags unanswered. Remote Assistance reported hard to get working (2024). — [FreeRDP Discussion #6747](https://github.com/FreeRDP/FreeRDP/discussions/6747)
- `freerdp-shadow-cli` implements an RDP shadow server under Linux/X11 (issues with GFX + mstsc). — [FreeRDP #9660](https://github.com/FreeRDP/FreeRDP/issues/9660)
- FreeRDP can open Remote Assistance invitations: `xfreerdp Invitation.msrcIncident /assistance:<password>`. — [FreeRDP #5287 (MSRA /OFFERRA)](https://github.com/FreeRDP/FreeRDP/issues/5287); [FreeRDP #5678](https://github.com/FreeRDP/FreeRDP/issues/5678); FreeRDP source has `libfreerdp/common/assistance.c` parsing connection strings — [assistance.c mirror](https://git.stg.centos.org/source-git/freerdp/blob/c8/f/libfreerdp/common/assistance.c)
- Remote Assistance protocol is documented: MS-RAI defines Connection String 1 (IPCHService) and 2 (IRASrv DCOM interface, section 3.4); MS-RA covers the session after connect. — [MS-RAI 2.2.1](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rai/959fd78d-e085-4776-bad1-89e531c42c91); [MS-RAI 3.4 IRASrv](https://msdn.microsoft.com/en-us/library/cc240155.aspx); [MS-RA](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-ra/a9380dcd-a46d-484b-af4a-fa2c5974a8f3)
- Unsolicited (offer) Remote Assistance requires policy preconfiguration on the target. — [RemoteAssistance Policy CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-remoteassistance); [TechNet Magazine](https://learn.microsoft.com/en-us/previous-versions/technet-magazine/ff356868(v=msdn.10))
- After January 2026 Microsoft security updates, Remote Assistance invite files reportedly can no longer be opened (workaround of old msra.exe is unsupported). — [deyda.net, Feb 2026](https://www.deyda.net/index.php/en/2026/02/05/workaround-after-remote-assistance-error/) (single blog source, unconfirmed)
- Guacamole: 2019 mailing list — FreeRDP (Guacamole's RDP lib) doesn't support shadow; it uses RPC & SMB. — [Guacamole user list](https://lists.apache.org/thread/wq4x9g8zvdwn1c61yfpy09443zd6q2c8). Guacamole 1.6.0 changelog (FreeRDP 3 support, GUACAMOLE-1026) has no shadow item. — [Guacamole 1.6.0 release](https://guacamole.apache.org/releases/1.6.0/)
- IronRDP README: no mention of shadowing/session attach. — [IronRDP README](https://github.com/Devolutions/IronRDP/blob/master/README.md)
- Remmina: open enhancement request #2774 for `/shadow:ID`. — [Remmina #2774](https://gitlab.com/Remmina/Remmina/-/issues/2774). rdesktop: feature request #141. — [rdesktop #141](https://github.com/rdesktop/rdesktop/issues/141)
- Royal TS (FreeRDP-based) does not support shadowing; vendor suggested command-task workaround. — [Royal Apps forum](https://support.royalapplications.com/support/discussions/topics/17000022823)
- No source confirms Windows App (macOS) supports shadow; vendor docs say shadowing requires the Remote Desktop Connection client (mstsc) on Windows; the Store app is unsupported. — [itopia help](https://helpcenter.itopia.com/en/articles/4809369-user-session-shadowing-support)

### Inferences
- From a browser or macOS, shadow is only achievable by running mstsc.exe somewhere on Windows and streaming that Windows session to the admin (jump host / RemoteApp / VM+VNC/RDP). Implementing RpcShadow2 + the RdpSa protocol yourself (e.g., with impacket for the SMB/RPC part) is theoretically possible but undocumented — high risk / R&D effort.
- Commercial products that offer browser shadowing (ControlUp, Citrix Director, etc.) appear to wrap mstsc or their own agents; not investigated.

### Gaps
- No found GitHub project that implements RpcShadow2 client side end-to-end (search didn't find one).
- Devolutions RDM/Gateway shadow support: not confirmed either way.

## 3. Alternative approaches for macOS admins (jump host, RemoteApp mstsc, custom URL protocol handler)

### Takeaway
Most practical: a Windows "admin jump host" (RDS server or admin VM) where mstsc /shadow runs; the macOS admin reaches it via Windows App (native) or via browser RDP (Guacamole/IronRDP). Publishing mstsc.exe as a RemoteApp with arguments, or a custom `shadow://` handler on Windows admin PCs, are variants. URI handlers must treat every input as hostile.

### Cited Findings
- Workaround in FreeRDP discussion: a Windows VM runs `mstsc.exe /v:host /shadow:1 /control /noConsentPrompt /prompt`, accessed remotely (VNC). — [FreeRDP #5656](https://github.com/FreeRDP/FreeRDP/issues/5656)
- Guacamole 1.6.0 fixed RemoteApp issues (unpublished RemoteApp black screen GUACAMOLE-1084; minimized RemoteApp windows inaccessible GUACAMOLE-1231) — relevant if mstsc is published as RemoteApp and opened via Guacamole. — [Guacamole 1.6.0](https://guacamole.apache.org/releases/1.6.0/)
- A third-party blog reports compatibility issues of Guacamole 1.6.0 with newer FreeRDP "especially on remote app part" (weak evidence). — [51sec blog](https://blog.51sec.org/2025/12/upgrade-freerdp-to-2117dfsg1-6deb12u1.html?m=0)
- URI scheme registration: key under HKEY_CLASSES_ROOT\<scheme> with empty "URL Protocol" string value plus shell\open\command; Microsoft warns handler apps receive data from untrusted sources and URI/parameters may contain malicious data that attempts to exploit the handler. — [Microsoft: Registering an Application to a URI Scheme](https://learn.microsoft.com/id-id/previous-versions/windows/internet-explorer/ie-developer/platform-apis/aa767914(v=vs.85))

### Inferences
- Jump-host design: control center calls (via API/WinRM/agent) on the jump host or simply generates a RemoteApp/.rdp that launches `mstsc.exe /v:<target> /shadow:<id> /control` — RDS RemoteApp supports command-line arguments (allow "any command-line parameters" or fixed) — the admin sees the shadow window as a RemoteApp. Jump host needs 445 + RdpSa dynamic ports to all endpoints; endpoints don't need 3389 from macOS.
- Credentials: the shadow runs with the jump-host session user's Kerberos ticket; per-admin accountability is preserved if each admin logs into the jump host as themselves (avoid shared service accounts).
- URL handler safety (from general practice, unconfirmed by source): strict regex whitelist of host (FQDN in domain / IP) and numeric session ID; never pass raw URI to a shell (`cmd /c`); launch mstsc via CreateProcess with argument array; consider signed tokens (HMAC with expiry) issued by the control center so a malicious web page cannot trigger shadow attempts; deploy via GPO only to admin PCs; browsers show "Open app?" prompt.

### Gaps
- No primary source found on RemoteApp argument-passing for mstsc specifically or on Windows App (macOS) RemoteApp behavior with mstsc.

## 4. Enumerating sessions on remote machines and locating users

### Takeaway
Use WTS API (`WTSOpenServer`+`WTSEnumerateSessions(Ex)`+`WTSQuerySessionInformation`) — the same MS-TSTS RPC that qwinsta/quser use — over SMB named pipes (`\PIPE\LSM_API_service` / legacy `\PIPE\Ctx_WinStation_API_service`, TCP 445). Needs Query Information permission (default for Administrators) and often `AllowRemoteRPC=1`. Scale by parallelizing with timeouts; for "where is user X" combine endpoint 4624 (types 2/7/10/11) via WEF, DC 4768 events, or a logon script/agent writing user↔host to a central store.

### Cited Findings
- WTSEnumerateSessions needs Query Information permission on the session; WTSOpenServer/Ex provides the remote server handle; min server 2008 (Ex: 2008 R2). — [WTSEnumerateSessionsA](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsenumeratesessionsa); [WTSEnumerateSessionsExW](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsenumeratesessionsexw); [WTSQuerySessionInformationA](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsquerysessioninformationa)
- MS-TSTS standards assignments: legacy interface over `\PIPE\Ctx_WinStation_API_service`; LSM Session/Notification/Enumeration interfaces over `\PIPE\LSM_API_service` (or `\PIPE\UNIFIED_API_service`). — [MS-TSTS Standards Assignments](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-tsts/4f5ff568-2887-4c50-8b36-38bb85170b58)
- The LSM named pipe endpoint is remotely accessible. — [Akamai research](https://www.akamai.com/blog/security-research/msrpc-lsm-cve-disturbing-hosts-rest)
- Remote session query: `qwinsta /server:<PC>`; console user typically session ID 1. — [woshub](https://woshub.com/rdp-session-shadow-to-windows-10-user); `query session /server:...` — [Make](https://makezine.com/article/technology/howto-kill-terminal-services-s/)
- `AllowRemoteRPC` under HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server (1 = allowed) for remote RDS RPC. — [TechNet: Allowing Remote RPC](https://technet.microsoft.com/library/ff710472(v=ws.10)); [MS Q&A](https://learn.microsoft.com/en-us/answers/questions/118448/rds-farm-error-1722-when-shadowing-session)
- Error 1722 (RPC server unavailable) typical when host offline/sleeping, DNS issues or network blocking. — [IBM support](https://www.ibm.com/support/pages/node/240899)
- Event 4624 is generated on the computer where the session was created (authoritative for interactive logons). — [Microsoft: event 4624](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/auditing/event-4624). Interactive logon types 2, 7, 10, 11; DC logs mostly Kerberos events and type-3 network logons. — [NetWitness community](https://community.netwitness.com/t5/netwitness-discussions/login-out-of-working-hours/m-p/475586)
- DC Kerberos TGT request events (4768) used to map users to client computers. — [ManageEngine KB](https://www.manageengine.com/products/active-directory-audit/kb/how-to/how-to-check-domain-user-logon-history.html)

### Inferences
- Linux/containerized backend option: implement MS-TSTS calls via impacket (Python) — impacket has a `tsch`/`tsts` module in recent versions (unconfirmed — verify) — or run a small Windows worker service using wtsapi32 and expose results via API.
- Scale: WTS calls are synchronous per host; use a worker pool (e.g., 50–200 concurrent), aggressive TCP pre-check on 445 (~500 ms), and cache results; offline hosts dominate latency (RPC timeouts). CIM/WMI (`Win32_ComputerSystem.UserName`, `Win32_LogonSession`+`Win32_LoggedOnUser`) over WinRM 5985/5986 or DCOM 135+dynamic is an alternative but heavier (no source found for performance numbers).
- Best "where is user" signal at scale: agent/logon-script push (user, host, IP, session ID, timestamp) to the control center on logon/unlock/logoff; reconcile with on-demand WTS queries.

### Gaps
- No source found for Win32_LogonSession performance or for benchmarked WTS enumeration across hundreds of hosts.
- "qwinsta Error 5" root cause (permissions vs UAC remote token filtering) not sourced.

## 5. RDP in the browser: Guacamole, IronRDP, Devolutions Gateway, Myrtille

### Takeaway
Guacamole (guacd + guacamole-common-js, Apache-2.0, 1.6.0 released 2025-06-22 with FreeRDP 3 support) is the most mature, embeddable, protocol-multiplexing option (RDP/VNC/SSH, recording). IronRDP (Rust, MIT/Apache-2.0) offers a WASM client + `@devolutions/iron-remote-desktop` web component but needs a WebSocket→TCP gateway speaking RDCleanPath (Devolutions Gateway). Myrtille appears unmaintained (last push March 2024). None can shadow.

### Cited Findings
- Guacamole architecture: guacd loads protocol "client plugins" and connects to remote desktops on instructions from the web app; guacamole-common-js provides the JS client and tunnels; Java `GuacamoleHTTPTunnelServlet` (guacamole-common) implements an HTTP tunnel you extend with your own auth; a custom app may use any connection parameters. — [Guacamole architecture](https://guacamole.apache.org/doc/gug/guacamole-architecture.html); [guacamole-common-js](https://guacamole.apache.org/doc/gug/guacamole-common-js.html); [Writing your own Guacamole app](https://guacamole.apache.org/doc/gug/writing-you-own-guacamole-app.html); [mailing list](https://mail-archive.com/search?f=1&l=user%40guacamole.apache.org&o=newest&q=subject%3A%22Re\%3A+Embedding+RDP+Connection+in+Custom+Website%22)
- Guacamole 1.6.0 (2025-06-22): rewritten guacd protocol optimizer, rendering performance, FreeRDP 3.0.0 support (GUACAMOLE-1026), known RDP cert fingerprint parameter (GUACAMOLE-1332), improved GFX/RemoteFX streaming (GUACAMOLE-377), SSH certificate support (GUACAMOLE-1290), recordings to named pipes (GUACAMOLE-1931), recording player improvements. — [Guacamole 1.6.0](https://guacamole.apache.org/releases/1.6.0/); [releases list](https://guacamole.apache.org/releases/)
- Third-party wrapper: guacamole-easy-tunnel for integrating RDP/VNC/SSH into apps. — [GitHub](https://github.com/volodymyrsmirnov/guacamole-easy-tunnel)
- IronRDP: MIT or Apache-2.0; client and server; NLA via CredSSP with NTLM and Kerberos; RemoteFX, ClearCodec, EGFX primitives; CLIPRDR clipboard; MS-TSGU (RD Gateway) transport; KDC proxy and RDCleanPath for gateway-mediated connections; WASM bindings and protocol-agnostic web component `@devolutions/iron-remote-desktop`; Devolutions Gateway uses it for browser access. H.264 not mentioned. — [IronRDP README](https://github.com/Devolutions/IronRDP/blob/master/README.md); [ARCHITECTURE.md](https://github.com/Devolutions/IronRDP/blob/master/ARCHITECTURE.md)
- Devolutions web RDP requires Devolutions Gateway 2023.1+, browsers Chrome 102 / Firefox 102 / Safari 16.4+. — [Devolutions docs](https://docs.devolutions.net/gateway/knowledge-base/how-to-articles/launch-an-rdp-session-in-a-web-client)
- Myrtille: HTML4/HTML5 RDP and SSH gateway (.NET/C#/C++, FreeRDP-based, Apache-2.0); default branch last pushed 2024-03-07, 64 open issues; Docker image ~5 years old. — [ecosyste.ms](https://awesome.ecosyste.ms/projects/github.com%2Fcedrozor%2Fmyrtille); [GitTrend](https://gittrend.io/repo/myrtille-rdp/myrtille); [Docker Hub](https://hub.docker.com/r/cedrozor/myrtille)

### Inferences
- Embedding pattern: control center issues short-lived signed connection tokens; backend tunnel (Java guacamole-common, or Node guacamole-lite — not verified here) validates token and passes hostname/credentials to guacd; browser uses Guacamole.Client + WebSocketTunnel inside the app's page. Credentials never reach the browser.
- IronRDP path suits a Rust/TS stack and modern web component, but requires running Devolutions Gateway (or implementing RDCleanPath proxy) and has a smaller ecosystem than Guacamole.
- Jump-host shadow can be delivered in-browser by opening a Guacamole RDP/RemoteApp connection to the jump host that launches mstsc /shadow.

### Gaps
- Devolutions Gateway licensing/standalone (free) use and token (JWT) model not verified in this pass.
- guacamole-lite (Node) maintenance status not verified.

## 6. SSH in the browser

### Takeaway
Standard pattern: xterm.js in the browser + WebSocket to a backend that holds the SSH client (Node `ssh2`, Go `golang.org/x/crypto/ssh`, Python `asyncssh`/paramiko), or reuse Guacamole's SSH protocol (supports SSH certificates since 1.6.0). WebSSH2 is a ready reference implementation (Node, MIT).

### Cited Findings
- WebSSH2: web SSH client using ssh2, socket.io, xterm.js, express; webssh2-server MIT, Node host runs SSH2 client relaying WebSocket/Socket.io; needs Node 22+; default port 2222 at /ssh. — [webssh2 (billchurch)](https://redirect.github.com/billchurch/webssh2); [webssh2-server](https://classic.yarnpkg.com/en/package/webssh2-server); [webssh2_client README](https://cdn.jsdelivr.net/npm/webssh2_client@5.1.0/README.md)
- Guacamole SSH: SSH certificate support (GUACAMOLE-1290), clipboard normalization, right modifier keys in 1.6.0. — [Guacamole 1.6.0](https://guacamole.apache.org/releases/1.6.0/)

### Inferences
- Key handling (general practice, unconfirmed by fetched sources): keep private keys server-side (vault/HSM or encrypted DB), never send to browser; prefer short-lived SSH certificates signed by an internal CA (OpenSSH CA / Vault SSH engine) per admin+session; pin host keys (known_hosts per target) to avoid MITM; record sessions (asciinema/Guacamole recording) for audit. For Windows targets, OpenSSH Server on Windows supports AD accounts (GSSAPI/Kerberos possible).
- Go alternative: `golang.org/x/crypto/ssh` client + `gorilla/websocket`/`nhooyr.io/websocket` bridging PTY data and resize events; Python: `asyncssh` + FastAPI/websockets.

### Gaps
- No fetched sources for Go/Python libraries (gliderlabs/ssh is a server library, not a client — relevant only if writing an SSH proxy); listed from general knowledge, needs verification.
