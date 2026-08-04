# Graph Report - Outlook Widget  (2026-08-04)

## Corpus Check
- 108 files · ~154,638 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1411 nodes · 3375 edges · 61 communities (57 shown, 4 thin omitted)
- Extraction: 87% EXTRACTED · 13% INFERRED · 0% AMBIGUOUS · INFERRED: 443 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `c5dcef03`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- .SignInAsync
- OutlookWidget.Core (shared .NET library)
- .RefreshAsync
- .SeedState
- OutlookWidget.Core.Refresh
- DisclosureTombstoneStore
- Outlook Inbox Widget
- .Attach
- TokenAcquisitionResult
- AuthenticationConfigurationTests
- SilentAuthProbe
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
- DeliveryWorker
- IOperationalLogger
- .Read
- SelectedAccountTests
- ProviderFactory
- .Record
- .ReadAsync
- ManualTimer
- RecordingLogger
- AuthenticationOutcomeTests
- CompanionWindow
- WidgetProvider
- StubGraphHandler
- .AcquireAsync
- ProviderCardTests
- .Main
- .Start
- CoordinationFixture
- .Current
- Q: proceed with next steps
- PackageIdentity
- .CreateAsync
- Q: What's next?
- Q: Should graphify files be gitignored?
- MailboxSnapshot
- OutlookLauncher
- .Unavailable
- MutationLock
- Q: Address that comment, but before committing, proceed with those recommendations and commit all at once for review.
- ActiveRefreshTimer
- CompanionLauncher
- ProtectedCache
- .Raise

## God Nodes (most connected - your core abstractions)
1. `OutlookWidget.Core.Refresh` - 45 edges
2. `GraphMailClientTests` - 42 edges
3. `SelectedAccountTests` - 36 edges
4. `IOperationalLogger` - 35 edges
5. `OutlookWidget.Core.Caching` - 33 edges
6. `OutlookWidget.Core.Diagnostics` - 32 edges
7. `CompanionWindow` - 31 edges
8. `DisclosureTombstoneStore` - 31 edges
9. `CoordinationStaticAnalysisTests` - 29 edges
10. `SelectedAccountStore` - 27 edges

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
  tests/OutlookWidget.Core.Tests/AccountSwitchCoordinatorTests.cs → src/OutlookWidget.Core/Caching/ProtectedCache.cs

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

## Communities (61 total, 4 thin omitted)

### Community 0 - ".SignInAsync"
Cohesion: 0.25
Nodes (7): IPublicClientApplication, Task, Program, Exception, AuthenticationConfigurationResult, PackagedStateResult, STAThread

### Community 1 - "OutlookWidget.Core (shared .NET library)"
Cohesion: 0.05
Nodes (66): AbandonedMutexException Recovery, Adaptive Cards Schema 1.5 Rendering, 20-Second Async Refresh Deadline, Bounded Atomic Replace Retry Ladder, Boot-Session Discriminator for Lease Expiry, Cached-First, Activation-Driven Refresh Model, Signature Timestamping Decision, Approval/Consent-Required Authorization State (+58 more)

### Community 2 - ".RefreshAsync"
Cohesion: 0.08
Nodes (31): Queue, RefreshWork, CancellationToken, Lock, long, Task, TimeSpan, DeliveryRequestOutcome (+23 more)

### Community 3 - ".SeedState"
Cohesion: 0.10
Nodes (14): Fact, Func, TimeSpan, DeliveryWorkerTests, Fact, FailingProtector, ProtectedCacheTests, int (+6 more)

### Community 4 - "OutlookWidget.Core.Refresh"
Cohesion: 0.06
Nodes (30): OutlookWidget.Core.Tests.TestInfrastructure, OutlookWidget.Core.Tests, OutlookWidget.Core.Authentication, OutlookWidget.Provider, OutlookWidget.Packaging, OutlookWidget.Core.Diagnostics, OutlookWidget.Core.Graph, OutlookWidget.Core.Refresh (+22 more)

### Community 5 - "DisclosureTombstoneStore"
Cohesion: 0.07
Nodes (21): ActiveOperationRegistry, HashSet, Func, Task, AccountSwitchCoordinator, AccountSwitchResult, DisclosureMode, Action (+13 more)

### Community 6 - "Outlook Inbox Widget"
Cohesion: 0.05
Nodes (49): Client/tenant IDs supplied via package configuration, never committed, Consent-blocked vs Graph HTTP 403 distinction, Entra ID app registration (single-tenant public client), Permission minimization (remove User.Read, forbid Mail.Read), Author self-consent to delegated Mail.ReadBasic, WAM broker redirect URI carrying the client ID, Build-Package.ps1, CoordinationPathsTests (+41 more)

