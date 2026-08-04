# Graph Report - C:\Users\rsmalley\OneDrive - 415 Group, Inc\Documents\GitHub\Outlook Widget  (2026-08-04)

## Corpus Check
- 107 files · ~155,990 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1450 nodes · 3424 edges · 72 communities (67 shown, 5 thin omitted)
- Extraction: 87% EXTRACTED · 13% INFERRED · 0% AMBIGUOUS · INFERRED: 446 edges (avg confidence: 0.8)
- Token cost: 333,371 input · 0 output

## Community Hubs (Navigation)
- Project Namespace Structure
- Graph HTTP Client Types
- Delivery Queue and Work
- Account Selection and Authorization Records
- Account Switch and Disclosure Modes
- Delivery Worker Tests
- Mailbox Readout Models
- Outlook Launcher
- Operational Logging and Timers
- Coordination Bounds and Static Analysis
- Companion Win32 Window
- Selected Account Tests
- Graph Test Stubs
- Mutation Mutex Tests
- Authentication Configuration
- Selected Account Store
- Boot Session and Clock
- Authentication Failure Classification
- Interactive Authentication Service
- State Commit Coordinator
- Package Manifest Tests
- Protected Cache Generation
- Mutation Mutex Primitive
- Coordination Path Resolution
- Refresh Lease Record
- Auth Scope and Consent Boundaries
- Engineering Invariants
- Package Dependencies
- Graph Response Reader
- Skeleton Card Rendering
- Delivery and Suppression Design
- Named Event Signals
- Companion Program Entry
- Package Identity Interop
- Silent Auth Service Internals
- Agent Guidance and Repo Map
- Refresh Fetch Pipeline
- COM Provider Factory
- Refresh Gates and Graphify Policy
- Delivery Worker
- Provider Card Tests
- Coordination Invariants and Roadmap
- Consent Failure Troubleshooting
- Entra Registration and Scope Gates
- Package Version and Install Errors
- Protected Cache Core
- Provider COM Registration
- Plan Overview and Refresh Model
- State Change Listener
- Sign-in Commit and Phase 1 Gap
- Logout and Token State Evidence
- Broker Client
- Provider Lifecycle Gates
- Asset Generation Script
- Auth Classification Tests
- Widget Discovery Gates
- Self-Consent Policy Block
- Packaged State Location
- Query Memory: What's Next
- Query Memory: Graphify Gitignore
- Query Memory: Next Steps
- Query Memory: Commit Review
- Local Authentication Config
- Build and Test Discipline
- Signing and Install Gate
- AGENTS.md Instructions

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
- `Boot-session discriminator` --semantically_similar_to--> `Cache generation only moves forward`  [INFERRED] [semantically similar]
  TECHNICAL_PLAN.md → AGENTS.md
- `Approved Graph field set boundary` --semantically_similar_to--> `No access or refresh token persisted`  [INFERRED] [semantically similar]
  AGENTS.md → README.md
- `Sign-in publishes account and mailbox decision together` --references--> `SelectedAccountStore`  [EXTRACTED]
  README.md → TECHNICAL_PLAN.md
- `MailboxReadout` --shares_data_with--> `MailboxSnapshot`  [INFERRED]
  graphify-out/memory/query_20260803_131456_what_s_next.md → TECHNICAL_PLAN.md
- `Delivery Stays Outside the Refresh Transaction` --rationale_for--> `RefreshCoordinator`  [EXTRACTED]
  graphify-out/memory/query_20260803_131456_what_s_next.md → TECHNICAL_PLAN.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Cross-process coordination core** — technical_plan_refreshcoordinator, technical_plan_mutation_mutex, technical_plan_refresh_lease_record, technical_plan_protectedcache, technical_plan_disclosure_tombstone, technical_plan_statechangelistener, technical_plan_sole_delivery_authority [EXTRACTED 1.00]
