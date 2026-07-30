# Phase 0 evidence report

Status: **in progress.** **Every native-surface gate except 11 now passes.** Gates 2, 3, 5 (as
superseded), 4, 6, and the native half of 7 all passed; gate 1 passed in the universal group. The
widget is discoverable, pinnable, renders at all three sizes, survives a reboot and a package upgrade
with its instance restored, restores its `CustomState` through the host, launches New Outlook and the
companion from its actions, and its provider process exits when the widget is unpinned.

**Gate 9 passes, and the surface decision is now settled.** The provider acquired a token silently with
a zero parent handle, observed on the pinned card as `silent auth Acquired`. That was the one remaining
gate that could have reopened the section 18 fallback: a provider unable to acquire silently would have
rendered a sign-in-required card forever, since interactive authentication belongs only to the companion
and the provider must fail closed, whereas a tray/popover UI process could have authenticated itself.
It did not fail. **The tray/popover branch is closed on evidence rather than on expectation**, which is
the distinction earlier versions of this report were careful about and can now stop hedging.

**One native-surface gate remains open: gate 11** — cached-first refresh and cross-process invalidation
across real Board activation and provider recycle. Its signalling half is real and now exercised in both
directions, but a refresh needs Graph, so it cannot close in Phase 0's native work. Gate 11 is **not** a
surface-choice gate: it would be equally unproven on the fallback surface, because both consume the same
coordination core, so it cannot reopen the decision gate 9 just settled.

Gate 5 was superseded after the Board was measured to allow only one instance per widget definition.
Two rendering defects were found by the resize test and fixed in 0.2.1.0.

**Gate 8 has been measured and split.** Brokered WAM sign-in **passes** — a delegated `Mail.ReadBasic`
token was acquired on 0.3.7.0. **Self-consent fails** on this tenant: the Microsoft-managed consent
policy refused it and an administrator had to grant consent for the registration. The gate asks two
questions and they got different answers, so it is not a PASS and not a FAIL.

**Gate 9 passes** — see its row below. Gates 10 and 12 still need
a Graph request to have been issued, and gate 11 needs a real refresh. `GraphMailClient` and the snapshot
model now exist and nothing calls them, so the blocker has moved rather than closed — code is not a gate
status. The Entra app registration is **created** and its identifiers ship in the package, so
nothing waits on a portal task any more. There is no outstanding *activation, lifecycle, rendering,
launch, or packaging* question. Nothing below is a projection; each row records what was actually
observed, and unproven items say so rather than being marked pass.

**"Implemented" is not a gate status.** Gates 8 and 9 have code, a build, an install, and a readout,
and no measurement. Two rows below say so explicitly, and the distinction is the whole point of this
document.

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
| Windows SDK | **10.0.26100** (installed during this session). `makeappx.exe`, `signtool.exe`, and `makepri.exe` all resolve under `Windows Kits\10\bin\10.0.26100.0\x64` |
| PowerShell host | **7.6.4 on .NET 10.0.10.** Recorded because it is a packaging prerequisite rather than incidental: the build script loads the built `net10.0-windows` Core assembly to validate authentication configuration, so the host runtime must be at least as new as the framework Core targets. PowerShell 7.0–7.4 run on .NET 3.1–8 and could not, even with the .NET 10 SDK installed |
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
| 8 — WAM sign-in with MFA/CA, self-consent to `Mail.ReadBasic` | **SPLIT: sign-in PASSES, self-consent FAILS** | Measured 2026-07-29 on 0.3.7.0. **Brokered sign-in works:** the companion acquired a delegated `Mail.ReadBasic` token through WAM with a real parent window, reporting `Acquired` with an expiry an hour out. **Self-consent does not:** the tenant's Microsoft-managed consent policy refused it, and the token was only obtainable after an administrator granted consent for the registration. Both halves are recorded below. This row must not be reduced to "PASS" — the gate asks two questions and they got different answers |
| 10 — `Mail.ReadBasic` returns exactly the approved properties | **Not started. `GraphMailClient` now exists and has never been called against Graph** | The registration exists and a token is obtainable — gate 8's half that passed, plus gate 9, cover that. The client, the response validation, and the snapshot model are now implemented and unit-tested against a stub handler. **That is code, not evidence**, and this row is the same distinction gates 8 and 9 carried before they were measured: the responses those tests assert against are ones this repository wrote, so they say what the client does with a shape it expects and nothing about what Graph returns. One thing is still missing before the gate can be measured: a refresh that calls the client. The plan's other prerequisite, the recorded home-account identifier, is implemented — and is itself unmeasured, for the reason recorded under "What is still blocking" |

A failure in this group stops the product rather than triggering the tray fallback, because
the fallback is also a packaged MSIX using the same certificate and the same delegated
permission.

### Native-surface gates

