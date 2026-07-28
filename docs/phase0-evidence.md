# Phase 0 evidence report

Status: **in progress.** **Every native-surface gate except 11 now passes.** Gates 1, 2, 3, 4, 5 (as
superseded), 6, and the native half of 7 were all observed on the reference machine: the widget is
discoverable, pinnable, renders at all three sizes, survives a reboot and a package upgrade with its
instance restored, launches New Outlook and the companion from its actions, and its provider process
exits when the widget is unpinned. The section 18 tray/popover fallback branch is **not** taken.

**Gate 11 has not passed and cannot yet.** It asks for cached-first refresh and cross-process
invalidation across real Board activation and provider recycle, and neither a refresh nor a state
commit is possible before authentication and Graph exist. Its signalling half is now real — see the
native-surface table — but the gate is open, and it is not a gate that decides between the native
surface and the fallback.

Gate 5 was superseded after the Board was measured to allow only one instance per widget definition.
Two rendering defects were found by the resize test and fixed in 0.2.1.0.

**What remains all depends on authentication:** gates 8, 9, 10, and 12 wait on the Entra app
registration, and gate 11 waits on a real refresh, which waits on the same thing. There is no
outstanding *activation, lifecycle, rendering, launch, or packaging* question. Nothing below is a
projection; each row records what was actually observed, and unproven items say so rather than being
marked pass.

Reference machine: the author's own Entra-managed PC. Recorded 2026-07-28.

Reproduce with:

```bash
pwsh -File scripts/Test-PackagePrerequisites.ps1
```

## Environment as measured

| Item | Observed |
|---|---|
| Windows | 11 Enterprise, build **26200** (baseline is 26100, so above it) |
| Architecture | x64 |
| Widgets policy | No `AllowNewsAndInterests` value present under machine policy or PolicyManager — Widgets are **not** policy-disabled |
| Widgets platform runtime | `Microsoft.WidgetsPlatformRuntime` 1.6.19.0 |
| Widgets Board host | `MicrosoftWindows.Client.WebExperience` 526.17301.40.0 |
| Windows App Runtime | `Microsoft.WindowsAppRuntime.2` **2.3.1.0** — matches the pinned Windows App SDK 2.3.1 |
| New Outlook | `Microsoft.OutlookForWindows` **1.2026.713.100**, family `Microsoft.OutlookForWindows_8wekyb3d8bbwe` |
| .NET SDK | 10.0.302 (installed during this session; the machine previously had runtimes only) |
| Visual Studio | **Not installed**, and not needed. Packaging is done directly with the Windows SDK tools |
| Windows SDK | **10.0.26100** (installed during this session). `makeappx.exe` and `signtool.exe` both resolve under `Windows Kits\10\bin\10.0.26100.0\x64` |
| Developer Mode | Off (`AllowDevelopmentWithoutDevLicense` absent). A properly signed, trusted MSIX installed without it, confirming it is not required for this workflow |
| Elevation | Available. Used once, for the certificate import |
| `LocalMachine\TrustedPeople` | Writable. The development certificate imported successfully — see gate 1 |
| Repository location | OneDrive-backed, per the revised section 12. Root pinned **Always keep on this device** (`P` set, `U` cleared) |
| Private key material in tree | None |

Three risk rows from section 19 are already retired on this machine: Widgets is not disabled
by policy, New Outlook is present, and the pinned Windows App Runtime is present.

## Gate status

Gates are grouped per section 17, because the group determines what a failure means.

### Universal product gates

| Gate | Status | Evidence |
|---|---|---|
| 1 — signed MSIX installs; certificate can be trusted | **PASS** | Managed-device policy **does** permit trusting a certificate in `LocalMachine\TrustedPeople` on this PC, and sideload installation succeeded. Installed as `415Group.OutlookInboxWidget_0.1.0.0_x64__dgbvqhastx60y`, family `415Group.OutlookInboxWidget_dgbvqhastx60y`, signed by `CN="415 Group, Inc."`. `signtool verify /pa` succeeds once the certificate is trusted, and the RFC 3161 timestamp verifies against DigiCert. Developer Mode was **off** throughout, so it is not required for this workflow |
| 8 — WAM sign-in with MFA/CA, self-consent to `Mail.ReadBasic` | **Not started** | Needs the Entra registration and the companion app |
| 10 — `Mail.ReadBasic` returns exactly the approved properties | **Not started** | Needs the registration and a token |

