# Phase 0 evidence report

Status: **in progress.** Device and toolchain preflight is complete and **gate 1 passes** —
a signed MSIX installs on the author's Entra-managed PC and the certificate can be trusted
there. The remaining native gates need the widget provider registration, which is
deliberately not yet in the manifest. Nothing below is a projection — each row records what
was actually observed, and unproven items say so rather than being marked pass.

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
| 2 — discoverable and pinnable in the Widgets Board | **Not started** | No longer blocked on tooling. The gate 1 manifest deliberately omits the widget provider registration, so there is nothing to discover yet. Needs the `com:Extension` COM server and the `uap3:AppExtension` widget registration |
| 3 — provider cold activation after reboot and package update | **Not started** | Needs the provider and its COM registration |
| 4 — `GetWidgetInfos()` restores all instances; final-instance exit | **Not started** | Needs the provider |
| 5 — two instances at different sizes render independently | **Not started** | Needs the provider |
| 6 — widget action launches the companion | **Not started** | Needs the provider |
| 9 — provider silent-only acquisition with a zero parent handle | **Not started** | Needs the registration and the broker skeleton |
| 11 — cached-first refresh and cross-process invalidation | **Partly established, in isolation** | The cross-process coordination subsystem is implemented and passing 88 automated tests, including genuine multi-process contention. That is not this gate: the gate requires the behaviour across real Board activation and provider recycle, which needs an installed package |

### Gate 7 — Outlook launch, which spans both groups

| Half | Status | Evidence |
|---|---|---|
| Universal — does bare `olk.exe` resolve and launch | **Resolution proven, launch not yet exercised** | `olk.exe` resolves to `%LocalAppData%\Microsoft\WindowsApps\olk.exe`. Package activation also resolves, as `Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows`. Run `scripts/Test-OutlookLaunch.ps1 -Launch` to exercise the launch itself |
| Native — can a Board action launch it | **Blocked** | Needs an installed provider |

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

1. **Entra app registration** — single-tenant, public client, delegated `Mail.ReadBasic` only,
   no secret. Gates 8, 9, 10, and 12 all wait on it. Settings are in
   [app-registration.md](app-registration.md).
2. **Widget provider registration** — the `com:Extension` COM server plus the
   `uap3:AppExtension` widget registration, and the provider itself. Gates 2 through 6 wait on
   it. This is code and manifest work, not a tooling or policy dependency.

No longer blocking, having been resolved during this work:

- **Windows SDK.** Installed as 10.0.26100; `makeappx.exe` and `signtool.exe` both resolve.
  Packaging is done directly with these tools rather than through a Visual Studio packaging
  project, so no VS installation is needed at all.
- **Elevation and certificate trust.** Proven to work on this managed device.
- **Provider source compilation.** A probe build confirmed `Microsoft.WindowsAppSDK` 2.3.1 and
  `WidgetManager.GetDefault().GetWidgetInfos()` compile with the .NET 10 SDK alone.

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

This is a real deviation and it carries one risk worth naming: slice 1's estimate assumed the
Phase 0 provider lifecycle and broker skeleton would be retained and evolved in place. That
skeleton does not exist yet, so the delivery worker's `IWidgetDeliverySink` currently has no
production implementation and the coordination layer has never run inside a real provider
process. Nothing in slice 1 depends on it for correctness, but the integration is unproven.