- **Two-process authentication flow** — technical_plan_brokerclient, technical_plan_interactiveauthservice, technical_plan_silentauthservice, technical_plan_selectedaccountstore, technical_plan_authorizationstatestore, technical_plan_authenticationfailures, technical_plan_shared_msal_token_cache [EXTRACTED 1.00]
- **Suppress-first disclosure-reducing operations** — technical_plan_logout_ordering, technical_plan_account_switching, technical_plan_counts_only_privacy_mode, technical_plan_disclosure_tombstone, readme_clear_interrupted_operations [EXTRACTED 1.00]
- **Sign-in, consent, and silent-acquisition flow across companion and provider** — docs_app_registration_entra_app_registration, docs_app_registration_wam_broker_redirect_uri, docs_app_registration_microsoft_managed_consent_policy, docs_phase0_evidence_gate_8_split_result, docs_phase0_evidence_gate_9_silent_zero_handle, docs_phase0_evidence_shared_msal_token_cache, docs_phase0_evidence_selected_account_store_record, docs_troubleshooting_signin_symptom_table [INFERRED 0.85]
- **Package version derivation and the install failures it prevents** — docs_phase0_evidence_derived_package_version, docs_phase0_evidence_every_commit_changes_every_assembly, docs_phase0_evidence_squash_merge_drops_commit_height, docs_phase0_evidence_pinned_widget_blocks_package_update, docs_troubleshooting_hresult_0x80073cfb, docs_troubleshooting_hresult_0x80073d02, docs_troubleshooting_derived_version_does_not_exceed_installed, docs_troubleshooting_never_uninstall_to_recover [EXTRACTED 1.00]
- **Fail-closed disclosure, state location, and forward-only generation** — docs_troubleshooting_details_hidden_four_cases, docs_troubleshooting_clear_interrupted_operations, docs_troubleshooting_signout_reported_failure, docs_phase0_evidence_packagedstate_locate_fail_closed, docs_phase0_evidence_protectedcache_tryreadgeneration, docs_phase0_evidence_installed_logout_measurement [INFERRED 0.85]
- **Disclosure Suppression and Sign-Out Coordination Flow** — graphify_out_memory_query_20260803_204158_address_that_comment__but_before_committing__proce_signoutcoordinator, graphify_out_memory_query_20260803_204158_address_that_comment__but_before_committing__proce_disclosuresuppression, graphify_out_memory_query_20260803_204158_address_that_comment__but_before_committing__proce_disclosuretombstonestore [EXTRACTED 1.00]
- **Production Refresh Slice Composition** — graphify_out_memory_query_20260803_131456_what_s_next_irefreshfetcher, graphify_out_memory_query_20260803_131456_what_s_next_refreshpayload [EXTRACTED 1.00]
- **Phase 1 Logout Slice Reused Components** — graphify_out_memory_query_20260803_190437_proceed_with_next_steps_phase1_logout_slice, graphify_out_memory_query_20260803_190437_proceed_with_next_steps_statecommitcoordinator, graphify_out_memory_query_20260803_190437_proceed_with_next_steps_provider_delivery_convergence [EXTRACTED 1.00]

## Communities (72 total, 5 thin omitted)

### Community 0 - "Project Namespace Structure"
Cohesion: 0.07
Nodes (23): OutlookWidget.Core.Tests.TestInfrastructure, OutlookWidget.Core.Tests, OutlookWidget.Core.Authentication, OutlookWidget.Provider, OutlookWidget.Packaging, OutlookWidget.Core.Diagnostics, OutlookWidget.Core.Graph, OutlookWidget.Core.Refresh (+15 more)

### Community 1 - "Graph HTTP Client Types"
Cohesion: 0.09
Nodes (22): GraphResponse, HttpClient, bool, CancellationToken, HttpResponseMessage, HttpStatusCode, int, string (+14 more)

