# Graph Report - Outlook Widget  (2026-07-28)

## Corpus Check
- 65 files · ~76,509 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 746 nodes · 1598 edges · 30 communities (27 shown, 3 thin omitted)
- Extraction: 85% EXTRACTED · 15% INFERRED · 0% AMBIGUOUS · INFERRED: 246 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `3f1dfbad`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- DeliveryWorker
- OutlookWidget.Core (shared .NET library)
- .RefreshAsync
- .SeedState
- OutlookWidget.Core.Refresh
- DisclosureTombstoneStore
- Outlook Inbox Widget
- WidgetProvider
- .Current
- PackageIdentity
- .Start
- CoordinationStaticAnalysisTests
- PackageManifestTests
- OutlookWidget.Core.csproj
- Sources of Truth
- Outlook Widget v1
- New-PlaceholderAssets.ps1
- Gate 7 — Outlook launch resolution
- Cross-Process Coordination
- Disclosure Tombstones
- Final Convergence Privacy Guarantee
- .Record
- ProviderFactory
- ProviderCardTests

## God Nodes (most connected - your core abstractions)
1. `OutlookWidget.Core.Refresh` - 28 edges
2. `DisclosureTombstoneStore` - 27 edges
3. `DisclosureTombstoneTests` - 26 edges
4. `RefreshCoordinatorTests` - 26 edges
5. `CoordinationStaticAnalysisTests` - 19 edges
6. `IOperationalLogger` - 18 edges
7. `DeliveryWorkerTests` - 18 edges
8. `ProtectedCacheTests` - 18 edges
9. `OutlookWidget.Core.Caching` - 17 edges
10. `DeliveryWorker` - 17 edges

## Surprising Connections (you probably didn't know these)
- `Cache is discarded and refetched, never migrated` --semantically_similar_to--> `No telemetry; logging API shape prevents sensitive data`  [INFERRED] [semantically similar]
  docs/troubleshooting.md → README.md
- `CoordinationFixture` --references--> `DisclosureTombstoneStore`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/TestInfrastructure/CoordinationFixture.cs → src/OutlookWidget.Core/Refresh/DisclosureTombstoneStore.cs
- `LeaseObservingRequester` --implements--> `IDeliveryRequester`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/RefreshCoordinatorTests.cs → src/OutlookWidget.Core/Refresh/RefreshCoordinator.cs
- `CoordinationFixture` --references--> `RefreshLeaseStore`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/TestInfrastructure/CoordinationFixture.cs → src/OutlookWidget.Core/Refresh/RefreshLeaseStore.cs
- `Unread count vs message list mismatch is expected` --conceptually_related_to--> `Delegated Mail.ReadBasic-only scope`  [INFERRED]
  docs/troubleshooting.md → README.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Outlook Widget Coordination Safety** — agents_cross_process_coordination, agents_expiring_refresh_lease, agents_disclosure_tombstones, agents_fail_closed_disclosure, agents_provider_only_delivery, agents_final_convergence [EXTRACTED 1.00]
- **Outlook Widget Security Boundaries** — agents_mail_readbasic_boundary, agents_split_authentication, agents_stable_package_identity [EXTRACTED 1.00]
- **Cross-Process Coordination Correctness Layer** — technical_plan_mutation_mutex, technical_plan_refresh_lease, technical_plan_disclosure_tombstone, technical_plan_protected_cache, technical_plan_serialized_delivery_worker, technical_plan_snapshot_generation, technical_plan_boot_session_discriminator [EXTRACTED 1.00]
- **Delegated Secretless Authentication Flow** — technical_plan_entra_app_registration, technical_plan_interactive_auth_service, technical_plan_silent_auth_service, technical_plan_wam_broker, technical_plan_mail_readbasic, technical_plan_consent_required_state [EXTRACTED 1.00]
- **Phase 0 Gate Classification and Branching** — technical_plan_phase0_gates, technical_plan_universal_product_gates, technical_plan_native_surface_gates, technical_plan_web_only_open_outlook_mode, technical_plan_tray_popover_fallback, technical_plan_phase0_evidence_report [EXTRACTED 1.00]
- **Disclosure-suppression privacy flow** — readme_suppress_before_state_change, readme_one_suppression_file_per_operation, readme_twenty_four_hour_staleness_suppression, docs_troubleshooting_suppression_ordered_causes, docs_troubleshooting_sign_out_reported_failure [INFERRED 0.85]
- **Load-bearing coordination invariants** — readme_mutationlock_ref_struct, readme_mandatory_mutex_timeout, readme_single_flight_expiring_record, readme_provider_only_updatewidget, readme_one_suppression_file_per_operation, readme_cross_process_concurrency_suite [EXTRACTED 1.00]
- **MSIX signing, publisher identity, and packaging chain** — docs_phase0_evidence_signing_decisions, docs_phase0_evidence_publisher_subject_quoting, docs_phase0_evidence_build_package_ps1, docs_phase0_evidence_msbuild_comma_path_constraint, docs_phase0_evidence_gate_1_signed_msix_install, docs_troubleshooting_install_failures [INFERRED 0.85]

