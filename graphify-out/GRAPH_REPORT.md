# Graph Report - Outlook Widget  (2026-08-12)

## Corpus Check
- 129 files · ~178,389 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1709 nodes · 4085 edges · 88 communities (82 shown, 6 thin omitted)
- Extraction: 87% EXTRACTED · 13% INFERRED · 0% AMBIGUOUS · INFERRED: 535 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `bd3e67c5`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- OutlookWidget.Core.Refresh
- .ReadAsync
- .RefreshAsync
- .Current
- DisclosureTombstoneStore
- .SeedState
- Window
- OutlookWidget.Provider widget provider
- ActiveRefreshTimer
- CoordinationStaticAnalysisTests
- MainWindow
- .Record
- StubGraphHandler
- Counts-only privacy rendering
- AuthenticationConfigurationTests
- ProviderRefreshWorker
- SettingsChangeTests
- OutlookLauncher
- IOperationalLogger
- PackageManifestTests
- ProtectedCache
- .Locate
- MailboxTextTests
- SelectedAccountTests
- Non-negotiable engineering invariants
- OutlookWidget.Core.csproj
- CoordinationFixture
- WidgetInstanceRegistry
- Disclosure tombstone (suppress-first)
- .Raise
- .SignInAsync
- .AcquireAsync
- .Read
- Refresh lease record (expiring single-flight)
- CompanionLauncher
- ProviderFactory
- .FetchAsync
- DeliveryWorker
- ProviderCardTests
- MailboxSnapshot
- The broker cannot distinguish a dismissed approval dialog from a policy block
- Entra ID app registration (single-tenant public client)
- Derived package version — commit height plus per-commit revision counter
- InboxCard
- MailboxTimeTests
- AuthenticationOutcomeTests
- .CreateAsync
- Installed sign-in after logout publishes atomically and delivers (0.4.22.0)
- Installed logout clears state and blocks OS-account fallback (0.4.19.0)
- MutationLock
- Provider lifetime is demand-driven, not pin-driven
- New-Assets.ps1
- Program
- Rendered-surface defects are only visible by looking
- Gate 8 — split: WAM sign-in passes, self-consent fails
- PackagedState.Locate — refuse to resolve state without package identity
- SilentAuthProbe
- IDataProtector
- .Main
- AuthenticationConfiguration.Load
- Build and test command discipline
- Gate 1 — signed MSIX installs and certificate can be trusted
- AGENTS.md Instructions
- Phase 0 acceptance gates
- ProtectedCache
- SelectedAccountStore
- PackageIdentity
- .Describe
- GraphMailClient
- .Only_states_a_sign_in_can_fix_offer_one
- StateChangeListener
- Q: What's next?
- CompanionWinUiTests
- App
- .TryDeserialize
- WidgetProvider
- Q: Should graphify files be gitignored?
- Q: proceed with next steps
- Q: Address that comment, but before committing, proceed with those recommendations and commit all at once for review.
- Q: Daily bug scan: scan commits since 2026-08-06T15:54:14.481Z for concrete defects
- Q: merge, delete branch, sync. then continue with next steps.
- MailboxRefreshFetcher

## God Nodes (most connected - your core abstractions)
1. `OutlookWidget.Core.Refresh` - 54 edges
2. `GraphMailClientTests` - 42 edges
3. `OutlookWidget.Core.Caching` - 39 edges
4. `IOperationalLogger` - 37 edges
5. `SelectedAccountTests` - 36 edges
6. `OutlookWidget.Core.Diagnostics` - 35 edges
7. `DisclosureTombstoneStore` - 34 edges
8. `OutlookWidget.Core.Tests` - 33 edges
9. `CoordinationStaticAnalysisTests` - 32 edges
10. `CoordinationPaths` - 30 edges

## Surprising Connections (you probably didn't know these)
- `Boot-session discriminator` --semantically_similar_to--> `Cache generation only moves forward`  [INFERRED] [semantically similar]
  TECHNICAL_PLAN.md → AGENTS.md
- `Approved Graph field set boundary` --semantically_similar_to--> `No access or refresh token persisted`  [INFERRED] [semantically similar]
  AGENTS.md → README.md
- `Real cross-process concurrency test suite` --references--> `Refresh lease record (expiring single-flight)`  [EXTRACTED]
  README.md → TECHNICAL_PLAN.md
