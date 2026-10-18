# Commercial and desktop connection managers / AD admin tools as a ready-made "domain admin control panel" (state as of Oct 2026)

Method note: research was done on 2026-10-18 with web search and vendor pages. Some vendor pages (Royal pricing, SolarWinds Dameware, ManageEngine store) returned 404 or no prices, so some figures come from resellers or aggregators and are flagged. Forum threads are often several years old; their age is noted.

Background on the mechanism (relevant to every tool): Windows RDS shadowing is driven by `mstsc /v:<host> /shadow:<sessionID> [/control] [/noConsentPrompt] [/prompt]`. Session IDs come from `qwinsta`/`quser /server:`. The target must allow it via GPO "Set rules for remote control of RDS user sessions", and only active sessions can be shadowed — [TruGrid help](https://help.trugrid.com/en/article/how-to-shadow-rdp-sessions-for-remote-support-without-third-party-tools-1ej3etx/), [WOSHub](https://woshub.com/rds-shadow-how-to-connect-to-a-user-session-in-windows-server-2012-r2). Nearly every third-party "shadow" feature found here is a wrapper that launches `mstsc.exe`, so it needs a Windows client. Vendors say the Microsoft RDP ActiveX control that connection managers embed has no shadowing API — [Royal Apps community, vendor reply Nov 2020](https://community.royalapps.com/t/shadow-rds-sessions-directly-from-dashboard/369).

## Q1. Which tools explicitly support RDP shadow sessions, and with what options (view/control, consent)?

### Takeaway
Only a few tools ship a real, documented `mstsc /shadow` launcher with view, control and consent options: LizardSystems Terminal Services Manager (best documented), Royal TS for Windows (dashboard, launches mstsc), Devolutions RDM for Windows (Terminal Services console, launches mstsc) and Parallels RAS (console, "Remote Control" / "Remote Control (prompt)"). RDCMan and mRemoteNG have no shadowing. Devolutions' newer "session shadowing" (RDM 2025.2) is a different thing: live viewing of sessions brokered and recorded through Devolutions Gateway, not native RDS shadowing of a user's console session.

### Cited Findings

**LizardSystems Terminal Services Manager (Windows)**
- Shadow dialog offers **View** (watch only), **Control** (share keyboard and mouse) and **Prompt for user consent**. With consent cleared, shadowing starts without a prompt only if GPO "Set rules for remote control of Remote Desktop Services user sessions" and permissions allow it. Multi-monitor follows the "Connect with /multimon" preference — [LizardSystems handbook: Shadowing a user](https://lizardsystems.com/terminal-services-manager/help/handbook/managing-user-sessions/shadowing-a-user/)
- It launches `mstsc.exe` with the matching flags, and closing that window ends the shadow — [same page](https://lizardsystems.com/terminal-services-manager/help/handbook/managing-user-sessions/shadowing-a-user/)
- Requirements: the account must be an admin on the target or be listed under "Remote control" in RDS session host config. Target must be Windows Server 2012 R2 or later. The client needs an mstsc with shadow support (Windows 8.1+) — [same page](https://lizardsystems.com/terminal-services-manager/help/handbook/managing-user-sessions/shadowing-a-user/)
- Other session actions: disconnect, log off, send message, reset session (handbook navigation). Page shows product version v26.10 (Oct 2026), help page updated Jun 4, 2026 — [same page](https://lizardsystems.com/terminal-services-manager/help/handbook/managing-user-sessions/shadowing-a-user/)
- Vendor how-to: TSM finds the session across servers and launches standard mstsc in shadow mode, with nothing to install on either end — [LizardSystems how-to](https://lizardsystems.com/terminal-services-manager/how-to/how-to-shadow-a-remote-desktop-session/)

**Royal TS (Windows) / Royal TSX (macOS)**
- History of the feature: requested Feb 2017. In Nov 2020 the vendor (Stefan Koell) said the ActiveX control has no shadowing API. A dashboard command that launches mstsc as an external app was built for V6 (beta May 2021, V6 released June 2021). A "Control" option was added, off by default, and `/prompt` and `/noConsentPrompt` toggles were promised — [Royal Apps community thread](https://community.royalapps.com/t/shadow-rds-sessions-directly-from-dashboard/369)
- Known issues reported by users in 2021: view-only failed while Control worked, and mstsc runs as the locally logged-on Windows user rather than the connection's stored credentials — [same thread](https://community.royalapps.com/t/shadow-rds-sessions-directly-from-dashboard/369); [Royal support forum](https://support.royalapps.com/support/discussions/topics/17000007227)
- Users asked whether the V7 move to FreeRDP would enable embedded shadowing. No confirmation was found — [Royal support forum](https://support.royalapplications.com/support/discussions/topics/17000022823)
- Royal TS has a "Terminal Services" connection type to "Manage terminal service sessions" — [Royal TS Windows features](https://www.royalapps.com/ts/win/features)

**Devolutions Remote Desktop Manager (RDM)**
- Shadowing cannot be opened embedded in RDM. Staff gave the path: RDP entry, right-click, View, Console management, Terminal Services console, then the "shadow" button for an active session. Restrictions are "the same as with using mstsc.exe" (same domain, admin) — [Devolutions forum: Enumerate and Shadow Remote RDS Sessions](https://forum.devolutions.net/topics/12920/enumerate-and-shadow-remote-rds-sessions) (undated, likely old)
- Forum thread (2022–2023) on Terminal Services console shadowing problems: credential mode "Find by name (User vault)" broke it, and switching the Tools/Management Tools credential setting to "Custom" fixed it. Ports TCP 445/139 must be open (WMI/SMB). There was also a regression report in 2023.1.23 — [Devolutions forum: remote session shadow](https://forum.devolutions.net/topics/37959/remote-session-shadow)
- User feature requests for session enumeration, consent prompt and view-only shadow options had no confirmed implementation in the threads found — [Devolutions forum](https://forum.devolutions.net/topics/12920/enumerate-and-shadow-remote-rds-sessions); [Supervise or overview RDP session of other user](https://forum.devolutions.net/topics/39626/supervise-or-overview-rdp-session-of-other-user)
- **RDM 2025.2 (June 2025) "Session shadowing"**: with a Devolutions Server data source, users can view privileged sessions, including active sessions and Gateway recordings, "shadow active sessions to observe what's happening in real time" and terminate sessions. No control or take-over is mentioned — [Devolutions blog: What's new in RDM 2025.2](https://devolutions.net/blog/whats-new-in-remote-desktop-manager-20252/)
- Devolutions Gateway docs: you can watch an in-progress session recording live through the entry's recording tab ("pulsing icon"). This applies to web-launched RDP sessions through the Gateway — [Devolutions Gateway docs: launch an RDP session in a web client](https://docs.devolutions.net/gateway/knowledge-base/how-to-articles/launch-an-rdp-session-in-a-web-client)

**Parallels RAS**
- Since RAS v17 the console has two options, "Remote Control" and "Remote Control (prompt)" (Farm / Site / RD Session Hosts / Sessions, right-click). It cannot shadow sessions on Windows 7 / 2008 R2 — [Parallels KB 124680](https://kb.parallels.com/en/124680); [Parallels KB 124469](https://kb.parallels.com/en/124469)
- This applies to RAS-managed RD Session Hosts, which means a full VDI/RDS platform rather than a connection manager. No pricing was collected.

**RDCMan (Sysinternals)**
- No shadowing. It only has "Session > List Sessions" with limited management: list, disconnect and log off. This needs Query Information permission plus Disconnect/Logoff permissions, and the target must be directly reachable, not behind a gateway — [Microsoft Learn: RDCMan](https://learn.microsoft.com/en-us/sysinternals/downloads/rdcman) (page dated Oct 15, 2026)

**mRemoteNG**
- No shadowing feature was found. A GitHub issue search for "shadow" returns only unrelated issues; the nearest are "Add a View Only mode" (#896, closed 2019) and "View Only mode kicks off logged in user" (#1727, open) — [mRemoteNG GitHub issues search](https://github.com/mRemoteNG/mRemoteNG/issues?q=shadow)
- A user on the Royal support forum claimed mRemoteNG supports shadowing "at least with its view only mode". This is unverified and likely confuses mRemoteNG's own view-only display mode with RDS shadowing — [Royal support forum](https://support.royalapps.com/support/discussions/topics/17000007227)

**GitHub / free "ShadowRDP-type" utilities (all wrap mstsc, Windows-only)**
- `Kirkc88/Easy-RDP-Shadow`: a single PowerShell file with a searchable GUI that finds a user by name and starts an mstsc shadow. Updated Aug 4, 2026, 1 star — [GitHub topic: mstsc](https://github.com/topics/mstsc)
- `d-v-d-k/Remote-Desktop-Shadow`: a C# tool to take over or shadow a local or remote session, with a setup window. Last updated Nov 2023, 11 stars — [GitHub topic: mstsc](https://github.com/topics/mstsc)
- `GarryDown/AVD-Shadowing`: PowerShell scripts for Azure Virtual Desktop shadowing (2023) — [GitHub topic: mstsc](https://github.com/topics/mstsc)
- PSWinOps PowerShell module `Connect-RdpSession` checks the session via qwinsta and then opens the shadow window — [PowerShell Gallery](https://www.powershellgallery.com/packages/PSWinOps/1.4.0/Content/Public%5Crdp%5CConnect-RdpSession.ps1)

### Inferences
- LizardSystems TSM is the most explicit and best-documented commercial shadow front-end (view/control/consent, multi-server session discovery). Royal TS and RDM treat shadowing as a secondary "launch mstsc" tool and have known credential quirks.
- Devolutions "session shadowing" (2025.2+) only helps if sessions are opened *through* Devolutions Gateway. It cannot attach to a user who is sitting at a workstation console or logged in normally. It is therefore not a replacement for `mstsc /shadow` helpdesk use.
- Shadowing workstations (Windows 10/11) should work with any mstsc-wrapper, since it is the same mstsc mechanism. However, LizardSystems documents only Server 2012 R2+ targets, so workstation support is not explicitly vendor-stated.

### Gaps
- Whether Royal TS 7.x (current Windows version 7.4) still has the dashboard shadow command and has fixed the credential/view-only issues: no current doc found.
- Whether any RDM release added consent/view-only options to the Terminal Services console shadow button: not found in release notes.
- Ericom (Connect/Shield) and Remote Desktop Plus: not researched due to time limits. No sourced data.

## Q2. Which tools work on macOS (the admin uses a Mac), and can they still shadow?

### Takeaway
No macOS-native client found can do Windows RDS shadowing. Microsoft's own non-Windows clients do not support it, and Royal TSX and RDM for Mac use FreeRDP or their own RDP stacks with no documented shadow. On a Mac, the realistic options are (a) a Windows VM or jump host running mstsc or one of the Windows tools above, (b) Devolutions Gateway-based live session viewing (only for sessions brokered through the Gateway), or (c) a third-party agent-based remote-support tool instead of RDP shadow.

### Cited Findings
- itopia help center (Aug 2026): shadowing Windows 10 cloud desktops requires the Microsoft Remote Desktop Connection client (mstsc) on a Windows system. Neither the Windows Store Remote Desktop app nor clients for other OSes support it. For RDS Session Hosts, itopia's console generates an RDP file usable by "any standard RDP client", which is a vendor-specific mechanism — [itopia: User session shadowing support](https://helpcenter.itopia.com/en/articles/4809369-user-session-shadowing-support)
- Royal TSX (macOS, version 6.4.5) RDP is "based on FreeRDP with Gateway support". Shadow is not mentioned. "External Apps" are "currently only available on Windows", and that is the mechanism Royal TS for Windows uses to launch mstsc — [Royal TSX features](https://www.royalapps.com/ts/mac/features); [Royal download page](https://www.royalapps.com/ts/win/download)
- FreeRDP has server-side "shadow" code (`freerdp/server/shadow.h`), which is FreeRDP's own shadow *server*, not a client for the MS RDS shadow protocol — [FreeRDP source mirror](https://git.stg.centos.org/source-git/freerdp/blob/c8/f/include/freerdp/server/shadow.h)
- RDM's shadow path goes through the Terminal Services console and mstsc. No source confirms this works on RDM macOS — [Devolutions forum](https://forum.devolutions.net/topics/12920/enumerate-and-shadow-remote-rds-sessions)
- RDM 2025.2 Gateway-based session viewing needs a Devolutions Server data source. The Gateway also offers web-client session viewing in a browser, which would work from a Mac — [RDM 2025.2 blog](https://devolutions.net/blog/whats-new-in-remote-desktop-manager-20252/); [Gateway docs](https://docs.devolutions.net/gateway/knowledge-base/how-to-articles/launch-an-rdp-session-in-a-web-client)
- Dameware Mini Remote Control connects to Windows, Mac OS X and Linux machines and supports Intel AMT/vPro and an iOS app. The console runs on Windows; no macOS console is mentioned — [SolarWinds Dameware Mini Remote Control](https://www.solarwinds.com/dameware-mini-remote-control)
- Workaround described across sources: run `mstsc /shadow:<id> /v:<server> /control` from a Windows VM (Parallels/VMware Fusion) or a Windows jump host, and get the ID via `quser /server:` — [LizardSystems how-to](https://lizardsystems.com/terminal-services-manager/how-to/how-to-shadow-a-remote-desktop-session/); [TruGrid](https://help.trugrid.com/en/article/how-to-shadow-rdp-sessions-for-remote-support-without-third-party-tools-1ej3etx/)

### Inferences
- A practical Mac setup is: RDM for Mac or Royal TSX for everyday RDP/SSH and a credential vault. Shadowing goes through an RDP connection to a Windows admin jump host (or a local Windows VM) running LizardSystems TSM, RDM for Windows, or a PowerShell `mstsc /shadow` GUI. This is double-hop RDP; the shadow window renders inside the jump-host session.
- A self-built web control panel would hit the same constraint: the shadow must be started by a Windows-side mstsc. Without an agent-based remote-control product, no browser or Mac path exists to a user's native console session.

### Gaps
- Microsoft Learn does not have an explicit statement that "Windows App for macOS does not support /shadow". The finding rests on itopia's statement and the absence of the feature. Microsoft's Windows App feature-comparison page was not fetched.
- Whether RDM for macOS exposes the AD dashboard or Terminal Services console entries was not confirmed.

## Q3. Which offer AD user/group/computer management (reset password, unlock, enable/disable) in the same UI?

### Takeaway
Devolutions RDM is the only connection manager found that has a built-in AD management entry: the "Active Directory dashboard" with create, enable/disable, unlock, reset password, delete, properties, group membership and BitLocker recovery key. It also offers a web-based AD console through Devolutions Server. ManageEngine ADManager Plus is a full AD management and helpdesk-delegation product but not a connection manager or shadow tool. Dameware Remote Support bundles AD management with remote control. Royal TS, RDCMan, mRemoteNG and LizardSystems TSM do not do AD user management.

### Cited Findings
- RDM "Active Directory dashboard" entry actions: create user or group, enable/disable object, unlock locked account, reset password, delete, view/modify properties, retrieve BitLocker recovery password, and search users, groups and computers. Group membership is managed through the Members tab. The docs recommend restricting the entry's folder to admins and enabling logs with mandatory comment and ticket number — [Devolutions docs: Active Directory dashboard entry](https://docs.devolutions.net/rdm/knowledge-base/how-to-articles/active-directory-dashboard-entry)
- The AD dashboard can be opened from the Devolutions Server web client. This needs Devolutions Gateway 2023.1+ and the entry set to "Always connect with Devolutions Gateway" — [Devolutions Server KB: web-based AD console](https://docs.devolutions.net/server/kb/how-to-articles/web-based-ad-console/)
- Devolutions Server Free includes AD and Entra ID integration — [Devolutions blog: Re-introducing Devolutions Server Free](https://blog.devolutions.net/2024/03/re-introducing-devolutions-server-free)
- RDM credential vault, RBAC, shared vaults and credential brokering (users never see plain-text passwords). Protocols: RDP, SSH, SFTP/FTP/FTPS, Telnet, rlogin, VNC (Real/Tight/Ultra), ICA/HDX, web, plus Hyper-V, VMware, Citrix, TeamViewer, Dameware and 60+ add-ons — [RDM features](https://devolutions.net/remote-desktop-manager/features/)
- ManageEngine ADManager Plus Standard edition: AD reporting, user, computer and multi-domain management, help desk delegation, M365/Exchange management. Professional adds group/OU/contact/GPO management, workflows, automation and file server management. Licenses are domain-based, plus help desk technicians — [ManageEngine ADManager Plus get-quote/editions](https://www.manageengine.com/products/ad-manager/get-quote.html)
- Royal TS: no AD import or user management listed. External data comes in via "Dynamic Folders" and RoyalJSON scripts, which can be scripted to pull computers from AD. It offers Windows Services and Windows Processes connection types — [Royal TS features](https://www.royalapps.com/ts/win/features)
- RDCMan: servers are added manually, by pattern or from a text file. It has no AD browse/import and no user management — [Microsoft Learn: RDCMan](https://learn.microsoft.com/en-us/sysinternals/downloads/rdcman)
- LizardSystems TSM: the handbook navigation mentions an "Active Directory source" for computer lists. The handbook does not describe AD user management — [LizardSystems handbook](https://lizardsystems.com/terminal-services-manager/help/handbook/managing-user-sessions/shadowing-a-user/)
- Dameware (SolarWinds) lists "Active Directory Management" in its feature menu and MFA, smart card and FIPS 140-2 crypto. The Mini Remote Control page gives no AD details. TrustRadius describes Remote Support as including "account and password management" — [SolarWinds DMRC](https://www.solarwinds.com/dameware-mini-remote-control); [TrustRadius](https://www.trustradius.com/compare-products/dameware)

### Inferences
- For "one UI: connect + AD admin + shadow", RDM (Windows) comes closest: the AD dashboard, a Terminal Services console with a shadow button, a vault and audit. On a Mac, only the Devolutions Server web AD console and Gateway session viewing are reachable without Windows.
- ADManager Plus (AD admin) plus LizardSystems TSM (sessions and shadow) is a two-tool combination that covers the functions but not a single pane.

### Gaps
- Exact AD feature parity of RDM for macOS was not confirmed.
- Whether ADManager Plus has any session/shadow or remote-control feature: not found.
- ManageEngine Remote Access Plus AD integration details were not verified (aggregators only mention remote desktop sharing, diagnostics, WoL and shutdown).

## Q4. Pricing for a small IT team of 1-5 admins (as of 2026)

### Takeaway
For 1–5 admins: RDM Free costs $0 but is single-user with no sharing. Devolutions "Remote desktop management & IT operations" is listed at $30 per user per month billed annually (about $360 per user per year), with a "Starter Pack" for teams of 5 or fewer at about half price. Royal TS costs €49 per user with no subscription, and the free Lite edition allows 10 connections. LizardSystems TSM is $299.95 per machine (perpetual) or free for personal use. ADManager Plus Standard costs $595 per year for 1 domain and 2 technicians. RDCMan and mRemoteNG are free.

### Cited Findings
- **Devolutions** (official pricing page, undated, per named user, billed annually): Workforce password management $3/user/month. Remote access management (Gateway, Launcher) $20. Remote desktop management & IT operations (includes RDM) $30. Privileged access management $50. "Starter Pack" for teams of 5 users or fewer is described as "half price for teams of 5" (page markup shows about $27.08). Enterprise is quoted from 100 users. RDM Free may be used commercially but is "limited to individual use only", with no shared vaults, RBAC or central management — [Devolutions pricing](https://devolutions.net/pricing/)
- The older "RDM Team" SKU name no longer appears on the pricing page. Resellers list a 1-year single-user "Remote Desktop Management & IT Operations" licence at about $352.80 — [ComponentSource](https://www.componentsource.com/product/remote-desktop-manager/prices). The vendor FAQ says RDM Free has no limit on number of connections — [RDM FAQ](https://amazon.remotedesktopmanager.com/support/faq)
- **Devolutions Server Free**: up to 10 users (raised from 3 in 2024.1) and includes AD/Entra ID integration. It requires an active RDM Enterprise licence, and web access needs paid CALs (minimum one) — [Devolutions blog 2024](https://blog.devolutions.net/2024/03/re-introducing-devolutions-server-free); [press release](https://devolutions.net/company/press-release/devolutions-unveils-enhanced-shared-account-and-password-management-solution-with-up-to-10-free-users). No confirmation was found that these terms still apply in 2026.
- **Royal TS / TSX**: Lite is €0 with a limit of 10 connections, 10 credentials and 1 document. "Single User" is €49.00 with no subscription and no annual fees. The same document format works across Windows, macOS, iOS and Android — [Royal TS features](https://www.royalapps.com/ts/win/features); [Royal TSX features](https://www.royalapps.com/ts/mac/features). Royal Server (v5.4) offers a 30-day trial, and its price was not shown — [Royal download page](https://www.royalapps.com/ts/win/download)
- **LizardSystems Terminal Services Manager**: Personal is free (non-commercial). Business is $299.95 per machine, perpetual, with 1 year of updates. Corporate is $5,999.95 for the whole organisation, with unlimited installs and technicians — [LizardSystems purchase page](https://lizardsystems.com/terminal-services-manager/purchase/)
- **ManageEngine ADManager Plus** (annual): 1 domain + 2 help desk technicians costs $595 (Standard) or $1,795 (Professional). Professional for 1 domain only costs $795. Regional pages differ — [ManageEngine pricing details](https://www.manageengine.com/products/ad-manager/pricing-details.html). The free edition supports up to 100 domain objects, according to TrustRadius, which is unconfirmed on the official page — [TrustRadius via search](https://www.trustradius.com/compare-products/exostar-vs-manageengine-admanager-plus)
- **ManageEngine Remote Access Plus**: the free edition covers up to 10 computers and 1 technician. Paid tiers are priced by technicians and computers, and aggregator figures conflict (e.g., $95/$145 vs $145/$195 vs $200/$300 for 5 technicians / 50 computers per year) — [Zoftware Hub](https://zoftwarehub.com/en-ae/products/manageengine-remote-access-plus/pricing); [TrustRadius](https://tr-web.dev.trustradius.com/products/manageengine-remote-access-plus/pricing)
- **Dameware** (SolarWinds): licensed per technician with unlimited endpoints. Price figures conflict: TrustRadius lists $520 (Remote Support) and $370 (Mini Remote Control), another TrustRadius page lists $388 and $276, and SHI lists $520 MSRP. A German reseller lists a 3-year per-technician subscription at about €1,234 — [TrustRadius](https://www.trustradius.com/compare-products/dameware); [SHI](https://www.publicsector.shidirect.com/product/50542440/DameWare-Remote-Support-Per-Technician-Lic-Sub); [software-express.de](https://www.software-express.de/hersteller/solarwinds/dameware/remote-support/). The DMRC page says "No monthly fees, connect to unlimited end devices", with a 14-day trial — [SolarWinds DMRC](https://www.solarwinds.com/dameware-mini-remote-control)
- **RDCMan**: free Sysinternals tool. Runs on Windows 11+ / Server 2016+. Passwords are encrypted with DPAPI or an X.509 certificate. Supports credential profiles and smart groups — [Microsoft Learn](https://learn.microsoft.com/en-us/sysinternals/downloads/rdcman)
- **mRemoteNG**: free and open source (GitHub project). Licence and pricing details were not separately fetched — [GitHub](https://github.com/mRemoteNG/mRemoteNG)

### Inferences
- Approximate cost for 3 admins per year: Devolutions RDM package about $1,080 list, less with the Starter Pack. Royal TS is a one-off €147. LizardSystems TSM Business is a one-off $899.85, since it is per machine and each admin PC needs one. ADManager Plus Standard is $595 per year for 2 technicians.
- Cheapest "functional" stack for a Mac admin: Royal TSX or RDM for Mac (or Free) for connections, a Windows jump host with LizardSystems TSM Business ($299.95, one machine) for shadowing, and native AD tools or the RDM AD dashboard on the jump host.

### Gaps
- No official Royal Server price was found (vendor pricing page returned 404).
- The official Devolutions Starter Pack exact price is ambiguous: the page text says "half price" while the markup shows about $27.08 per user per month. Verify on the live page.
- No official 2026 Dameware price list was found. SolarWinds product URLs returned 404 for Dameware Remote Support.
- No pricing for Parallels RAS, Ericom or Remote Desktop Plus was collected.
- Reddit r/sysadmin comparisons were not reviewed due to the tool-call budget.