## Communities (30 total, 3 thin omitted)

### Community 0 - "DeliveryWorker"
Cohesion: 0.07
Nodes (26): Detail, Headline, Instance, SemaphoreSlim, bool, CancellationTokenSource, Lock, long (+18 more)

### Community 1 - "OutlookWidget.Core (shared .NET library)"
Cohesion: 0.05
Nodes (66): AbandonedMutexException Recovery, Adaptive Cards Schema 1.5 Rendering, 20-Second Async Refresh Deadline, Bounded Atomic Replace Retry Ladder, Boot-Session Discriminator for Lease Expiry, Cached-First, Activation-Driven Refresh Model, Signature Timestamping Decision, Approval/Consent-Required Authorization State (+58 more)

### Community 2 - ".RefreshAsync"
Cohesion: 0.12
Nodes (22): CancellationToken, Lock, long, Task, TimeSpan, DeliveryRequestOutcome, IDeliveryRequester, IRefreshFetcher (+14 more)

### Community 3 - ".SeedState"
Cohesion: 0.16
Nodes (7): Fact, Func, TimeSpan, DeliveryWorkerTests, Fact, ProtectedCacheTests, TimeSpan

### Community 4 - "OutlookWidget.Core.Refresh"
Cohesion: 0.07
Nodes (20): OutlookWidget.Core.Tests.TestInfrastructure, OutlookWidget.Core.Tests, OutlookWidget.Provider, OutlookWidget.Packaging, OutlookWidget.Core.Diagnostics, OutlookWidget.Core.Refresh, OutlookWidget.Core.Delivery, OutlookWidget.Core.Launching (+12 more)

### Community 5 - "DisclosureTombstoneStore"
Cohesion: 0.08
Nodes (16): ActiveOperationRegistry, HashSet, DisclosureMode, Action, bool, Dictionary, Func, Guid (+8 more)

### Community 6 - "Outlook Inbox Widget"
Cohesion: 0.05
Nodes (49): Client/tenant IDs supplied via package configuration, never committed, Consent-blocked vs Graph HTTP 403 distinction, Entra ID app registration (single-tenant public client), Permission minimization (remove User.Read, forbid Mail.Read), Author self-consent to delegated Mail.ReadBasic, WAM broker redirect URI carrying the client ID, Build-Package.ps1, CoordinationPathsTests (+41 more)

### Community 7 - "WidgetProvider"
Cohesion: 0.07
Nodes (25): IWidgetProvider, Func, IEnumerable, ProcessStartInfo, string, OutlookLauncher, OutlookLaunchResult, OutlookLaunchStrategy (+17 more)

### Community 8 - ".Current"
Cohesion: 0.09
Nodes (19): TimeSpan, BootSessionStamp, DateTimeOffset, ISystemClock, SystemClock, Guid, JsonSerializerOptions, LeaseRecord (+11 more)

### Community 9 - "PackageIdentity"
Cohesion: 0.08
Nodes (20): Exception, IdentityKind, IntPtr, LibraryImport, uint, Program, int, LibraryImport (+12 more)

### Community 10 - ".Start"
Cohesion: 0.09
Nodes (17): EventWaitHandle, IDisposable, Process, Action, bool, CancellationTokenSource, long, Thread (+9 more)

### Community 11 - "CoordinationStaticAnalysisTests"
Cohesion: 0.12
Nodes (8): TimeSpan, CoordinationBounds, Fact, IEnumerable, string, CoordinationStaticAnalysisTests, IEnumerable, RepositorySources