- `SelectedAccountTests` --references--> `AuthenticationOptions`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/SelectedAccountTests.cs → src/OutlookWidget.Core/Authentication/AuthenticationOptions.cs
- `FileOperationalLoggerTests` --references--> `CoordinationPaths`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/FileOperationalLoggerTests.cs → src/OutlookWidget.Core/Caching/CoordinationPaths.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Cross-process coordination core** — technical_plan_refreshcoordinator, technical_plan_mutation_mutex, technical_plan_refresh_lease_record, technical_plan_protectedcache, technical_plan_disclosure_tombstone, technical_plan_statechangelistener, technical_plan_sole_delivery_authority [EXTRACTED 1.00]
- **Two-process authentication flow** — technical_plan_brokerclient, technical_plan_interactiveauthservice, technical_plan_silentauthservice, technical_plan_selectedaccountstore, technical_plan_authorizationstatestore, technical_plan_authenticationfailures, technical_plan_shared_msal_token_cache [EXTRACTED 1.00]
- **Suppress-first disclosure-reducing operations** — technical_plan_logout_ordering, technical_plan_account_switching, technical_plan_counts_only_privacy_mode, technical_plan_disclosure_tombstone, readme_clear_interrupted_operations [EXTRACTED 1.00]
- **Sign-in, consent, and silent-acquisition flow across companion and provider** — docs_app_registration_entra_app_registration, docs_app_registration_wam_broker_redirect_uri, docs_app_registration_microsoft_managed_consent_policy, docs_phase0_evidence_gate_8_split_result, docs_phase0_evidence_gate_9_silent_zero_handle, docs_phase0_evidence_shared_msal_token_cache, docs_phase0_evidence_selected_account_store_record, docs_troubleshooting_signin_symptom_table [INFERRED 0.85]
- **Package version derivation and the install failures it prevents** — docs_phase0_evidence_derived_package_version, docs_phase0_evidence_every_commit_changes_every_assembly, docs_phase0_evidence_squash_merge_drops_commit_height, docs_phase0_evidence_pinned_widget_blocks_package_update, docs_troubleshooting_hresult_0x80073cfb, docs_troubleshooting_hresult_0x80073d02, docs_troubleshooting_derived_version_does_not_exceed_installed, docs_troubleshooting_never_uninstall_to_recover [EXTRACTED 1.00]
- **Fail-closed disclosure, state location, and forward-only generation** — docs_troubleshooting_details_hidden_four_cases, docs_troubleshooting_clear_interrupted_operations, docs_troubleshooting_signout_reported_failure, docs_phase0_evidence_packagedstate_locate_fail_closed, docs_phase0_evidence_protectedcache_tryreadgeneration, docs_phase0_evidence_installed_logout_measurement [INFERRED 0.85]

## Communities (88 total, 6 thin omitted)

### Community 0 - "OutlookWidget.Core.Refresh"
Cohesion: 0.06
Nodes (24): OutlookWidget.Core.Tests.TestInfrastructure, OutlookWidget.Core.Tests, OutlookWidget.Core.Authentication, OutlookWidget.Provider, OutlookWidget.Packaging, OutlookWidget.Core.Diagnostics, OutlookWidget.Core.Graph, OutlookWidget.Core.Refresh (+16 more)

### Community 1 - ".ReadAsync"
Cohesion: 0.09
Nodes (22): GraphResponse, HttpClient, bool, CancellationToken, HttpResponseMessage, HttpStatusCode, int, string (+14 more)

### Community 2 - ".RefreshAsync"
Cohesion: 0.12
Nodes (18): CancellationToken, Lock, long, Task, TimeSpan, IDeliveryRequester, IRefreshFetcher, RefreshCoordinator (+10 more)

### Community 3 - ".Current"
Cohesion: 0.16
Nodes (11): TimeSpan, BootSessionStamp, DateTimeOffset, ISystemClock, SystemClock, Fact, BootSessionStampTests, DateTimeOffset (+3 more)

### Community 4 - "DisclosureTombstoneStore"
Cohesion: 0.07
Nodes (21): ActiveOperationRegistry, HashSet, Func, Task, AccountSwitchCoordinator, AccountSwitchResult, DisclosureMode, Action (+13 more)

