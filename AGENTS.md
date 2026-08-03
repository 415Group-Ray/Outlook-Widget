# AGENTS.md

Guidance for AI coding agents working in this repository.

## What this project is

This is a single-user Windows 11 Outlook inbox widget for one Microsoft 365 tenant. The
approved v1 direction is a packaged Win32 Windows Widgets provider plus a small companion
application, with a tray/popover surface only if the native Phase 0 gates fail.

The repository is in early implementation. Phase 0 is approved and **every native-surface gate has
passed on the reference machine** — the widget is discoverable, pinnable, renders at all
three sizes, survives a reboot with its instance restored, survives a package upgrade while pinned,
launches New Outlook and the companion, and its provider exits when unpinned. Gate 4 is fully verified,
`CustomState` round trip included. **Gate 9 passes**: the provider acquired a token silently with a zero
parent handle, observed as `silent auth Acquired` on the pinned card.

**The surface decision is settled on evidence.** Gate 9 was the one gate that could have reopened it,
and it passed, so the tray/popover fallback branch is closed. Do not build it. **Gate 11 now passes**
on installed package 0.4.13.2: stale activation refreshed, provider recycle recovered the existing pin
and fresh cache without another Graph request, and a separate process's state-change signal reached the
provider. Every native-surface gate now passes.

**Gate 8 is split, and this matters for any future deployment.** Brokered WAM sign-in passes. **Self-consent
does not** on the reference tenant: "Let Microsoft manage your consent settings" permits user consent to
mail permissions only for a fixed list of six Microsoft-chosen mail clients, so an administrator granted
consent for this registration. That grant was a local unblock and **not** a change to the intended flow
— self-consent remains the designed path and `ApprovalRequired` remains a first-class outcome. Do not
reduce gate 8 to "PASS", and do not make admin consent a documented prerequisite.

Note one permanent operational consequence: **the provider holds the package open while a widget is
pinned**, so upgrades require `Add-AppxPackage -ForceApplicationShutdown`. Do not advise unpinning
instead; that loses the pin for no reason.

**The package version is derived, not edited.** `Build-Package.ps1` stamps Build and Revision at package
time — Build from git commit height, Revision from a per-commit build counter at
`src\OutlookWidget.Package\.package-version.json` — and it does **not** modify the tracked manifest. That
counter deliberately sits beside the package **project** and not in `AppPackages`, which is build output
and exists to be deletable; do not "tidy" it into the output directory. Major and minor in `Package.appxmanifest` remain a
deliberate decision; the Build and Revision digits there are placeholders and editing them achieves
nothing.

This replaced a manual bump that had become a per-commit obligation, because **any commit changes every
assembly in the package**: the .NET SDK embeds the git commit SHA in each assembly's informational
version, so even a docs-only commit produces a different payload under the same version and the install
fails with `0x80073CFB`. If you do hit that code, the fix is still never to uninstall — that loses the
pin. Measured; see the evidence report.

The cross-process coordination core is implemented and tested, and **authentication now exists**: the
companion signs in interactively through WAM and the provider acquires silently, for the account the user
actually chose rather than whichever one MSAL enumerates first. **Logout is implemented and measured on
installed package 0.4.19.0**: the companion suppresses first, removes the selected account from the app's
MSAL cache, commits a durable signed-out marker plus cleared mailbox state, and then clears only its own
tombstone. Cold provider activation left the cleared generation and app-local token cache unchanged, so
the signed-out marker blocked the operating-system-account fallback. The local commit must replace the
selected identifier last, preserving it across any earlier mutation failure so retry cannot broaden into
removing every cached account. Account switching remains unbuilt.

**The narrow production refresh is wired and measured.** Stale activation, post-sign-in convergence,
manual actions, and the opportunistic five-minute active timer call `GraphMailClient` through
`MailboxRefreshFetcher` and `RefreshCoordinator`, then commit a validated snapshot. The timer is one
provider-wide opportunity, starts with the first active instance, and stops after the last deactivation;
that lifecycle has automated coverage and is measured on installed package 0.4.18.0: the cache advanced
after 324.6 seconds active, then remained unchanged for 340.8 seconds after deactivation while the same
provider process stayed alive. Gate 10 and gate 11 pass. Gate 12's filter syntax, header independence,
and warm latency pass; comparing its returned Focused count with New Outlook remains manual. The
provider still renders the Phase 0 placeholder card rather than the cached mail; the settings-change
trigger also remains a later slice.

