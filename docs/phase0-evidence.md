# Phase 0 evidence report

Status: **in progress.** Device and toolchain preflight is complete. Every gate that
requires a signed MSIX is **blocked** on missing packaging tooling, and one is blocked on
elevation. Nothing below is a projection — each row records what was actually observed, and
unproven items say so rather than being marked pass.

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
| Visual Studio | **Not installed.** No `vswhere.exe` present |
| Windows SDK packaging tools | **Not installed.** No `makeappx.exe` under `Windows Kits\10\bin` |
| Developer Mode | Off (`AllowDevelopmentWithoutDevLicense` absent) |
| Elevation | Not available in the session that ran the preflight |
| `LocalMachine\TrustedPeople` | Readable, 0 certificates |
| Repository location | OneDrive-backed, per the revised section 12. Root pinned **Always keep on this device** (`P` set, `U` cleared) |
| Private key material in tree | None |

Three risk rows from section 19 are already retired on this machine: Widgets is not disabled
by policy, New Outlook is present, and the pinned Windows App Runtime is present.

## Gate status

Gates are grouped per section 17, because the group determines what a failure means.

### Universal product gates

| Gate | Status | Evidence |
|---|---|---|
| 1 — signed MSIX installs; certificate can be trusted | **Blocked** | Requires `makeappx.exe`/`signtool.exe` to produce and sign the package, and elevation to place the public certificate in `LocalMachine\TrustedPeople`. Neither is available yet. The store is readable and empty; whether device policy permits *adding* a certificate is proven only by attempting it elevated |
| 8 — WAM sign-in with MFA/CA, self-consent to `Mail.ReadBasic` | **Not started** | Needs the Entra registration and the companion app |
| 10 — `Mail.ReadBasic` returns exactly the approved properties | **Not started** | Needs the registration and a token |

A failure in this group stops the product rather than triggering the tray fallback, because
the fallback is also a packaged MSIX using the same certificate and the same delegated
permission.

### Native-surface gates

| Gate | Status | Evidence |
|---|---|---|
| 2 — discoverable and pinnable in the Widgets Board | **Blocked** | Needs an installed package |
| 3 — provider cold activation after reboot and package update | **Blocked** | Needs an installed package |
| 4 — `GetWidgetInfos()` restores all instances; final-instance exit | **Blocked** | Needs an installed package |
| 5 — two instances at different sizes render independently | **Blocked** | Needs an installed package |
| 6 — widget action launches the companion | **Blocked** | Needs an installed package |
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

## Signing decisions

**Not yet recorded, and required before the first install.** Section 15 asks for the
certificate Subject, its validity period and key-retention location, the manifest
`Publisher`, the stated continuity position, and the timestamping decision. None of these
can be filled in until a certificate exists, and the certificate cannot be created usefully
until the packaging toolchain is installed.

The plan's recommended posture is to accept remove-and-reinstall as the stated v1 position
while keeping the MSIX persistent-identity option alive for free — which costs only a
long-lived development certificate whose private key is retained outside the repository and
outside OneDrive.

## What is blocking, and what unblocks it

1. **Windows SDK** — provides `makeappx.exe` and `signtool.exe`. Without them no MSIX can be
   produced or signed, so every packaging and Widgets-Board gate is unreachable.
2. **Elevation, once** — to place the public signing certificate in
   `LocalMachine\TrustedPeople`. Whether managed-device policy permits this is itself gate 1
   evidence and cannot be assumed either way.
3. **Entra app registration** — single-tenant, public client, delegated `Mail.ReadBasic`
   only, no secret. Gates 8, 9, 10, and 12 all wait on it.

Notably **not** blocking: provider source code. A probe build confirmed that
`Microsoft.WindowsAppSDK` 2.3.1 and the widget provider API
(`Microsoft.Windows.Widgets.Providers.WidgetManager.GetDefault().GetWidgetInfos()`) compile
with the .NET 10 SDK alone, with no Visual Studio installed. The provider lifecycle skeleton
can therefore be written and compiled now; only installing and activating it is gated on the
packaging toolchain.

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
