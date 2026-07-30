# Outlook Inbox Widget

A glanceable Windows 11 widget showing Microsoft 365 Inbox counts and the newest few email
messages, without opening Outlook.

**Status: early implementation, native surface proven, brokered sign-in working.** The cross-process
coordination core is built and tested, and a signed MSIX registers a COM widget provider that appears
in the Widgets Board, pins, renders at small, medium, and large, survives both a reboot and a package
upgrade with its instance restored, launches New Outlook and the companion from its actions, and exits
when unpinned. **Every native-surface Phase 0 gate except gate 11 has passed on the reference
machine**, so the tray/popover fallback is not being built.

**Sign-in works, and gate 8 split.** The companion has a real window and acquires a delegated
`Mail.ReadBasic` token through WAM — measured on the reference tenant. But **self-consent does not
work there**: the tenant's Microsoft-managed consent policy permits user consent to mail permissions
only for a fixed list of Microsoft-chosen mail clients, so an administrator had to grant consent for
this registration. The gate asks two questions and they got different answers, so it is neither a pass
nor a fail. See [docs/phase0-evidence.md](docs/phase0-evidence.md).

**Gate 9 passes.** The provider acquired a token silently with a zero parent handle — observed on the
pinned card as `silent auth Acquired`, in a different process from the one that signed in. That was the
last gate that could have sent this design to a tray/popover surface, so **that branch is now closed on
evidence rather than on expectation.** It also proves the shared token cache works cross-process: the
companion wrote the account metadata and the provider found it.

**Gate 11** — cached-first refresh and cross-process invalidation across a real host — remains open and
cannot be observed until there is something to refresh. It is not a surface-choice gate; both surfaces
consume the same coordination core, so it would be equally unproven either way.

**No Microsoft Graph request has been made yet, so the provider still draws a placeholder card
describing coordination and authentication state — nothing here shows real mail.** The Graph client,
the response validation, and the cached snapshot model are now built and unit-tested, but nothing calls
them: a refresh has to, and that is the next slice. Treat the client's existence as code rather than as
mail on the card. See the evidence report for exactly what has and has not been proven, and
[TECHNICAL_PLAN.md](TECHNICAL_PLAN.md) for the full design.

### Known platform limitation: one widget instance only

The Widgets Board was measured to allow only one pinned instance per widget definition, despite the
package declaring `AllowMultiple="true"`. Once **Outlook Inbox** is pinned, its picker entry is
greyed out and marked as added. Size and active state are still tracked per instance rather than
globally, because that is a correctness requirement and the constraint is the host's, not this
package's.

## What it does

- Shows **Inbox unread** and total counts, plus the newest three to five messages: sender,
  subject, received time, and read state.
- Renders cached content immediately, then refreshes when activation and platform lifetime
  permit.
- Offers user-initiated paths into New Outlook, or into Outlook on the web for an individual
  message.

## What it deliberately does not do

- **No message bodies.** It requests delegated `Mail.ReadBasic` only, which excludes bodies,
  body previews, attachments, recipient lists, and extended properties. Not shown, not
  cached, not requested.
- **No authentication UI outside the companion app.** The widget provider can only acquire
  tokens silently and fails closed to a "sign in required" card. It has no code path to
  interactive authentication and cannot open a browser. This is enforced by the assembly
  reference graph rather than by convention: the single `AcquireTokenInteractive` call site
  lives in the companion, which the provider does not reference at all.
- **No access or refresh token in any file this app writes.** WAM keeps the refresh token
  device-bound inside the broker, and access tokens are never persisted.

  MSAL's shared cache — which the two processes need so the provider can find the account the
  companion signed in — **does** hold ID tokens along with account metadata. An ID token is a
  token, so that is stated rather than glossed: it is a signed record of who signed in, not a
  credential for reading mail, and possessing it does not grant mailbox access. It is
  DPAPI-protected under the current user and sits inside the package store, so uninstall removes
  it.
- **No telemetry.** Nothing leaves the device. Local operational logs record an event name, a
  status category, a duration, and a count — the logging API has no parameter capable of
  accepting a sender, subject, account, link, or token, which is enforced by its shape rather
  than by filtering.
- **No background process.** No startup task, service, scheduled task, webhook, or hosted
  backend. Refresh is cached-first and activation-driven.

## Privacy behaviour

- The cached snapshot is encrypted with Windows DPAPI under the current user.
- The small widget size always shows counts only. A "hide message details" setting applies
  counts-only rendering at every size.
- After 24 hours without a successful refresh, message details are suppressed rather than
  presenting old subjects as current.
- Logging out or switching accounts **suppresses display before it changes state**, so a
  failure part-way through cannot leave the previous account's subjects on screen.

### Known limitation: privacy-state rendering can be delayed

A widget update already handed to the Windows Widgets host cannot be recalled. If the host is
slow or wedged, content read just before a logout, account switch, or privacy change can still
appear briefly, and with a wedged host noticeably later, before the follow-up update replaces
it.

The guarantee is **final convergence**: the last content delivered always reflects the newest
committed state. It is not retraction. This window is narrow by construction — content is read
immediately before each host call rather than captured earlier — but it is not zero, and this
is stated rather than claimed away.

## Supported environment

- Windows 11 24H2, build 26100 or later, x64.
- New Outlook (`Microsoft.OutlookForWindows`). Classic Outlook is explicitly out of scope and
  has no code path.
- A Microsoft 365 mailbox in a single tenant, accessed with delegated permission.
- The Widgets Board must not be disabled by the `AllowNewsAndInterests` policy or the
  **Allow widgets** Group Policy.

Check a machine before building:

```bash
pwsh -File scripts/Test-PackagePrerequisites.ps1
```

## Repository layout

