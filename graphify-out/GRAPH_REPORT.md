# Graph Report - Outlook Widget  (2026-07-28)

## Corpus Check
- 49 files · ~57,732 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 579 nodes · 1283 edges · 28 communities (26 shown, 2 thin omitted)
- Extraction: 83% EXTRACTED · 17% INFERRED · 0% AMBIGUOUS · INFERRED: 222 edges (avg confidence: 0.81)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `1bbc9574`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- MutationLock
- OutlookWidget.Core (shared .NET library)
- .RefreshAsync
- .SeedState
- OutlookWidget.Core.Refresh
- DisclosureTombstoneStore
- Outlook Inbox Widget
- .Current
- Program
- .Start
- CoordinationStaticAnalysisTests
- Single-flight as expiring record
- OutlookWidget.Core.csproj
- Sources of Truth
- Outlook Widget v1
- New-PlaceholderAssets.ps1
- Gate 7 — Outlook launch resolution
- Cross-Process Coordination
- Disclosure Tombstones
- Final Convergence Privacy Guarantee
- DeliveryWorker
- RecordingLogger

## God Nodes (most connected - your core abstractions)
1. `DisclosureTombstoneStore` - 27 edges
2. `DisclosureTombstoneTests` - 26 edges
3. `RefreshCoordinatorTests` - 26 edges
4. `OutlookWidget.Core.Refresh` - 24 edges
5. `DeliveryWorkerTests` - 18 edges
6. `ProtectedCacheTests` - 18 edges
7. `MutationLock` - 17 edges
8. `ProtectedCache` - 16 edges
9. `DeliveryWorker` - 16 edges
10. `RefreshCoordinator` - 16 edges

## Surprising Connections (you probably didn't know these)
- `Cache is discarded and refetched, never migrated` --semantically_similar_to--> `No telemetry; logging API shape prevents sensitive data`  [INFERRED] [semantically similar]
  docs/troubleshooting.md → README.md
- `RecordingLogger` --implements--> `IOperationalLogger`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/TestInfrastructure/CoordinationFixture.cs → src/OutlookWidget.Core/Diagnostics/IOperationalLogger.cs
- `CoordinationFixture` --references--> `DisclosureTombstoneStore`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/TestInfrastructure/CoordinationFixture.cs → src/OutlookWidget.Core/Refresh/DisclosureTombstoneStore.cs
- `Unread count vs message list mismatch is expected` --conceptually_related_to--> `Delegated Mail.ReadBasic-only scope`  [INFERRED]
  docs/troubleshooting.md → README.md
- `Refresh lease indicator with 30-second ceiling` --references--> `Single-flight as expiring record`  [INFERRED]
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

## Communities (28 total, 2 thin omitted)

### Community 0 - "MutationLock"
Cohesion: 0.06
Nodes (31): byte, JsonSerializerOptions, Mutex, string, CoordinationPaths, int, CacheCommitResult, IDataProtector (+23 more)

### Community 1 - "OutlookWidget.Core (shared .NET library)"
Cohesion: 0.05
Nodes (66): AbandonedMutexException Recovery, Adaptive Cards Schema 1.5 Rendering, 20-Second Async Refresh Deadline, Bounded Atomic Replace Retry Ladder, Boot-Session Discriminator for Lease Expiry, Cached-First, Activation-Driven Refresh Model, Signature Timestamping Decision, Approval/Consent-Required Authorization State (+58 more)

### Community 2 - ".RefreshAsync"
Cohesion: 0.11
Nodes (23): CancellationToken, Lock, long, Task, TimeSpan, DeliveryRequestOutcome, IDeliveryRequester, IRefreshFetcher (+15 more)

### Community 3 - ".SeedState"
Cohesion: 0.16
Nodes (7): Fact, Func, TimeSpan, DeliveryWorkerTests, Fact, ProtectedCacheTests, TimeSpan

### Community 4 - "OutlookWidget.Core.Refresh"
Cohesion: 0.13
Nodes (12): OutlookWidget.Core.Tests.TestInfrastructure, OutlookWidget.Core.Tests, OutlookWidget.Core.Diagnostics, OutlookWidget.Core.Refresh, OutlookWidget.Core.Delivery, OutlookWidget.App, OutlookWidget.Core.Caching, CacheCommitStatus (+4 more)

### Community 5 - "DisclosureTombstoneStore"
Cohesion: 0.07
Nodes (17): Action, ActiveOperationRegistry, Dictionary, HashSet, TimeSpan, DisclosureMode, bool, Func (+9 more)

