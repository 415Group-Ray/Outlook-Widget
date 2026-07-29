# Entra ID app registration

One single-tenant registration, public client, no secret. A separate production registration
is a multi-user concern and is created only if the tool is shared beyond the author.

Status: **created.** The identifiers are configured and ship in the package. Gates 8, 9, 10, and 12
now wait on the authentication code rather than on the registration.

## Settings

| Setting | Value |
|---|---|
| Supported account type | Accounts in this organizational directory only |
| Application type | Public client, mobile and desktop |
| Redirect URI | `ms-appx-web://microsoft.aad.brokerplugin/{client-id}` under **Mobile and desktop applications** |
| Allow public client flows | Yes |
| Client secret / certificate | **None** |
| Implicit grant | Disabled |
| API permission | Microsoft Graph delegated **`Mail.ReadBasic`** only |
| App roles / application permissions | **None** |
| Owners | The author; add a second organizational owner only if the tool is shared |

The redirect URI is the WAM broker's, and it must carry the registration's own client ID.
Brokered authentication will not complete without it.

## Permissions: what to add and what to remove

Add delegated `Mail.ReadBasic`. Nothing else.

- **Remove `User.Read`** if the portal adds it to a new registration. No Graph profile endpoint
  is called in v1; account display information comes from the MSAL authentication result.
- **Do not add `Mail.Read`.** It would permit message-body access, which the privacy design
  prohibits.
- **Do not add `offline_access` manually.** MSAL supplies its standard OpenID Connect and
  offline scopes as part of its protocol behaviour. The app requests `Mail.ReadBasic` as its
  resource scope.
- **Do not add any application permission,** client secret, or certificate. There is no daemon
  identity and no tenant-wide mailbox access.

## Consent

The author can self-consent to delegated `Mail.ReadBasic`; Microsoft does not mark it as
requiring admin consent. No administrator step is on the critical path.

Phase 0 still confirms this empirically at first sign-in, because a tenant user-consent policy
can require administrator approval regardless. **If that happens it is an authorization
failure, not a Graph failure**, and the two must not be conflated:

| | Consent blocked by tenant policy | Graph HTTP 403 |
|---|---|---|
| When | During interactive Entra/MSAL authorization | After a token was issued |
| Token issued | **No** | Yes |
| Graph called | **No** | Yes, and refused |
| Companion shows | "This app needs approval before it can read your mailbox" | "Mailbox access needs approval" |

Recording them as one state is how a tenant policy change gets misdiagnosed as a permissions
bug in the app. See [troubleshooting.md](troubleshooting.md).

Tenant-wide admin consent belongs to the multi-user step if it ever happens. It is operational
convenience, not evidence that the delegated permission intrinsically requires admin consent.

## Where the identifiers live

**Created.** The registration exists in the 415 Group tenant, single tenant, public client, no
secret, delegated `Mail.ReadBasic` only.

The client ID and tenant ID are identifiers rather than secrets — both appear in ordinary network
requests — but they are **not committed**, because a committed development value is the one most
likely to be aimed at the wrong environment by accident.

| | |
|---|---|
| Real values | `src/OutlookWidget.Package/config/authentication.local.json` — git-ignored |
| Template | `src/OutlookWidget.Package/config/authentication.template.json` — committed, placeholder zeros |
| In the package | copied to `authentication.json` beside **both** executables |
| Read by | `AuthenticationConfiguration.Load` in `OutlookWidget.Core` |

`Build-Package.ps1` refuses to build when the local file is missing, when it still contains the
placeholder zeros, or when it fails validation **by the product's own loader** — the script stages the
file into the layout and then calls `AuthenticationConfiguration.Load` against the copies that would
actually ship. So malformed JSON, a missing property, and a non-GUID value are all caught at build
time rather than at first sign-in.

The loader is invoked rather than reimplemented in PowerShell so the two cannot drift. An earlier
version checked only for placeholder zeros, which let every other kind of unusable file through and
made the claim below false. If the packaging host cannot load the assembly, packaging **stops**
rather than falling back to a weaker check: a validation step that quietly downgrades itself is worse
than none, because the surrounding output still says the configuration was verified.

A package therefore cannot be produced that installs and then fails every sign-in for want of
configuration.

Two values are deliberately **not** configurable, and adding them to the file changes nothing
because the loader has nowhere to put them:

- **The scope.** `Mail.ReadBasic` is a compile-time constant. Section 6's permission decision is
  reviewed once, not re-decided per deployment, so no file on the machine can widen what this
  application may read.
- **The authority.** Derived from `tenantId`, so no file can redirect sign-in to `common`,
  `organizations`, or another tenant — which would quietly turn a single-tenant registration into a
  multi-tenant one.

## Recording the result

Record in [phase0-evidence.md](phase0-evidence.md): that the registration was created, the exact
permission granted, the redirect URI platform used, whether self-consent succeeded without an
administrator, and the date.

**Do not record the raw tenant or client ID there.** That file is committed, and an earlier version
of this document asked for both — telling the reader to keep the identifiers out of Git and then to
write them into a tracked file four lines later. The facts are what the evidence report needs; the
values belong in the ignored configuration file.