| Gate | Status | Evidence |
|---|---|---|
| 2 — discoverable and pinnable in the Widgets Board | **PASS** | Observed on the reference machine. **Outlook Inbox** appeared in the Add Widgets picker and pinned successfully, and the pinned card rendered the provider's own content — headline "No cached state yet", the detail line, and all three action buttons. This is the gate that decides the architecture: the native surface works, so the section 18 tray/popover fallback branch is not taken |
| 3 — provider cold activation after reboot and package update | **PASS** | All three cases observed. **Cold start:** `CoCreateInstance` on CLSID `254395D8-5EAC-4A2D-9971-90C99BFFD192` from an ordinary PowerShell session succeeded with no provider running, proving the `com:ExeServer` registration, framework resolution, `CoRegisterClassObject`, and the class factory. **Board activation:** a rendered card cannot happen without it. **Reboot:** the widget rendered again after a restart. **Package update:** 0.2.1.0 → 0.2.2.0 was installed with the widget pinned, and the widget still rendered afterwards; the companion launched from the widget reported package full name `415Group.OutlookInboxWidget_0.2.2.0_x64__dgbvqhastx60y`, confirming the upgraded package is the one serving the widget rather than a stale process |
| 4 — `GetWidgetInfos()` restores all instances; final-instance exit | **PASS** | All three criteria observed. The widget rendered again after a reboot, which requires `RecoverEnabledInstances` to have rebuilt the instance map from `GetWidgetInfos()` before the class object was registered — the Board does not replay `CreateWidget` for an already-pinned widget, so a provider that started empty would have rendered nothing. That covers pinned IDs, definitions, and per-instance sizes. **The provider process exited when the widget was unpinned**, confirming `DeleteWidget` signalled on the transition to empty and `Main` revoked its registration and returned. And **`CustomState` recovery is confirmed**: the large card reports `delivered 0` rather than `delivered none`, so the generation the sink wrote into `CustomState` came back through `GetWidgetInfos()` on a later provider start. The first implementation wrote that value without ever reading it back, so it round-tripped nowhere; this is the observation that the round trip is now closed |
| 5 — two instances at different sizes render independently | **Superseded; replacement PASSES** | **A second instance could not be pinned.** After pinning, the picker entry was greyed out and marked as added. The cause is not the manifest: the installed definition carries `AllowMultiple="true"` and declares all three sizes. The replacement gate — one instance rendering correctly at small, medium, and large — **passes**, with two rendering defects found and fixed along the way. See the gate 5 section below |
| 6 — widget action launches the companion | **PASS** | Clicking **Open companion** on the pinned widget launched the companion, which displayed package identity `415Group.OutlookInboxWidget_0.2.0.0_x64__dgbvqhastx60y` and its coordination root inside the package store. Note that the companion did **not** report a launch argument, which is the correct outcome and is explained below: the documented shell-activation candidate succeeded, and that path carries no arguments |
| 9 — provider silent-only acquisition with a zero parent handle | **PASS** | Observed on the pinned large card: `config Loaded · silent auth Acquired · widget 07879d4c-…`, with the detail line reading "The provider acquired a token silently with no window of its own, so
gate 9 passes." The provider built its client through `BrokerClient` passing `BrokerClient.NoParentWindow`, ran `SilentAuthService` on a background task after `CoRegisterClassObject`, and got a token — **in a different process from the one that signed in, with a zero parent handle.** This also proves the shared token cache end to end rather than by documentation: the companion wrote the account metadata and the provider found it. Three source-level tests enforce the boundary: no interactive API in the core, none in the provider, and the zero-handle helper is what the provider passes |
| 11 — cached-first refresh and cross-process invalidation | **Partly established; NOT passed** | The coordination subsystem passes the automated suite — 213 tests at the time of writing — including genuine multi-process contention. Separately, and new: **the named events now exist.** Both `OutlookWidget-StateChanged-v1` and `OutlookWidget-SuppressDetails-v1` were confirmed present while the installed provider ran. Until `StateChangeListener` was written nothing created them, so `StateCommitCoordinator` and `DisclosureTombstoneStore` were opening a non-existent event and swallowing the failure — every cross-process signal in the product was a silent no-op. **The gate itself is not met:** it requires cached-first refresh and cross-process invalidation observed across real Board activation and provider recycle, and neither a refresh nor a state commit can happen until authentication and Graph exist. It cannot be closed in Phase 0's native work |

**One native-surface gate has not passed: 11.** Any status claim elsewhere must say "every
native-surface gate **except 11**", and must not say the native group is complete.

**Gate 11 is not a surface-choice gate.** It asks whether refresh and invalidation behave correctly
across a real host, which would be equally unproven on the fallback surface because both consume the
same coordination core. It cannot decide between them.

Gate 9 was the gate that could, and it passed — so unlike every earlier version of this section, the
sentence "the fallback branch is not taken" now rests on a measurement rather than an expectation. The
hedging that used to live here is deliberately gone, and the reason it is gone is recorded in the gate 9
row above rather than assumed.

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

The query is now implemented, which changes nothing about the gate's status and is worth one note.
The gate asks four questions — whether the filter syntax is accepted for this tenant and mailbox,
whether the count agrees with New Outlook, whether latency is acceptable, and whether any
undocumented header such as `ConsistencyLevel: eventual` is required. **The client deliberately sends
no such header**, and a test holds that absence, because sending one pre-emptively would answer the
fourth question by hiding it. If the filter is later found to need one, that is a measurement to
record here and a change to make then.

## Measured: a token was acquired, and what that does and does not prove

On 0.3.7.0 the companion reported **`Acquired`** for delegated `Mail.ReadBasic`, with an expiry roughly
an hour ahead. Brokered WAM sign-in against this registration, from a real Win32 parent window, works.