### Community 2 - "Delivery Queue and Work"
Cohesion: 0.08
Nodes (31): Queue, RefreshWork, CancellationToken, Lock, long, Task, TimeSpan, DeliveryRequestOutcome (+23 more)

### Community 3 - "Account Selection and Authorization Records"
Cohesion: 0.08
Nodes (27): ImmutableArray, AccountSelectionResult, AuthenticationOptions, DateTimeOffset, Guid, JsonSerializerOptions, AuthorizationRecord, AuthorizationStateStore (+19 more)

### Community 4 - "Account Switch and Disclosure Modes"
Cohesion: 0.07
Nodes (21): ActiveOperationRegistry, HashSet, Func, Task, AccountSwitchCoordinator, AccountSwitchResult, DisclosureMode, Action (+13 more)

### Community 5 - "Delivery Worker Tests"
Cohesion: 0.11
Nodes (13): Fact, Func, TimeSpan, DeliveryWorkerTests, Fact, ProtectedCacheTests, int, IReadOnlyList (+5 more)

### Community 6 - "Mailbox Readout Models"
Cohesion: 0.06
Nodes (28): ReadOnlySpan, IReadOnlyList, MailboxReadout, DateTimeOffset, Guid, int, IReadOnlyList, JsonSerializerOptions (+20 more)

### Community 7 - "Outlook Launcher"
Cohesion: 0.06
Nodes (28): IWidgetProvider, Func, IEnumerable, ProcessStartInfo, string, OutlookLauncher, OutlookLaunchResult, OutlookLaunchStrategy (+20 more)

### Community 8 - "Operational Logging and Timers"
Cohesion: 0.06
Nodes (28): Count, DueTime, Id, ITimer, ManualTimer, Outcome, Period, OperationalEventId (+20 more)

### Community 9 - "Coordination Bounds and Static Analysis"
Cohesion: 0.10
Nodes (8): TimeSpan, CoordinationBounds, Fact, IEnumerable, string, CoordinationStaticAnalysisTests, IEnumerable, RepositorySources

### Community 10 - "Companion Win32 Window"
Cohesion: 0.15
Nodes (16): MarshalAs, MSG, RECT, Func, int, IntPtr, LibraryImport, string (+8 more)

### Community 11 - "Selected Account Tests"
Cohesion: 0.12
Nodes (9): AccountId, IAccount, SelectedAccountResult, IAccount, IReadOnlyList, Fact, string, SelectedAccountTests (+1 more)

### Community 12 - "Graph Test Stubs"
Cohesion: 0.07
Nodes (17): ConcurrentBag, HttpMessageHandler, HttpRequestMessage, Memory, SeekOrigin, Stream, CancellationToken, HttpResponseMessage (+9 more)

### Community 13 - "Mutation Mutex Tests"
Cohesion: 0.13
Nodes (9): Process, Fact, MutationMutexTests, Fact, StateCommitCoordinatorTests, bool, string, TimeSpan (+1 more)

### Community 14 - "Authentication Configuration"
Cohesion: 0.16
Nodes (10): Guid, JsonSerializerOptions, string, AuthenticationConfiguration, ConfigurationFile, Fact, InlineData, string (+2 more)

### Community 15 - "Selected Account Store"
Cohesion: 0.09
Nodes (11): byte, Guid, JsonSerializerOptions, AccountRecord, SelectedAccountStore, IDataProtector, FailingProtector, FailingProtector (+3 more)

### Community 16 - "Boot Session and Clock"
Cohesion: 0.16
Nodes (11): TimeSpan, BootSessionStamp, DateTimeOffset, ISystemClock, SystemClock, Fact, BootSessionStampTests, DateTimeOffset (+3 more)

### Community 17 - "Authentication Failure Classification"
Cohesion: 0.18
Nodes (5): string, AuthenticationFailures, AuthenticationPhase, Fact, AuthenticationOutcomeTests