### Community 6 - "Outlook Inbox Widget"
Cohesion: 0.07
Nodes (34): Client/tenant IDs supplied via package configuration, never committed, Consent-blocked vs Graph HTTP 403 distinction, Entra ID app registration (single-tenant public client), Permission minimization (remove User.Read, forbid Mail.Read), Author self-consent to delegated Mail.ReadBasic, WAM broker redirect URI carrying the client ID, Build-Package.ps1, CoordinationPathsTests (+26 more)

### Community 8 - ".Current"
Cohesion: 0.14
Nodes (13): TimeSpan, BootSessionStamp, DateTimeOffset, ISystemClock, SystemClock, CancellationToken, LeaseClaim, Fact (+5 more)

### Community 9 - "Program"
Cohesion: 0.17
Nodes (8): IntPtr, LibraryImport, Program, STAThread, Fact, string, CoordinationPathsTests, uint

### Community 10 - ".Start"
Cohesion: 0.21
Nodes (4): Fact, MutationMutexTests, Fact, StateCommitCoordinatorTests

### Community 11 - "CoordinationStaticAnalysisTests"
Cohesion: 0.19
Nodes (5): IEnumerable, TimeSpan, CoordinationBounds, Fact, CoordinationStaticAnalysisTests

### Community 12 - "Single-flight as expiring record"
Cohesion: 0.16
Nodes (15): IWidgetDeliverySink has no production implementation, Deliberate roadmap deviation — slice 1 built before review, Refresh lease indicator with 30-second ceiling, Refresh and delivery accounted separately, Sign-out reported failure under mutex contention, Message details suppressed — ordered causes and manual recovery, Real cross-process concurrency test suite, Final convergence, not retraction (+7 more)

### Community 13 - "OutlookWidget.Core.csproj"
Cohesion: 0.14
Nodes (12): Microsoft.Identity.Client, Microsoft.Identity.Client.Broker, Microsoft.NET.Test.Sdk, System.Security.Cryptography.ProtectedData, xunit.runner.visualstudio, xunit.v3, net10.0-windows, Microsoft.NET.Sdk (+4 more)

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

### Community 27 - "DeliveryWorker"
Cohesion: 0.07
Nodes (21): CancellationTokenSource, IDisposable, ManualResetEventSlim, Process, SemaphoreSlim, bool, Lock, long (+13 more)

### Community 28 - "RecordingLogger"
Cohesion: 0.17
Nodes (11): Count, Id, Outcome, OperationalEventId, OperationalOutcome, TimeSpan, NullOperationalLogger, IReadOnlyList (+3 more)

## Ambiguous Edges - Review These
- `Tray/Popover Fallback Surface` → `Widgets Policy Preflight (AllowNewsAndInterests)`  [AMBIGUOUS]
  TECHNICAL_PLAN.md · relation: conceptually_related_to

## Knowledge Gaps
- **27 isolated node(s):** `net10.0-windows`, `Microsoft.NET.Sdk`, `OutlookWidget.App`, `CacheReadStatus`, `CacheCommitStatus` (+22 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **2 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `Tray/Popover Fallback Surface` and `Widgets Policy Preflight (AllowNewsAndInterests)`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **Why does `OutlookWidget.Core.Refresh` connect `OutlookWidget.Core.Refresh` to `MutationLock`, `.RefreshAsync`, `DisclosureTombstoneStore`, `.Current`, `CoordinationStaticAnalysisTests`?**
  _High betweenness centrality (0.086) - this node is a cross-community bridge._
- **Why does `CoordinationFixture` connect `MutationLock` to `.RefreshAsync`, `.SeedState`, `OutlookWidget.Core.Refresh`, `DisclosureTombstoneStore`, `.Current`, `DeliveryWorker`, `RecordingLogger`?**
  _High betweenness centrality (0.060) - this node is a cross-community bridge._
- **Why does `DisclosureTombstoneStore` connect `DisclosureTombstoneStore` to `MutationLock`, `.Current`, `DeliveryWorker`, `OutlookWidget.Core.Refresh`?**
  _High betweenness centrality (0.052) - this node is a cross-community bridge._
- **What connects `net10.0-windows`, `Microsoft.NET.Sdk`, `OutlookWidget.App` to the rest of the system?**
  _27 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `MutationLock` be split into smaller, more focused modules?**
  _Cohesion score 0.06153846153846154 - nodes in this community are weakly interconnected._
- **Should `OutlookWidget.Core (shared .NET library)` be split into smaller, more focused modules?**
  _Cohesion score 0.05081585081585081 - nodes in this community are weakly interconnected._