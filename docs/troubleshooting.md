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

## Sign-in problems

| Symptom | Cause | Action |
|---|---|---|
| "Sign in required" on the widget | Silent token acquisition threw `MsalUiRequiredException` | Open the companion and sign in. The provider cannot prompt, by design |
| "Authentication broker unavailable" | WAM broker construction or use failed | Companion diagnostics. No browser will open from the provider under any circumstances |
| "This app needs approval before it can read your mailbox" | Tenant user-consent policy blocked self-consent | **No token was issued and no Graph call was made.** Request administrator approval for delegated `Mail.ReadBasic` |
| "Mailbox access needs approval" | Graph returned HTTP 403 | **A token was issued** and the mailbox request was refused. Different cause from the row above; check the mailbox and tenant configuration |
| Conditional Access or MFA challenge | Tenant policy | Complete it in the companion. There is no background prompt |

The two approval rows are the pair most often conflated. The log's status category
distinguishes them (`ApprovalRequired` versus a recorded 403), and so must any diagnosis.

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