### Community 18 - "Interactive Authentication Service"
Cohesion: 0.15
Nodes (14): AccountSelection, CancellationToken, Func, IPublicClientApplication, string, Task, InteractiveAuthService, CancellationToken (+6 more)

### Community 19 - "State Commit Coordinator"
Cohesion: 0.18
Nodes (15): SignOutCoordinator, IOperationalLogger, byte, CancellationToken, long, string, ClearStateAction, CommitAccountSwitchStateAction (+7 more)

### Community 20 - "Package Manifest Tests"
Cohesion: 0.20
Nodes (7): Height, Fact, string, PackageManifestTests, Width, XDocument, XElement

### Community 21 - "Protected Cache Generation"
Cohesion: 0.23
Nodes (8): GenerationReadStatus, byte, int, CacheCommitResult, GenerationReadStatus, ProtectedCache, CommitSignedOutStateAction, SuppressMessage

### Community 22 - "Mutation Mutex Primitive"
Cohesion: 0.14
Nodes (12): IDisposable, Mutex, bool, CancellationToken, int, MutationLock, MutationLockOutcome, MutationMutex (+4 more)

### Community 23 - "Coordination Path Resolution"
Cohesion: 0.20
Nodes (7): Func, Fact, string, CoordinationPathsTests, Fact, string, PackagedStateTests

### Community 24 - "Refresh Lease Record"
Cohesion: 0.16
Nodes (8): TimeSpan, Guid, JsonSerializerOptions, LeaseRecord, CancellationToken, Guid, LeaseClaim, RefreshLeaseStore

### Community 25 - "Auth Scope and Consent Boundaries"
Cohesion: 0.14
Nodes (18): Approved Graph field set boundary, Phase-aware authentication classification, Scope and phase gate discipline, No access or refresh token persisted, BrokerClient.NoParentWindow named member, AuthenticationFailures classifier, AuthorizationStateStore, BrokerClient (+10 more)

### Community 26 - "Engineering Invariants"
Cohesion: 0.16
Nodes (18): COM class ID agreement in three places, Non-negotiable engineering invariants, Derived MSIX package version, Delivery thread must be unkillable, Clear interrupted operations recovery control, Privacy-state rendering can be delayed, No telemetry, metadata-free local logs, Account switching flow (+10 more)

### Community 27 - "Package Dependencies"
Cohesion: 0.20
Nodes (17): net10.0-windows10.0.26100.0, Microsoft.Identity.Client.Broker, Microsoft.Identity.Client.Extensions.Msal, Microsoft.NET.Test.Sdk, Microsoft.WindowsAppSDK, System.Security.Cryptography.ProtectedData, xunit.runner.visualstudio, xunit.v3 (+9 more)

### Community 28 - "Graph Response Reader"
Cohesion: 0.21
Nodes (7): JsonElement, IReadOnlyList, string, GraphResponseReader, string, OutlookWebLink, Uri

### Community 29 - "Skeleton Card Rendering"
Cohesion: 0.17
Nodes (11): Detail, Headline, Instance, AuthenticationConfigurationStatus, DeliveryState, JsonSerializerOptions, SkeletonCard, SkeletonCardData (+3 more)

### Community 30 - "Delivery and Suppression Design"
Cohesion: 0.15
Nodes (17): Delivery Stays Outside the Refresh Transaction, Account Switching Deferred to the Following Slice, Cached-First, Activation-Driven Refresh Model, Phase 1 Logout Slice Before Rendering or Settings, Provider Delivery Convergence, StateCommitCoordinator, DisclosureSuppression, DisclosureTombstoneStore (+9 more)

### Community 31 - "Named Event Signals"
Cohesion: 0.16
Nodes (10): MemberData, EventWaitHandle, Func, NamedEventSignal, StateChangeSignal, Exception, Fact, Theory (+2 more)