A failure in this group stops the product rather than triggering the tray fallback, because
the fallback is also a packaged MSIX using the same certificate and the same delegated
permission.

### Native-surface gates

| Gate | Status | Evidence |
|---|---|---|
| 2 — discoverable and pinnable in the Widgets Board | **PASS** | Observed on the reference machine. **Outlook Inbox** appeared in the Add Widgets picker and pinned successfully, and the pinned card rendered the provider's own content — headline "No cached state yet", the detail line, and all three action buttons. This is the gate that decides the architecture: the native surface works, so the section 18 tray/popover fallback branch is not taken |
| 3 — provider cold activation after reboot and package update | **PASS** | All three cases observed. **Cold start:** `CoCreateInstance` on CLSID `254395D8-5EAC-4A2D-9971-90C99BFFD192` from an ordinary PowerShell session succeeded with no provider running, proving the `com:ExeServer` registration, framework resolution, `CoRegisterClassObject`, and the class factory. **Board activation:** a rendered card cannot happen without it. **Reboot:** the widget rendered again after a restart. **Package update:** 0.2.1.0 → 0.2.2.0 was installed with the widget pinned, and the widget still rendered afterwards; the companion launched from the widget reported package full name `415Group.OutlookInboxWidget_0.2.2.0_x64__dgbvqhastx60y`, confirming the upgraded package is the one serving the widget rather than a stale process |
| 4 — `GetWidgetInfos()` restores all instances; final-instance exit | **PASS** | Both halves observed. The widget rendered again after a reboot, which requires `RecoverEnabledInstances` to have rebuilt the instance map from `GetWidgetInfos()` before the class object was registered — the Board does not replay `CreateWidget` for an already-pinned widget, so a provider that started empty would have rendered nothing. And **the provider process exited when the widget was unpinned**, confirming that `DeleteWidget` signalled on the transition to empty and `Main` revoked its registration and returned |
| 5 — two instances at different sizes render independently | **Superseded; replacement PASSES** | **A second instance could not be pinned.** After pinning, the picker entry was greyed out and marked as added. The cause is not the manifest: the installed definition carries `AllowMultiple="true"` and declares all three sizes. The replacement gate — one instance rendering correctly at small, medium, and large — **passes**, with two rendering defects found and fixed along the way. See the gate 5 section below |
| 6 — widget action launches the companion | **PASS** | Clicking **Open companion** on the pinned widget launched the companion, which displayed package identity `415Group.OutlookInboxWidget_0.2.0.0_x64__dgbvqhastx60y` and its coordination root inside the package store. Note that the companion did **not** report a launch argument, which is the correct outcome and is explained below: the documented shell-activation candidate succeeded, and that path carries no arguments |
| 9 — provider silent-only acquisition with a zero parent handle | **Not started** | Needs the Entra registration and the broker skeleton. A source-level test now asserts the provider contains no `AcquireTokenInteractive`, which is the enforcement rather than the gate |
| 11 — cached-first refresh and cross-process invalidation | **Partly established; NOT passed** | The coordination subsystem passes 136 automated tests including genuine multi-process contention. Separately, and new: **the named events now exist.** Both `OutlookWidget-StateChanged-v1` and `OutlookWidget-SuppressDetails-v1` were confirmed present while the installed provider ran. Until `StateChangeListener` was written nothing created them, so `StateCommitCoordinator` and `DisclosureTombstoneStore` were opening a non-existent event and swallowing the failure — every cross-process signal in the product was a silent no-op. **The gate itself is not met:** it requires cached-first refresh and cross-process invalidation observed across real Board activation and provider recycle, and neither a refresh nor a state commit can happen until authentication and Graph exist. It cannot be closed in Phase 0's native work |

**Gate 11 is the one native-surface gate that has not passed, and it is not a surface-choice gate.**
The decision between the native provider and the tray/popover fallback rests on discoverability,
pinning, rendering, lifecycle, and launch — all of which pass. Gate 11 asks whether refresh and
invalidation behave correctly across a real host, which would be equally unproven on the fallback
surface because both consume the same coordination core. So the fallback branch is settled while
gate 11 stays open, and any status claim elsewhere must say "every native-surface gate **except 11**"
rather than "every native-surface gate".

### Gate 5 — the Widgets Board allows only one instance of a widget definition