### Community 5 - ".SeedState"
Cohesion: 0.06
Nodes (22): Process, Fact, Func, TimeSpan, DeliveryWorkerTests, Fact, MutationMutexTests, Fact (+14 more)

### Community 6 - "Window"
Cohesion: 0.17
Nodes (15): ClearInterruptedButton, OperationInfoBar, OperationProgress, PrivacyToggleButton, ShowDiagnosticsButton, SignInButton, SignOutButton, StatusTextBox (+7 more)

### Community 7 - "OutlookWidget.Provider widget provider"
Cohesion: 0.19
Nodes (13): AGENTS.md agent guidance, Upgrades require -ForceApplicationShutdown, graphify working agreement, Repository map (project layout), PowerShell 7.6+ packaging host requirement, BrokerClient, InteractiveAuthService, OutlookWidget.App companion (+5 more)

### Community 8 - "ActiveRefreshTimer"
Cohesion: 0.11
Nodes (17): DueTime, ITimer, ManualTimer, Period, Action, bool, ITimer, Lock (+9 more)

### Community 9 - "CoordinationStaticAnalysisTests"
Cohesion: 0.08
Nodes (10): TimeSpan, CoordinationBounds, Fact, CompanionPrivacyToggleTests, Fact, IEnumerable, string, CoordinationStaticAnalysisTests (+2 more)

### Community 10 - "MainWindow"
Cohesion: 0.22
Nodes (7): RoutedEventArgs, Func, int, IntPtr, Task, CompanionCommands, MainWindow

### Community 11 - ".Record"
Cohesion: 0.18
Nodes (8): TimeSpan, Guid, JsonSerializerOptions, LeaseRecord, CancellationToken, Guid, LeaseClaim, RefreshLeaseStore

### Community 12 - "StubGraphHandler"
Cohesion: 0.07
Nodes (17): ConcurrentBag, HttpMessageHandler, HttpRequestMessage, Memory, SeekOrigin, Stream, CancellationToken, HttpResponseMessage (+9 more)

### Community 13 - "Counts-only privacy rendering"
Cohesion: 0.50
Nodes (4): No telemetry, metadata-free local logs, Adaptive Cards schema 1.5 rendering, Counts-only privacy rendering, OperationalLogger

### Community 14 - "AuthenticationConfigurationTests"
Cohesion: 0.16
Nodes (10): Guid, JsonSerializerOptions, string, AuthenticationConfiguration, ConfigurationFile, Fact, InlineData, string (+2 more)

### Community 15 - "ProviderRefreshWorker"
Cohesion: 0.07
Nodes (31): Queue, RefreshWork, DeliveryRequestOutcome, RefreshOutcome, RefreshResult, RefreshTrigger, Func, long (+23 more)

### Community 16 - "SettingsChangeTests"
Cohesion: 0.07
Nodes (22): DirectoryRemover, IDisposable, LockedWriteOutcome, Func, LockedWriteOutcome, SettingsChangeCoordinator, SettingsChangeOutcome, SettingsChangeResult (+14 more)

### Community 17 - "OutlookLauncher"
Cohesion: 0.20
Nodes (9): Func, IEnumerable, ProcessStartInfo, string, OutlookLauncher, OutlookLaunchResult, OutlookLaunchStrategy, StartInfo (+1 more)

### Community 19 - "IOperationalLogger"
Cohesion: 0.17
Nodes (18): SignOutCoordinator, string, CoordinationPaths, IOperationalLogger, byte, CancellationToken, long, string (+10 more)

### Community 20 - "PackageManifestTests"
Cohesion: 0.20
Nodes (7): Height, Fact, string, PackageManifestTests, Width, XDocument, XElement

### Community 21 - "ProtectedCache"
Cohesion: 0.21
Nodes (7): GenerationReadStatus, byte, int, CacheCommitResult, GenerationReadStatus, ProtectedCache, SuppressMessage

### Community 23 - ".Locate"
Cohesion: 0.20
Nodes (7): Func, Fact, string, CoordinationPathsTests, Fact, string, PackagedStateTests

### Community 24 - "MailboxTextTests"
Cohesion: 0.10
Nodes (12): IReadOnlyList, JsonElement, string, GraphResponseReader, string, OutlookWebLink, string, MailboxText (+4 more)