### Community 32 - "Companion Program Entry"
Cohesion: 0.28
Nodes (7): IPublicClientApplication, Task, Program, Exception, AuthenticationConfigurationResult, PackagedStateResult, STAThread

### Community 33 - "Package Identity Interop"
Cohesion: 0.21
Nodes (7): Exception, IdentityKind, int, LibraryImport, IdentityKind, PackageIdentity, PackageIdentityException

### Community 34 - "Silent Auth Service Internals"
Cohesion: 0.20
Nodes (10): bool, CancellationToken, CancellationTokenSource, int, IPublicClientApplication, Lock, long, Task (+2 more)

### Community 35 - "Agent Guidance and Repo Map"
Cohesion: 0.20
Nodes (12): AGENTS.md agent guidance, Upgrades require -ForceApplicationShutdown, graphify working agreement, PackagedState.Locate identity gate, Repository map (project layout), Why OutlookWidget.Packaging exists, PowerShell 7.6+ packaging host requirement, InteractiveAuthService (+4 more)

### Community 36 - "Refresh Fetch Pipeline"
Cohesion: 0.18
Nodes (12): IRefreshFetcher (production implementation over silent auth plus GraphMailClient), MailboxReadout, RefreshPayload, Supported environment baseline, Optional Focused unread count query, GraphMailClient, GraphResponseReader validation boundary, MailboxRefreshFetcher (+4 more)

### Community 37 - "COM Provider Factory"
Cohesion: 0.24
Nodes (7): PreserveSig, Func, Guid, int, IntPtr, IClassFactory, ProviderFactory

### Community 38 - "Refresh Gates and Graphify Policy"
Cohesion: 0.18
Nodes (11): Package Upgrade with ForceApplicationShutdown, Gate 10 (real Graph refresh measurement), Gate 11 (Board activation and provider recycle), Gate 12 (Graph filter syntax and Focused count), Phase 0 Evidence Report, Phase Ordering Discipline (no Phase 2 UI, no tray fallback first), Production Refresh Vertical Slice, Graphify Working Agreement (+3 more)

### Community 39 - "Delivery Worker"
Cohesion: 0.22
Nodes (8): SemaphoreSlim, bool, CancellationTokenSource, Lock, long, Thread, DeliveryWorker, IWidgetDeliverySink

### Community 41 - "Coordination Invariants and Roadmap"
Cohesion: 0.22
Nodes (10): Cache generation only moves forward, Real cross-process concurrency test suite, Sign-in publishes account and mailbox decision together, Boot-session discriminator, Deferred Intune/RMM deployment design, Error handling and offline state table, Mutation mutex (bounded, never across await), Phased implementation roadmap (+2 more)

### Community 42 - "Consent Failure Troubleshooting"
Cohesion: 0.20
Nodes (10): Consent block is an authorization failure, not a Graph 403, Never record raw tenant or client ID in the committed evidence report, The broker cannot distinguish a dismissed approval dialog from a policy block, Phase 0 evidence report, Phase-aware consent-failure classification, The companion's bounded Signals: diagnostic line, Microsoft.AccountsControl re-registration when the WAM picker never appears, Sign-in symptom/cause/action table (+2 more)

### Community 43 - "Entra Registration and Scope Gates"
Cohesion: 0.20
Nodes (10): Entra ID app registration (single-tenant public client), Delegated Mail.ReadBasic — the only requested scope, WAM broker redirect URI under Mobile and desktop applications, Gate 10 — Mail.ReadBasic returns exactly the approved properties, Gate 12 — Focused unread count, partly measured, Gate 6 — widget action launches the companion, Gate 7 — Outlook launch, spanning both gate groups, Mailbox problems — Graph signal to meaning table (+2 more)