The current companion is a packaging and authentication probe with a minimal Win32 window and Sign in,
Sign out, and Clear interrupted operations controls, not the finished WinUI experience. A failed sign-out
must complete its in-process suppression handle without deleting the marker, so the explicit recovery
control can remove that orphan without touching a disclosure operation still in flight.

Two authentication invariants that are easy to break and were each already broken once:

- **Interactive authentication lives in `OutlookWidget.App`, not the core** — a deviation from the plan's
  section 12, made so the provider cannot link the interactive API at all. Three source-level tests hold
  it.
- **Classification is phase-aware.** A consent failure means "go interactive" when silent and "an
  administrator must approve" when interactive. Getting this wrong once made the product refuse to prompt
  a user who could have self-consented.

## Sources of truth

Use these in order and reconcile discrepancies rather than silently choosing one:

1. `TECHNICAL_PLAN.md` — approved scope, architecture, invariants, phase gates, and acceptance
   criteria.
2. `docs/phase0-evidence.md` — what has actually been measured or proven on the target device.
3. Current code and tests — implemented behavior.
4. `README.md` and the remaining `docs/` files — user-facing status, commands, limitations,
   registration, and troubleshooting.

Do not treat a planned behavior as implemented or a unit test as proof of Windows, Widgets
host, New Outlook, WAM, tenant-policy, or installed-package behavior. When sources disagree,
identify the mismatch and update every affected source as part of the approved change.

## Repository map

- `src/OutlookWidget.Core` — surface-independent caching, coordination, refresh, delivery,
  launching, diagnostics, and **authentication** (`BrokerClient`, `SilentAuthService`,
  `AuthenticationFailures`, `SelectedAccountStore`, the shared token cache), plus `Graph/`
  (`GraphMailClient`, `GraphResponseReader`) and `Models/` (`MailboxSnapshot`, `MessagePreview`,
  `MailboxReadout`). Interactive authentication is deliberately **not** here; see below.
- `src/OutlookWidget.Packaging` — MSIX package-identity interop only, shared by the two
  executables so the core stays free of any knowledge that MSIX exists. Do not grow it into a
  general utility assembly.
- `src/OutlookWidget.App` — packaged companion application; a Phase 0 probe with a minimal Win32
  window, and the home of `InteractiveAuthService` — **the single `AcquireTokenInteractive` call site
  in the product.** It is here rather than in the core so the provider cannot link the interactive API
  at all; see the deviation note above.
- `src/OutlookWidget.Provider` — the packaged COM Windows Widgets provider: lifecycle, the six
  callbacks, the instance registry, the single `UpdateWidget` call site, and `SilentAuthProbe`, which
  acquires silently with a zero parent handle and re-probes on the state-changed signal. The provider
  owns the narrow background Graph refresh composition, while widget callbacks only enqueue work.
  **Authentication remains silent-only**; it must never gain an interactive path.
- `src/OutlookWidget.Package` — MSIX identity, assets, COM server and widget registration.
- `tests/OutlookWidget.Core.Tests` — unit, source-level, and genuine cross-process tests.
- `scripts` — prerequisites, signing, packaging, installation, asset, and Outlook-launch
  probes.
- `docs` — measured evidence, Entra registration, and troubleshooting.
- `graphify-out` — generated knowledge-graph outputs; never edit them by hand.

## Commands

```powershell
dotnet build
dotnet test
pwsh -File scripts/Test-PackagePrerequisites.ps1
pwsh -File scripts/Build-Package.ps1
graphify update .
```

- Run focused tests while iterating, and both `dotnet build` and the full `dotnet test` before
  claiming a code change complete.
- **`dotnet test` does not compile `OutlookWidget.Provider`.** The test project deliberately does
  not reference it — doing so would force the test project onto the provider's
  Windows-SDK-versioned target framework — so provider compile errors pass a green test run. This
  has already happened once. `dotnet build` on the solution is the check that catches it, and it is
  not optional.
