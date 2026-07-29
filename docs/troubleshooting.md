# Troubleshooting

Each section names the states that are genuinely different from each other. Several pairs below
look identical to a user and have completely different causes, and collapsing them is how the
wrong thing gets fixed.

## The widget does not appear in the Widgets Board

Distinguish four causes before changing anything:

1. **Widgets disabled by policy.** The `AllowNewsAndInterests` CSP or the **Allow widgets**
   Group Policy is set to `0`. No third-party widget can appear, and nothing about this
   package will change that. Run `pwsh -File scripts/Test-PackagePrerequisites.ps1`, which
   reports this explicitly rather than as a generic failure.
2. **Unsupported Windows build.** The baseline is Windows 11 24H2, build 26100 or later.
3. **The package is not installed, or installed but not registered.** Check with
   `Get-AppxPackage -Name *OutlookWidget*`.
4. **The provider is registered but failing to activate.** The package is present and the
   widget is listed, but pinning produces nothing. This is a COM activation problem, not a
   policy one.

## The package will not install

- **Certificate not trusted.** Sideloading a signed MSIX requires the signing certificate in
  `LocalMachine\TrustedPeople`, which needs administrator rights. On an Entra-managed device,
  policy may block adding it at all — that is a device-policy outcome, not a build problem.
- **Certificate expired and the package was not timestamped.** An already-installed package
  keeps running after its certificate expires, but a retained package can no longer be
  installed. This surfaces only at the moment a rollback is needed, which is exactly when it is
  least welcome.
- **Lower version over higher.** MSIX will not install a lower version over a higher one. The
  rollback procedure is remove the current package, then install the prior signed package —
  which loses widget pins and package-local cache and settings.
- **Publisher mismatch.** The manifest `Publisher` must exactly match the signing certificate
  Subject.
- **HRESULT `0x80073D02`, "the package could not be installed because resources it modifies are
  currently in use."** This is the most likely install failure once a widget is pinned, and it is
  not a packaging, certificate, or policy problem.

  The widget provider runs for as long as a widget is pinned, and Windows will not replace a
  package whose processes are running. The error names the *package*, not the process, so nothing
  in the message points at the provider or at the pinned widget that caused it to start.

  Install with `-ForceApplicationShutdown`:

  ```powershell
  pwsh -File scripts/Install-DevelopmentPackage.ps1 -SkipCertificateTrust -ForceApplicationShutdown
  ```

  Terminating the provider mid-update is safe by design rather than by luck: no named primitive is
  held across an `await`, refresh single-flight is an expiring lease record rather than a held lock,
  and a killed disclosure operation leaves its fail-closed tombstone in place. The Widgets host
  re-activates the provider on demand afterwards, and the widget keeps its pin.

  **Do not unpin the widget to work around this.** It would let the install proceed, and it
  destroys the pin for no reason — the force-shutdown path preserves it.

- **HRESULT `0x80073CFB`, "the provided package is already installed, and reinstallation of the
  package was blocked."** The full text is more useful than the code: *the provided package has the
  same identity as an already-installed package but the contents are different.*

  **This should no longer happen, and there is nothing to edit if it does.**
  `Build-Package.ps1` stamps Build and Revision itself — Build from git commit height, Revision from a
  per-commit build counter in `src\OutlookWidget.Package\.package-version.json` — and also raises the
  revision past whatever is already installed. Every build therefore produces a higher version than the
  last, without anyone remembering to bump anything.

  **Do not edit `Package.appxmanifest` to recover.** Its Build and Revision digits are placeholders that
  packaging overwrites, so changing them accomplishes nothing; changing Major or Minor is a durable
  identity decision, not a workaround for a failed install.

  Causes worth checking if you see this code anyway:

  - **A stale package was installed out of order,** for instance by passing an explicit older `.msix` to
    the install script. Build again and install the newest.
  - **The counter was deleted *and* nothing is installed to compare against** — for example packaging on
    one machine for another. Build again; the counter now advances.
  - **History was rewritten,** so commit height went down. The script refuses this with a named error
    rather than producing a downgrade.

  The fix is never to uninstall to force it through: uninstalling loses widget pins and package-local
  cache and settings, which is a real cost for a problem another build solves.