**The shared token cache was written, which is the part that mattered most.** `msal-v1.bin`, 3510 bytes,
appeared at
`%LocalAppData%\Packages\415Group.OutlookInboxWidget_dgbvqhastx60y\LocalCache\Local\OutlookWidget\`.
Until this point the need for a shared cache was an argument from Microsoft's documentation; it is now
an observed file, inside the package store, where uninstall removes it. This is the mechanism gate 9
depends on to find in the provider the account the companion signed in.

**It proves nothing about self-consent, and the companion said otherwise until this was caught.** The
report text asserted that a successful acquisition meant self-consent had succeeded without an
administrator step. That was false the first time it ran on a tenant needing admin consent, which was
this one: an acquisition is **indistinguishable** between "the user consented" and "an administrator had
already granted consent". The claim was removed rather than reworded, because the process genuinely
cannot know — which consent path was exercised is a fact about the tenant, not about the token.

Worth generalising, because it is the same shape as the clipped button and the misclassified
cancellation: **a check that cannot fail is not evidence.** That sentence would have printed on every
successful sign-in on every tenant regardless of how consent was obtained.

## Measured: every commit changes every assembly, so every rebuild needs a version bump

A comment-only change was rebuilt deliberately to find out whether the payload actually differs. It
does — and not for the reason assumed.

Comparing the 0.3.10.0 and 0.3.11.0 packages, **all four assemblies differ**, including
`OutlookWidget.Core.dll`, whose source did not change at all between them. The cause is the embedded
informational version:

| Package | `OutlookWidget.Core.dll` ProductVersion |
|---|---|
| 0.3.10.0 | `0.1.0+3baac1e3e11d9afd3b7f1ed528b39b154d8907fa` |
| 0.3.11.0 | `0.1.0+0acacba89ea51c08f5d080950adc3c2b0b476550` |

That suffix is the **git commit SHA**, added by the .NET SDK's default
`IncludeSourceRevisionInInformationalVersion`. So the real rule is stronger than the one recorded in the
troubleshooting guide, which said a rebuild after *a code change* needs a bump:

> **Any rebuild from a different commit produces a different payload.** A documentation-only commit,
> a comment-only commit, and a whitespace commit all change every assembly in the package.

Two practical consequences:

- A manual version bump became an obligation on **every commit that will be packaged**, not only on
  commits that touch code. **That is why the bump is now automated** rather than remembered — see the
  section below.
- `Deterministic=true` is doing its job and does not make builds reproducible *across commits*. It makes
  a rebuild of the *same* commit reproducible, which is a different and narrower guarantee than it is
  easy to assume from the property name.

**Worth knowing about this repository's own history:** the claim that 0.3.11.0's payload differed
"because source comments changed" was wrong in its reasoning while right in its conclusion. The comments
were irrelevant; the commit was what changed the binaries, and the docs-only commit before it would have
done the same.

## Implemented: the package version is derived, not edited

`Build-Package.ps1` now stamps `Build` and `Revision` at package time. Major and minor stay in the
manifest as a deliberate decision; the Build and Revision digits there are placeholders and the build does
**not** modify the tracked file.

- **Build** is the git commit height.
- **Revision** counts builds within one commit, from
  `src\OutlookWidget.Package\.package-version.json`, and is raised past the installed package when that
  is higher. It started out in `AppPackages` and moved out of it for the reason recorded below.

**Both parts are needed and neither alone works.** Commit height does not change when rebuilding a dirty
tree, which is the normal development loop. A bare counter would not be meaningful across clones.
Together they increase in the order builds actually happen.

Measured while implementing it:

| Build | Derived version |
|---|---|
| First, at commit height 15 | `0.3.15.0` |
| Rebuild, same commit | `0.3.15.1` |
| Rebuild, same commit | `0.3.15.2` — **this build failed** |
| Rebuild, same commit | `0.3.15.3`, installed over 0.3.11.0 |

The failed build is worth recording twice over. First, it confirms the intended trade: revision state is
written *before* packing, so a failure burns a number rather than letting two builds share one. Second,
**its cause is unknown** — the output was filtered to the version line at the time and the error text is
gone. Three of four builds in that sequence succeeded, and the repository is OneDrive-backed with a
documented sharing-violation risk, which makes transient contention the obvious suspect and an
unverified one. If packaging failures recur, this is the first thing to reproduce properly.

**A shallow clone defeated the first version of this, and the comment claimed otherwise.** The
derivation checked only `git rev-list --count HEAD`'s exit code, with a comment asserting that a shallow
clone would therefore be caught. It would not: that command **succeeds** in a shallow clone and returns
the fetched depth, so a `--depth 1` clone derives `0.3.1.0` — lower than anything installed, which MSIX
refuses. Neither safety net caught it, because the backward-version guard reads state a fresh clone does
not have, and the failure then surfaces at install time as a version error naming the package rather
than the clone.

Now checked explicitly and before the count: `--is-inside-work-tree`, then
`--is-shallow-repository`, each a named failure. Shallow is rejected rather than worked around —
deepening the clone would silently mutate the caller's repository, and guessing a floor would produce a
version unrelated to history.

Worth generalising, because it is the second instance: **a comment asserting a guarantee is not the
guarantee.** The clipped Sign in button, the sentence claiming a successful acquisition proved
self-consent, and this all share one shape — prose describing protection that the code did not
implement, in a form no test would contradict.

**The counter alone was not durable enough, which a second review round caught.** It lived in
`AppPackages` — build output, deletable by design — and it is git-ignored, so it is also absent in a
fresh clone. Either way the revision restarts at 0 at an unchanged commit height and collides with a
package already installed from that same commit, which a rebuild is likely to differ from anyway through
uncommitted source or a different `authentication.json`. The shallow-clone check does not help: the clone
is complete and the height is correct.

Two changes, because the file was the wrong authority:

- It moved beside the project rather than into the output directory whose purpose is to be deleted.
- **The build now also asks the machine what is installed.** That is the version which actually has to be
  exceeded, and unlike a file it cannot be lost by cleaning or cloning. It only ever raises the revision,
  so building for a different machine is unaffected.

Verified against the exact scenario: with `0.3.17.0` installed and the counter deleted, the next build
derived **`0.3.17.1`** rather than repeating `0.3.17.0`.

**A third round found that raising the revision is not always possible**, and the script now stops
instead. The revision adjustment only applies when the installed Build equals the derived one; a branch
whose commit height is *lower* than the installed package — a fork point, or a fresh clone of a shorter
branch — skipped it and produced a version MSIX refuses. No revision can rescue that, because Build is
compared before Revision: `0.3.20.999` is still below `0.3.30.0`. The whole resolved version is now
compared against the installed one and the build fails with the options named, so the remedy that loses
widget pins is not the one discovered first. Verified by temporarily lowering the manifest's minor
against the installed 0.3.17.0:

```
Derived version 0.2.18.1 does not exceed the installed 0.3.17.0, so the package could not be
installed over it.
```

**Every git query is scoped with `-C` to the repository root, not to the ambient working directory.** The
unscoped commands answered about whatever repository the caller happened to be standing in: run from
inside a *different* clone, that repository's shallow status and commit height would have been stamped
into this package with no error raised. Scoping also makes the script location-independent — verified by
packaging from `%TEMP%`, which previously failed the work-tree check and now derives correctly.

The tracked manifest is left alone deliberately: the version stops appearing in every diff, and the
packaged value stays derived rather than remembered.

## A recurring mistake worth naming: reference-tenant detail in portable text

Three separate findings in this PR were the same error — a measurement from the reference tenant written
into text that runs everywhere:

1. The plan's assumption that self-consent works, falsified.
2. Its replacement, "treat an administrator step as likely", which overcorrected into general deployment
   guidance and would have encouraged pre-granting consent.
3. The companion's approval-required copy, which named the reference tenant's specific consent setting and
   Microsoft's fixed client list — shown to **any** tenant returning `ApprovalRequired`, most of which
   would have been blocked by a different policy. A confident wrong diagnosis.

The rule that resolves all three: **this report is where tenant-specific measurements belong; shipped copy
and the plan get the mechanism.** For consent the mechanism is that a permission not marked as requiring
admin consent can still be withheld by tenant policy, and that the registration's own "admin consent
required" column shows the organization default rather than the effective policy. That is true anywhere.
Which policy did it, and on which tenant, is evidence. The layout manifest is stamped by a substitution
scoped to the `Identity` element, and the script then asserts both that the stamp landed and that the
framework dependency's `MinVersion` did not move — the two failure modes a careless regex would produce,
one of which a test already guards.

## Measured: silent renewal through the broker survives token expiry

Observed on 0.3.11.0, and it closes a question the convergence section had left open.

The token acquired earlier expired at `19:22:50Z`. A later sign-in reported `Acquired` with a new expiry
of `20:53:00Z`, so the broker renewed silently — no prompt, no `InteractionRequired`, and no interactive
path taken. The expiry moving is the evidence that this was a fresh token rather than the cached one.

This matters for the convergence work: the natural sign-in-required state that section suggested waiting
for — "the first time a token expires beyond what the broker will silently renew" — did not arrive at
ordinary expiry, because WAM holds a device-bound refresh token and renewed from it. Reaching a genuine
`InteractionRequired` on this machine therefore needs something stronger than waiting: revoked consent, a
Conditional Access re-authentication requirement, or a removed account. The bad-to-good card transition
remains unmeasured, and it is now clearer why it is hard to stage rather than merely inconvenient.

## Measured: the companion's sign-in signal reaches a running provider

Verified on 0.3.10.0, and stated narrowly because only part of the loop was exercised.

**What was measured.** The provider was activated by `CoCreateInstance` on its CLSID from an ordinary
PowerShell session, and both named events — `OutlookWidget-StateChanged-v1` and
`OutlookWidget-SuppressDetails-v1` — were confirmed present, which only happens if its
`StateChangeListener` constructed. The companion was then launched and its sign-in triggered, and its
report ended with:

> A running provider was notified and will re-acquire, so a pinned widget converges without being
> unpinned.

That sentence prints only when `StateChangeSignal.Raise` returns true, meaning `OpenExisting` found the
event and set it. So the cross-process signal crosses from companion to a live listener — which is the
half that did not exist before and the half whose absence caused the convergence defect.

**What was not measured.** The visible transition from a sign-in-required card to an authenticated one.
A token was already cached, so silent acquisition succeeded and there was no failed state to recover
from — the reported expiry was unchanged from the earlier sign-in, confirming the token came from cache
rather than a fresh prompt. Forcing a genuine failure is awkward on this machine: deleting the shared
cache would likely still succeed via the `OperatingSystemAccount` fallback, because the Windows account
here *is* the mailbox account.

So: the signal path is measured, the probe-and-deliver reaction is implementation covered by tests, and
the end-to-end visual transition is not yet observed. It should be recorded when a natural
sign-in-required state next occurs — most likely the first time a token expires beyond what the broker
will silently renew.

**Operational note worth keeping.** The companion's report can be read without a screen capture by
sending `WM_GETTEXT` to its report control — enumerate the main window's children for the `Edit` class.
That returns only this application's own text. Capturing the screen by window rectangle is not a
substitute: the window may not be foreground, and the capture then contains whatever is on top of it.

## Measured: the tenant blocks self-consent, and the setting that does it looks permissive

**Gate 8's self-consent criterion fails on this tenant.** Pressing Sign in reached Entra and returned
the **Approval required** dialog — "This app requires your admin's approval to…" — rather than a
consent prompt. No token was issued.

The tenant's user-consent setting is **"Let Microsoft manage your consent settings (Recommended)"**
with **"Enable user consent for popular Mail clients"** checked. That reads as permissive and is not.
It maps to the Microsoft-managed policy `microsoft-user-allow-default-consent-apps`, which permits user
consent to mail permissions for a **fixed list of six Microsoft-chosen application IDs** — Apple Mail,
Spark Email, eM Client, Android-Samsung, Android-Mail, and Thunderbird. Microsoft owns the list and it
cannot be added to, so a first-party registration of one's own can never self-consent under this
setting. `Mail.ReadBasic` is also not in the low-impact class the policy otherwise allows.

This is the section 19 risk row "tenant user-consent policy blocks self-consent" occurring, not a
defect. The code reached the platform and the platform refused.

**Two readings that look like contradictions and are not:**

- The API permissions blade shows `Admin consent required: No` for `Mail.ReadBasic`. That column
  states the *organization default*, and the blade's own notice says user consent "can be customized
  per permission, user, or app" and that the column "may not reflect the value in your organization".
  The permission does not inherently require admin consent; this tenant's policy withholds it.
- The consent dialog lists **three** items where the registration configures **one**. The other two
  are MSAL's automatic OIDC scopes: "Maintain access to data you have given it access to" is
  `offline_access`, and "View users' basic profile" is `profile` — confirmed by its expanded
  description, "basic profile (e.g., name, picture, user name, email address)". It is **not**
  `User.Read`, which grants the full profile and displays as "Sign in and read user profile". No scope
  beyond `Mail.ReadBasic` is requested or configured.

**Resolution, as an explicit scope decision.** Admin consent was granted for this single registration
as a local unblock for this tenant. It is **not** a change to the product's intended flow: self-consent
remains the designed path, `ApprovalRequired` remains a first-class outcome for environments where it
is blocked, and the tenant consent policy was not modified. Granting it covers delegated
`Mail.ReadBasic` for this app only, which lets a signed-in user read *their own* basic mail; there are
no application permissions on the registration, so no other mailbox becomes reachable.

## Measured: the broker does not distinguish a dismissed approval dialog from a policy block

Two attempts at the same user action — reaching the **Approval required** dialog and dismissing it —
produced **two different results**, and that is the finding.

| Package | Reported status | Signals |
|---|---|---|
| 0.3.5.0 | `Failed` | Not captured; the diagnostic line did not exist yet |
| 0.3.6.0 | `Cancelled` | `MsalClientException · code authentication_canceled` |

**The 0.3.5.0 `Failed` is unexplained, and an earlier version of this section explained it wrongly.**
That version attributed it to consent being matched only by `AADSTS` substring. That cannot be the
cause: `authentication_canceled` was handled by the original classifier and has always mapped to
`Cancelled`. So the first run carried some different signal that was never recorded, and it is not
reproducible now that consent has been granted. It is left recorded as unexplained rather than given a
plausible cause.

**What follows from the pair is more useful than either run alone.** The same gesture yielding
different MSAL errors means the broker does not reliably separate "the user closed the dialog" from
"tenant policy refused". Therefore `ApprovalRequired` **cannot be inferred** from an interactive
failure, and 0.3.6.0's first attempt to do so — mapping `access_denied` to it — was withdrawn before it
shipped as a conclusion:

- Claiming `ApprovalRequired` on an ambiguous signal asserts an administrator is required and removes
  the retry affordance, so a user who merely closed a window is told something false with no way
  forward.
- Reporting `Cancelled` offers the retry and claims nothing that was not observed.

The state is now claimed only on a **definite** signal: MSAL's typed
`UiRequiredExceptionClassification.ConsentRequired`, an Entra consent code, or the OAuth
`consent_required`. It under-reports rather than mislabels. Because that means a real policy block can
surface as `Cancelled`, the cancellation copy carries the distinction the classifier cannot: it says
that a recurring *Approval required* dialog is tenant policy rather than a retryable condition.

This is a **platform limitation, not a satisfied requirement.** Section 8 wants the approval state
distinguishable, and on this broker it is only reliably distinguishable when Entra volunteers a consent
code. That is worth revisiting if a later MSAL or WAM version reports dismissal more precisely.

**One change here was sound and is kept:** classification is now phase-aware. A consent failure means
"go interactive and self-consent" during silent acquisition and "an administrator must approve" during
interactive acquisition, and paired tests assert the same exception classifies differently by phase.

**A status word was not enough to diagnose any of this,** which is the general lesson. There was no way
to discover which signal a failure carried without inspecting it, so the companion now prints a bounded
`Signals:` line — exception type name, MSAL error code, and extracted `AADSTS\d+` tokens. That line is
what produced the table above. Categories only, never `Exception.Message`, and deliberately not routed
through `IOperationalLogger`, whose API has nowhere to put a string and must keep it that way. A test
plants an account address and a Graph URL in a message and asserts neither survives into the output.

## Measured: the companion window's button was clipped, and only looking at it showed that

The companion's new Win32 window was verified three ways before anyone looked at it: it built clean,
it created a real top-level window when run unpackaged, and it created one when launched through
package activation from the installed 0.3.4.0 package. All three passed. A screen capture of the
rendered window showed the **Sign in button cut off at the bottom edge**, and the report box stopping
well short of the right margin.

The cause is the same mistake in both axes. `CreateWindowEx` takes the *outer* window size, including
the caption and border, and the child controls were positioned by subtracting guessed multiples of the
margin from that outer figure. The client area is smaller by an amount that depends on frame metrics,
so the button's lower edge fell past the bottom of it. Fixed in 0.3.5.0 by calling `GetClientRect` and
laying the children out from the real client size, which removes the guess rather than correcting it.

Worth recording for the same reason the two resize defects above are: **a window handle is not a
rendered window.** Every automated check available here — compile, process alive, non-zero
`MainWindowHandle`, correct window title — passed with a visibly broken control, because none of them
inspect pixels. Phase 2 replaces this window with the WinUI one, and the lesson carries: the accepted
widget screenshots were also reviewed by eye, and that is not incidental.

## Measured: the companion does not exit on its own after launching

The companion was reported to open and then close quickly after the 0.3.4.0 install. That was
**not reproduced**, and it is recorded because the negative result is what rules out a defect in the
new message loop.

Three launch routes were tried, and in all three the process created a window and stayed alive:

| Launch route | Result |
|---|---|
| Unpackaged build output, direct | Window created, alive until killed |
| Installed executable under `WindowsApps`, direct | Window created, alive until killed |
| Package activation, `shell:AppsFolder\...!App` | Window created, **alive past 54 seconds** |

No `Application Error` or `.NET Runtime` event was logged for `OutlookWidget.App` in the surrounding
half hour — the only matching event was the expected `Application Hang` for
`OutlookWidget.Provider`, which is deployment terminating it under `-ForceApplicationShutdown`.

The most likely explanation for the original observation is that the launch landed inside the install's
force-shutdown window, when deployment terminates every process belonging to the package — which
includes the companion, not only the provider, and which produces no error log because the process is
killed rather than failing. **This remains an inference; the symptom was not reproduced, so it is not
recorded as explained.** If it recurs outside an install, it is a real defect and this table is the
baseline to compare against.

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

**A Graph request.** Nothing about authentication or the native surface is outstanding any more. Gates 1
through 9 are settled — 8 as a split, the rest as passes — and what remains needs mail to have actually
been read:

- **Gates 10 and 12** need a Graph request to have been issued and its response observed.
  `GraphMailClient` now exists, so the blocker has moved: it is no longer a missing client, it is that
  nothing calls the client. **Do not read the client's existence as progress on either gate** — this is
  exactly the "implemented is not a gate status" rule this document opens with.
- **Gate 11** needs a real refresh to invalidate, so it follows 10.

One piece stands between here and a first real request:

- **`RefreshCoordinator` has to call the client**, inside the section 8 refresh algorithm, and commit
  the resulting snapshot through `ProtectedCache`.

The second prerequisite is done. **The selected MSAL home-account identifier is now recorded**, so
silent acquisition asks for the account the user chose instead of whichever one MSAL enumerates first.
It refuses rather than falling back when the recorded account is no longer cached, because a fallback
there would read a different mailbox and look exactly like success.

**This is implemented and not measured, and the distinction matters more here than usual.** On this
reference machine the account count is 1, so the old first-account behaviour and the new recorded
selection agree by construction and a green run proves nothing about the difference between them. The
unit tests cover the selection rule against real `IAccount` values, which is the part that is ours.
What is unobserved is the write path on a real WAM sign-in: whether `AuthenticationResult.Account`
carries a `HomeAccountId` through the broker on this tenant. If it does not, the file is never written,
the fallback would stand. That is no longer silent in either direction: a write that fails now fails the
sign-in, so the companion reports it rather than claiming success, and with more than one cached account
a missing record refuses instead of guessing. What remains unobserved is the ordinary path — whether the
broker returns a `HomeAccountId` at all on this tenant. **Confirm `account-v1.bin` exists in the
package store after the next companion sign-in** — that is a one-look check and it is the only evidence
this works. Existence is all that can be checked by eye: the record is DPAPI-protected, per section 4
step 6, so its contents are not readable in a text editor.

This section previously listed the manual steps for gates 8 and 9. All of them are done, and the
measurements are recorded above rather than as instructions here.

Two operational notes worth keeping, because they apply to any future card read:

- The provider's lifetime is demand-driven rather than pin-driven, so it may not be running even with a
  widget pinned. Opening the Board activates it.
- The probe runs on a background task started after `CoRegisterClassObject`, so a card can legitimately
  read `silent auth pending` for a moment; if it stays there, the acquisition has not returned rather
  than having failed. It is **no longer once per process** — the provider re-probes on the state-changed
  signal, which is what makes a sign-in from a pinned widget converge without unpinning.

**Reinstalling no longer requires a manual version bump.** It did, and forgetting it produced HRESULT
`0x80073CFB` — hit going from 0.3.3.0 to 0.3.4.0, and recorded in the troubleshooting guide along with why
it is neither an elevation problem nor the running-process case `-ForceApplicationShutdown` exists for.
`Build-Package.ps1` now derives the version, so this is history rather than a step. Elevation is not
needed for upgrades — the certificate is already trusted, so `-SkipCertificateTrust` applies — but a
pinned widget holds the package open, so `-ForceApplicationShutdown` is.

Gates 10 and 12 still need a Graph request to have been issued, so they follow gate 8 as before.

**The shared-cache requirement is no longer an argument from documentation.** MSAL keeps ID tokens and
account metadata in its own cache even when the broker holds the device-bound refresh token, and
Microsoft's documentation is explicit that without persisting it, "restarting the app means that
`GetAccounts` API will miss some of the accounts". The companion and the provider are different
processes, so without a *shared* cache the provider would enumerate no accounts and report
sign-in-required immediately after a successful companion sign-in — and the obvious reading of that
would have been that the zero parent handle failed, which is the opposite of true.

**The file exists and the mechanism is proven end to end.** `msal-v1.bin`, 3510 bytes, in the
coordination root inside the package store, written by the companion's sign-in — and then read by the
*provider*, a different process, which acquired a token from it with a zero parent handle. That is gate
9's pass, and it is simultaneously the measurement that retires this documentation argument: the cache
is created, correctly placed, and actually consumed cross-process.

The registration could not have been avoided by prompting the user. Consent *is* a user prompt in
principle — though not on this tenant, where policy escalated it to an administrator; see the gate 8
finding above — but the registration is the application's identity and is what issues the client ID, and
no token request can be made without one. Entra does not implement OpenID Connect Dynamic Client Registration, so there
was no bootstrap path: creating a registration through Graph would itself require a token.

## The Entra app registration, as created

Recorded 2026-07-28 in the 415 Group tenant. **The raw tenant and client IDs are deliberately absent
from this file**, which is committed; they live in git-ignored package configuration. See
[app-registration.md](app-registration.md) for where, and for why an earlier version of that document
contradicted itself by asking for both here.

| Item | As created |
|---|---|
| Supported account types | Accounts in this organizational directory only — single tenant |
| Application type | Public client, mobile and desktop |
| Redirect URI | `ms-appx-web://microsoft.aad.brokerplugin/{client-id}`, under **Mobile and desktop applications** |
| Allow public client flows | Yes |
| Client secret / certificate | **None** |
| API permission | Microsoft Graph delegated **`Mail.ReadBasic`** only; `User.Read` removed. **Verified in the portal 2026-07-29**, not merely asserted: the API permissions blade reads `Microsoft Graph (1)` with `Mail.ReadBasic` / Delegated / "Read user basic mail" as the sole row |
| Application permissions | **None** |
| Admin consent | **Granted 2026-07-29**, after gate 8 measured that this tenant blocks self-consent. It was deliberately withheld until then, for the reason below |