**Measured:** after **Outlook Inbox** was pinned, it remained listed in the Add Widgets picker but
was **greyed out and marked as added**, so a second instance could not be pinned.

That specific symptom is what makes the cause clear. The Board found the widget, recognised it as
already pinned, and deliberately disabled the entry. A manifest problem, a failed activation, or a
crashed provider would present differently — the widget would still be offered, or clicking would
error, or the picker entry would be missing entirely. The installed manifest carries
`AllowMultiple="true"`, so the package is asking for what the schema documents and the host is
declining it.

Labelled as **locally measured behaviour on build 26200**, not as a documented platform
limitation. `AllowMultiple` is documented as defaulting to true with "set to false if only one
instance of this widget is supported", which describes what the *provider* permits and says nothing
about what the *host* implements. The authoritative Microsoft support page for Widgets does not
address multiple instances either way. So the honest position is that the host does not honour
`AllowMultiple` on this build, and why is unresolved.

**Consequences, in order of importance.**

1. **Gate 5 as written cannot pass on this host, and that is not a product defect.** It asked for
   two pinned instances at different sizes. One instance is the maximum available.
2. **The design requirement behind it stands unchanged.** Section 3 requires size and active state
   to be per instance and never global, and `WidgetInstanceRegistry` keys everything by widget ID.
   That code is not more complex for being per-instance, and it is what would be needed if the host
   ever honours `AllowMultiple`. It is kept.
3. **What is still testable is the size path on one instance.** Resizing through the widget's
   more-options menu to Small, Medium, and Large drives `OnWidgetContextChanged`, the registry's
   per-instance size update, and the card's three `$when` conditions on `$host.widgetSize`. That
   exercises everything gate 5 covered except the two-instance part.
4. **A second widget *definition* would restore genuine multi-instance coverage,** because two
   different definitions can each be pinned. It is deliberately not added: it would exist only to
   satisfy a test, section 1 treats counts-only as a *state* of one widget rather than a separate
   widget, and a definition shipped for test scaffolding would then have to be supported or removed
   later. Multi-instance correctness stays covered by unit tests over the registry and is recorded
   here as unproven against a real host.

Gate 5 is therefore **superseded** rather than failed. Its replacement is the resize check in the
revised gate list, and section 17 has been updated so the gate list and this report do not
disagree.

**The replacement gate passes.** One instance was resized to Small, Medium, and Large, and all
three rendered distinctly and correctly: Small omitted the detail paragraph, Medium showed it, and
Large added the diagnostic. `OnWidgetContextChanged` therefore fires on resize, the registry records
the new size per instance, and the card's three `$host.widgetSize` conditions all evaluate. The
large-size diagnostic confirmed `Size = Large` and `active`, so `Activate` had also fired.

### Two rendering defects the resize test found, both fixed in 0.2.1.0 and re-verified

Neither was visible at the medium size the widget pins at by default, which is why resizing was
worth doing rather than assuming. Both fixes were confirmed on 0.2.1.0 at all three sizes:

- **Small** renders the headline and a single **Open companion** button, on one row, unclipped.
- **Medium** renders the headline, the detail paragraph, and both action rows.
- **Large** additionally renders the diagnostic as **three distinct lines** —
  `InboxWidget · Large · active`, then `generation 0 · mode Full · read Absent · payload none`,
  then `widget 7c0a7db5-…` — rather than one run-together paragraph.

The widget ID also changed between the two rounds of observation, from `d7c0709a-…` to
`7c0a7db5-…`, because the widget was unpinned and re-pinned. Worth noting rather than glossing:
widget IDs are per pinned instance and are not reused across an unpin, which is why the registry
keys on them and why nothing durable may be located by them.

That small carries no **Refresh** while the cache is absent is deliberate, not an oversight of the
one-row constraint. With nothing cached and no account, refresh has no useful outcome, so the one
available row goes to the action that can actually change the state. Once a snapshot exists, small
shows **Open Outlook** and **Refresh** on that single row, because the companion action drops away.

1. **Three action buttons clipped the card at the small size.** Small showed *Open Outlook* and
   *Refresh* on one row and *Open companion* cut in half at the bottom edge. The small widget frame
   is tall enough for one row of actions; the host neither scrolls a widget nor reports that content
   overflowed, so nothing surfaces the problem except looking at it. Fixed by suppressing the mail
   actions at the small size when the companion action is present, so the small card never exceeds
   one row. The decision is made in C# rather than in a compound `$when`, because the data document
   is built per instance and already knows the size.