- **"Derived version … does not exceed the installed …"** from `Build-Package.ps1`, rather than from
  deployment. This is the same conflict caught early and deliberately: the current branch's commit height
  is at or below the branch the installed package came from, which happens at a fork point or in a fresh
  clone of a shorter branch. No revision can fix it, because Build is compared before Revision.

  The message lists the options — build from the branch with the greater height, raise Minor as a
  deliberate decision, or remove the installed package accepting the loss of pins. It fails at build time
  precisely so the third option is not the one you discover first.

  Background on why this is automated at all, because it is not obvious: **any commit changes every
  assembly in the package.** The .NET SDK embeds the git commit SHA in each assembly's informational
  version by default, so a documentation-only or comment-only commit produces a different payload.
  Comparing the 0.3.10.0 and 0.3.11.0 packages showed even `OutlookWidget.Core.dll` differing though its
  source was untouched. That made a manual bump a per-commit obligation whose omission always surfaced
  here, at install time, in an error naming the package rather than the forgotten edit.

  `Deterministic=true` does not prevent this and is not meant to: it makes a rebuild of the *same*
  commit reproducible, not builds across commits.

  It is worth stating what this is *not*, because the failure arrives with a list of plausible
  neighbours and none of them apply:

  - It is **not** an elevation problem. It fails identically elevated, because nothing about it
    concerns certificate trust.
  - It is **not** `0x80073D02`. That one is about running processes and is fixed by
    `-ForceApplicationShutdown`. Passing that flag does not help here, and its message about
    terminating the provider can make it look as though it should.
  - It is **not** the "lower version over higher" case below. The versions are equal, not inverted,
    so the remedy there — remove and reinstall — is the wrong tool and the expensive one.

## Sign-in problems

| Symptom | Cause | Action |
|---|---|---|
| "Sign in required" on the widget | Silent token acquisition threw `MsalUiRequiredException` | Open the companion and sign in. The provider cannot prompt, by design |
| Companion reports `Cancelled` but the dialog said **"Approval required"** | Tenant policy withheld consent, and the broker reported the dismissal as an ordinary cancellation | **Retrying will not help.** Grant admin consent for the registration. Measured limitation: the broker does not reliably distinguish a closed dialog from a policy refusal, so this status cannot separate the two — the companion's cancellation text says as much |
| "Authentication broker unavailable" | WAM broker construction or use failed | Companion diagnostics. No browser will open from the provider under any circumstances |
| "This app needs approval before it can read your mailbox" | Tenant user-consent policy blocked self-consent | **No token was issued and no Graph call was made.** Request administrator approval for delegated `Mail.ReadBasic` |
| "Mailbox access needs approval" | Graph returned HTTP 403 | **A token was issued** and the mailbox request was refused. Different cause from the row above; check the mailbox and tenant configuration |
| Conditional Access or MFA challenge | Tenant policy | Complete it in the companion. There is no background prompt |

The two approval rows are the pair most often conflated. The log's status category
distinguishes them (`ApprovalRequired` versus a recorded 403), and so must any diagnosis.

### "Approval required" despite a permissive-looking user-consent setting

Measured on the reference tenant, and the setting that causes it does not read like a restriction.

**"Let Microsoft manage your consent settings (Recommended)"** with **"Enable user consent for popular
Mail clients"** checked does *not* mean users may consent to mail permissions for any application. It
maps to the Microsoft-managed policy `microsoft-user-allow-default-consent-apps`, which permits user
consent to mail permissions for a **fixed list of Microsoft-chosen application IDs** — Apple Mail,
Spark Email, eM Client, Android-Samsung, Android-Mail, and Thunderbird. The list is Microsoft's and
cannot be added to, so your own registration can never self-consent while that setting is in force.

