# Outlook Inbox Widget

A glanceable Windows 11 widget showing Microsoft 365 Inbox counts and the newest few email
messages, without opening Outlook.

**Status: early implementation.** The cross-process coordination core is built and tested.
There is no installable package yet, and no widget appears on a Widgets Board yet. See
[docs/phase0-evidence.md](docs/phase0-evidence.md) for exactly what has and has not been
proven, and [TECHNICAL_PLAN.md](TECHNICAL_PLAN.md) for the full design.

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
  interactive authentication and cannot open a browser.
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
src/OutlookWidget.Core      Surface-agnostic core: coordination, caching, auth, Graph, models
src/OutlookWidget.App       WinUI 3 companion for sign-in, settings, diagnostics  (not yet built)
src/OutlookWidget.Provider  Widgets Board provider, packaged COM local server     (not yet built)
src/OutlookWidget.Package   MSIX identity, widget and COM registration            (not yet built)
tests/                      Automated tests, including the concurrency suite
docs/                       Evidence report, app registration, troubleshooting
scripts/                    Preflight and Outlook-launch probes
```

## Build and test

Requires the .NET 10 SDK. Producing an MSIX additionally requires the Windows SDK, which
supplies `makeappx.exe` and `signtool.exe`.

```bash
dotnet test
```

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
  pending marker. The companion commits state and signals; it never delivers.
- **Disclosure suppression is one file per operation,** deleted only by its own operation. A
  shared file cannot be safely reclaimed, because "read the owner, then delete if it matches"
  is not an atomic conditional delete.

Each of those has a test asserting the invariant, and in several cases a source-level check.