2. **Newlines in the diagnostic were dropped.** The three-line diagnostic was built as one string
   with `\n` separators and rendered as a single wrapped paragraph, reading
   "… · active generation 0 · mode Full · read Absent · payload none widget d7c0709a…" — where
   "active" and "generation" appear to be one field. An Adaptive Card `TextBlock` gives no guarantee
   that a lone newline survives. Fixed by binding three separate `TextBlock`s with explicit spacing,
   which needs no such guarantee.

### The companion launch argument does not appear on the normal path

`CompanionLauncher` passes `--from-widget` so the probe can report that a widget action started it.
It did not appear, and that is correct rather than a bug: the first candidate is shell activation of
`<family>!App`, which is the documented way to start a packaged application and **carries no
arguments**. The argument only reaches the companion on the fallback path that starts the sibling
executable directly.

So the marker is not a usable discriminator for gate 6 — the companion window appearing at all in
response to a button click is the evidence. The argument is kept because it still distinguishes the
fallback path when that path runs, but the expectation that it would show was wrong and is corrected
here rather than left to mislead the next reader.

### Gate 7 — Outlook launch, which spans both groups

| Half | Status | Evidence |
|---|---|---|
| Universal — does bare `olk.exe` resolve and launch | **Resolution proven, launch not yet exercised** | `olk.exe` resolves to `%LocalAppData%\Microsoft\WindowsApps\olk.exe`. Package activation also resolves, as `Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows`. Run `scripts/Test-OutlookLaunch.ps1 -Launch` to exercise the launch itself |
| Native — can a Board action launch it | **PASS** | Clicking **Open Outlook** on the pinned widget launched New Outlook. `OutlookLauncher` tries the `olk.exe` app execution alias first and shell activation of the application user model ID second; which candidate won was not instrumented, so only the outcome is recorded. No versioned `WindowsApps` path is used by either |

Both launch strategies resolve, so `OutlookLauncher` has two candidates and need not
hard-code the versioned `WindowsApps` path — which section 9 forbids and which this machine
shows would be `...Microsoft.OutlookForWindows_1.2026.713.100_x64__8wekyb3d8bbwe`, changing
on every Outlook update.

### Gate 12 — Focused count

**Not started.** Optional; gates only the Focused unread setting.

## Signing decisions, as recorded

All of section 15's required decisions are now made, and made before the first install.

| Decision | Value |
|---|---|
| Certificate Subject | `CN="415 Group, Inc."` |
| Manifest `Publisher` | `CN="415 Group, Inc."` — byte-identical to the Subject |
| Thumbprint | `F39705644AC44B95EA8E9245A9F7642C732F1B46` |
| Validity | 2026-07-28 to **2036-07-28** (10 years) |
| Key retention | `Cert:\CurrentUser\My`. The private key was **never exported**; no `.pfx` exists anywhere |
| Public certificate | `%LocalAppData%\OutlookWidget\signing\OutlookWidget-Development.cer`, outside the repository and outside OneDrive |
| Continuity position | **Accept the break.** Expanding beyond one user may mean remove-and-reinstall. The persistent-identity option stays available for free through the long validity and retained key |
| Timestamping | **Timestamped** via `http://timestamp.digicert.com`. Verified: `Tue Jul 28 11:00:08 2026`, DigiCert Trusted G4 TimeStamping RSA4096 SHA256 2025 CA1. Retained rollback packages therefore have **no** shelf life tied to certificate expiry |

**The Subject required quoting, and this is worth knowing.** `CN=415 Group, Inc.` was
requested; Windows normalized it to `CN="415 Group, Inc."` because a comma separates elements
in an X.500 name, so a value containing one must be quoted. The manifest carries the quotes
XML-escaped. A manifest reading `CN=415 Group, Inc.` would look correct and would produce a
*different package identity* than the certificate signs. `Build-Package.ps1` compares the two
and refuses to build on a mismatch, because this failure is otherwise silent.

The `.gitignore` excludes `*.pfx`, `*.p12`, `*.snk`, `*.key`, and `*.cer`, and the preflight
script fails if any key file appears under the repository root.

## What is still blocking, and what unblocks it

**The Entra app registration, and nothing else.** Single-tenant, public client, delegated
`Mail.ReadBasic` only, no secret. Gates 8, 9, 10, and 12 all wait on it. Settings are in
[app-registration.md](app-registration.md).