### Community 44 - "Package Version and Install Errors"
Cohesion: 0.27
Nodes (10): Derived package version — commit height plus per-commit revision counter, Every commit changes every assembly (embedded git SHA), Gate 3 — provider cold activation after reboot and package update, Comma in the repository path breaks MSBuild property passing, A pinned widget blocks its own package update (0x80073D02), Squash merge drops commit height below the installed package, "Derived version does not exceed the installed" build-time refusal, HRESULT 0x80073CFB — same identity, different contents (+2 more)

### Community 45 - "Protected Cache Core"
Cohesion: 0.20
Nodes (4): CacheCommitStatus, CacheReadResult, CacheReadStatus, CurrentUserDataProtector

### Community 46 - "Provider COM Registration"
Cohesion: 0.29
Nodes (6): Guid, int, IntPtr, LibraryImport, uint, Program

### Community 47 - "Plan Overview and Refresh Model"
Cohesion: 0.25
Nodes (9): Sources of truth ordering, Outlook Inbox Widget (README overview), One pinned widget instance only, GetWidgetInfos startup instance recovery, IWidgetProvider six-callback contract, Opportunistic five-minute active refresh timer, Outlook Inbox Widget Technical Plan, Twelve-step refresh algorithm (+1 more)

### Community 48 - "State Change Listener"
Cohesion: 0.22
Nodes (7): Action, bool, CancellationTokenSource, EventWaitHandle, long, Thread, StateChangeListener

### Community 49 - "Sign-in Commit and Phase 1 Gap"
Cohesion: 0.29
Nodes (8): CommitInteractiveSelectionAction — atomic identifier and mailbox publication, Remaining Phase 1 gap — cross-account isolation blocked on a second account, DeliveryWorker.RunOnePass broad guard — the unkillable delivery thread, Installed sign-in after logout publishes atomically and delivers (0.4.22.0), ProtectedCache.TryReadGeneration — forward-only generation counter, Selected account record (account-v1.bin) and SelectedAccountStore.Write, Refresh and delivery are recorded separately, "Refresh already in progress" follows the 30-second lease

### Community 50 - "Logout and Token State Evidence"
Cohesion: 0.25
Nodes (8): Gate 9 — provider silent acquisition with a zero parent handle, Installed logout clears state and blocks OS-account fallback (0.4.19.0), Shared MSAL token cache (msal-v1.bin) in the package store, Section 18 tray/popover fallback branch closed on evidence, Clear interrupted operations — explicit recovery for orphaned tombstones, Message details hidden — four suppression causes in order, Reading the provider's token state from the large card, Sign-out reported a failure — what remains true in that state

### Community 51 - "Broker Client"
Cohesion: 0.29
Nodes (6): Func, IntPtr, IPublicClientApplication, string, Task, BrokerClient

### Community 52 - "Provider Lifecycle Gates"
Cohesion: 0.29
Nodes (7): Gate 11 — cached-first refresh and cross-process invalidation, Gate 4 — GetWidgetInfos() restores instances and CustomState round trip, Opportunistic five-minute active timer measured on 0.4.18.0, Provider lifetime is demand-driven, not pin-driven, StateChangeListener and the two named cross-process events, Recycle the provider without unpinning, Companion signed in but the widget still says sign-in required

### Community 53 - "Asset Generation Script"
Cohesion: 0.57
Nodes (5): New-AppIcon(), New-Canvas(), New-GradientBrush(), New-RoundedPath(), New-WidgetScreenshot()

### Community 55 - "Widget Discovery Gates"
Cohesion: 0.33
Nodes (6): A check that cannot fail is not evidence, Gate 2 — discoverable and pinnable in the Widgets Board, Gate 5 superseded — Board allows only one instance per definition, Rendered-surface defects are only visible by looking, Qualified asset variants need a resources.pri from MakePri, Widget does not appear — four distinct causes

### Community 57 - "Self-Consent Policy Block"
Cohesion: 0.60
Nodes (5): microsoft-user-allow-default-consent-apps policy, Try self-consent before involving an administrator, Gate 8 — split: WAM sign-in passes, self-consent fails, Reference-tenant detail must not leak into portable text, "Approval required" despite a permissive-looking user-consent setting

