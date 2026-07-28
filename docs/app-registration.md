# Entra ID app registration

One single-tenant registration, public client, no secret. A separate production registration
is a multi-user concern and is created only if the tool is shared beyond the author.

Status: **not yet created.** Gates 8, 9, 10, and 12 all wait on it.

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

## Recording the result

The client ID and tenant ID are identifiers rather than secrets, but they must not be
committed. Supply them through environment-specific package configuration so a development
value cannot be used against another environment by accident.

Record in [phase0-evidence.md](phase0-evidence.md): the tenant ID, the client ID, the exact
permission granted, whether self-consent succeeded without an administrator, and the date.
