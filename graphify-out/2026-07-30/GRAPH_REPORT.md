# Graph Report - Outlook Widget  (2026-07-29)

## Corpus Check
- 82 files · ~119,190 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 967 nodes · 2156 edges · 42 communities (39 shown, 3 thin omitted)
- Extraction: 88% EXTRACTED · 12% INFERRED · 0% AMBIGUOUS · INFERRED: 256 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `28152d77`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- AuthenticationOutcomeTests
- OutlookWidget.Core (shared .NET library)
- .RefreshAsync
- .SeedState
- OutlookWidget.Core.Refresh
- DisclosureTombstoneStore
- Outlook Inbox Widget
- WidgetProvider
- .Current
- .Record
- .Start
- CoordinationStaticAnalysisTests
- PackageManifestTests
- OutlookWidget.Core.csproj
- Sources of Truth
- Outlook Widget v1
- New-Assets.ps1
- Gate 7 — Outlook launch resolution
- Cross-Process Coordination
- Disclosure Tombstones
- Final Convergence Privacy Guarantee
- AuthenticationConfigurationTests
- CoordinationFixture
- ProtectedCache
- CoordinationPaths
- PackageIdentity
- ProviderFactory
- ProviderCardTests
- DeliveryWorker
- .Locate
- SkeletonCard
- StateCommitCoordinator
- CompanionWindow
- ProtectedCache.cs
- .Raise
- MutationLock

## God Nodes (most connected - your core abstractions)
1. `CompanionWindow` - 31 edges
2. `OutlookWidget.Core.Refresh` - 30 edges
3. `DisclosureTombstoneStore` - 27 edges
4. `DisclosureTombstoneTests` - 26 edges
5. `RefreshCoordinatorTests` - 26 edges
6. `OutlookWidget.Core.Caching` - 24 edges
7. `OutlookWidget.Core.Diagnostics` - 24 edges
8. `IOperationalLogger` - 24 edges
9. `AuthenticationOutcomeTests` - 23 edges
10. `CoordinationStaticAnalysisTests` - 23 edges

## Surprising Connections (you probably didn't know these)
- `Cache is discarded and refetched, never migrated` --semantically_similar_to--> `No telemetry; logging API shape prevents sensitive data`  [INFERRED] [semantically similar]
  docs/troubleshooting.md → README.md
- `CoordinationFixture` --references--> `CoordinationPaths`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/TestInfrastructure/CoordinationFixture.cs → src/OutlookWidget.Core/Caching/CoordinationPaths.cs
- `CoordinationFixture` --references--> `ProtectedCache`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/TestInfrastructure/CoordinationFixture.cs → src/OutlookWidget.Core/Caching/ProtectedCache.cs
- `FakeWidgetHost` --references--> `DeliveryState`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/TestInfrastructure/FakeWidgetHost.cs → src/OutlookWidget.Core/Delivery/IWidgetDeliverySink.cs
- `FakeWidgetHost` --implements--> `IWidgetDeliverySink`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/TestInfrastructure/FakeWidgetHost.cs → src/OutlookWidget.Core/Delivery/IWidgetDeliverySink.cs

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

## Communities (42 total, 3 thin omitted)

### Community 0 - "AuthenticationOutcomeTests"
Cohesion: 0.08
Nodes (19): IAccount, CancellationToken, IPublicClientApplication, Task, InteractiveAuthService, Exception, string, AuthenticationFailures (+11 more)

### Community 1 - "OutlookWidget.Core (shared .NET library)"
Cohesion: 0.05
Nodes (66): AbandonedMutexException Recovery, Adaptive Cards Schema 1.5 Rendering, 20-Second Async Refresh Deadline, Bounded Atomic Replace Retry Ladder, Boot-Session Discriminator for Lease Expiry, Cached-First, Activation-Driven Refresh Model, Signature Timestamping Decision, Approval/Consent-Required Authorization State (+58 more)

### Community 2 - ".RefreshAsync"
Cohesion: 0.11
Nodes (23): CancellationToken, Lock, long, Task, TimeSpan, DeliveryRequestOutcome, IDeliveryRequester, IRefreshFetcher (+15 more)

### Community 3 - ".SeedState"
Cohesion: 0.11
Nodes (13): Fact, Func, TimeSpan, DeliveryWorkerTests, Fact, ProtectedCacheTests, int, IReadOnlyList (+5 more)

### Community 4 - "OutlookWidget.Core.Refresh"
Cohesion: 0.09
Nodes (16): OutlookWidget.Core.Tests.TestInfrastructure, OutlookWidget.Core.Tests, OutlookWidget.Core.Authentication, OutlookWidget.Provider, OutlookWidget.Packaging, OutlookWidget.Core.Diagnostics, OutlookWidget.Core.Refresh, OutlookWidget.Core.Delivery (+8 more)