### Community 7 - ".Attach"
Cohesion: 0.33
Nodes (4): ManualTimer, Fact, ActiveRefreshTimerTests, TimerCallback

### Community 8 - "TokenAcquisitionResult"
Cohesion: 0.23
Nodes (8): CancellationToken, Func, IPublicClientApplication, string, Task, InteractiveAuthService, DateTimeOffset, TokenAcquisitionResult

### Community 9 - "AuthenticationConfigurationTests"
Cohesion: 0.15
Nodes (10): Guid, JsonSerializerOptions, string, AuthenticationConfiguration, ConfigurationFile, Fact, InlineData, string (+2 more)

### Community 10 - "SilentAuthProbe"
Cohesion: 0.19
Nodes (10): bool, CancellationToken, CancellationTokenSource, int, IPublicClientApplication, Lock, long, Task (+2 more)

### Community 11 - "CoordinationStaticAnalysisTests"
Cohesion: 0.10
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

### Community 28 - "DeliveryWorker"
Cohesion: 0.10
Nodes (19): Detail, Headline, Instance, SemaphoreSlim, AuthenticationConfigurationStatus, bool, CancellationTokenSource, Lock (+11 more)

### Community 29 - "IOperationalLogger"
Cohesion: 0.14
Nodes (23): byte, Guid, JsonSerializerOptions, AccountRecord, SelectedAccountStore, SignOutCoordinator, string, CoordinationPaths (+15 more)

### Community 30 - ".Read"
Cohesion: 0.06
Nodes (30): ImmutableArray, AccountSelectionResult, AuthenticationOptions, DateTimeOffset, Guid, JsonSerializerOptions, AuthorizationRecord, AuthorizationStateStore (+22 more)

### Community 31 - "SelectedAccountTests"
Cohesion: 0.11
Nodes (10): AccountId, IAccount, SelectedAccountResult, IAccount, IReadOnlyList, Fact, string, PassThroughProtector (+2 more)

### Community 32 - "ProviderFactory"
Cohesion: 0.24
Nodes (7): PreserveSig, Func, Guid, int, IntPtr, IClassFactory, ProviderFactory

### Community 33 - ".Record"
Cohesion: 0.17
Nodes (9): TimeSpan, Guid, JsonSerializerOptions, LeaseRecord, CancellationToken, CancellationToken, Guid, LeaseClaim (+1 more)

### Community 34 - ".ReadAsync"
Cohesion: 0.08
Nodes (26): GraphResponse, HttpClient, JsonElement, bool, CancellationToken, HttpResponseMessage, HttpStatusCode, int (+18 more)

### Community 35 - "ManualTimer"
Cohesion: 0.24
Nodes (7): DueTime, ITimer, Period, List, TimeSpan, ValueTask, ManualTimer

### Community 36 - "RecordingLogger"
Cohesion: 0.17
Nodes (11): Count, Id, Outcome, OperationalEventId, OperationalOutcome, TimeSpan, NullOperationalLogger, IReadOnlyList (+3 more)

### Community 37 - "AuthenticationOutcomeTests"
Cohesion: 0.19
Nodes (5): string, AuthenticationFailures, AuthenticationPhase, Fact, AuthenticationOutcomeTests

### Community 38 - "CompanionWindow"
Cohesion: 0.15
Nodes (16): MarshalAs, MSG, RECT, Func, int, IntPtr, LibraryImport, string (+8 more)

### Community 39 - "WidgetProvider"
Cohesion: 0.12
Nodes (13): IWidgetProvider, Func, WidgetDeliverySink, Dictionary, Func, Lock, WidgetInstance, WidgetInstanceRegistry (+5 more)

### Community 40 - "StubGraphHandler"
Cohesion: 0.07
Nodes (17): ConcurrentBag, HttpMessageHandler, HttpRequestMessage, Memory, SeekOrigin, Stream, CancellationToken, HttpResponseMessage (+9 more)

### Community 41 - ".AcquireAsync"
Cohesion: 0.31
Nodes (6): AccountSelection, CancellationToken, IPublicClientApplication, Task, AccountSelection, SilentAuthService

### Community 43 - ".Main"
Cohesion: 0.33
Nodes (6): Guid, int, IntPtr, LibraryImport, uint, Program

### Community 44 - ".Start"
Cohesion: 0.13
Nodes (9): Process, Fact, MutationMutexTests, Fact, StateCommitCoordinatorTests, bool, string, TimeSpan (+1 more)

### Community 45 - "CoordinationFixture"
Cohesion: 0.12
Nodes (12): IDisposable, bool, MutationMutex, Action, bool, CancellationTokenSource, EventWaitHandle, long (+4 more)

### Community 46 - ".Current"
Cohesion: 0.15
Nodes (11): TimeSpan, BootSessionStamp, DateTimeOffset, ISystemClock, SystemClock, Fact, BootSessionStampTests, DateTimeOffset (+3 more)