### Community 58 - "Packaged State Location"
Cohesion: 0.40
Nodes (5): LocalApplicationData is not redirected for a packaged full-trust app, OutlookWidget.Packaging — the fifth project the plan did not list, PackagedState.Locate — refuse to resolve state without package identity, Solution and project builds write the provider to different directories, The cache is reconstructible and has no migration path

### Community 59 - "Query Memory: What's Next"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: What's next?, Source Nodes

### Community 60 - "Query Memory: Graphify Gitignore"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Should graphify files be gitignored?, Source Nodes

### Community 61 - "Query Memory: Next Steps"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: proceed with next steps, Source Nodes

### Community 62 - "Query Memory: Commit Review"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Address that comment, but before committing, proceed with those recommendations and commit all at once for review., Source Nodes

### Community 64 - "Local Authentication Config"
Cohesion: 0.67
Nodes (3): authentication.local.json — git-ignored real identifiers, AuthenticationConfiguration.Load, Scope and authority are deliberately not configurable

## Ambiguous Edges - Review These
- `Delegated Mail.ReadBasic — the only requested scope` → `Outlook will not open — New Outlook only, no Classic fallback`  [AMBIGUOUS]
  docs/troubleshooting.md · relation: conceptually_related_to
- `Package Upgrade with ForceApplicationShutdown` → `Machine-Local Graphify Artifacts (interpreter/root files, caches, query stamps, vocabulary, session memory, dated snapshots)`  [AMBIGUOUS]
  graphify-out/memory/query_20260803_132739_should_graphify_files_be_gitignored.md · relation: semantically_similar_to

## Knowledge Gaps
- **79 isolated node(s):** `Answer`, `Outcome`, `Source Nodes`, `Answer`, `Outcome` (+74 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **5 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `Delegated Mail.ReadBasic — the only requested scope` and `Outlook will not open — New Outlook only, no Classic fallback`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **What is the exact relationship between `Package Upgrade with ForceApplicationShutdown` and `Machine-Local Graphify Artifacts (interpreter/root files, caches, query stamps, vocabulary, session memory, dated snapshots)`?**
  _Edge tagged AMBIGUOUS (relation: semantically_similar_to) - confidence is low._
- **Why does `IOperationalLogger` connect `State Commit Coordinator` to `Graph HTTP Client Types`, `Delivery Queue and Work`, `Account Selection and Authorization Records`, `Account Switch and Disclosure Modes`, `Silent Auth Service Internals`, `Delivery Worker`, `Operational Logging and Timers`, `Outlook Launcher`, `Authentication Configuration`, `Selected Account Store`, `State Change Listener`, `Interactive Authentication Service`, `Protected Cache Generation`, `Mutation Mutex Primitive`, `Refresh Lease Record`?**
  _High betweenness centrality (0.113) - this node is a cross-community bridge._
- **Why does `OutlookWidget.Core.Refresh` connect `Project Namespace Structure` to `Account Switch and Disclosure Modes`, `Coordination Bounds and Static Analysis`, `Protected Cache Core`, `Boot Session and Clock`, `State Commit Coordinator`, `Mutation Mutex Primitive`, `Refresh Lease Record`, `Named Event Signals`?**
  _High betweenness centrality (0.074) - this node is a cross-community bridge._
- **Why does `GraphMailClient` connect `Graph HTTP Client Types` to `Project Namespace Structure`, `State Commit Coordinator`, `Mutation Mutex Primitive`?**
  _High betweenness centrality (0.060) - this node is a cross-community bridge._
- **What connects `Answer`, `Outcome`, `Source Nodes` to the rest of the system?**
  _79 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Project Namespace Structure` be split into smaller, more focused modules?**
  _Cohesion score 0.0669753086419753 - nodes in this community are weakly interconnected._