Two things that look like they contradict this and do not:

- The registration's API permissions blade may show `Admin consent required: No`. That column reports
  the **organization default**, not your tenant's effective policy — the blade says so itself. The
  permission does not inherently need admin consent; the policy withholds it.
- The consent dialog lists three permissions where the registration configures one. The extra two are
  MSAL's automatic OIDC scopes: `offline_access` ("Maintain access to data you have given it access
  to") and `profile` ("View users' basic profile"). Neither is `User.Read`, which grants the full
  profile and reads "Sign in and read user profile".

The remedy is to grant admin consent for that single registration, which covers delegated
`Mail.ReadBasic` for that app only and lets a signed-in user read their own basic mail. Loosening the
tenant-wide consent policy is a much larger blast radius for the same outcome.

### The reported status does not match the failure

The companion prints a `Signals:` line beneath the status — the exception type, MSAL's error code, and
any `AADSTS` numbers. Include it in any report of a misclassified sign-in.

It exists because a status word alone was insufficient once: a tenant consent block arrived carrying
none of the `AADSTS` codes the classifier knew about and reported as a generic `Failed`. If a state is
reported as `Failed` where a specific one applies, that line is what identifies the missing rule.
It carries categories only and never the exception message.

### Reading the provider's own token state

The provider has no console and no window, and the operational log records categories rather than a
running state, so the card is the readout. At the **large** size the diagnostic line ends with
`silent auth <status>`, where the status is one of `Acquired`, `InteractionRequired`,
`ApprovalRequired`, `BrokerUnavailable`, `NoConfiguration`, `Cancelled`, or `Failed`, and `pending`
before the attempt finishes. The medium and large detail line says the same thing in words.

The attempt runs on a background task started after COM registration, and again whenever committed
state or authentication state changes. If the status reads `pending` and stays there, the acquisition
has not returned — not that it failed.

### The companion signed in successfully, but the widget still says sign-in required

**This should now resolve itself within moments.** The companion raises the state-changed event after a
successful sign-in and the provider re-acquires in response, so a pinned widget converges without being
unpinned. The companion's window says which happened: *"A running provider was notified and will
re-acquire"* when a provider was listening, or that none was — normal when the companion was opened from
Start rather than from the widget, since a provider probes on its own start anyway.

If it does **not** resolve, distinguish two causes, because they look identical and one of them is not a
sign-in problem:

1. **No provider was listening and none has started since.** Opening the Widgets Board activates the
   provider, which probes on start. The provider's lifetime is demand-driven rather than pin-driven, so
   it can legitimately not be running even with a widget pinned.
2. **The two processes are not sharing MSAL's token cache.** WAM keeps the refresh token
   device-bound inside the broker, but MSAL keeps the *account metadata* in its own cache — and
   without that cache the provider enumerates no accounts and reports interaction-required no matter
   what the companion just did. The cache is a DPAPI-protected file in the coordination root; the
   companion's window prints its full path after a successful sign-in.

   If that file is missing or unreadable while the companion reports `Acquired`, this is the cause,
   and it is **not** evidence that the provider's zero window handle failed. Distinguishing the two is
   the whole reason the path is printed.

### The WAM account picker does not appear

Microsoft documents that a Windows update can leave the `Microsoft.AccountsControl` component
incorrectly registered, and the symptom is that the account picker never comes up. Re-register it from
an elevated PowerShell session:

```powershell
if (-not (Get-AppxPackage Microsoft.AccountsControl)) { Add-AppxPackage -Register "$env:windir\SystemApps\Microsoft.AccountsControl_cw5n1h2txyewy\AppxManifest.xml" -DisableDevelopmentMode -ForceApplicationShutdown }
```

This is a machine-state problem rather than anything about this package, and it presents as a
cancelled sign-in because the dialog closes without returning an account.

## Mailbox problems