### Community 47 - "Q: proceed with next steps"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: proceed with next steps, Source Nodes

### Community 48 - "PackageIdentity"
Cohesion: 0.21
Nodes (7): Exception, IdentityKind, int, LibraryImport, IdentityKind, PackageIdentity, PackageIdentityException

### Community 49 - ".CreateAsync"
Cohesion: 0.29
Nodes (6): Func, IntPtr, IPublicClientApplication, string, Task, BrokerClient

### Community 50 - "Q: What's next?"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: What's next?, Source Nodes

### Community 51 - "Q: Should graphify files be gitignored?"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Should graphify files be gitignored?, Source Nodes

### Community 52 - "MailboxSnapshot"
Cohesion: 0.06
Nodes (28): ReadOnlySpan, IReadOnlyList, MailboxReadout, DateTimeOffset, Guid, int, IReadOnlyList, JsonSerializerOptions (+20 more)

### Community 53 - "OutlookLauncher"
Cohesion: 0.20
Nodes (9): Func, IEnumerable, ProcessStartInfo, string, OutlookLauncher, OutlookLaunchResult, OutlookLaunchStrategy, StartInfo (+1 more)

### Community 55 - "MutationLock"
Cohesion: 0.17
Nodes (7): GenerationReadStatus, Mutex, CacheCommitResult, int, MutationLock, MutationLockOutcome, SuppressMessage

### Community 56 - "Q: Address that comment, but before committing, proceed with those recommendations and commit all at once for review."
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Address that comment, but before committing, proceed with those recommendations and commit all at once for review., Source Nodes

### Community 57 - "ActiveRefreshTimer"
Cohesion: 0.29
Nodes (6): Action, bool, ITimer, Lock, TimeSpan, ActiveRefreshTimer

### Community 58 - "CompanionLauncher"
Cohesion: 0.22
Nodes (6): Func, IEnumerable, ProcessStartInfo, string, CompanionLauncher, WidgetActionInvokedArgs

### Community 59 - "ProtectedCache"
Cohesion: 0.24
Nodes (5): byte, int, GenerationReadStatus, IDataProtector, ProtectedCache

### Community 61 - ".Raise"
Cohesion: 0.16
Nodes (10): MemberData, EventWaitHandle, Func, NamedEventSignal, StateChangeSignal, Exception, Fact, Theory (+2 more)

## Ambiguous Edges - Review These
- `Tray/Popover Fallback Surface` → `Widgets Policy Preflight (AllowNewsAndInterests)`  [AMBIGUOUS]
  TECHNICAL_PLAN.md · relation: conceptually_related_to

## Knowledge Gaps
- **57 isolated node(s):** `net10.0-windows`, `Microsoft.Identity.Client`, `Microsoft.NET.Sdk`, `AccountSwitchOutcome`, `ConfigurationFile` (+52 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **4 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `Tray/Popover Fallback Surface` and `Widgets Policy Preflight (AllowNewsAndInterests)`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **Why does `IOperationalLogger` connect `IOperationalLogger` to `.Record`, `.ReadAsync`, `.RefreshAsync`, `RecordingLogger`, `DisclosureTombstoneStore`, `WidgetProvider`, `TokenAcquisitionResult`, `AuthenticationConfigurationTests`, `.AcquireAsync`, `SilentAuthProbe`, `CoordinationFixture`, `OutlookLauncher`, `MutationLock`, `ActiveRefreshTimer`, `CompanionLauncher`, `ProtectedCache`, `DeliveryWorker`, `.Read`?**
  _High betweenness centrality (0.107) - this node is a cross-community bridge._
- **Why does `OutlookWidget.Core.Refresh` connect `OutlookWidget.Core.Refresh` to `.Record`, `DisclosureTombstoneStore`, `CoordinationStaticAnalysisTests`, `.Current`, `IOperationalLogger`, `MutationLock`, `.Raise`?**
  _High betweenness centrality (0.082) - this node is a cross-community bridge._
- **Why does `GraphMailClient` connect `.ReadAsync` to `IOperationalLogger`, `OutlookWidget.Core.Refresh`, `CoordinationFixture`?**
  _High betweenness centrality (0.077) - this node is a cross-community bridge._
- **What connects `net10.0-windows`, `Microsoft.Identity.Client`, `Microsoft.NET.Sdk` to the rest of the system?**
  _57 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `OutlookWidget.Core (shared .NET library)` be split into smaller, more focused modules?**
  _Cohesion score 0.05081585081585081 - nodes in this community are weakly interconnected._
- **Should `.RefreshAsync` be split into smaller, more focused modules?**
  _Cohesion score 0.07876712328767123 - nodes in this community are weakly interconnected._