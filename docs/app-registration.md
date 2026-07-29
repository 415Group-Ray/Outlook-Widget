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

**Measured on the reference tenant: self-consent was refused and an administrator had to grant
consent.** Expect to need an administrator step, and read the paragraph after the table before
concluding otherwise.

Microsoft does not mark delegated `Mail.ReadBasic` as requiring admin consent, and that is what an
earlier version of this section relied on when it said no administrator step was on the critical path.
The permission's own default is not what decides it — **the tenant's user-consent policy is.** On the
415 Group tenant that policy is "Let Microsoft manage your consent settings" with mail-client consent
enabled, which permits user consent to mail permissions only for a fixed list of six Microsoft-chosen
mail clients (Apple Mail, Spark, eM Client, Android-Samsung, Android-Mail, Thunderbird). Microsoft owns
that list and it cannot be added to, so a registration of your own can never self-consent while that
setting is in force.

Two readings that look like they contradict this and do not: the API permissions blade may show
`Admin consent required: No`, which reports the *organization default* rather than your effective policy
— the blade says so itself; and the consent dialog lists three permissions where the registration
configures one, the other two being MSAL's automatic `offline_access` and `profile`.

Sequencing matters if you want the question answered rather than skipped: **do not grant admin consent
before first sign-in.** Doing so pre-approves the permission, the self-consent path never executes, and
you learn nothing about whether it would have worked. Attempt sign-in first, record what happened, then
grant if it was refused.

**A consent block is an authorization failure, not a Graph failure**, and the two must not be
conflated:

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
administrator, **and if it did not, whether admin consent was subsequently granted and when**, and the
date.

That last part is not bookkeeping. Once admin consent is granted the self-consent question can no longer
be re-measured on that tenant, because a sign-in then succeeds regardless of which path would have been
taken. A record that says only "sign-in succeeded" is indistinguishable from one where the gate was never
really tested.

**Do not record the raw tenant or client ID there.** That file is committed, and an earlier version
of this document asked for both — telling the reader to keep the identifiers out of Git and then to
write them into a tracked file four lines later. The facts are what the evidence report needs; the
values belong in the ignored configuration file.