It cannot be avoided by prompting the user. Consent *is* already a user prompt — self-consent to
`Mail.ReadBasic` at first sign-in, no administrator step — but the registration is the application's
identity and is what issues the client ID, and no token request can be made without one. Entra does
not implement OpenID Connect Dynamic Client Registration, so there is no bootstrap path: creating a
registration through Graph would itself require a token. It is a one-time task, not a per-user one.

**Gate 11 also waits on it**, though indirectly: cached-first refresh and cross-process invalidation
cannot be observed until there is something to refresh and something to commit.

No longer blocking:

- **Every activation, lifecycle, rendering, launch, and packaging observation.** Discoverability,
  pinning, rendering at all three sizes, reboot survival, instance recovery, both launch actions,
  final-instance process exit, and upgrade with a widget pinned have all been observed on the
  reference machine.

No longer blocking, having been resolved during this work:

- **Widget provider registration.** The `com:Extension` COM server and the `uap3:AppExtension`
  widget registration are both in the manifest and both survive installation. The provider
  exists, is COM-activatable, and holds the only `UpdateWidget` call site in the product.

- **Windows SDK.** Installed as 10.0.26100; `makeappx.exe` and `signtool.exe` both resolve.
  Packaging is done directly with these tools rather than through a Visual Studio packaging
  project, so no VS installation is needed at all.
- **Elevation and certificate trust.** Proven to work on this managed device.
- **Provider source compilation.** A probe build confirmed `Microsoft.WindowsAppSDK` 2.3.1 and
  `WidgetManager.GetDefault().GetWidgetInfos()` compile with the .NET 10 SDK alone.

## Measured: `LocalApplicationData` is NOT redirected for a packaged full-trust app

Running the packaged companion reported its coordination state root as:

```text
C:\Users\rsmalley\AppData\Local\OutlookWidget
```