The redirect URI platform matters and is a silent failure if wrong: current Microsoft documentation
states WAM redirect URIs must be configured under *Mobile and desktop applications*, and a
registration that places the same string under the Web platform simply never completes brokered
sign-in.

**Admin consent was withheld until first sign-in, then granted because the gate required it.** Both
halves of that were decisions, and the order mattered.

Withholding it was a gate decision rather than an oversight. Gate 8 asks whether the author can
self-consent to `Mail.ReadBasic` without an administrator step. Granting tenant-wide admin consent up
front would have pre-approved the permission, the self-consent path would never have executed, and the
gate would have become unprovable while appearing to work. **That sequencing is what makes the finding
below trustworthy**: the tenant refused self-consent under observation, rather than the question being
quietly skipped.

Granting it afterwards was a scope decision, recorded above with the measurement. It covers delegated
`Mail.ReadBasic` for this registration only, so a signed-in user reads *their own* basic mail; there are
no application permissions, so no other mailbox becomes reachable. It is **not** a change to the intended
flow — self-consent remains the designed path and `ApprovalRequired` remains a first-class outcome for
tenants that permit it.

One consequence for anyone reproducing gate 8: **it cannot be re-measured on this tenant now.** Consent
is granted, so a sign-in succeeds regardless of which path would have been taken, and the companion
deliberately no longer claims otherwise. Reproducing the self-consent question needs a tenant where
consent has not been granted.