| Symptom | Graph signal | Meaning |
|---|---|---|
| "This account has no supported Exchange Online mailbox" | HTTP 404 with `error.code` `MailboxNotEnabledForRESTAPI` | The account has no REST-accessible mailbox. Use a supported mailbox |
| Counts or messages briefly unavailable | `error.code` `ErrorItemNotFound` | The folder or message changed. The next refresh resolves it |
| Refresh delayed | HTTP 429 | Throttled. `Retry-After` is honoured; the cache is kept |
| Stale timestamp, cache retained | 5xx, timeout, or offline | Retries on the next approved trigger |

**Unread count does not match the message list.** Expected, not a bug. `unreadItemCount` is
authoritative for the whole Inbox folder and includes all item types, meeting requests among
them. The adjacent list is labelled *newest email messages* and is not expected to reconcile
item-for-item.

## Message details are hidden and will not come back

Message details are suppressed by design in several situations. Check them in this order:

1. **Small widget size** always shows counts only.
2. **"Hide message details"** is enabled in the companion.
3. **No successful refresh for 24 hours** — old subjects are suppressed rather than presented
   as current.
4. **An interrupted operation left suppression in place.** A logout, account switch, or
   privacy change writes a suppression marker *before* changing state, so that a failure
   part-way through cannot leave the previous account's subjects on screen. If the operation's
   process was killed, its marker persists — which is the safe direction.

For case 4 the companion's diagnostics show *"message details are suppressed by an interrupted
operation"* with a clear button. Recovery requires that explicit action on purpose: an
automatic timeout would re-disclose the previous account's subjects at exactly the moment
nobody was watching.

## Sign-out reported a failure

"Could not complete sign-out — it will finish when the widget board is closed; try again"
means the local state commit could not acquire the coordination mutex within its bound, because
another process was wedged inside a critical section.

What is true in that state:

- **Message details are already hidden.** Suppression was written before the commit was
  attempted and does not depend on the mutex.
- The account was very likely already removed from this app's token cache, since that step does
  not need the mutex either. The provider's next silent acquisition then fails and it converges
  on the signed-out card regardless.
- The failure is reported rather than swallowed. A sign-out that silently did nothing while
  reporting success would be a privacy failure, not an inconvenience.

Retrying usually succeeds. Closing the Widgets Board releases the provider process.

## The widget shows stale content while "Refresh already in progress" persists

Another process holds the refresh lease. The indicator follows the lease, so its ceiling is 30
seconds. If the lease owner was killed, the indicator clears when the lease ages out and shows
"Refresh status unknown — try again".

## The widget stops updating but counts are correct elsewhere

Refresh and delivery are recorded separately, and "refresh succeeded, delivery slow or failed"
is a real state. A slow or wedged Widgets host delays rendering only: the snapshot is already
committed, the lease is already clear, refresh accounting is unaffected, and the next
activation re-renders from committed state. Closing and reopening the Widgets Board is the
usual remedy.

## Outlook will not open

- **New Outlook not installed.** There is no Classic Outlook fallback — New Outlook is the only
  supported client. The companion reports the detected state and offers the Outlook on the web
  fallback.
- **`olk.exe` does not resolve.** Check with `pwsh -File scripts/Test-OutlookLaunch.ps1`, which
  reports every available launch strategy. If none resolves, the product may still run in a
  web-only Open Outlook mode.
- **"Open directly to Inbox" does not work.** Microsoft documents no New Outlook
  Inbox-selection command. Launching may restore whatever view Outlook had. This is a known
  limitation, not a defect.
- **A message link opens the browser rather than New Outlook.** Expected. Individual messages
  open through the Graph-provided `webLink`, which is documented to open Outlook on the web and
  may ask for browser sign-in.

## Cache and state recovery

The cache is reconstructible from Graph and has no migration path: an unsupported format
version or corrupt content is discarded and refetched rather than upgraded. Clearing cached
mailbox data from the companion is always safe.

Uninstalling removes package-local cache and settings. It does **not** revoke tenant consent
and does **not** remove the Windows or WAM account.