### Community 5 - "DisclosureTombstoneStore"
Cohesion: 0.08
Nodes (16): ActiveOperationRegistry, HashSet, DisclosureMode, Action, bool, Dictionary, Func, Guid (+8 more)

### Community 6 - "Outlook Inbox Widget"
Cohesion: 0.05
Nodes (49): Client/tenant IDs supplied via package configuration, never committed, Consent-blocked vs Graph HTTP 403 distinction, Entra ID app registration (single-tenant public client), Permission minimization (remove User.Read, forbid Mail.Read), Author self-consent to delegated Mail.ReadBasic, WAM broker redirect URI carrying the client ID, Build-Package.ps1, CoordinationPathsTests (+41 more)

### Community 7 - "WidgetProvider"
Cohesion: 0.06
Nodes (27): IWidgetProvider, Func, IEnumerable, ProcessStartInfo, string, OutlookLauncher, OutlookLaunchResult, OutlookLaunchStrategy (+19 more)

### Community 8 - ".Current"
Cohesion: 0.09
Nodes (19): TimeSpan, BootSessionStamp, DateTimeOffset, ISystemClock, SystemClock, Guid, JsonSerializerOptions, LeaseRecord (+11 more)

### Community 9 - ".Record"
Cohesion: 0.15
Nodes (10): EventWaitHandle, TimeSpan, IOperationalLogger, NullOperationalLogger, Action, bool, CancellationTokenSource, long (+2 more)

### Community 10 - ".Start"
Cohesion: 0.13
Nodes (9): Process, Fact, MutationMutexTests, Fact, StateCommitCoordinatorTests, bool, string, TimeSpan (+1 more)

### Community 11 - "CoordinationStaticAnalysisTests"
Cohesion: 0.11
Nodes (8): TimeSpan, CoordinationBounds, Fact, IEnumerable, string, CoordinationStaticAnalysisTests, IEnumerable, RepositorySources

### Community 12 - "PackageManifestTests"
Cohesion: 0.20
Nodes (7): Height, Fact, string, PackageManifestTests, Width, XDocument, XElement

### Community 13 - "OutlookWidget.Core.csproj"
Cohesion: 0.10
Nodes (19): net10.0-windows10.0.26100.0, Microsoft.Identity.Client.Broker, Microsoft.Identity.Client.Extensions.Msal, Microsoft.NET.Test.Sdk, Microsoft.WindowsAppSDK, System.Security.Cryptography.ProtectedData, xunit.runner.visualstudio, xunit.v3 (+11 more)

### Community 14 - "Sources of Truth"
Cohesion: 0.33
Nodes (6): Graphify Working Agreement, docs/phase0-evidence.md, Phase-Gated Delivery, Platform Evidence Model, Sources of Truth, TECHNICAL_PLAN.md

### Community 15 - "Outlook Widget v1"
Cohesion: 0.33
Nodes (6): Mail.ReadBasic Boundary, Native Widget Architecture, Outlook Widget v1, Companion Interactive and Provider Silent Authentication, Surface-Independent Core, AGENTS.md Instructions

### Community 16 - "New-Assets.ps1"
Cohesion: 0.57
Nodes (5): New-AppIcon(), New-Canvas(), New-GradientBrush(), New-RoundedPath(), New-WidgetScreenshot()

### Community 17 - "Gate 7 — Outlook launch resolution"
Cohesion: 0.67
Nodes (4): Gate 7 — Outlook launch resolution, OutlookLauncher two launch candidates, no versioned path, Outlook will not open — no Classic fallback, weblink behaviour, scripts/Test-OutlookLaunch.ps1 launch probe

### Community 18 - "Cross-Process Coordination"
Cohesion: 0.67
Nodes (3): Cross-Process Coordination, Expiring Refresh Lease, Stable Package Identity

### Community 27 - "AuthenticationConfigurationTests"
Cohesion: 0.16
Nodes (10): Guid, JsonSerializerOptions, string, AuthenticationConfiguration, ConfigurationFile, Fact, InlineData, string (+2 more)

### Community 28 - "CoordinationFixture"
Cohesion: 0.15
Nodes (12): Count, Id, Outcome, OperationalEventId, OperationalOutcome, TimeSpan, IReadOnlyList, List (+4 more)

### Community 29 - "ProtectedCache"
Cohesion: 0.27
Nodes (5): byte, int, CacheCommitResult, ProtectedCache, SuppressMessage