### Configuration, and what it deliberately cannot change

The identifiers ship as `authentication.json` beside **both** executables — verified present in the
installed 0.3.0.0 package — so neither process walks a relative path out of its own directory to find
them. Two values are **not** configurable, and adding them to the file changes nothing because the
loader has nowhere to put them:

- **The scope.** `Mail.ReadBasic` is a compile-time constant, so no file on the machine can widen
  what this application may read. A test writes a configuration file requesting `Mail.Read`,
  `Mail.ReadWrite`, `User.Read`, a `common` authority, and a client secret, and asserts that every
  one of them is ignored.
- **The authority.** Derived from the tenant ID, so no file can redirect sign-in to `common`,
  `organizations`, or another tenant — which would quietly turn a single-tenant registration into a
  multi-tenant one.

`Build-Package.ps1` refuses to build when the configuration is missing or still contains the
template's placeholder zeros, and the loader rejects an all-zero GUID at runtime as well. Every load
failure is a state rather than an exception: a provider the Widgets host started in the background
must not die because the package shipped without configuration.

The large-size card reports `config Loaded` so the packaged configuration is observable on the
device without the raw identifiers ever appearing on a surface someone could read over a shoulder.
**Observed on the reference machine** in the installed 0.3.2.0 package: the widget's large card shows
`config Loaded`, so the file ships correctly, the loader finds it beside the provider executable, and
both identifiers parsed to non-empty GUIDs. That is the whole configuration path proven end to end
short of a token request.

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
| 0.2.2.0 → **0.2.3.0**, a real version upgrade | **Yes** | Succeeded with `-ForceApplicationShutdown`; the script's pre-install notice fired correctly, the pin survived, and the widget still renders afterwards |
| 0.3.3.0 → 0.3.3.0, same version, **different contents** | Yes | **Failed** with `0x80073CFB`, and failed identically elevated. A rebuilt payload under an unchanged version is refused outright. This is a *different* failure from the first row and has a different fix — bump the manifest version, do not uninstall. See the troubleshooting guide |
| 0.3.3.0 through 0.3.11.0, then **0.3.15.3** (first auto-derived version), ten upgrades | **Yes** each time | All succeeded with `-SkipCertificateTrust -ForceApplicationShutdown` from a **non-elevated** session, confirming the certificate stays trusted across upgrades and that elevation is a first-install requirement only. The widget pin survived every one. The version jump from 0.3.11.0 to 0.3.15.3 is the switch to git-height derivation, not a skipped release |

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