### Community 25 - "SelectedAccountTests"
Cohesion: 0.12
Nodes (9): AccountId, IAccount, SelectedAccountResult, IAccount, IReadOnlyList, Fact, string, SelectedAccountTests (+1 more)

### Community 26 - "Non-negotiable engineering invariants"
Cohesion: 0.21
Nodes (12): COM class ID agreement in three places, Non-negotiable engineering invariants, Derived MSIX package version, PackagedState.Locate identity gate, Delivery thread must be unkillable, Why OutlookWidget.Packaging exists, Final convergence, not retraction, MSIX persistent identity / publisher continuity (+4 more)

### Community 27 - "OutlookWidget.Core.csproj"
Cohesion: 0.10
Nodes (20): Microsoft.Identity.Client.Broker, Microsoft.Identity.Client.Extensions.Msal, Microsoft.NET.Test.Sdk, System.Security.Cryptography.ProtectedData, xunit.runner.visualstudio, xunit.v3, net10.0-windows10.0.26100.0, Microsoft.Identity.Client (+12 more)

### Community 28 - "CoordinationFixture"
Cohesion: 0.10
Nodes (19): Count, Id, Outcome, Func, long, TimeSpan, FileOperationalLogger, OperationalEventId (+11 more)

### Community 29 - "WidgetInstanceRegistry"
Cohesion: 0.23
Nodes (7): Func, WidgetDeliverySink, Dictionary, Func, Lock, WidgetInstance, WidgetInstanceRegistry

### Community 30 - "Disclosure tombstone (suppress-first)"
Cohesion: 0.39
Nodes (8): Clear interrupted operations recovery control, Real cross-process concurrency test suite, Sign-in publishes account and mailbox decision together, Account switching flow, Disclosure tombstone (suppress-first), Logout suppress-first ordering, Mutation mutex (bounded, never across await), SelectedAccountStore

### Community 31 - ".Raise"
Cohesion: 0.14
Nodes (11): MemberData, EventWaitHandle, Func, NamedEventSignal, SignInCompletedSignal, StateChangeSignal, Exception, Fact (+3 more)

### Community 32 - ".SignInAsync"
Cohesion: 0.38
Nodes (4): CompanionOperationResult, Task, AuthenticationConfigurationResult, PackagedStateResult

### Community 33 - ".AcquireAsync"
Cohesion: 0.14
Nodes (14): AccountSelection, CancellationToken, Func, IPublicClientApplication, string, Task, InteractiveAuthService, CancellationToken (+6 more)

### Community 34 - ".Read"
Cohesion: 0.08
Nodes (25): ImmutableArray, AccountSelectionResult, AuthenticationOptions, DateTimeOffset, Guid, JsonSerializerOptions, AuthorizationRecord, AuthorizationStateStore (+17 more)

### Community 35 - "Refresh lease record (expiring single-flight)"
Cohesion: 0.38
Nodes (7): Phase-aware authentication classification, AuthenticationFailures classifier, AuthorizationStateStore, Error handling and offline state table, Gate 8 split (brokered sign-in vs self-consent), Refresh lease record (expiring single-flight), Tenant consent-policy block on self-consent

### Community 36 - "CompanionLauncher"
Cohesion: 0.22
Nodes (6): Func, IEnumerable, ProcessStartInfo, string, CompanionLauncher, WidgetActionInvokedArgs

### Community 37 - "ProviderFactory"
Cohesion: 0.24
Nodes (7): PreserveSig, Func, Guid, int, IntPtr, IClassFactory, ProviderFactory

### Community 38 - ".FetchAsync"
Cohesion: 0.20
Nodes (8): CancellationToken, Task, MailboxRefreshAccess, RefreshPayload, Fact, Guid, Task, MailboxRefreshFetcherTests

### Community 39 - "DeliveryWorker"
Cohesion: 0.15
Nodes (11): SemaphoreSlim, bool, CancellationTokenSource, Lock, long, Thread, DeliveryWorker, DeliveryState (+3 more)

### Community 40 - "ProviderCardTests"
Cohesion: 0.18
Nodes (5): Action, Fact, JsonElement, string, ProviderCardTests

### Community 41 - "MailboxSnapshot"
Cohesion: 0.13
Nodes (12): DetailSuppression, IReadOnlyList, MailboxReadout, DateTimeOffset, Guid, int, IReadOnlyList, JsonSerializerOptions (+4 more)

