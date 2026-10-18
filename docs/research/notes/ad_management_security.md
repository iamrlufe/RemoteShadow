# Custom web admin control center for on-prem AD and Windows machines: management options and security architecture

Research date: 2026-10-18. Sources are mostly Microsoft Learn primary docs; offensive-security sources are used for delegation-abuse context. Anything not backed by a fetched source is in "Inferences" (labelled as background knowledge) or "Gaps".

## (a1) AD operations from a custom app: LDAP/LDAPS vs PowerShell ActiveDirectory module vs ADWS, and what requires an encrypted channel

### Takeaway
The most portable backend is direct LDAP over an encrypted channel (LDAPS 636, StartTLS, or SASL sign+seal via Kerberos). It works from .NET (including Linux via libldap), Python ldap3 and Go. Password set/reset (`unicodePwd`) requires a 128-bit TLS or SASL-encrypted connection. The PowerShell ActiveDirectory module is easier but Windows-only and talks to ADWS on TCP 9389. Windows LAPS passwords are readable as LDAP attributes, but encrypted ones need DPAPI-NG (CNG DPAPI) decryption by an authorized principal.

### Cited Findings
**Password reset / change over LDAP**
- AD's password attribute is `unicodePwd`. It "can be written under restricted conditions, but cannot be read", and it can only be modified, not added at creation or read by a search. — [MS Learn: Set a user's password with Ldifde (KB 263991)](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/set-user-password-with-ldifde)
- "To modify this attribute, the client must have a 128-bit Secure Sockets Layer/Transport Layer Security (SSL/TLS) or SASL encrypted connection to the server." SASL encryption is an accepted alternative to LDAPS (the ldifde `/h` switch). — [same](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/set-user-password-with-ldifde)
- There are two modify forms. A **user change-password** is one modify request with a delete of the old quoted password plus an add of the new quoted password. An **admin reset** is a single replace operation with the new password in quotation marks, done by a principal with rights to reset passwords. "this password becomes the new password regardless of what the old password was." The value is the password in quotes, Unicode-encoded and then Base64 for LDIF (the example value decodes to UTF-16LE `"newPassword"`). — [same](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/set-user-password-with-ldifde)
- If the password violates policy, the DC returns "Unwilling To Perform". — [same](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/set-user-password-with-ldifde)

**LDAP hardening defaults (relevant to any LDAP client)**
- On Windows Server 2025, new AD deployments require LDAP signing by default through the "Domain controller: LDAP server signing requirements enforcement" policy. Microsoft's page says channel binding is set to "When supported" by default. In-place upgrades keep existing LDAP security settings. — [MS Learn: LDAP signing](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/ldap-signing); mechanism explained by [TrustedSec: LDAP Channel Binding and LDAP Signing](https://trustedsec.com/blog/ldap-channel-binding-and-ldap-signing). One vendor KB ([ManageEngine](https://www.manageengine.com/products/ad-manager/kb/ldap/ldap-signing.html)) says channel binding is still disabled by default, which conflicts with Microsoft's page. Treat Microsoft as authoritative and verify on the actual DCs.