**Outside the package store.** The assumption that
`Environment.GetFolderPath(LocalApplicationData)` is redirected into the package's own local
data store when the process is packaged holds for UWP and is **false** for a packaged
full-trust desktop application on this build. `%LocalAppData%\Packages\<family>\` exists with
`LocalCache`, `LocalState`, and the rest, and the app was writing beside it rather than inside
it.

This was a real defect, not untidiness. Section 11 and the troubleshooting guide both state
that uninstall removes package-local cache and settings. State outside the package store
survives uninstall, so a DPAPI-protected snapshot containing senders and subjects would have
been left behind on the machine after the app was removed — a privacy claim the product would
not have honoured.

Fixed by locating state explicitly rather than relying on redirection:
`%LocalAppData%\Packages\<PackageFamilyName>\LocalCache\Local\OutlookWidget`. The family name
is used rather than the full name because the full name carries the version, so state located
by it would move on every update and orphan the previous version's cache and suppression files.
`CoordinationPathsTests` asserts the packaged and unpackaged roots differ, which is the
regression that would otherwise return silently.

## Also verified

- **Upgrade over the previous version.** 0.1.0.0 → 0.1.1.0 installed as an upgrade, keeping the
  same package family. No elevation was needed for the upgrade, because the certificate was
  already trusted from the first install — so only the very first install on a machine requires
  an administrator.
- **Corrected package-local state path in the installed probe.** The launched 0.1.1.0 Phase 0
  probe reported package family `415Group.OutlookInboxWidget_dgbvqhastx60y` and coordination
  root `%LocalAppData%\Packages\415Group.OutlookInboxWidget_dgbvqhastx60y\LocalCache\Local\OutlookWidget`.
  This is target-device evidence that the packaged process now uses uninstall-scoped storage,
  rather than relying only on the path-selection unit test. The same probe displayed the
  configured 2-second mutex wait, 20-second async deadline, and 30-second lease horizon. It
  remains a packaging probe: this does not establish any Widgets Board/provider gate.
- **Signature verification after trust.** `signtool verify /pa` reports
  `Number of files successfully Verified: 1` once the certificate is in
  `LocalMachine\TrustedPeople`. Before trust it failed with an untrusted-root error, which is
  the expected sequence rather than a problem.
- **Second upgrade, now carrying the provider.** 0.1.1.0 → 0.2.0.0 installed as an upgrade from a
  non-elevated session, as `415Group.OutlookInboxWidget_0.2.0.0_x64__dgbvqhastx60y`. Adding a COM
  server registration, a widget extension, and a framework dependency did not require elevation,
  a fresh certificate decision, or a new package identity.
- **The Windows App SDK framework dependency resolves.** The installed package reports its
  dependency satisfied by `Microsoft.WindowsAppRuntime.2` **2.3.1.0 x64** — the framework package
  already present on this machine, matching the pinned SDK. The manifest values were taken from
  the SDK package's own `WindowsAppSDK-VersionInfo.xml` and `AppXReference.props` rather than
  guessed. This matters because the provider's own output carries only the managed projection
  assemblies; the native `Microsoft.Windows.Widgets.dll` comes from this framework package, and
  omitting the dependency would have produced a package that installs and a provider that fails
  to activate on a missing DLL.
- **The provider composes its coordination stack inside the installed package.** After
  activation, `%LocalAppData%\Packages\415Group.OutlookInboxWidget_dgbvqhastx60y\LocalCache\Local\OutlookWidget`
  existed and contained the `suppression-v1` directory. This is the packaged path, not the
  redirected one, so the uninstall-scoped storage fix holds for the provider as well as the
  companion.

## Measured: nothing had ever created the cross-process notification events

`StateCommitCoordinator` signals a committed change by calling
`EventWaitHandle.OpenExisting(StateChangedEventName)`, and `DisclosureTombstoneStore` does the
same for the suppress-details event. Both catch `WaitHandleCannotBeOpenedException` and treat it
as "no listener is running", which is correct — state on disk is authoritative and the event is
only an accelerant.

**No code in the product created either event.** Every cross-process signal was therefore
opening a non-existent kernel object and taking the swallow path, one hundred percent of the
time. The behaviour was correct by construction and had never once been delivered, and no unit
test could have noticed: the signal is deliberately best-effort, so a test asserting that a
missed signal costs nothing passes whether or not signalling works at all.

`StateChangeListener` is the missing listener. It creates both events, waits on either, resets
before invoking its callback rather than after, and in the provider that callback is
`DeliveryWorker.RequestDelivery`. Confirmed on the installed package: both
`OutlookWidget-StateChanged-v1` and `OutlookWidget-SuppressDetails-v1` were openable by name
from a separate process while the provider ran, and absent once it exited.

The reset-then-notify ordering is load-bearing and looks like a lost-wakeup bug at first glance.
It is not, because the callback re-reads state from disk rather than acting on anything the
signal carried: a signal arriving between the reset and the notification either reaches a pass
that has not yet read state, or causes one more pass afterwards.

## Measured: a pinned widget blocks its own package update

Replacing the installed package while a widget was pinned failed with HRESULT **`0x80073D02`** —
"the package could not be installed because resources it modifies are currently in use", elaborated
as "Unable to install because the following apps need to be closed
`415Group.OutlookInboxWidget_0.2.1.0_x64__dgbvqhastx60y`".

The cause is the provider. It runs for as long as a widget is pinned, and Windows will not replace a
package whose processes are running. **The error names the package, not the process**, so nothing in
it points at the provider or at the pinned widget that caused the provider to start — and none of
the four causes the install script previously listed (certificate trust, sideload policy, publisher
mismatch, missing dependency) is right. That misdirection is the expensive part, not the failure.

Resolved with `Add-AppxPackage -ForceApplicationShutdown`, now exposed as a switch on
`Install-DevelopmentPackage.ps1`. Preferred over stopping the process by hand because deployment
sequences the shutdown against its own package lock rather than racing it. The script now also
reports a running provider *before* attempting the install, and special-cases `0x80073D02` in its
failure output with the exact remedy.

Terminating the provider mid-update is safe by design rather than by luck, and this is a genuine
validation of the coordination invariants rather than a hopeful assertion: no named primitive is
held across an `await`, refresh single-flight is an expiring lease record rather than a held lock
whose owner must survive, and a killed disclosure operation leaves its fail-closed tombstone on
disk. A force-kill is exactly the abandoned-holder case the design was built for.

**This is permanent, not a bug to fix.** Any surface that keeps a process alive while content is
displayed has the same constraint, including the tray/popover fallback. Section 15 and
`docs/troubleshooting.md` now carry it, and the v1 update instructions must not tell a user to unpin
first — that loses the pin for no reason.

### What each install actually established

Three separate observations, kept separate because they prove different things:

| Install | Provider running | Result |
|---|---|---|
| 0.2.1.0 → 0.2.1.0, same version | Yes | **Failed** with `0x80073D02`. This is the finding |
| 0.2.1.0 → 0.2.1.0 with `-ForceApplicationShutdown` | Yes | Succeeded; deployment terminated the provider |
| 0.2.1.0 → **0.2.2.0**, a real version upgrade | No — already exited | Succeeded with the widget still pinned |
| 0.2.2.0 → **0.2.3.0**, a real version upgrade | **Yes** | Succeeded with `-ForceApplicationShutdown`; the script's pre-install notice fired correctly |

The 0.2.1.0 → 0.2.2.0 upgrade had one limitation worth stating: the provider had already exited from
the previous force-shutdown and the Board had not re-activated it, so the in-use path was not
re-exercised on that version upgrade itself. **The 0.2.2.0 → 0.2.3.0 upgrade closed that gap** — the
provider was alive from the pinned widget, the script's pre-install notice fired, and
`-ForceApplicationShutdown` carried the upgrade through. That is the complete case: a real version
increment, a pinned widget, and a running provider.

**Which upgrade was confirmed how, kept separate because the two were verified differently.**

*0.2.1.0 → 0.2.2.0 was confirmed end to end.* The pinned widget still rendered afterwards, and the
companion launched from it reported package full name
`415Group.OutlookInboxWidget_0.2.2.0_x64__dgbvqhastx60y` — so the upgraded package was genuinely the
one serving the widget, rather than a stale provider still running from the previous version. That
second check is what makes it end to end: a rendered card alone would not have distinguished the two,
because the Widgets host retains the last card it was given. **This is the observation gate 3's
package-update case rests on.**

*0.2.2.0 → 0.2.3.0 was verified differently, and less completely.* It established the operational
half — a live provider, the pre-install notice, and `-ForceApplicationShutdown` carrying the upgrade
through with the pin intact. Afterwards the provider was confirmed to COM-activate from a cold start
and to create both named notification events. **The Board was not reopened, so whether the widget
re-renders on 0.2.3.0 is unobserved**, and the provider process was stopped by hand after the
activation check rather than being left for the Board to reuse. Nothing here needs it: gate 3's
package-update case is already carried by the 0.2.2.0 observation above, and this install was run to
validate a code change rather than to re-prove the gate. It is recorded this way so the two are not
read as one stronger result than either is.

## Observed characteristic: provider lifetime is demand-driven, not pin-driven

Immediately after the force-shutdown, with the widget still pinned, `Get-Process
OutlookWidget.Provider` returned **nothing** — and the widget remained pinned and the Board still
displayed its last delivered card.

So the provider is not kept alive merely because a widget exists. The Widgets host starts it when it
wants content and the host retains the last card it was given, which is why an absent provider is
invisible until something needs an update. Two consequences worth stating: a stopped provider is not
a symptom in itself, and the Board's displayed content is not evidence that the provider is running.

## Observed characteristic: a provider activated with no widgets never exits

The provider blocks until `DeleteWidget` removes the last enabled instance. Activated directly
by `CoCreateInstance` with nothing pinned, it therefore waits forever, and the process had to be
stopped by hand. `LockServer` is a deliberate no-op, so COM client reference counts do not
influence lifetime either.

This is not obviously wrong — the documented sample behaves identically, and the Widgets Board
should not activate a provider it has no widgets for — but it is an unbounded process lifetime
reached by a path that exists. It is recorded rather than fixed: adding an idle timeout would
introduce a second lifetime rule that could disagree with the first, and a provider that exited
on its own schedule while the Board was mid-pin would be a worse failure than a lingering
process. Section 19 fault injection is the right place to decide, with a measurement rather than
a guess.

## Two defects found by the manifest checks, worth recording

Both were in the new validation rather than the product, and both would have made the checks
silently vacuous rather than wrong — the failure mode that makes a test worse than no test.

1. **`Properties/Logo` is an element, not an attribute.** The build script collected referenced
   paths by scanning attributes, so `//@Logo` matched nothing and the store logo was the one
   manifest image reference never validated. The count it printed was the clue: seven references
   where eight were expected.
