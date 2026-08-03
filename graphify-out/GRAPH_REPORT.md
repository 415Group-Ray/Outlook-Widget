# Graph Report - Outlook Widget  (2026-08-03)

## Corpus Check
- 98 files · ~141,523 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1246 nodes · 2841 edges · 52 communities (49 shown, 3 thin omitted)
- Extraction: 89% EXTRACTED · 11% INFERRED · 0% AMBIGUOUS · INFERRED: 315 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `7a08dc49`
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
- GraphMailClient
- ProviderRefreshWorker
- AuthenticationConfigurationTests
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
- .Locate
- SkeletonCard
- .Record
- CoordinationPaths
- SelectedAccountTests
- ProviderFactory
- ProviderCardTests
- .ReadAsync
- RefreshLeaseStore
- CoordinationFixture
- StateCommitCoordinator
- CompanionWindow
- WidgetProvider
- StubGraphHandler
- IOperationalLogger
- OutlookLauncher
- DeliveryWorker
- MailboxSnapshot
- ProtectedCache.cs
- DeliveryState
- PackageIdentity
- CompanionLauncher
- WidgetInstanceRegistry
- Q: What's next?
- Q: Should graphify files be gitignored?

## God Nodes (most connected - your core abstractions)
1. `GraphMailClientTests` - 42 edges
2. `OutlookWidget.Core.Refresh` - 35 edges
3. `SelectedAccountTests` - 34 edges
4. `CompanionWindow` - 31 edges
5. `OutlookWidget.Core.Caching` - 27 edges
6. `OutlookWidget.Core.Diagnostics` - 27 edges
7. `IOperationalLogger` - 27 edges
8. `DisclosureTombstoneStore` - 27 edges
9. `DisclosureTombstoneTests` - 26 edges
10. `RefreshCoordinatorTests` - 26 edges

## Surprising Connections (you probably didn't know these)
- `Cache is discarded and refetched, never migrated` --semantically_similar_to--> `No telemetry; logging API shape prevents sensitive data`  [INFERRED] [semantically similar]
  docs/troubleshooting.md → README.md
- `SelectedAccountTests` --references--> `AuthenticationOptions`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/SelectedAccountTests.cs → src/OutlookWidget.Core/Authentication/AuthenticationOptions.cs
- `SelectedAccountTests` --references--> `CoordinationPaths`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/SelectedAccountTests.cs → src/OutlookWidget.Core/Caching/CoordinationPaths.cs
- `CoordinationFixture` --references--> `CoordinationPaths`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/TestInfrastructure/CoordinationFixture.cs → src/OutlookWidget.Core/Caching/CoordinationPaths.cs
- `FailingProtector` --implements--> `IDataProtector`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/ProtectedCacheTests.cs → src/OutlookWidget.Core/Caching/ProtectedCache.cs

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

## Communities (52 total, 3 thin omitted)

### Community 0 - "AuthenticationOutcomeTests"
Cohesion: 0.07
Nodes (28): AccountSelection, CancellationToken, IPublicClientApplication, string, Task, InteractiveAuthService, IPublicClientApplication, Task (+20 more)

### Community 1 - "OutlookWidget.Core (shared .NET library)"
Cohesion: 0.05
Nodes (66): AbandonedMutexException Recovery, Adaptive Cards Schema 1.5 Rendering, 20-Second Async Refresh Deadline, Bounded Atomic Replace Retry Ladder, Boot-Session Discriminator for Lease Expiry, Cached-First, Activation-Driven Refresh Model, Signature Timestamping Decision, Approval/Consent-Required Authorization State (+58 more)

### Community 2 - ".RefreshAsync"
Cohesion: 0.06
Nodes (37): TimeSpan, BootSessionStamp, DateTimeOffset, ISystemClock, SystemClock, bool, Func, Guid (+29 more)

### Community 3 - ".SeedState"
Cohesion: 0.11
Nodes (14): Fact, Func, TimeSpan, DeliveryWorkerTests, Fact, FailingProtector, ProtectedCacheTests, int (+6 more)

### Community 4 - "OutlookWidget.Core.Refresh"
Cohesion: 0.07
Nodes (22): OutlookWidget.Core.Tests.TestInfrastructure, OutlookWidget.Core.Tests, OutlookWidget.Core.Authentication, OutlookWidget.Provider, OutlookWidget.Packaging, OutlookWidget.Core.Diagnostics, OutlookWidget.Core.Graph, OutlookWidget.Core.Refresh (+14 more)