*0.2.2.0 → 0.2.3.0 was verified differently, and stops one step short.* It established the
operational half — a live provider, the pre-install notice, and `-ForceApplicationShutdown` carrying
the upgrade through with the pin intact. Afterwards the provider was confirmed to COM-activate from a
cold start and to create both named notification events. **The widget was then confirmed to still
render on 0.2.3.0.**

What that render does and does not establish is worth being exact about, because the difference is
the one this report got wrong once already. It establishes that the upgrade did not break the pinned
widget, which is the outcome that matters operationally. It does **not** by itself prove the 0.2.3.0
provider delivered that card: the Widgets host retains the last card it was given, the card is
visually identical between the two versions, and no provider process was running when the package
version was checked afterwards — provider lifetime is demand-driven. Distinguishing a fresh delivery
from a retained card needs the companion launched from the widget to report a `0.2.3.0` package
identity, which is the check that made the 0.2.1.0 → 0.2.2.0 upgrade end to end. That was not done
here.

Nothing depends on it. Gate 3's package-update case is carried by the 0.2.2.0 observation above, and
this install was run to validate a code change rather than to re-prove the gate — a purpose the cold
activation check already served. Recorded at this precision so the two installs are not read as one
stronger result than either is.

**One thing the render does establish, and it is the reason the install happened.** The delivery
sink's composition changed in this build — it now takes the disclosure read as a delegate and
re-checks before every host call. A widget that still renders is evidence that the new constructor
wiring works in the real Widgets host rather than only under `CoCreateInstance`, which is the part a
unit test could not reach.