```text
src/OutlookWidget.Core       Surface-agnostic coordination, caching, refresh, delivery, launching
src/OutlookWidget.Packaging  MSIX package-identity interop, shared by the two executables
src/OutlookWidget.App        Packaged companion; Phase 0 probe plus the only interactive sign-in
src/OutlookWidget.Provider   Packaged COM Widgets provider; lifecycle and delivery, no mail yet
src/OutlookWidget.Package    MSIX identity, assets, COM server and widget registration
tests/                       Automated tests, including the concurrency suite
docs/                        Evidence report, app registration, troubleshooting
scripts/                     Preflight, signing, packaging, installation, asset, launch probes
graphify-out/                Generated knowledge-graph artifacts; never hand-edited
```

Icon and widget picker assets are generated by `scripts/New-Assets.ps1`, which is the source of
truth for what it draws — change the drawing code rather than editing the committed PNGs. The
widget picker screenshots are accepted for v1. **The app icon is interim**: its design is being
settled separately, so expect it to be replaced. 415 Group branding is declined either way.

`OutlookWidget.Packaging` exists because two executables need the package family name and
`OutlookWidget.Core` may not be the one to supply it: the core is deliberately free of any
knowledge that MSIX exists, and `CoordinationPaths.Resolve` takes the family name as a parameter
for that reason. The alternative was two copies of the same two-call Win32 buffer protocol.

## Build and test

Requires the .NET 10 SDK. Producing an MSIX additionally requires the Windows SDK, which
supplies `makeappx.exe`, `signtool.exe`, and `makepri.exe`. `Test-PackagePrerequisites.ps1` checks
for all three; MakePri indexes the scale- and targetsize-qualified icon assets, and without it the
package build stops.

Packaging also needs **PowerShell 7.6 or later**, because it runs on .NET 10. This is a requirement on
the *host*, not on the SDK: `Build-Package.ps1` loads the built `OutlookWidget.Core` assembly to
validate the authentication configuration with the product's own loader, so the host runtime must be
at least as new as the framework Core targets. PowerShell 7.0 through 7.4 run on .NET 3.1 to 8 and
cannot load it, even with a .NET 10 SDK installed — the SDK builds the assembly, the host has to run
it. The preflight and the build script both check this, and the required version is read from Core's
project file rather than hardcoded.

```bash
dotnet test
```

**`dotnet test` does not compile the provider,** so run `dotnet build` on the solution too. The test
project deliberately does not reference `OutlookWidget.Provider` — referencing it would force the
tests onto the provider's Windows-SDK-versioned target framework — which means a provider compile
error survives a green test run.

```bash
dotnet build
```

To produce, sign, and install the package:

```bash
pwsh -File scripts/Build-Package.ps1
```

```bash
pwsh -File scripts/Install-DevelopmentPackage.ps1
```

The first install on a machine must be elevated, to trust the development certificate in
`LocalMachine\TrustedPeople`. Later upgrades do not, because the certificate is already trusted.
Pass `-SkipCertificateTrust` to install an upgrade from an ordinary session.

**Once a widget is pinned, upgrades need `-ForceApplicationShutdown`:**

```bash
pwsh -File scripts/Install-DevelopmentPackage.ps1 -SkipCertificateTrust -ForceApplicationShutdown
```

The provider process runs for as long as a widget is pinned, and Windows will not replace a package
whose processes are running — the install fails with HRESULT `0x80073D02`, whose message names the
package rather than the process. Terminating the provider mid-update is safe by design; see
[docs/troubleshooting.md](docs/troubleshooting.md).

The suite includes real cross-process concurrency tests: they spawn peer processes that hold
the coordination mutex, kill them mid-commit to abandon it, stall a fake widget host, and step
a simulated clock across a reboot. They are not mocks of those situations — mocking the
coordination primitives would test the mock.

## Design notes worth knowing before changing the core

The coordination subsystem is small but every part of it is load-bearing, and several
constraints are enforced mechanically because they are easy to break by accident:

- **`MutationLock` is a `ref struct`.** `System.Threading.Mutex` is thread-affine, so a named
  mutex held across an `await` can fail to release even inside a correct `try/finally`. Being
  a `ref struct` makes the compiler reject capture in a closure or across an `await`.
- **Every mutex acquisition passes a timeout.** The parameterless `WaitOne()` is prohibited
  and a test fails the build if one appears.
- **Single-flight is an expiring record, not a held lock.** A killed owner leaves a record that
  expires; there is no watchdog and no reliance on `AbandonedMutexException`.
- **Only the provider calls `UpdateWidget`,** from one serialized worker with a coalescing
  pending marker, in exactly one file. The companion commits state and signals; it never
  delivers. Source-level tests assert both that the core never names the call and that exactly
  one provider file does.
- **The provider's COM class ID appears in three places** — the provider source, the manifest's
  COM server registration, and the widget extension's activation entry. A mismatch installs
  cleanly and then fails to activate with nothing surfaced in the Widgets Board, so the build
  script and a test both check the three agree.
- **Disclosure suppression is one file per operation,** deleted only by its own operation. A
  shared file cannot be safely reclaimed, because "read the owner, then delete if it matches"
  is not an atomic conditional delete.
- **Interactive authentication lives in the companion, not the core.** The plan originally filed
  it under the core; putting it there would make the provider link an assembly containing
  `AcquireTokenInteractive`, leaving a source grep as the only barrier. Three source-level tests
  hold the boundary: none in the core, none in the provider, and exactly one file in the
  companion.
- **The provider passes `BrokerClient.NoParentWindow`,** a named member rather than an inline
  `() => IntPtr.Zero`, so the zero handle gate 9 tests is searchable and cannot be chosen by
  accident. A test asserts the provider builds its client no other way.

Each of those has a test asserting the invariant, and in several cases a source-level check.