2. **XPath treats an unprefixed name as "no namespace".** The manifest declares the foundation
   namespace as its *default*, so unprefixed elements — including the widget registration
   elements inside `uap3:Properties` — are in that namespace. `//CreateInstance` matched nothing,
   and the script reported a missing activation class ID on a manifest that had one. The
   equivalent LINQ-to-XML test compared `Name.LocalName` and was unaffected, which is why the
   test passed while the script failed.

## Observed cost: the Windows App SDK metapackage is broad

The provider's published output is roughly 42 MB, almost none of it needed. `Microsoft.WindowsAppSDK`
is a metapackage, and referencing it brings WinUI (7 MB), the full Windows SDK projection
(25 MB), ONNX Runtime, WebView2, and the AI projections into a process that uses only
`Microsoft.Windows.Widgets`.

`Microsoft.WindowsAppSDK.Widgets` is a separate package and referencing it alone would likely cut
most of that. It is deliberately **not** done here: section 13 pins the metapackage, the Phase 0
probe build validated against it, and changing the dependency strategy while the native gates are
still unproven would put a new variable into the middle of the evidence. Disk size is also not
startup cost — .NET loads only the assemblies it uses — so the case for changing it is tidiness,
which is a Phase 2 conversation.

## An environmental constraint the OneDrive decision introduced