## Measured: the widget screenshot was the wrong size, and nothing failed because of it

The picker documentation specifies the widget screenshot as **300 pixels wide and 304 tall, with
transparent rounded corners, showing the medium size of the widget**. The original asset was
**480×480 and opaque**.

Nothing failed. Gate 2 passed with it, because the picker accepts the image and stretches it into
its own 300×304 slot — so a wrong-sized asset renders as a plain block that looks like the provider
supplied no preview at all. This is the third defect in this project whose only symptom was
"it looks slightly wrong", after the clipped small card and the dropped diagnostic newlines. None
was reachable by a unit test; all three needed either a documented requirement to check against or
a person looking at a screen.

A test now reads each screenshot's PNG `IHDR` and asserts 300×304, so the dimension cannot drift
back. It reads the bytes directly rather than taking an imaging dependency in the test project.

## Measured: qualified asset variants do nothing without a `resources.pri`

The icon assets were also single-resolution. Adding `scale-125/150/200/400` and
`targetsize-16/24/32/48/256` files is not sufficient on its own: Windows resolves the manifest's
`Assets\Square44x44Logo.png` to that literal file unless the package carries a `resources.pri`
indexing the qualifiers. A Visual Studio packaging project runs MakePri for this; packaging directly
with the SDK tools means running it explicitly, which `Build-Package.ps1` now does.

Verified in the installed 0.3.1.0 package: `resources.pri` present, 32 asset files, and MakePri
reported 15 scale variants and 10 target sizes indexed with zero warnings.

The flat single-colour placeholder had a specific visible failure worth recording: an unplated blue
square on the taskbar's blue plate reads as a missing icon.

### The first replacement was also wrong, in a way only visible in place

The first redraw made the icon a **self-contained rounded tile** — a white envelope on a blue
gradient square — reasoning that an icon supplying its own background renders correctly whether or
not Windows draws a plate. Seen in the Add Widgets picker's provider list next to Weather, Traffic,
Timer, Watchlist and the rest, it looked dated: **every neighbour in that list is a glyph on
transparency with no plate**, and the filled square read as an icon from an older design era.

Two things this established, neither of which was reachable from documentation:

1. **`ProviderIcons/Icon` is what the picker's provider list draws.** Not obvious from the element
   name, and it is the icon a user sees before they have pinned anything. The manifest now says so.