**Libraries**
- .NET `System.DirectoryServices.Protocols` (`LdapConnection`) runs on Linux by P/Invoking OpenLDAP `libldap` (`LdapConnection.Linux.cs` in dotnet/runtime). If libldap is missing, constructing `LdapConnection` throws `TypeInitializationException`. Per the package description, some features such as client/server certificate options are not available on Unix. — [dotnet/runtime issue #110391](https://github.com/dotnet/runtime/issues/110391); [source.dot.net S.DS.Protocols](https://source.dot.net/System.DirectoryServices.Protocols/R/6dead6012164c32e.html); [MS Learn namespace](https://learn.microsoft.com/nl-be/dotnet/api/system.directoryservices.protocols?view=net-8.0)
- Python ldap3 has Microsoft-specific extended operations: `modify_password(user, new_password, old_password=None)`, `dir_sync(...)` (DirSync) and `persistent_search` (uses LDAP_SERVER_NOTIFICATION_OID 1.2.840.113556.1.4.528; reports modifications only, not create/delete; needs the AsyncStream strategy). — [ldap3 docs: Microsoft extended operations](https://ldap3.readthedocs.io/en/latest/microsoft.html)

**Ports**
- ADWS (used by the PowerShell ActiveDirectory module and ADAC) is TCP 9389. LDAP SSL is TCP 636, the Global Catalog over SSL is TCP 3269, and RPC dynamic ports are 49152–65535. — [MS Learn: Service overview and network port requirements](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/service-overview-and-network-port-requirements)

**Windows LAPS retrieval**
- Windows LAPS stores the managed local-admin password on the AD computer object. It is protected by ACLs (`Set-LapsADReadPasswordPermission`, `Set-LapsADResetPasswordPermission`) and optionally by encryption. — [MS Learn: Windows LAPS architecture](https://learn.microsoft.com/en-us/windows-server/identity/laps/laps-concepts-overview)
- Encryption needs a domain functional level of Windows Server 2016 or later. It uses CNG DPAPI with AES-256 and encrypts to a **single** principal (`ADPasswordEncryptionPrincipal`, default Domain Admins). Use a wrapper group to give access to several principals. The decryptor cannot be changed after encryption. Microsoft "highly recommend[s]" enabling encryption. — [same](https://learn.microsoft.com/en-us/windows-server/identity/laps/laps-concepts-overview)
- Microsoft describes three permission rings: read/set the expiry attribute (rotation; "nondestructive"), read password attributes, and decrypt. It recommends reserving the inner two for Domain Admins in the most secure approach, and tailoring the rings to how sensitive the machines are. Encrypted password history and DSRM password backup require encryption. — [same](https://learn.microsoft.com/en-us/windows-server/identity/laps/laps-concepts-overview)
- You can force early rotation by changing the expiry time in AD. The device polls hourly; this is not configurable. Post-authentication auto-rotation is supported. `msLAPS-CurrentPasswordVersion` (rollback detection) needs the Windows Server 2025 forest schema. — [same](https://learn.microsoft.com/en-us/windows-server/identity/laps/laps-concepts-overview)

### Inferences
- **Recommended AD backend pattern.** Use an LDAP client from the app server (not a PowerShell shell-out per request) with Kerberos/GSSAPI SASL bind with sign+seal, or LDAPS 636 / StartTLS with certificate validation. This works everywhere, is fast for searches (paged results), and satisfies `unicodePwd` encryption and Server 2025 signing defaults. Simple bind over plain 389 will break against Server 2025 DCs and must never be used.
- **PowerShell ActiveDirectory module.** Pro: high-level cmdlets (`Unlock-ADAccount`, `Set-ADAccountPassword -Reset`, `Move-ADObject`, `Search-ADAccount -LockedOut`). Con: Windows/RSAT only, needs ADWS 9389 on DCs, slower per-call runspace startup, and string-built scripts invite injection. Fits best behind JEA or a dedicated Windows worker.
- Background knowledge (not fetched this session; verify before relying on it):
  - Unlock means writing `lockoutTime=0`.
  - Enable/disable means toggling the `userAccountControl` ACCOUNTDISABLE bit (0x2).
  - Move OU is an LDAP ModifyDN.
  - Group membership is an add/delete of the group's `member` attribute.
  - `lastLogon` is not replicated, so query every DC and take the maximum. `lastLogonTimestamp` is replicated but lags by about 9–14 days.
  - Lockout status should come from `msDS-User-Account-Control-Computed` (UF_LOCKOUT) and `lockoutTime`; `badPwdCount` is per-DC.
  - Legacy LAPS uses the plaintext `ms-Mcs-AdmPwd` attribute. Windows LAPS uses `msLAPS-Password` (plaintext JSON) or `msLAPS-EncryptedPassword`, plus `msLAPS-PasswordExpirationTime`.
  - `Get-LapsADPassword -AsPlainText` decrypts on Windows. Decrypting on Linux needs an MS-GKDI/DPAPI-NG implementation (open-source offensive tools have one) rather than a Microsoft-supported library.

### Gaps
- No Microsoft doc was fetched that explicitly confirms `unicodePwd` writes over SASL **Kerberos** sign+seal from a non-Windows client (KB 263991 says "SASL encrypted" generically). Test against the target DCs.
- Did not fetch Go `go-ldap` docs. Background knowledge: it supports StartTLS/LDAPS and simple bind, and Kerberos/GSSAPI bind was added via `gssapi` subpackages; maturity is unverified.
- Did not fetch a Microsoft-supported way to decrypt `msLAPS-EncryptedPassword` from Linux/.NET without PowerShell.
- No source fetched for the exact `lastLogon`/`lastLogonTimestamp` semantics. They are listed above as background knowledge.

## (a2) Remote Windows machine operations: WinRM/PS Remoting, CIM/WMI, JEA, WTS/msg/logoff/reboot/services/processes/event logs, ports

### Takeaway
PowerShell Remoting over WinRM (5985 HTTP with Kerberos message encryption, or 5986 HTTPS) is Microsoft's recommended remote management channel. It is what Windows Admin Center itself uses ("Remote PowerShell and WMI over WinRM"). It can be locked down with JEA endpoints that run as virtual accounts or gMSAs and log transcripts. Classic DCOM/RPC WMI and RPC-based tools need 135 plus 49152–65535, which is hard to firewall.

### Cited Findings
- "PowerShell Remoting is the recommended way to manage Windows systems." It is enabled by default on Windows Server 2012 R2 and later. Ports: HTTP 5985, HTTPS 5986. By default only Administrators may connect. Sessions run in the user's context. — [MS Learn: Security considerations for PowerShell Remoting using WinRM](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)
- Default firewall: the private profile accepts all connections; the public profile allows only the same subnet. — [same](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)
- WinRM "always encrypts all PowerShell remoting communication after initial authentication". Over HTTP it uses message-level encryption from the auth protocol: Kerberos uses the ticket etype (AES-256 on modern systems), NTLM uses RC4-128, and Basic has no encryption. Over HTTPS it uses TLS. — [same](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)
- Kerberos is the default when connecting by computer name and authenticates both client and server. Connecting by IP falls back to NTLM, which does not prove server identity. NTLM is off by default unless HTTPS is configured or the host is in TrustedHosts. TrustedHosts only suppresses the identity error. — [same](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)
- Second-hop problem: the remote machine does not receive the user's credentials, so it cannot reach a further resource without delegation (CredSSP, Kerberos delegation, etc.). — [same](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)
- WinRM runs as Network Service and spawns isolated per-user host processes. — [same](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)
- Remote cmdlets using `-ComputerName` (not Remoting) use RPC instead. — [same](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)
- JEA: delegated administration for anything PowerShell can manage. It uses virtual accounts or gMSAs to perform privileged actions for non-admin users, limits which cmdlets/functions/external commands are allowed, and produces transcripts and logs of exactly which commands ran. — [MS Learn: JEA overview](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/jea/overview)
- Ports from Microsoft's port reference:
  - WinRM 2.0: TCP 5985/5986
  - RDS: TCP/UDP 3389
  - DCOM / RPC (WMI, remote GP update, Event Log, Service Control Manager): TCP 135 plus random 49152–65535
  - Remote Group Policy refresh "requires RPC/WMI access through port 135 and ports 49152-65535"
  - — [MS Learn: Service overview and network port requirements](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/service-overview-and-network-port-requirements)

### Inferences
- Run CIM over WSMan (`New-CimSession`, the default protocol is WSMan) instead of DCOM so that **only 5985/5986** need to be opened from the portal's worker to endpoints. In that design, every operation (services, processes, event logs, reboot via `Win32_OperatingSystem`/`Restart-Computer`, logoff via `Win32_OperatingSystem.Win32Shutdown` or `logoff.exe`/`quser`) runs inside a WinRM session. This is background knowledge, not fetched.
- Messaging and session control:
  - `msg.exe`, `quser`/`query session`, `logoff` and the WTS API (`WTSSendMessage`, `WTSLogoffSession`, `WTSEnumerateSessions`) can be called remotely over RPC (TermService named pipes over SMB 445). They can also be run locally on the target inside a WinRM/JEA session, which avoids opening RPC.
  - Running them locally inside a JEA session that exposes only wrapper functions (e.g. `Send-UserMessage`, `Invoke-UserLogoff`) is the least-privilege pattern.
  - This is background knowledge; no Microsoft source was fetched for WTS API remote behavior.
- Prefer WinRM over HTTPS (5986) with certificates if any endpoints are non-domain or reached by IP. With Kerberos-only and FQDN targeting, HTTP 5985 is still encrypted.

### Gaps
- The exact JEA configuration details (session configuration `.pssc`, role capability `.psrc`, `RunAsVirtualAccount`, `GroupManagedServiceAccount`, `TranscriptDirectory`, `SessionType RestrictedRemoteServer`, NoLanguage mode) were not fetched beyond the overview page.
- Microsoft's JEA security considerations page (risks of exposing commands like `Start-Process` or `Invoke-Expression` that allow escape) was not fetched.
- No source fetched on remote `msg.exe` requirements (the `AllowRemoteRPC` registry value) or the WTS API.

## (a3) How existing tools do it: Windows Admin Center, PowerShell Universal

### Takeaway
Windows Admin Center (WAC) is the reference architecture: a browser front end and an HTTPS gateway (.NET 8/Kestrel) that manages nodes with Remote PowerShell and WMI over WinRM. It uses the **user's own identity** and admin rights on targets, with optional JEA-based RBAC on the targets. Microsoft explicitly says WAC "does not implement, monitor, or enforce a security boundary". PowerShell Universal is a script-hosting platform. It maps AD groups to app roles and runs jobs as stored credentials or gMSAs, so it is closer to the "service account plus app RBAC" model.

### Cited Findings
- WAC gateway: installed on Windows Server, it "manages servers by using Remote PowerShell and WMI over WinRM". It uses a .NET 8 backend, an ASP.NET Core Kestrel server and HTTP/2, plus a multi-process microservice model (a process manager spawning task subprocesses, with plugins in their own subprocesses). — [MS Learn: What is Windows Admin Center](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/understand/what-is)
- Install modes: local client (`https://localhost:6516`), gateway server, managed server, failover cluster. "Installing Windows Admin Center on a Domain controller is not supported." Version 2410 does not support high availability. — [MS Learn: WAC installation options](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/plan/installation-options)
- "Windows Admin Center as an application depends on the operating system and infrastructure for security. Windows Admin Center does not implement, monitor, or enforce a security boundary." — [MS Learn: WAC user access options](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/plan/user-access-options)
- Gateway roles: **Gateway users** and **Gateway administrators**. "Access to the gateway does not imply access to the target servers"; the user must have admin credentials on the target. — [same](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/plan/user-access-options)
- Gateway identity providers are AD/local groups or Entra ID. Smart card enforcement is possible by requiring membership in additional smart-card-based groups. MFA/conditional access is available only via Entra. — [same](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/plan/user-access-options)
- WAC RBAC deploys a JEA endpoint on each managed server (via DSC). When a user connects, "a temporary local administrator account is created to manage the system on their behalf" and is removed afterwards.
  - Built-in roles: Administrators (no RDP/PowerShell), Readers, and Hyper-V Administrators.
  - Custom roles are not supported.
  - With limited access, Files upload/download, PowerShell and RDP are unavailable.
  - — [same](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/plan/user-access-options)
- PowerShell Universal (Devolutions/Ironman):
  - Built-in roles are Administrator, Operator, Execute and Reader.
  - Roles map to claims such as AD group membership (`New-PSURole -ClaimType/-ClaimValue`), and AD groups can be imported when Windows auth is on.
  - Group-based policy needs Windows auth or WS-Fed; forms auth will not work for it.
  - — [Devolutions docs: PSU authorization](https://docs.devolutions.net/powershell-universal/security/authorization); [PSU security](https://docs.devolutions.net/powershell-universal/security/security)
- PSU secrets: secrets can carry roles, and role-protected secrets are hidden from scheduled jobs. Jobs can run as a gMSA credential (`domain\user$`, "Password Not Required"). Under a gMSA, the local vault and PSUSecretStore are unavailable; the database vault (AES, key in appsettings or an environment variable) is the alternative. — [Devolutions docs: PSU variables/secrets](https://docs.devolutions.net/powershell-universal/platform/variables)
- A community report says "run as" with a gMSA failed in PSU even though the docs claim support. The issue was unresolved in the thread. — [Ironman forums](https://forums.ironmansoftware.com/t/issues-with-gmsa-and-secret-stores/12584)

### Inferences
- WAC's design avoids storing privileged credentials in the gateway. Every action is authorized by Windows ACLs on the target with the operator's own identity, which puts audit attribution in Windows logs. The cost is that the gateway must perform Kerberos delegation (see b2) or prompt for credentials. A custom portal can copy this: put all target-side authorization in JEA role capabilities, and keep the portal as a thin, audited broker.

### Gaps
- The WAC page about Kerberos delegation for the gateway (RBCD configured on each managed node, allowing the gateway computer account to delegate) and the modernized-gateway port defaults were not retrieved; the modernized-gateway URL redirected to the overview. Background knowledge: Microsoft documents configuring RBCD with `Set-ADComputer -PrincipalsAllowedToDelegateToAccount <gateway>` on each managed node for single sign-on. Verify this.
- WAC logging details (the "Microsoft-ServerManagementExperience" event channel on the gateway and nodes) were not fetched.

## (b1) Authenticating admins to the portal: Kerberos/Negotiate, LDAP bind, ADFS/OIDC, on-prem MFA (Duo, privacyIDEA, TOTP, FIDO2/WebAuthn)

### Takeaway
For a domain-only portal, Windows Integrated Auth (Negotiate/Kerberos), backed by AD group claims and a mandatory second factor at the web layer (WebAuthn/FIDO2 security keys preferred, or TOTP/Duo/privacyIDEA), is the practical baseline. Pure LDAP-bind forms login means the portal handles plaintext admin passwords and should be avoided. Microsoft's own WAC offers MFA only through Entra, plus smart-card group gating on-prem.

### Cited Findings
- WAC supports AD/local groups or Entra ID for gateway auth. On-prem it can enforce smart card via extra smart-card groups. MFA/conditional access needs Entra. — [MS Learn: WAC user access options](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/plan/user-access-options)
- PSU group-based authorization needs Windows auth or WS-Federation (which covers ADFS) to receive group claims. Forms auth does not provide them. — [Devolutions docs: PSU security](https://docs.devolutions.net/powershell-universal/security/security)
- Kerberos authenticates user and server "without sending any sort of reusable credential". NTLM does not guarantee server identity. — [MS Learn: WinRM security](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)
- Protected Users members cannot use NTLM (only Kerberos AES). Their TGT lifetime is limited to 4 hours. Credential caching on hosts is blocked. — [MS Learn: Protected Users](https://learn.microsoft.com/en-us/windows-server/security/credentials-protection-and-management/protected-users-security-group)

### Inferences
- If portal admins are Tier 0 accounts in Protected Users, the portal **must** support Kerberos (Negotiate with the SPN `HTTP/portal.fqdn` on the service account). NTLM fallback will fail for them by design.
- ADFS (on-prem) can act as an OIDC/WS-Fed IdP with MFA adapters (Duo, privacyIDEA, certificates), which moves MFA out of the custom app. This is background knowledge, unverified this session.
- WebAuthn/FIDO2 implemented directly in the app (e.g. with server libraries such as `fido2-net-lib`, `py_webauthn` or `go-webauthn`) gives phishing-resistant MFA without Entra. Pair it with a short session lifetime and step-up re-auth for destructive actions (password reset of privileged users, LAPS reveal). Background knowledge.

### Gaps
- Did not fetch Duo for ADFS/Windows docs, privacyIDEA docs, or ADFS MFA-adapter docs, so none of these are cited.
- Did not fetch Microsoft guidance on Windows Hello for Business / FIDO2 for on-prem-only AD. FIDO2 sign-in to Windows generally depends on Entra hybrid or cloud Kerberos trust; web-level WebAuthn does not.

## (b2) Service account model: gMSA, acting as the logged-in admin (KCD/RBCD) vs privileged service account + app RBAC; tiering and PAW

### Takeaway
There are two viable models:
1. **Impersonation/delegation.** The portal's gMSA does Kerberos constrained delegation or RBCD to WinRM/LDAP on targets, so actions run as the operator. Attribution is good, but privileged Tier 0 accounts cannot be delegated if they are in Protected Users or marked sensitive. RBCD always permits protocol transition, which is a well-known abuse primitive.
2. **Privileged gMSA with app-enforced RBAC.** Simpler, but the portal's service identity becomes as powerful as the union of all operations, and attribution relies entirely on the app's audit log.

Either way, anything that can control DCs or Tier 0 objects is itself Tier 0 / control plane and must be built and administered from PAWs.

### Cited Findings
**gMSA**
- The DC computes the password from Key Distribution Service (`kdssvc.dll`) keys. Member hosts can retrieve current and previous passwords. It removes manual password management and gives one identity across a server farm. It is supported for Windows services, IIS app pools and scheduled tasks. gMSAs don't apply before Windows Server 2012. Always configure AES via `msDS-SupportedEncryptionTypes`. — [MS Learn: gMSA overview](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/group-managed-service-accounts-overview)
- Never add service or computer accounts to Protected Users. — [MS Learn: Protected Users](https://learn.microsoft.com/en-us/windows-server/security/credentials-protection-and-management/protected-users-security-group)

**Delegation**
- Classic KCD is configured by domain admins on the front-end account and is limited to a single domain. RBCD (Windows Server 2012+) is configured on the **resource** (`PrincipalsAllowedToDelegateToAccount`, attribute `msDS-AllowedToActOnBehalfOfOtherIdentity`) and works cross-domain via S4U2Proxy. — [MS Learn: Kerberos constrained delegation overview](https://learn.microsoft.com/en-us/windows-server/security/kerberos/kerberos-constrained-delegation-overview)
- "Resource-based constrained delegation can't use the Trusted-to-Authenticate-for-Delegation bit... The KDC always allows protocol transition when performing resource-based constrained delegation." Resource ACLs can distinguish identities by SIDs: S-1-18-1 means asserted by the authentication authority, and S-1-18-2 means service-asserted (protocol transition). — [same](https://learn.microsoft.com/en-us/windows-server/security/kerberos/kerberos-constrained-delegation-overview)
- Protected Users members cannot "Delegate with unconstrained or constrained delegation". The group also gives AES-only Kerberos and a 4-hour TGT. Microsoft warns against adding DA/EA members before testing because of lockout risk. — [MS Learn: Protected Users](https://learn.microsoft.com/en-us/windows-server/security/credentials-protection-and-management/protected-users-security-group)
- Abuse context:
  - With protocol transition, a compromised front-end service can use S4U2Self plus S4U2Proxy to impersonate **any** user who is not sensitive or in Protected Users to the allowed backend SPNs. — [The Hacker Recipes: KCD](https://www.thehacker.recipes/ad/movement/kerberos/delegations/constrained); [Black Hills InfoSec: Abusing Delegation with Impacket pt 2](https://www.blackhillsinfosec.com/abusing-delegation-with-impacket-part-2/)
  - For RBCD, "the only right needed is the ability to edit this property on a computer object". Write access to `msDS-AllowedToActOnBehalfOfOtherIdentity` therefore becomes a takeover primitive. — [SpecterOps: Another Word on Delegation](https://specterops.io/blog/2018/10/25/another-word-on-delegation/)
- WinRM default auth sends no delegable credential, which is the second-hop problem. CredSSP is the main alternative and does send credentials. — [MS Learn: WinRM security](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)

**JEA as target-side least privilege**
- JEA runs as a virtual account or gMSA on the target so that operators (or a portal identity) need **no** admin rights, only access to the endpoint. — [MS Learn: JEA overview](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/jea/overview)

**Tiering / enterprise access model**
- Microsoft's enterprise access model replaces the AD tier model:
  - Tier 0 becomes the **control plane** (identity and access control).
  - Tier 1 splits into the **management plane** (enterprise-wide IT management) and the **data/workload plane**.
  - Tier 2 splits into user access and app access.
  - Required principle: "Enforce hierarchy – to prevent control of higher planes from lower planes", plus assume breach, least privilege, and continuous audit for escalation paths.
  - — [MS Learn: Enterprise access model](https://learn.microsoft.com/en-us/security/privileged-access-workstations/privileged-access-access-model)
- WAC is not supported on a DC. — [MS Learn: WAC installation options](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/plan/installation-options)

### Inferences
- **The portal's tier equals the highest tier it can affect.** If it can reset Domain Admin passwords, write any group membership, read DC LAPS/DSRM passwords or manage DCs over WinRM, then the portal host, its database, its gMSA, its deployment pipeline and its admins are all Tier 0. That follows from the "prevent control of higher planes from lower planes" rule.
- Recommended split: run separate portal instances or worker identities per tier, so the helpdesk/Tier 2 instance cannot touch Tier 0 objects. Enforce this in AD with OU-scoped delegated ACLs on the gMSA, not just in the app. That limits the blast radius if the app is compromised.
- Delegation choice:
  - RBCD to target computers lets operator identity flow, but it cannot carry Protected Users/Tier 0 identities.
  - RBCD inherently allows S4U2Self protocol transition, so a stolen portal gMSA key can impersonate any non-protected user to the configured targets.
  - Mitigations: mark all privileged accounts "sensitive and cannot be delegated" or put them in Protected Users; scope RBCD only to `HTTP/WSMAN` on Tier 1/2 machines; audit writes to `msDS-AllowedToActOnBehalfOfOtherIdentity`.
- A pragmatic hybrid:
  - The portal authenticates operators with Kerberos+MFA and authorizes in-app.
  - Workers use a per-tier gMSA with **only** AD-delegated rights on specific OUs (reset password, write `lockoutTime`, `userAccountControl`, specific group `member`, LAPS read/decrypt via a wrapper group).
  - Machine actions go through JEA endpoints that expose only portal-specific functions.
  - The portal passes the operator UPN to JEA as a parameter for target-side logging, which is not authoritative.
- PAW: operators of Tier 0 functions should reach the portal only from PAWs. Enforce this with network ACLs or a reverse proxy that allows source IPs or device certs from the PAW range. Microsoft's PAW guidance pages were not fetched (see Gaps).

### Gaps
- Microsoft's PAW deployment and "privileged access devices" pages were not fetched; only the access-model page was.
- Did not fetch Microsoft docs on **dMSA** (delegated Managed Service Accounts, Windows Server 2025). Background knowledge: there was published 2025 research ("BadSuccessor", Akamai) about dMSA abuse via OU-level create rights. Verify it before citing.
- Did not confirm from a primary source whether RBCD S4U2Proxy is blocked for Protected Users or sensitive accounts (Microsoft's Protected Users page says "constrained delegation" generally). The search summary flagged this as unverified for RBCD specifically.
- The legacy AD tier model doc (Tier 0/1/2 logon restrictions) was not fetched; only its evolution into the enterprise access model was.

## (b3) RBAC design, audit logging, session recording, credential vault, secrets at rest

### Takeaway
Use defense in depth:
- App-level RBAC scoped by tier, OU, action and target group, mapped from AD groups.
- Target-level enforcement by AD-delegated ACLs and JEA role capabilities, so app compromise cannot exceed those rights.
- An immutable, append-only audit log of who/what/target/before-after/result, forwarded off-box (SIEM), plus native Windows logs (JEA transcripts, AD directory-service change auditing, LAPS events).
- Secrets in a dedicated vault, not in the app DB.

Existing tools offer only coarse roles (WAC has three fixed roles and no custom roles; PSU has four built-in roles plus claim mapping).

### Cited Findings
- JEA gives transcripts and logs "that show you exactly which commands a user executed during their session". — [MS Learn: JEA overview](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/jea/overview)
- WAC roles: Administrators, Readers, Hyper-V Administrators; "you cannot create custom roles". — [MS Learn: WAC user access options](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/plan/user-access-options)
- PSU roles: Administrator, Operator, Execute, Reader, mapped via AD group claims. Secrets can be role-gated. The database vault uses an AES key held in config/env. — [PSU authorization](https://docs.devolutions.net/powershell-universal/security/authorization); [PSU variables](https://docs.devolutions.net/powershell-universal/platform/variables)
- LAPS permission rings: read/set expiry, read password, decrypt. Microsoft says the read-password and decrypt rights are "sensitive" and "should be carefully monitored". LAPS logs tamper-attempt event 10031. — [MS Learn: Windows LAPS architecture](https://learn.microsoft.com/en-us/windows-server/identity/laps/laps-concepts-overview)
- PowerShell Remoting sessions leave event-log evidence (Mandiant "Investigating PowerShell Attacks" referenced by Microsoft). — [MS Learn: WinRM security](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)

### Inferences
- **RBAC model sketch:**
  - Roles are bundles of permissions: `user.read`, `user.unlock`, `user.resetPassword`, `user.enableDisable`, `user.move`, `group.member.write`, `computer.laps.read`, `computer.session.message`, `computer.session.logoff`, `computer.reboot`, `computer.service.control`, `computer.process.kill`, `computer.eventlog.read`.
  - Each grant is scoped to an OU subtree or group set and a target tier.
  - Deny anything touching objects with `adminCount=1` or members of privileged groups, unless the operator holds an explicit Tier 0 role.
  - Require step-up MFA and a reason/ticket field for reset, LAPS reveal, and group writes to privileged groups.
  - Consider four-eyes approval for Tier 0 actions.
- **Audit:**
  - Fields: timestamp, operator UPN and SID, auth method/MFA, source IP/device, action, target DN/host, parameters (passwords redacted), result, correlation ID.
  - Ship to a SIEM/WORM store the portal cannot rewrite.
  - Correlate with DC events 4724 (password reset), 4767 (unlock), 4738/5136 (object change), 4728/4732/4756 (group adds), and JEA transcripts. Event IDs are background knowledge, not fetched.
- **Session recording.** For RDP/SSH brokering, record via a bastion/gateway (e.g. Apache Guacamole, Devolutions Gateway, or commercial PAM). For PowerShell, use JEA transcripts plus Script Block Logging. Products and capabilities are background knowledge, not fetched.
- **Credential vault:**
  - Prefer not storing reusable privileged credentials at all (gMSA plus JEA plus LAPS on demand).
  - Where RDP/SSH creds must be stored, use HashiCorp Vault (KV with ACL policies, or dynamic secrets; there is an AD/LDAP secrets engine for password rotation) or a PAM product.
  - Encrypt with keys outside the app DB (Vault transit, DPAPI-NG to a gMSA/group, or an HSM).
  - DPAPI (machine or user scope) on a Windows app server is acceptable for local secrets but ties them to that host or account.
  - Background knowledge; Vault docs not fetched.
- **LAPS reveal:** fetch it just-in-time, show it once, then trigger rotation by setting the expiry attribute to now (Microsoft documents that this rotates the password). Combine with post-authentication auto-rotation.

### Gaps
- Did not fetch HashiCorp Vault LDAP secrets engine docs, Devolutions Server/Gateway docs, Guacamole recording docs, or Microsoft's recommended AD audit-policy event IDs. Those statements are background knowledge only.

## (b4) Main risks of building such a portal, and Microsoft guidance

### Takeaway
The portal concentrates the ability to reset passwords, change group memberships, read LAPS passwords and run code on every machine. It is therefore a control-plane (Tier 0) asset and a prime lateral-movement target. Microsoft's own WAC docs state it adds no security boundary, so security must come from OS/AD controls (least-privilege delegation, JEA, Protected Users, hardened LDAP/WinRM) and from treating the portal host as Tier 0.

### Cited Findings
- WAC "does not implement, monitor, or enforce a security boundary". — [MS Learn: WAC user access options](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/plan/user-access-options)
- "Highly privileged accounts used to administer your servers pose a serious security risk. Should an attacker compromise one of these accounts, they could launch lateral attacks across your organization." — [MS Learn: JEA overview](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/jea/overview)
- Management/control planes are "an attractive pathway for attackers". Microsoft also requires preventing "control of higher planes from lower planes" and continuous auditing for configuration that enables inadvertent escalation. — [MS Learn: Enterprise access model](https://learn.microsoft.com/en-us/security/privileged-access-workstations/privileged-access-access-model)
- Delegation configuration creates attack paths. A compromised front-end with delegation lets the attacker reach the back-end resources. RBCD write rights let an attacker set up impersonation. — [MS Learn: KCD overview](https://learn.microsoft.com/en-us/windows-server/security/kerberos/kerberos-constrained-delegation-overview); [SpecterOps](https://specterops.io/blog/2018/10/25/another-word-on-delegation/)
- NTLM/IP-based WinRM and TrustedHosts weaken server authentication. — [MS Learn: WinRM security](https://learn.microsoft.com/en-us/powershell/scripting/security/remoting/winrm-security)
- WAC is not supported on DCs. — [MS Learn: WAC installation options](https://learn.microsoft.com/en-us/windows-server/manage/windows-admin-center/plan/installation-options)

### Inferences
- **Top risks:**
  1. Web vulnerabilities (XSS/CSRF/SSRF/injection into LDAP filters or PowerShell) turn directly into domain compromise.
  2. Theft of the service identity (the gMSA password is retrievable by any principal in `PrincipalsAllowedToRetrieveManagedPassword`, so keep that list to the portal hosts only).
  3. Over-privileged AD ACLs granted to the service, such as GenericAll on OUs or DCSync-adjacent rights.
  4. Delegation abuse (S4U/RBCD).
  5. Storing plaintext credentials or LAPS secrets in logs, caches or the DB.
  6. An insider operator abusing broad rights without approvals.
  7. Supply chain / CI pipeline for a Tier 0 app.
  8. Unencrypted LDAP (simple bind on 389) leaking credentials. This is blocked by Server 2025 defaults on new forests, but not on upgraded ones.
- **Mitigations:**
  - Host on dedicated hardened Tier 0 servers (not DCs).
  - Reach it only from PAWs.
  - Apply strict CSP, anti-CSRF, parameterized LDAP filter escaping (RFC 4515), and no string-built PowerShell (pass parameters to fixed JEA functions).
  - Use separate per-tier service identities with OU-scoped AD delegation.
  - Use Kerberos-only LDAP and WinRM with sign/seal or TLS.
  - Require MFA and step-up.
  - Keep an off-box immutable audit log.
  - Alert on writes to `msDS-AllowedToActOnBehalfOfOtherIdentity`, privileged group changes and LAPS reads.

### Gaps
- Did not fetch Microsoft's "Securing domain controllers against attack" / "Best practices for securing Active Directory" pages, or Microsoft's guidance on AD delegation of control (Delegation of Control wizard, OU-scoped rights). These would strengthen the Microsoft-guidance citations.
- No published 2026-specific incident data on custom AD admin portals being exploited was found or searched.