The repository path contains a comma — `OneDrive - 415 Group, Inc`. The dotnet CLI turns
`--output` into an MSBuild `PublishDir` property, and **MSBuild splits property values on
commas**, so publishing directly into a layout under this path fails with `MSB1006: Property
is not valid` and a truncated path as the bogus switch.

`Build-Package.ps1` therefore publishes to a comma-free staging directory outside the
repository and copies the result into the layout. That also keeps publish intermediates out of
OneDrive's sync scope, which section 12 asks for. This is a permanent consequence of keeping
the clone in this location, not a transient bug, and any future build step that passes a
repository path to MSBuild as a property value will hit it again.

## Deviation from the roadmap, recorded deliberately

Section 18 has Phase 1 begin only after this report is reviewed. Phase 1 slice 1 — the
coordination subsystem — was implemented ahead of that review because it was the only
substantial unblocked work: it is pure .NET, depends on no Graph call, no MSIX, and no
Entra registration, and it is the slice the plan singles out as needing to be established
test-first rather than discovered later.

This was a real deviation, and it carried one risk that has now been retired. Slice 1's estimate
assumed the Phase 0 provider lifecycle and broker skeleton would be retained and evolved in
place, and that skeleton did not exist — so `IWidgetDeliverySink` had no production
implementation and the coordination layer had never run inside a real provider process.

**Both are now resolved.** `WidgetDeliverySink` is the production implementation and the single
`UpdateWidget` call site, and the coordination stack has been composed and observed running inside
the installed provider. The integration that was unproven is proven; what remains unproven is the
Widgets Board's behaviour toward it, which is a different thing and is recorded as such above.

The broker skeleton still does not exist, and gate 9 remains not started. That half of the
assumption is unchanged.

## A second deviation: one project the plan's folder structure does not list

Section 12 lists four projects under `src/`. There are now five: `OutlookWidget.Packaging` holds
the `GetCurrentPackageFamilyName` and `GetCurrentPackageFullName` interop, moved out of the
companion.

The reason is a constraint the plan already imposes rather than a new preference.
`CoordinationPaths.Resolve` takes the package family name as a parameter specifically so the core
stays surface-agnostic and free of any knowledge that MSIX exists, and that decision is documented
in the type itself. The provider needs the same value the companion needs. The alternatives were
two copies of a two-call Win32 buffer protocol whose error codes are easy to get subtly wrong, or
putting MSIX interop into the core and contradicting a documented decision. A third option existed
for the provider alone — `Package.Current.Id.FamilyName`, one line, available because the provider
has a Windows-SDK-versioned target framework — but the companion does not have that framework, so
taking it would have left the two executables answering the same question by two mechanisms.

The plan's section 12 has been updated to list it.

## Reproducing the provider evidence

```powershell
pwsh -File scripts/Build-Package.ps1
pwsh -File scripts/Install-DevelopmentPackage.ps1 -SkipCertificateTrust
```

Then, to repeat the cold COM activation without the Widgets Board:

```powershell
$clsid = [Guid]"254395D8-5EAC-4A2D-9971-90C99BFFD192"
[Activator]::CreateInstance([Type]::GetTypeFromCLSID($clsid, $false))
Get-Process -Name OutlookWidget.Provider
```

Stop the process afterwards. Per the observation above, it will not exit on its own with no
widgets pinned.