2. **The reasoning "self-contained is safer" was backwards for this surface.** It optimised for
   never looking wrong against an unknown background and ignored what the icon sits beside.

The icon is now a gradient envelope on full transparency with no tile: body and flap as separate
gradients with a translucent seam between them for depth, and an amber unread badge in the corner the
envelope leaves free. The seam and badge are dropped below 32px rather than scaled, because the
taskbar size is seen most often and both become noise there. Colours avoid white as a primary fill,
since the icon has to read on a dark taskbar and a light Start flyout alike — which is what the
white-on-blue tile got away with only by supplying its own background.

### Decision: screenshots accepted, app icon not

Reviewed on 2026-07-28. The outcome was split, and an earlier version of this section recorded it as
a clean acceptance of everything — which was wrong within hours and is corrected here rather than
left to mislead.

**The widget picker screenshots are accepted.** That turns a preview into an obligation: the
screenshot depicts the approved medium card — unread count plus the newest messages — which the
provider **does not render yet**. Having been accepted as shipping artwork it is the design
reference the medium card is expected to match when Phase 2 builds it, not merely an illustration.
Until then the package advertises a card more finished than the one it draws, which is normal for a
store preview and worth knowing rather than discovering.

**The app icon is not accepted.** Three designs were rejected in sequence:

| Attempt | Rejected because |
|---|---|
| White envelope on a filled blue tile | Looked dated beside the glyph-on-transparency icons around it in the picker's provider list |
| Flatter gradient envelope on transparency | Not liked; no specific fault recorded |
| Open envelope with a card rising out of it | Not liked; discarded before packaging |

The icon is being designed outside this repository and will be supplied later. **What ships now is
interim** — the second attempt, which is what the installed 0.3.2.0 package carries. Phase 2 owes
the replacement, so section 18 no longer claims otherwise.

415 Group branding remains **declined**; that half of the decision holds. The open question is the
icon's design, not whether it carries a company mark.

Worth naming the pattern rather than just the outcome: three of these iterations were spent
discovering that an icon can only be judged in place, next to its neighbours, at the size it is
actually drawn. Dimensions, transparency, and resource indexing were all verifiable from
documentation and all correct; none of that had any bearing on whether the thing looked right.

## Measured: the provider fell back to unpackaged state storage instead of failing closed

Found by review. `PackageIdentity.TryGetFamilyName` returns `null` for an unpackaged process by
design, and `CoordinationPaths.Resolve` accepts `null` and answers with the ordinary per-user path by
design. The provider's startup treated only a *thrown* `PackageIdentityException` as fatal, so a null
family name passed straight through into `Resolve`, the resulting directories were created, and the
process carried on — placing coordination state at `%LocalAppData%\OutlookWidget`, outside the package
store, where uninstall cannot remove it.

Both composed behaviours were individually correct. The fault was the composition, and it sat
**directly underneath a comment explaining why that exact fallback is unacceptable**. The reasoning
was right; the null case was simply never covered.

**Confirmed on the reference machine.** Running the provider executable directly from its build
output, where it genuinely has no package identity, created
`%LocalAppData%\OutlookWidget\suppression-v1` and then kept running. Nothing today writes mailbox
data there, so no privacy guarantee has actually been broken — but the location would have become the
cache once authentication landed.

Fixed by moving the composition behind `PackagedState.Locate` in `OutlookWidget.Packaging`, which
rejects a null or empty family name **before resolving a path at all**, so a refusing process never
computes an unpackaged location and never touches the filesystem. `Unpackaged` and
`IdentityQueryFailed` are distinct statuses: both fail closed, but one means the executable was
started directly and the other means a broken query, and they send an operator to different places.
The provider now exits 2 and 1 respectively.

After the fix, the same direct run **exits 2 and creates nothing**, while packaged COM activation
still succeeds and still creates state under
`%LocalAppData%\Packages\415Group.OutlookInboxWidget_dgbvqhastx60y\...`.

`PackagedState` lives in `OutlookWidget.Packaging` rather than the core, and Packaging now references
Core rather than the reverse — the core must stay free of any knowledge that MSIX exists, which is
why `CoordinationPaths.Resolve` takes the family name as a parameter in the first place. Six unit
tests cover both refusal paths through an injectable identity query, which is the only way to reach
them: a test process cannot acquire package identity, cannot make the Win32 query fail on demand, and
cannot launch the COM server.

A source-level test additionally asserts the provider never calls `CoordinationPaths.Resolve`
directly, so the two behaviours cannot be recombined at a new call site.

## Measured: solution builds and project builds write to different output directories

Found while verifying the fix above, and worth recording because it produced a false negative that
looked exactly like the fix not working.

| Command | Provider output |
|---|---|
| `dotnet build` (solution) | `bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\` |
| `dotnet build src\OutlookWidget.Provider\...csproj` | `bin\Debug\net10.0-windows10.0.26100.0\win-x64\` |

The split comes from the solution declaring `<Platform Project="x64" />` for this project, which
solution-level builds honour and project-level builds do not. So a solution build leaves the
`bin\Debug` copy untouched, and running the executable from there can execute a binary many hours
old while a successful build scrolls past. That is what happened: the first verification run appeared
to show the guard having no effect, when it was running yesterday's binary.

Two lessons, recorded because the second is the more dangerous:

1. When running a provider executable by hand, check its timestamp or build the project explicitly
   first. `dotnet build` succeeding does not mean the copy you are about to run was rebuilt.
2. **Searching a .NET assembly for a type name must use UTF-8.** The first attempt to confirm whether
   the guard was compiled in searched with `-Encoding unicode` and reported `False` for assemblies
   that did contain it, because assembly metadata strings are UTF-8. A verification method that
   silently reports absence is worse than no verification, and this one briefly redirected the
   investigation toward a build problem that did not exist.

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

**That assumption is now fully retired.** The broker skeleton exists too — `BrokerClient`,
`SilentAuthService`, and the provider's `SilentAuthProbe` — and gate 9 passes, so neither half of the
slice 1 estimate's assumption is still outstanding.

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