### Community 5 - "DisclosureTombstoneStore"
Cohesion: 0.08
Nodes (16): ActiveOperationRegistry, HashSet, DisclosureMode, Action, bool, Dictionary, Func, Guid (+8 more)

### Community 6 - "Outlook Inbox Widget"
Cohesion: 0.05
Nodes (49): Client/tenant IDs supplied via package configuration, never committed, Consent-blocked vs Graph HTTP 403 distinction, Entra ID app registration (single-tenant public client), Permission minimization (remove User.Read, forbid Mail.Read), Author self-consent to delegated Mail.ReadBasic, WAM broker redirect URI carrying the client ID, Build-Package.ps1, CoordinationPathsTests (+41 more)

### Community 7 - "GraphMailClient"
Cohesion: 0.09
Nodes (19): GraphResponse, HttpClient, JsonElement, bool, CancellationToken, HttpResponseMessage, HttpStatusCode, int (+11 more)

### Community 8 - "ProviderRefreshWorker"
Cohesion: 0.17
Nodes (10): Queue, RefreshWork, RefreshTrigger, bool, CancellationTokenSource, Lock, Task, TimeSpan (+2 more)

### Community 9 - "AuthenticationConfigurationTests"
Cohesion: 0.15
Nodes (10): Guid, JsonSerializerOptions, string, AuthenticationConfiguration, ConfigurationFile, Fact, InlineData, string (+2 more)

### Community 10 - ".Start"
Cohesion: 0.09
Nodes (17): EventWaitHandle, IDisposable, Process, Action, bool, CancellationTokenSource, long, Thread (+9 more)

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

### Community 27 - ".Locate"
Cohesion: 0.18
Nodes (7): Func, Fact, string, CoordinationPathsTests, Fact, string, PackagedStateTests

### Community 28 - "SkeletonCard"
Cohesion: 0.18
Nodes (10): Detail, Headline, Instance, AuthenticationConfigurationStatus, JsonSerializerOptions, SkeletonCard, SkeletonCardData, State (+2 more)

### Community 29 - ".Record"
Cohesion: 0.24
Nodes (6): byte, int, CacheCommitResult, ProtectedCache, TimeSpan, SuppressMessage

### Community 30 - "CoordinationPaths"
Cohesion: 0.06
Nodes (37): ImmutableArray, AuthenticationOptions, DateTimeOffset, Guid, JsonSerializerOptions, AuthorizationRecord, AuthorizationStateStore, Func (+29 more)

### Community 31 - "SelectedAccountTests"
Cohesion: 0.09
Nodes (15): AccountId, IAccount, byte, Guid, JsonSerializerOptions, AccountRecord, SelectedAccountResult, SelectedAccountStore (+7 more)

### Community 32 - "ProviderFactory"
Cohesion: 0.24
Nodes (7): PreserveSig, Func, Guid, int, IntPtr, IClassFactory, ProviderFactory

### Community 34 - ".ReadAsync"
Cohesion: 0.16
Nodes (10): TimeSpan, GraphMailResult, GraphMailStatus, Fact, HttpStatusCode, InlineData, string, Task (+2 more)

### Community 35 - "RefreshLeaseStore"
Cohesion: 0.23
Nodes (6): Guid, JsonSerializerOptions, LeaseRecord, CancellationToken, Guid, RefreshLeaseStore

### Community 36 - "CoordinationFixture"
Cohesion: 0.13
Nodes (13): Count, Id, Outcome, OperationalEventId, OperationalOutcome, TimeSpan, NullOperationalLogger, IReadOnlyList (+5 more)

### Community 37 - "StateCommitCoordinator"
Cohesion: 0.32
Nodes (7): CancellationToken, ClearStateAction, CommitSnapshotAction, IStateCommitAction, StateCommitCoordinator, StateCommitOutcome, StateCommitResult

### Community 38 - "CompanionWindow"
Cohesion: 0.15
Nodes (16): MarshalAs, MSG, RECT, Func, int, IntPtr, LibraryImport, string (+8 more)

### Community 39 - "WidgetProvider"
Cohesion: 0.18
Nodes (7): IWidgetProvider, Func, WidgetInstance, ManualResetEventSlim, WidgetProvider, WidgetContext, WidgetContextChangedArgs

### Community 40 - "StubGraphHandler"
Cohesion: 0.07
Nodes (17): ConcurrentBag, HttpMessageHandler, HttpRequestMessage, Memory, SeekOrigin, Stream, CancellationToken, HttpResponseMessage (+9 more)