### Community 12 - "PackageManifestTests"
Cohesion: 0.25
Nodes (5): Fact, string, PackageManifestTests, XDocument, XElement

### Community 13 - "OutlookWidget.Core.csproj"
Cohesion: 0.11
Nodes (17): net10.0-windows10.0.26100.0, Microsoft.Identity.Client, Microsoft.Identity.Client.Broker, Microsoft.NET.Test.Sdk, Microsoft.WindowsAppSDK, System.Security.Cryptography.ProtectedData, xunit.runner.visualstudio, xunit.v3 (+9 more)

### Community 14 - "Sources of Truth"
Cohesion: 0.33
Nodes (6): Graphify Working Agreement, docs/phase0-evidence.md, Phase-Gated Delivery, Platform Evidence Model, Sources of Truth, TECHNICAL_PLAN.md

### Community 15 - "Outlook Widget v1"
Cohesion: 0.33
Nodes (6): Mail.ReadBasic Boundary, Native Widget Architecture, Outlook Widget v1, Companion Interactive and Provider Silent Authentication, Surface-Independent Core, AGENTS.md Instructions

### Community 16 - "New-PlaceholderAssets.ps1"
Cohesion: 0.73
Nodes (5): Get-BigEndianBytes(), Get-Crc32(), Get-ZlibStream(), New-PngChunk(), New-SolidPng()

### Community 17 - "Gate 7 — Outlook launch resolution"
Cohesion: 0.67
Nodes (4): Gate 7 — Outlook launch resolution, OutlookLauncher two launch candidates, no versioned path, Outlook will not open — no Classic fallback, weblink behaviour, scripts/Test-OutlookLaunch.ps1 launch probe

### Community 18 - "Cross-Process Coordination"
Cohesion: 0.67
Nodes (3): Cross-Process Coordination, Expiring Refresh Lease, Stable Package Identity

### Community 28 - ".Record"
Cohesion: 0.06
Nodes (36): byte, Count, Id, Mutex, Outcome, string, CoordinationPaths, int (+28 more)

### Community 32 - "ProviderFactory"
Cohesion: 0.24
Nodes (7): PreserveSig, Func, Guid, int, IntPtr, IClassFactory, ProviderFactory

## Ambiguous Edges - Review These
- `Tray/Popover Fallback Surface` → `Widgets Policy Preflight (AllowNewsAndInterests)`  [AMBIGUOUS]
  TECHNICAL_PLAN.md · relation: conceptually_related_to

## Knowledge Gaps
- **34 isolated node(s):** `net10.0-windows`, `Microsoft.NET.Sdk`, `OutlookWidget.App`, `CacheReadStatus`, `CacheCommitStatus` (+29 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **3 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `Tray/Popover Fallback Surface` and `Widgets Policy Preflight (AllowNewsAndInterests)`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **Why does `OutlookWidget.Core.Refresh` connect `OutlookWidget.Core.Refresh` to `.RefreshAsync`, `DisclosureTombstoneStore`, `.Current`, `CoordinationStaticAnalysisTests`, `.Record`?**
  _High betweenness centrality (0.099) - this node is a cross-community bridge._
- **Why does `OutlookWidget.Core.Tests.TestInfrastructure` connect `OutlookWidget.Core.Refresh` to `.Start`, `CoordinationStaticAnalysisTests`?**
  _High betweenness centrality (0.066) - this node is a cross-community bridge._
- **Why does `IOperationalLogger` connect `.Record` to `DeliveryWorker`, `.RefreshAsync`, `DisclosureTombstoneStore`, `WidgetProvider`, `.Current`, `.Start`?**
  _High betweenness centrality (0.048) - this node is a cross-community bridge._
- **What connects `net10.0-windows`, `Microsoft.NET.Sdk`, `OutlookWidget.App` to the rest of the system?**
  _34 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `DeliveryWorker` be split into smaller, more focused modules?**
  _Cohesion score 0.06685633001422475 - nodes in this community are weakly interconnected._
- **Should `OutlookWidget.Core (shared .NET library)` be split into smaller, more focused modules?**
  _Cohesion score 0.05081585081585081 - nodes in this community are weakly interconnected._