### Community 42 - "The broker cannot distinguish a dismissed approval dialog from a policy block"
Cohesion: 0.20
Nodes (10): Consent block is an authorization failure, not a Graph 403, Never record raw tenant or client ID in the committed evidence report, The broker cannot distinguish a dismissed approval dialog from a policy block, Phase 0 evidence report, Phase-aware consent-failure classification, The companion's bounded Signals: diagnostic line, Microsoft.AccountsControl re-registration when the WAM picker never appears, Sign-in symptom/cause/action table (+2 more)

### Community 43 - "Entra ID app registration (single-tenant public client)"
Cohesion: 0.20
Nodes (10): Entra ID app registration (single-tenant public client), Delegated Mail.ReadBasic — the only requested scope, WAM broker redirect URI under Mobile and desktop applications, Gate 10 — Mail.ReadBasic returns exactly the approved properties, Gate 12 — Focused unread count, partly measured, Gate 6 — widget action launches the companion, Gate 7 — Outlook launch, spanning both gate groups, Mailbox problems — Graph signal to meaning table (+2 more)

### Community 44 - "Derived package version — commit height plus per-commit revision counter"
Cohesion: 0.27
Nodes (10): Derived package version — commit height plus per-commit revision counter, Every commit changes every assembly (embedded git SHA), Gate 3 — provider cold activation after reboot and package update, Comma in the repository path breaks MSBuild property passing, A pinned widget blocks its own package update (0x80073D02), Squash merge drops commit height below the installed package, "Derived version does not exceed the installed" build-time refusal, HRESULT 0x80073CFB — same identity, different contents (+2 more)

### Community 45 - "InboxCard"
Cohesion: 0.12
Nodes (15): CardSituation, Detail, Headline, MessageRow, DateTimeOffset, int, IReadOnlyList, JsonSerializerOptions (+7 more)

### Community 46 - "MailboxTimeTests"
Cohesion: 0.25
Nodes (7): DateTimeOffset, MailboxTime, DateTimeOffset, Fact, InlineData, Theory, MailboxTimeTests

### Community 48 - ".CreateAsync"
Cohesion: 0.29
Nodes (6): Func, IntPtr, IPublicClientApplication, string, Task, BrokerClient

### Community 49 - "Installed sign-in after logout publishes atomically and delivers (0.4.22.0)"
Cohesion: 0.29
Nodes (8): CommitInteractiveSelectionAction — atomic identifier and mailbox publication, Remaining Phase 1 gap — cross-account isolation blocked on a second account, DeliveryWorker.RunOnePass broad guard — the unkillable delivery thread, Installed sign-in after logout publishes atomically and delivers (0.4.22.0), ProtectedCache.TryReadGeneration — forward-only generation counter, Selected account record (account-v1.bin) and SelectedAccountStore.Write, Refresh and delivery are recorded separately, "Refresh already in progress" follows the 30-second lease

### Community 50 - "Installed logout clears state and blocks OS-account fallback (0.4.19.0)"
Cohesion: 0.25
Nodes (8): Gate 9 — provider silent acquisition with a zero parent handle, Installed logout clears state and blocks OS-account fallback (0.4.19.0), Shared MSAL token cache (msal-v1.bin) in the package store, Section 18 tray/popover fallback branch closed on evidence, Clear interrupted operations — explicit recovery for orphaned tombstones, Message details hidden — four suppression causes in order, Reading the provider's token state from the large card, Sign-out reported a failure — what remains true in that state

### Community 51 - "MutationLock"
Cohesion: 0.20
Nodes (9): Mutex, bool, CancellationToken, int, MutationLock, MutationLockOutcome, MutationMutex, ThrowingCommitAction (+1 more)

### Community 52 - "Provider lifetime is demand-driven, not pin-driven"
Cohesion: 0.29
Nodes (7): Gate 11 — cached-first refresh and cross-process invalidation, Gate 4 — GetWidgetInfos() restores instances and CustomState round trip, Opportunistic five-minute active timer measured on 0.4.18.0, Provider lifetime is demand-driven, not pin-driven, StateChangeListener and the two named cross-process events, Recycle the provider without unpinning, Companion signed in but the widget still says sign-in required