### Community 41 - "IOperationalLogger"
Cohesion: 0.29
Nodes (8): Mutex, IOperationalLogger, bool, CancellationToken, int, MutationLock, MutationLockOutcome, MutationMutex

### Community 42 - "OutlookLauncher"
Cohesion: 0.21
Nodes (9): Func, IEnumerable, ProcessStartInfo, string, OutlookLauncher, OutlookLaunchResult, OutlookLaunchStrategy, StartInfo (+1 more)

### Community 43 - "DeliveryWorker"
Cohesion: 0.25
Nodes (7): SemaphoreSlim, bool, CancellationTokenSource, Lock, long, Thread, DeliveryWorker

### Community 44 - "MailboxSnapshot"
Cohesion: 0.08
Nodes (23): ReadOnlySpan, IReadOnlyList, MailboxReadout, DateTimeOffset, Guid, int, IReadOnlyList, JsonSerializerOptions (+15 more)

### Community 45 - "ProtectedCache.cs"
Cohesion: 0.22
Nodes (5): CacheCommitStatus, CacheReadResult, CacheReadStatus, CurrentUserDataProtector, IDataProtector

### Community 46 - "DeliveryState"
Cohesion: 0.28
Nodes (4): DeliveryState, IWidgetDeliverySink, Func, WidgetDeliverySink

### Community 47 - "PackageIdentity"
Cohesion: 0.21
Nodes (7): Exception, IdentityKind, int, LibraryImport, IdentityKind, PackageIdentity, PackageIdentityException

### Community 48 - "CompanionLauncher"
Cohesion: 0.32
Nodes (5): Func, IEnumerable, ProcessStartInfo, string, CompanionLauncher

### Community 49 - "WidgetInstanceRegistry"
Cohesion: 0.25
Nodes (4): Dictionary, Lock, WidgetInstanceRegistry, WidgetActionInvokedArgs

### Community 50 - "Q: What's next?"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: What's next?, Source Nodes

### Community 51 - "Q: Should graphify files be gitignored?"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Should graphify files be gitignored?, Source Nodes

## Ambiguous Edges - Review These
- `Tray/Popover Fallback Surface` → `Widgets Policy Preflight (AllowNewsAndInterests)`  [AMBIGUOUS]
  TECHNICAL_PLAN.md · relation: conceptually_related_to

## Knowledge Gaps
- **47 isolated node(s):** `net10.0-windows`, `Microsoft.Identity.Client`, `Microsoft.NET.Sdk`, `ConfigurationFile`, `SelectedAccountStatus` (+42 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **3 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `Tray/Popover Fallback Surface` and `Widgets Policy Preflight (AllowNewsAndInterests)`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **Why does `IOperationalLogger` connect `IOperationalLogger` to `AuthenticationOutcomeTests`, `.RefreshAsync`, `RefreshLeaseStore`, `CoordinationFixture`, `DisclosureTombstoneStore`, `StateCommitCoordinator`, `GraphMailClient`, `ProviderRefreshWorker`, `AuthenticationConfigurationTests`, `OutlookLauncher`, `DeliveryWorker`, `.Start`, `WidgetProvider`, `DeliveryState`, `CompanionLauncher`, `.Record`, `CoordinationPaths`, `SelectedAccountTests`?**
  _High betweenness centrality (0.130) - this node is a cross-community bridge._
- **Why does `GraphMailClient` connect `GraphMailClient` to `IOperationalLogger`, `.Start`, `OutlookWidget.Core.Refresh`, `.ReadAsync`?**
  _High betweenness centrality (0.081) - this node is a cross-community bridge._
- **Why does `OutlookWidget.Core.Refresh` connect `OutlookWidget.Core.Refresh` to `.RefreshAsync`, `RefreshLeaseStore`, `DisclosureTombstoneStore`, `StateCommitCoordinator`, `IOperationalLogger`, `CoordinationStaticAnalysisTests`, `ProtectedCache.cs`, `DeliveryState`?**
  _High betweenness centrality (0.070) - this node is a cross-community bridge._
- **What connects `net10.0-windows`, `Microsoft.Identity.Client`, `Microsoft.NET.Sdk` to the rest of the system?**
  _47 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `AuthenticationOutcomeTests` be split into smaller, more focused modules?**
  _Cohesion score 0.06634615384615385 - nodes in this community are weakly interconnected._
- **Should `OutlookWidget.Core (shared .NET library)` be split into smaller, more focused modules?**
  _Cohesion score 0.05081585081585081 - nodes in this community are weakly interconnected._