- **Solution builds and project builds put the provider in different directories.** The solution
  declares `<Platform Project="x64" />` for it, so `dotnet build` writes
  `bin\x64\Debug\...\win-x64\` while `dotnet build <the csproj>` writes `bin\Debug\...\win-x64\`.
  Running the executable by hand from the wrong one executes a stale binary while a successful build
  scrolls past. Check the timestamp, or build the project explicitly first.
- Changes to packaging, signing, the manifest, assets, package identity, or certificate
  handling also require the relevant prerequisite and package-build checks.
- Record device-, tenant-, Widgets-host-, WAM-, New Outlook-, and installed-package results in
  `docs/phase0-evidence.md`. Never claim an unperformed manual gate passed.
- Warnings are errors. Preserve the repository analyzers, static checks, and central package
  management in `Directory.Packages.props`.

## Non-negotiable engineering invariants

These are load-bearing security, privacy, and concurrency rules. Preserve their tests and add
coverage when changing nearby behavior.

1. Never hold `System.Threading.Mutex` or `MutationLock` across `await`. `MutationLock` remains
   a `ref struct` so unsafe capture fails at compile time.
2. Every mutex acquisition is bounded. Never introduce parameterless `WaitOne()`.
3. Cross-process refresh single-flight uses the expiring lease record, not a long-held mutex,
   watchdog, background service, or reliance on process lifetime.
4. Disclosure-reducing operations write their own tombstone before attempting the state
   mutation. They clear only that tombstone, and only after a successful commit.
5. Unreadable or ambiguous disclosure state fails closed. Lease-state corruption follows the
   separately documented availability-oriented behavior; do not make the two policies
   symmetric.
6. Only the provider may call `WidgetManager.UpdateWidget`. The companion commits state and
   signals; it never delivers widget content.
7. Widget delivery remains outside the refresh transaction and lease bound.
8. Delivery is serialized and coalesced. Each pass re-reads the current snapshot and generation
   before rendering, and re-reads the disclosure tombstone immediately before **every** host call
   rather than once per pass — a reduction observed mid-pass abandons the remaining instances
   instead of sending them. Snapshot staleness only delays convergence; disclosure staleness
   discloses, and a call not yet made can still be withheld.
9. The privacy guarantee is final convergence, not retraction of an update already handed to
   the Widgets host. Do not claim a stronger guarantee without measured platform evidence.
10. Cache and coordination state remain scoped to the current Windows user and stable package
    identity. Sensitive cache content remains protected with DPAPI. **A process without package
    identity must refuse to run rather than resolve state.** `PackageIdentity.TryGetFamilyName`
    returns null when unpackaged and `CoordinationPaths.Resolve` accepts null and answers with the
    per-user path — both correct alone, and a silent fallback outside the package store when
    composed. Go through `PackagedState.Locate`, which rejects a null identity before resolving any
    path; never call `CoordinationPaths.Resolve` from an executable. A source-level test enforces
    this for the provider.
11. The provider's COM class ID appears in exactly three places and they must agree:
    `Program.ProviderClassId`, the manifest's `com:Class Id`, and the widget extension's
    `CreateInstance ClassId`. A mismatch installs cleanly and then fails activation with nothing
    surfaced in the Widgets Board. `Build-Package.ps1` and `PackageManifestTests` both check it.

Do not weaken, delete, skip, or rewrite a safety test merely to make a change pass. Prefer the
smallest coherent fix that preserves existing contracts, and avoid broad refactors while
platform gates are still being proved.

## Privacy, authentication, and security boundaries

- Request only delegated `Mail.ReadBasic`. Do not add `Mail.Read`, application permissions,
  tenant-wide consent, client-secret authentication, or another Graph permission without an
  explicit architecture and scope decision.
- Never request, cache, render, or log message bodies, `bodyPreview`, attachments, recipient
  lists, access tokens, raw Graph responses, or other mailbox content outside the approved
  fields. **The approved set is the plan's, not a shorter paraphrase of it.** Earlier wording
  here said "sender/subject/received-time/read-state" and read as exhaustive; it is not.
  Sections 6, 7, and 8 of `TECHNICAL_PLAN.md` also approve `id`, `inferenceClassification`,
  and `webLink` in the message query, and section 8 caches `webLink` — which section 9's
  "open an individual message" depends on, so removing it would delete an approved feature
  rather than tighten a boundary. Only `webLink` is retained beyond the four display fields;
  `id` and `inferenceClassification` are requested and not cached. Widening past that list, in
  the query or the cache, still needs an explicit scope decision.
- Interactive authentication belongs only in the companion. Provider authentication is
  silent-only and fails closed; it must not open a browser or display authentication UI.
- Do not add telemetry, a hosted backend, webhooks, startup tasks, scheduled tasks, services,
  or persistent background processes without explicit approval.
- Signing private keys must remain outside Git and outside OneDrive. Never add a private-key
  artifact to the repository, logs, test output, or a support artifact.
- The MSIX manifest `Publisher` must exactly match the signing certificate subject. Treat the
  package name, publisher, certificate retention, and package version as durable identity
  decisions.
- New Outlook is the only supported client. Do not introduce a Classic Outlook fallback.

## Scope and phase gates

- Each phase ends with the user review required by `TECHNICAL_PLAN.md`; do not begin the next
  phase merely because its work is convenient or adjacent.
- Do not expand v1 to multiple users, managed/Intune or RMM deployment, enterprise signing,
  Store publication, a production Entra registration, tenant-wide consent, or broader
  mailbox access without explicit approval.
- Do not build and maintain both a full native widget and a full tray/popover implementation
  in v1. Follow the native-gate fallback decision in the plan.
- A request to review authorizes inspection and findings, not edits. When asked to review,
  make no file, Git, PR, or external-system changes unless the user separately requests them.

## Platform evidence and documentation

- For Windows Widgets, Windows App SDK, MSIX, Microsoft Graph, MSAL/WAM, New Outlook, and
  Windows policy behavior, verify changeable technical claims against current official
  Microsoft documentation.
- Label claims as documented behavior, locally measured behavior, assumption, or unresolved
  proof-of-concept gate. Do not turn observations into general platform guarantees.
- Update `docs/phase0-evidence.md` when measured platform results change.
- Update `TECHNICAL_PLAN.md` when approved architecture, scope, risks, gates, or acceptance
  criteria change.
- Update `README.md` and relevant troubleshooting/registration documentation in the same
  change when current behavior, support, limitations, prerequisites, commands, or recovery
  steps change.

## graphify working agreement

This repository has a knowledge graph in `graphify-out/`.

- For architecture, dependency, data-flow, or cross-file questions, query the existing graph
  first with `graphify query`, `graphify explain`, or `graphify path`, then verify important
  conclusions against current source and tests.
- Run `graphify update .` after meaningful changes to source, tests, scripts, manifests,
  dependencies, or architecture documentation. Typo-only and formatting-only changes do not
  require a refresh.
- Never hand-edit generated graphify artifacts.
- Do not claim a graph-affecting change complete until the update succeeds. If it cannot run,
  report that explicitly rather than implying the graph is current.
- A post-commit graphify hook is optional and is not sufficient by itself because it ignores
  documentation and image changes.

## Dependencies and implementation discipline

- Keep package versions centralized in `Directory.Packages.props`; do not pin versions in
  individual project files.
- Use stable Windows App SDK packages only. Preview or Experimental dependencies require
  explicit approval and new platform validation.
- Keep the core surface-independent. UI, package activation, and Widgets-host details do not
  belong in `OutlookWidget.Core` unless exposed through a narrow interface.
- Preserve x64-only v1 unless ARM64 work is explicitly approved and tested on an ARM64 device.
- Preserve deterministic builds, nullable analysis, warnings-as-errors, and the repository's
  existing style.
- Build outputs, packages, certificates, test results, local settings, and signing material
  remain untracked.

## Git and pull-request completion

- Preserve unrelated user changes in a dirty worktree. Inspect the diff before staging or
  committing.
- Do not commit, push, open or merge a pull request, publish a package, install on another
  machine, or change external configuration unless the user requests that action.
- Before a commit or PR, exclude signing material, package artifacts, local configuration,
  test output, generated temporary files, and unrelated changes.
- For requested PR review fixes: address every actionable thread, validate the result, push
  the fix, reply with the commit and validation performed, resolve the thread, and re-fetch
  thread-aware state to confirm no actionable unresolved threads remain.
- Merging is separate authorization even after every review thread and check is resolved.