### Community 53 - "New-Assets.ps1"
Cohesion: 0.57
Nodes (5): New-AppIcon(), New-Canvas(), New-GradientBrush(), New-RoundedPath(), New-WidgetScreenshot()

### Community 54 - "Program"
Cohesion: 0.12
Nodes (14): AppActivationArguments, AppInstance, DispatcherQueue, MarshalAs, Action, Func, IntPtr, IPublicClientApplication (+6 more)

### Community 55 - "Rendered-surface defects are only visible by looking"
Cohesion: 0.33
Nodes (6): A check that cannot fail is not evidence, Gate 2 — discoverable and pinnable in the Widgets Board, Gate 5 superseded — Board allows only one instance per definition, Rendered-surface defects are only visible by looking, Qualified asset variants need a resources.pri from MakePri, Widget does not appear — four distinct causes

### Community 57 - "Gate 8 — split: WAM sign-in passes, self-consent fails"
Cohesion: 0.60
Nodes (5): microsoft-user-allow-default-consent-apps policy, Try self-consent before involving an administrator, Gate 8 — split: WAM sign-in passes, self-consent fails, Reference-tenant detail must not leak into portable text, "Approval required" despite a permissive-looking user-consent setting

### Community 58 - "PackagedState.Locate — refuse to resolve state without package identity"
Cohesion: 0.40
Nodes (5): LocalApplicationData is not redirected for a packaged full-trust app, OutlookWidget.Packaging — the fifth project the plan did not list, PackagedState.Locate — refuse to resolve state without package identity, Solution and project builds write the provider to different directories, The cache is reconstructible and has no migration path

### Community 60 - "SilentAuthProbe"
Cohesion: 0.19
Nodes (10): bool, CancellationToken, CancellationTokenSource, int, IPublicClientApplication, Lock, long, Task (+2 more)

### Community 61 - "IDataProtector"
Cohesion: 0.08
Nodes (10): CacheCommitStatus, CacheReadResult, CacheReadStatus, CurrentUserDataProtector, IDataProtector, FailingProtector, FailingProtector, FailingProtector (+2 more)

### Community 62 - ".Main"
Cohesion: 0.33
Nodes (6): Guid, int, IntPtr, LibraryImport, uint, Program

### Community 64 - "AuthenticationConfiguration.Load"
Cohesion: 0.67
Nodes (3): authentication.local.json — git-ignored real identifiers, AuthenticationConfiguration.Load, Scope and authority are deliberately not configurable

### Community 72 - "Phase 0 acceptance gates"
Cohesion: 0.19
Nodes (13): Scope and phase gate discipline, Sources of truth ordering, Privacy-state rendering can be delayed, BrokerClient.NoParentWindow named member, Outlook Inbox Widget (README overview), One pinned widget instance only, Tray/popover fallback branch (closed), Gate 9 zero-HWND silent acquisition (+5 more)

### Community 73 - "ProtectedCache"
Cohesion: 0.18
Nodes (12): Cache generation only moves forward, Boot-session discriminator, DPAPI cache protection and atomic replace, Deferred Intune/RMM deployment design, MailboxRefreshFetcher, MailboxSnapshot, Opportunistic five-minute active refresh timer, Phased implementation roadmap (+4 more)

### Community 74 - "SelectedAccountStore"
Cohesion: 0.31
Nodes (5): byte, Guid, JsonSerializerOptions, AccountRecord, SelectedAccountStore

### Community 75 - "PackageIdentity"
Cohesion: 0.21
Nodes (7): Exception, IdentityKind, int, LibraryImport, IdentityKind, PackageIdentity, PackageIdentityException

### Community 76 - ".Describe"
Cohesion: 0.29
Nodes (4): Exception, string, AuthenticationFailures, AuthenticationPhase

### Community 77 - "GraphMailClient"
Cohesion: 0.18
Nodes (12): Approved Graph field set boundary, No access or refresh token persisted, Supported environment baseline, Single-tenant Entra app registration, Optional Focused unread count query, GraphMailClient, GraphResponseReader validation boundary, Delegated Mail.ReadBasic permission (+4 more)

### Community 79 - "StateChangeListener"
Cohesion: 0.22
Nodes (7): Action, bool, CancellationTokenSource, EventWaitHandle, long, Thread, StateChangeListener