### Community 30 - "CoordinationPaths"
Cohesion: 0.07
Nodes (34): ImmutableArray, IPublicClientApplication, Task, Program, AuthenticationConfigurationResult, AuthenticationConfigurationStatus, AuthenticationOptions, DateTimeOffset (+26 more)

### Community 31 - "PackageIdentity"
Cohesion: 0.21
Nodes (7): Exception, IdentityKind, int, LibraryImport, IdentityKind, PackageIdentity, PackageIdentityException

### Community 32 - "ProviderFactory"
Cohesion: 0.24
Nodes (7): PreserveSig, Func, Guid, int, IntPtr, IClassFactory, ProviderFactory

### Community 34 - "DeliveryWorker"
Cohesion: 0.22
Nodes (8): SemaphoreSlim, bool, CancellationTokenSource, Lock, long, Thread, DeliveryWorker, IWidgetDeliverySink

### Community 35 - ".Locate"
Cohesion: 0.12
Nodes (13): Func, Guid, int, IntPtr, LibraryImport, uint, Program, Fact (+5 more)

### Community 36 - "SkeletonCard"
Cohesion: 0.19
Nodes (10): Detail, Headline, Instance, DeliveryState, JsonSerializerOptions, SkeletonCard, SkeletonCardData, State (+2 more)

### Community 37 - "StateCommitCoordinator"
Cohesion: 0.32
Nodes (7): CancellationToken, ClearStateAction, CommitSnapshotAction, IStateCommitAction, StateCommitCoordinator, StateCommitOutcome, StateCommitResult

### Community 38 - "CompanionWindow"
Cohesion: 0.15
Nodes (16): MarshalAs, MSG, RECT, Func, int, IntPtr, LibraryImport, string (+8 more)

### Community 39 - "ProtectedCache.cs"
Cohesion: 0.16
Nodes (6): CacheCommitStatus, CacheReadResult, CacheReadStatus, CurrentUserDataProtector, IDataProtector, FailingProtector

### Community 40 - ".Raise"
Cohesion: 0.48
Nodes (3): StateChangeSignal, Fact, StateChangeSignalTests

### Community 41 - "MutationLock"
Cohesion: 0.27
Nodes (8): IDisposable, Mutex, bool, CancellationToken, int, MutationLock, MutationLockOutcome, MutationMutex

## Ambiguous Edges - Review These
- `Tray/Popover Fallback Surface` → `Widgets Policy Preflight (AllowNewsAndInterests)`  [AMBIGUOUS]
  TECHNICAL_PLAN.md · relation: conceptually_related_to

## Knowledge Gaps
- **37 isolated node(s):** `net10.0-windows`, `Microsoft.Identity.Client`, `Microsoft.NET.Sdk`, `ConfigurationFile`, `CacheReadStatus` (+32 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **3 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `Tray/Popover Fallback Surface` and `Widgets Policy Preflight (AllowNewsAndInterests)`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **Why does `IOperationalLogger` connect `.Record` to `AuthenticationOutcomeTests`, `DeliveryWorker`, `.RefreshAsync`, `DisclosureTombstoneStore`, `StateCommitCoordinator`, `WidgetProvider`, `.Current`, `MutationLock`, `AuthenticationConfigurationTests`, `CoordinationFixture`, `ProtectedCache`, `CoordinationPaths`?**
  _High betweenness centrality (0.090) - this node is a cross-community bridge._
- **Why does `OutlookWidget.Core.Refresh` connect `OutlookWidget.Core.Refresh` to `.RefreshAsync`, `DisclosureTombstoneStore`, `StateCommitCoordinator`, `ProtectedCache.cs`, `.Current`, `MutationLock`, `CoordinationStaticAnalysisTests`?**
  _High betweenness centrality (0.076) - this node is a cross-community bridge._
- **Why does `OutlookWidget.Core.Tests.TestInfrastructure` connect `OutlookWidget.Core.Refresh` to `CoordinationStaticAnalysisTests`, `.Start`, `.SeedState`?**
  _High betweenness centrality (0.054) - this node is a cross-community bridge._
- **What connects `net10.0-windows`, `Microsoft.Identity.Client`, `Microsoft.NET.Sdk` to the rest of the system?**
  _37 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `AuthenticationOutcomeTests` be split into smaller, more focused modules?**
  _Cohesion score 0.07896575821104122 - nodes in this community are weakly interconnected._
- **Should `OutlookWidget.Core (shared .NET library)` be split into smaller, more focused modules?**
  _Cohesion score 0.05081585081585081 - nodes in this community are weakly interconnected._