### Community 80 - "Q: What's next?"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: What's next?, Source Nodes

### Community 82 - "App"
Cohesion: 0.29
Nodes (3): LaunchActivatedEventArgs, Application, App

### Community 83 - ".TryDeserialize"
Cohesion: 0.26
Nodes (5): ReadOnlySpan, Fact, InlineData, Theory, MailboxSnapshotTests

### Community 84 - "WidgetProvider"
Cohesion: 0.18
Nodes (6): IWidgetProvider, Lock, ManualResetEventSlim, WidgetProvider, WidgetContext, WidgetContextChangedArgs

### Community 85 - "Q: Should graphify files be gitignored?"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Should graphify files be gitignored?, Source Nodes

### Community 86 - "Q: proceed with next steps"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: proceed with next steps, Source Nodes

### Community 88 - "Q: Address that comment, but before committing, proceed with those recommendations and commit all at once for review."
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Address that comment, but before committing, proceed with those recommendations and commit all at once for review., Source Nodes

### Community 90 - "Q: Daily bug scan: scan commits since 2026-08-06T15:54:14.481Z for concrete defects"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Daily bug scan: scan commits since 2026-08-06T15:54:14.481Z for concrete defects, Source Nodes

### Community 91 - "Q: merge, delete branch, sync. then continue with next steps."
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: merge, delete branch, sync. then continue with next steps., Source Nodes

### Community 93 - "MailboxRefreshFetcher"
Cohesion: 0.50
Nodes (4): bool, Func, Guid, MailboxRefreshFetcher

## Ambiguous Edges - Review These
- `Delegated Mail.ReadBasic — the only requested scope` → `Outlook will not open — New Outlook only, no Classic fallback`  [AMBIGUOUS]
  docs/troubleshooting.md · relation: conceptually_related_to

## Knowledge Gaps
- **96 isolated node(s):** `InfoBar`, `ProgressRing`, `TextBox`, `net10.0-windows10.0.26100.0`, `Microsoft.Identity.Client` (+91 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **6 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Work-memory lessons

**Preferred sources** — corroborated by past sessions; start here.
- `DisclosureTombstoneStore` (2× useful, score=1.777562835)
- `StateCommitCoordinator` (2× useful, score=1.776283266)
- `Phase 0 evidence report` (2× useful, score=1.774461121)

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `Delegated Mail.ReadBasic — the only requested scope` and `Outlook will not open — New Outlook only, no Classic fallback`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **Why does `IOperationalLogger` connect `IOperationalLogger` to `.ReadAsync`, `.RefreshAsync`, `DisclosureTombstoneStore`, `ActiveRefreshTimer`, `.Record`, `AuthenticationConfigurationTests`, `ProviderRefreshWorker`, `SettingsChangeTests`, `OutlookLauncher`, `ProtectedCache`, `CoordinationFixture`, `WidgetInstanceRegistry`, `.AcquireAsync`, `.Read`, `CompanionLauncher`, `DeliveryWorker`, `MutationLock`, `SilentAuthProbe`, `SelectedAccountStore`, `StateChangeListener`, `WidgetProvider`?**
  _High betweenness centrality (0.093) - this node is a cross-community bridge._
- **Why does `OutlookWidget.Core.Refresh` connect `OutlookWidget.Core.Refresh` to `.Current`, `DisclosureTombstoneStore`, `DeliveryWorker`, `CoordinationStaticAnalysisTests`, `.Record`, `ProviderRefreshWorker`, `SettingsChangeTests`, `MutationLock`, `IOperationalLogger`, `IDataProtector`, `.Raise`?**
  _High betweenness centrality (0.085) - this node is a cross-community bridge._
- **What connects `InfoBar`, `ProgressRing`, `TextBox` to the rest of the system?**
  _96 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `OutlookWidget.Core.Refresh` be split into smaller, more focused modules?**
  _Cohesion score 0.06153846153846154 - nodes in this community are weakly interconnected._
- **Should `.ReadAsync` be split into smaller, more focused modules?**
  _Cohesion score 0.09057609057609058 - nodes in this community are weakly interconnected._
- **Should `.RefreshAsync` be split into smaller, more focused modules?**
  _Cohesion score 0.1243885394828791 - nodes in this community are weakly interconnected._