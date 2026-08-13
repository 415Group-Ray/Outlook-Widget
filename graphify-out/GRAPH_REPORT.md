# Graph Report - Outlook Widget  (2026-08-13)

## Corpus Check
- 134 files · ~186,962 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1753 nodes · 4250 edges · 88 communities (78 shown, 10 thin omitted)
- Extraction: 87% EXTRACTED · 13% INFERRED · 0% AMBIGUOUS · INFERRED: 555 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `d16ccc31`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- .RefreshAsync
- OutlookWidget.Core.Refresh
- .SeedState
- IOperationalLogger
- .Read
- .ReadAsync
- DisclosureTombstoneStore
- SettingsChangeTests
- TokenAcquisitionResult
- CoordinationStaticAnalysisTests
- MailboxSnapshot
- SelectedAccountTests
- MailboxTextTests
- Microsoft Graph
- StubGraphHandler
- CoordinationPaths
- InboxCard
- SilentAuthProbe
- ProviderCardTests
- ActiveRefreshTimer
- AuthenticationConfigurationTests
- Program
- Gate 8 — split: WAM sign-in passes, self-consent fails
- RefreshPresentation
- .Current
- AuthenticationOutcomeTests
- PackageManifestTests
- .Raise
- ProtectedCache
- .Locate
- OutlookWidget.Core.csproj
- MailboxTimeTests
- MainWindow
- .SignInAsync
- Window
- .Record
- PackageIdentity
- DeliveryWorker
- WidgetInstanceRegistry
- WidgetProvider
- ProviderFactory
- .FetchAsync
- OutlookLauncher
- .TryDeserialize
- Derived package version — commit height plus per-commit revision counter
- CompanionLauncher
- CompanionWinUiTests
- .CreateAsync
- Entra ID app registration (single-tenant public client)
- App
- New-Assets.ps1
- Disclosure Suppression Tombstones
- DPAPI-Protected Local Cache
- SettingsChangeCoordinator
- Cross-Process Coordination Core
- PackagedState.Locate — refuse to resolve state without package identity
- AuthenticationConfiguration.Load
- Provider lifetime is demand-driven, not pin-driven
- Gate 1 — signed MSIX installs and certificate can be trusted
- Gate 6 — widget action launches the companion
- Monotonic Cache Generation
- Packaged State Identity Boundary
- AGENTS.md Instructions
- Consent block is an authorization failure, not a Graph 403
- Metadata-Free Local Diagnostics
- One Widget Instance Limitation
- .ToRow
- MutationLock
- IDataProtector
- SelectedAccountStore
- .Main
- StateChangeListener
- TokenAcquisitionStatus
- .Only_states_a_sign_in_can_fix_offer_one
- Q: What's next?
- Q: Should graphify files be gitignored?
- Q: proceed with next steps
- Q: Address that comment, but before committing, proceed with those recommendations and commit all at once for review.
- Q: Daily bug scan: scan commits since 2026-08-06T15:54:14.481Z for concrete defects
- Q: merge, delete branch, sync. then continue with next steps.
- Q: Daily bug scan: scan recent commits for authorization, suppression, refresh, peer, lease, delivery, mailbox, cache, loading, error, account, and provider defects.
- MailboxRefreshFetcher

## God Nodes (most connected - your core abstractions)
1. `OutlookWidget.Core.Refresh` - 58 edges
2. `OutlookWidget.Core.Caching` - 43 edges
3. `GraphMailClientTests` - 42 edges
4. `IOperationalLogger` - 40 edges
5. `CoordinationPaths` - 37 edges
6. `OutlookWidget.Core.Diagnostics` - 37 edges
7. `SelectedAccountTests` - 36 edges
8. `OutlookWidget.Core.Tests` - 35 edges
9. `DisclosureTombstoneStore` - 34 edges
10. `CoordinationStaticAnalysisTests` - 33 edges

## Surprising Connections (you probably didn't know these)
- `SelectedAccountTests` --references--> `AuthenticationOptions`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/SelectedAccountTests.cs → src/OutlookWidget.Core/Authentication/AuthenticationOptions.cs
- `AuthorizationSuppressionTests` --references--> `CoordinationPaths`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/AuthorizationSuppressionTests.cs → src/OutlookWidget.Core/Caching/CoordinationPaths.cs
- `SelectedAccountTests` --references--> `CoordinationPaths`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/SelectedAccountTests.cs → src/OutlookWidget.Core/Caching/CoordinationPaths.cs
- `SettingsChangeTests` --references--> `CoordinationPaths`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/SettingsChangeTests.cs → src/OutlookWidget.Core/Caching/CoordinationPaths.cs
- `WidgetSettingsTests` --references--> `CoordinationPaths`  [EXTRACTED]
  tests/OutlookWidget.Core.Tests/WidgetSettingsTests.cs → src/OutlookWidget.Core/Caching/CoordinationPaths.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Native Widget Architecture Components** — technical_plan_outlookwidget_app, technical_plan_outlookwidget_provider, technical_plan_outlookwidget_package [EXTRACTED 1.00]
- **Privacy Fail-Closed Mechanisms** — agents_disclosure_suppression, agents_durable_authorization_suppression, agents_selected_account_state_commit, agents_mailbox_text_rendering_boundary [EXTRACTED 1.00]
- **Troubleshooting State Recovery** — docs_troubleshooting_clear_interrupted_operations, docs_troubleshooting_refresh_lease_recovery, docs_troubleshooting_cache_state_recovery [EXTRACTED 1.00]
- **Sign-in, consent, and silent-acquisition flow across companion and provider** — docs_app_registration_entra_app_registration, docs_app_registration_wam_broker_redirect_uri, docs_app_registration_microsoft_managed_consent_policy, docs_phase0_evidence_gate_8_split_result, docs_phase0_evidence_gate_9_silent_zero_handle, docs_phase0_evidence_shared_msal_token_cache, docs_phase0_evidence_selected_account_store_record [INFERRED 0.85]
- **Fail-closed disclosure, state location, and forward-only generation** — docs_troubleshooting_clear_interrupted_operations, docs_phase0_evidence_packagedstate_locate_fail_closed, docs_phase0_evidence_protectedcache_tryreadgeneration, docs_phase0_evidence_installed_logout_measurement [INFERRED 0.85]

## Communities (88 total, 10 thin omitted)

### Community 0 - ".RefreshAsync"
Cohesion: 0.07
Nodes (37): Queue, RefreshWork, CancellationToken, Lock, long, Task, TimeSpan, DeliveryRequestOutcome (+29 more)

### Community 1 - "OutlookWidget.Core.Refresh"
Cohesion: 0.06
Nodes (23): OutlookWidget.Core.Tests.TestInfrastructure, OutlookWidget.Core.Tests, OutlookWidget.Core.Authentication, OutlookWidget.Provider, OutlookWidget.Packaging, OutlookWidget.Core.Diagnostics, OutlookWidget.Core.Graph, OutlookWidget.Core.Refresh (+15 more)

### Community 2 - ".SeedState"
Cohesion: 0.06
Nodes (23): Process, Fact, Func, TimeSpan, DeliveryWorkerTests, Fact, MutationMutexTests, Fact (+15 more)

### Community 3 - "IOperationalLogger"
Cohesion: 0.18
Nodes (15): SignOutCoordinator, IOperationalLogger, byte, CancellationToken, long, string, ClearStateAction, CommitAccountSwitchStateAction (+7 more)

### Community 4 - ".Read"
Cohesion: 0.07
Nodes (28): ImmutableArray, AuthenticationOptions, DateTimeOffset, Guid, JsonSerializerOptions, AuthorizationRecord, AuthorizationStateStore, Func (+20 more)

### Community 5 - ".ReadAsync"
Cohesion: 0.09
Nodes (22): GraphResponse, HttpClient, bool, CancellationToken, HttpResponseMessage, HttpStatusCode, int, string (+14 more)

### Community 6 - "DisclosureTombstoneStore"
Cohesion: 0.07
Nodes (22): ActiveOperationRegistry, HashSet, Func, Task, AccountSwitchCoordinator, AccountSwitchResult, DisclosureMode, Action (+14 more)

### Community 7 - "SettingsChangeTests"
Cohesion: 0.10
Nodes (15): DirectoryRemover, IDisposable, JsonSerializerOptions, SettingsReadResult, SettingsReadStatus, WidgetSettingsStore, bool, Fact (+7 more)

### Community 8 - "TokenAcquisitionResult"
Cohesion: 0.15
Nodes (14): AccountSelection, CancellationToken, Func, IPublicClientApplication, string, Task, InteractiveAuthService, CancellationToken (+6 more)

### Community 9 - "CoordinationStaticAnalysisTests"
Cohesion: 0.08
Nodes (10): TimeSpan, CoordinationBounds, Fact, CompanionPrivacyToggleTests, Fact, IEnumerable, string, CoordinationStaticAnalysisTests (+2 more)

### Community 10 - "MailboxSnapshot"
Cohesion: 0.16
Nodes (11): IReadOnlyList, MailboxReadout, DateTimeOffset, Guid, int, IReadOnlyList, JsonSerializerOptions, string (+3 more)

### Community 11 - "SelectedAccountTests"
Cohesion: 0.10
Nodes (10): AccountId, IAccount, SelectedAccountResult, IAccount, IReadOnlyList, Fact, string, PassThroughProtector (+2 more)

### Community 12 - "MailboxTextTests"
Cohesion: 0.10
Nodes (12): IReadOnlyList, JsonElement, string, GraphResponseReader, string, OutlookWebLink, string, MailboxText (+4 more)

### Community 13 - "Microsoft Graph"
Cohesion: 0.06
Nodes (38): Gate 8 Consent Split, Mailbox Text Rendering Boundary, Native Surface Decision, Provider-Only Delivery Authority, Unkillable Delivery Thread, Effective Tenant Consent Policy, Mailbox Error Mapping, New Outlook Launch Troubleshooting (+30 more)

### Community 14 - "StubGraphHandler"
Cohesion: 0.07
Nodes (17): ConcurrentBag, HttpMessageHandler, HttpRequestMessage, Memory, SeekOrigin, Stream, CancellationToken, HttpResponseMessage (+9 more)

### Community 15 - "CoordinationPaths"
Cohesion: 0.06
Nodes (28): Count, Id, Outcome, string, CoordinationPaths, Func, long, TimeSpan (+20 more)

### Community 16 - "InboxCard"
Cohesion: 0.13
Nodes (12): CardSituation, Detail, DetailSuppression, Headline, int, JsonSerializerOptions, CardSituation, DetailSuppression (+4 more)

### Community 17 - "SilentAuthProbe"
Cohesion: 0.18
Nodes (10): bool, CancellationToken, CancellationTokenSource, int, IPublicClientApplication, Lock, long, Task (+2 more)

### Community 18 - "ProviderCardTests"
Cohesion: 0.17
Nodes (5): Action, Fact, JsonElement, string, ProviderCardTests

### Community 19 - "ActiveRefreshTimer"
Cohesion: 0.11
Nodes (17): DueTime, ITimer, ManualTimer, Period, Action, bool, ITimer, Lock (+9 more)

### Community 20 - "AuthenticationConfigurationTests"
Cohesion: 0.16
Nodes (10): Guid, JsonSerializerOptions, string, AuthenticationConfiguration, ConfigurationFile, Fact, InlineData, string (+2 more)

### Community 21 - "Program"
Cohesion: 0.12
Nodes (14): AppActivationArguments, AppInstance, DispatcherQueue, MarshalAs, Action, Func, IntPtr, IPublicClientApplication (+6 more)

### Community 22 - "Gate 8 — split: WAM sign-in passes, self-consent fails"
Cohesion: 0.09
Nodes (24): microsoft-user-allow-default-consent-apps policy, Try self-consent before involving an administrator, A check that cannot fail is not evidence, The broker cannot distinguish a dismissed approval dialog from a policy block, CommitInteractiveSelectionAction — atomic identifier and mailbox publication, Remaining Phase 1 gap — cross-account isolation blocked on a second account, DeliveryWorker.RunOnePass broad guard — the unkillable delivery thread, Gate 11 — cached-first refresh and cross-process invalidation (+16 more)

### Community 23 - "RefreshPresentation"
Cohesion: 0.06
Nodes (27): Record, AuthorizationSuppressionReason, AuthorizationSuppressionResult, AuthorizationSuppressionStatus, AuthorizationSuppressionStore, Record, RefreshResult, bool (+19 more)

### Community 24 - ".Current"
Cohesion: 0.16
Nodes (11): TimeSpan, BootSessionStamp, DateTimeOffset, ISystemClock, SystemClock, Fact, BootSessionStampTests, DateTimeOffset (+3 more)

### Community 25 - "AuthenticationOutcomeTests"
Cohesion: 0.20
Nodes (4): Exception, AuthenticationPhase, Fact, AuthenticationOutcomeTests

### Community 26 - "PackageManifestTests"
Cohesion: 0.20
Nodes (7): Height, Fact, string, PackageManifestTests, Width, XDocument, XElement

### Community 27 - ".Raise"
Cohesion: 0.14
Nodes (11): MemberData, EventWaitHandle, Func, NamedEventSignal, SignInCompletedSignal, StateChangeSignal, Exception, Fact (+3 more)

### Community 28 - "ProtectedCache"
Cohesion: 0.23
Nodes (7): GenerationReadStatus, byte, int, CacheCommitResult, GenerationReadStatus, ProtectedCache, SuppressMessage

### Community 29 - ".Locate"
Cohesion: 0.20
Nodes (7): Func, Fact, string, CoordinationPathsTests, Fact, string, PackagedStateTests

### Community 30 - "OutlookWidget.Core.csproj"
Cohesion: 0.10
Nodes (20): Microsoft.Identity.Client.Broker, Microsoft.Identity.Client.Extensions.Msal, Microsoft.NET.Test.Sdk, System.Security.Cryptography.ProtectedData, xunit.runner.visualstudio, xunit.v3, net10.0-windows10.0.26100.0, Microsoft.Identity.Client (+12 more)

### Community 31 - "MailboxTimeTests"
Cohesion: 0.25
Nodes (7): DateTimeOffset, MailboxTime, DateTimeOffset, Fact, InlineData, Theory, MailboxTimeTests

### Community 32 - "MainWindow"
Cohesion: 0.22
Nodes (7): RoutedEventArgs, Func, int, IntPtr, Task, CompanionCommands, MainWindow

### Community 33 - ".SignInAsync"
Cohesion: 0.36
Nodes (4): CompanionOperationResult, Task, AuthenticationConfigurationResult, PackagedStateResult

### Community 34 - "Window"
Cohesion: 0.17
Nodes (15): ClearInterruptedButton, OperationInfoBar, OperationProgress, PrivacyToggleButton, ShowDiagnosticsButton, SignInButton, SignOutButton, StatusTextBox (+7 more)

### Community 35 - ".Record"
Cohesion: 0.19
Nodes (8): TimeSpan, Guid, JsonSerializerOptions, LeaseRecord, CancellationToken, Guid, LeaseClaim, RefreshLeaseStore

### Community 36 - "PackageIdentity"
Cohesion: 0.21
Nodes (7): Exception, IdentityKind, int, LibraryImport, IdentityKind, PackageIdentity, PackageIdentityException

### Community 37 - "DeliveryWorker"
Cohesion: 0.15
Nodes (11): SemaphoreSlim, bool, CancellationTokenSource, Lock, long, Thread, DeliveryWorker, DeliveryState (+3 more)

### Community 38 - "WidgetInstanceRegistry"
Cohesion: 0.23
Nodes (7): Func, WidgetDeliverySink, Dictionary, Func, Lock, WidgetInstance, WidgetInstanceRegistry

### Community 39 - "WidgetProvider"
Cohesion: 0.18
Nodes (6): IWidgetProvider, Lock, ManualResetEventSlim, WidgetProvider, WidgetContext, WidgetContextChangedArgs

### Community 40 - "ProviderFactory"
Cohesion: 0.24
Nodes (7): PreserveSig, Func, Guid, int, IntPtr, IClassFactory, ProviderFactory

### Community 41 - ".FetchAsync"
Cohesion: 0.19
Nodes (8): CancellationToken, Task, MailboxRefreshAccess, RefreshPayload, Fact, Guid, Task, MailboxRefreshFetcherTests

### Community 42 - "OutlookLauncher"
Cohesion: 0.20
Nodes (9): Func, IEnumerable, ProcessStartInfo, string, OutlookLauncher, OutlookLaunchResult, OutlookLaunchStrategy, StartInfo (+1 more)

### Community 43 - ".TryDeserialize"
Cohesion: 0.26
Nodes (5): ReadOnlySpan, Fact, InlineData, Theory, MailboxSnapshotTests

### Community 44 - "Derived package version — commit height plus per-commit revision counter"
Cohesion: 0.18
Nodes (11): Derived Package Version, Derived package version — commit height plus per-commit revision counter, Every commit changes every assembly (embedded git SHA), Gate 3 — provider cold activation after reboot and package update, Comma in the repository path breaks MSBuild property passing, A pinned widget blocks its own package update (0x80073D02), Squash merge drops commit height below the installed package, HRESULT 0x80073CFB (+3 more)

### Community 45 - "CompanionLauncher"
Cohesion: 0.22
Nodes (6): Func, IEnumerable, ProcessStartInfo, string, CompanionLauncher, WidgetActionInvokedArgs

### Community 47 - ".CreateAsync"
Cohesion: 0.29
Nodes (6): Func, IntPtr, IPublicClientApplication, string, Task, BrokerClient

### Community 48 - "Entra ID app registration (single-tenant public client)"
Cohesion: 0.29
Nodes (7): Entra ID app registration (single-tenant public client), Delegated Mail.ReadBasic — the only requested scope, Never record raw tenant or client ID in the committed evidence report, WAM broker redirect URI under Mobile and desktop applications, Gate 10 — Mail.ReadBasic returns exactly the approved properties, Gate 12 — Focused unread count, partly measured, Phase 0 evidence report

### Community 49 - "App"
Cohesion: 0.29
Nodes (3): LaunchActivatedEventArgs, Application, App

### Community 50 - "New-Assets.ps1"
Cohesion: 0.57
Nodes (5): New-AppIcon(), New-Canvas(), New-GradientBrush(), New-RoundedPath(), New-WidgetScreenshot()

### Community 52 - "Disclosure Suppression Tombstones"
Cohesion: 0.40
Nodes (5): Disclosure Suppression Tombstones, Durable Authorization Suppression, Selected Account State Commit, Clear Interrupted Operations, Hidden Message Details

### Community 53 - "DPAPI-Protected Local Cache"
Cohesion: 0.40
Nodes (5): Cache and State Recovery, Suppress-First Disclosure Changes, DPAPI-Protected Local Cache, Cached-First Refresh Model, Two Coordination Primitives

### Community 54 - "SettingsChangeCoordinator"
Cohesion: 0.28
Nodes (7): LockedWriteOutcome, Func, LockedWriteOutcome, SettingsChangeCoordinator, SettingsChangeOutcome, SettingsChangeResult, WidgetSettings

### Community 55 - "Cross-Process Coordination Core"
Cohesion: 0.50
Nodes (4): Cross-Process Coordination Core, Expiring Refresh Lease, MutationLock, Refresh Lease Recovery

### Community 56 - "PackagedState.Locate — refuse to resolve state without package identity"
Cohesion: 0.50
Nodes (4): LocalApplicationData is not redirected for a packaged full-trust app, OutlookWidget.Packaging — the fifth project the plan did not list, PackagedState.Locate — refuse to resolve state without package identity, Solution and project builds write the provider to different directories

### Community 58 - "AuthenticationConfiguration.Load"
Cohesion: 0.67
Nodes (3): authentication.local.json — git-ignored real identifiers, AuthenticationConfiguration.Load, Scope and authority are deliberately not configurable

### Community 59 - "Provider lifetime is demand-driven, not pin-driven"
Cohesion: 0.67
Nodes (3): Gate 4 — GetWidgetInfos() restores instances and CustomState round trip, Opportunistic five-minute active timer measured on 0.4.18.0, Provider lifetime is demand-driven, not pin-driven

### Community 71 - ".ToRow"
Cohesion: 0.33
Nodes (4): MessageRow, DateTimeOffset, IReadOnlyList, InboxCardData

### Community 72 - "MutationLock"
Cohesion: 0.24
Nodes (7): Mutex, bool, CancellationToken, int, MutationLock, MutationLockOutcome, MutationMutex

### Community 73 - "IDataProtector"
Cohesion: 0.18
Nodes (5): CacheCommitStatus, CacheReadResult, CacheReadStatus, CurrentUserDataProtector, IDataProtector

### Community 74 - "SelectedAccountStore"
Cohesion: 0.24
Nodes (6): byte, Guid, JsonSerializerOptions, AccountRecord, SelectedAccountStore, CommitSignedOutStateAction

### Community 75 - ".Main"
Cohesion: 0.33
Nodes (6): Guid, int, IntPtr, LibraryImport, uint, Program

### Community 76 - "StateChangeListener"
Cohesion: 0.22
Nodes (7): Action, bool, CancellationTokenSource, EventWaitHandle, long, Thread, StateChangeListener

### Community 77 - "TokenAcquisitionStatus"
Cohesion: 0.29
Nodes (4): AccountSelectionResult, string, AuthenticationFailures, TokenAcquisitionStatus

### Community 79 - "Q: What's next?"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: What's next?, Source Nodes

### Community 80 - "Q: Should graphify files be gitignored?"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Should graphify files be gitignored?, Source Nodes

### Community 81 - "Q: proceed with next steps"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: proceed with next steps, Source Nodes

### Community 82 - "Q: Address that comment, but before committing, proceed with those recommendations and commit all at once for review."
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Address that comment, but before committing, proceed with those recommendations and commit all at once for review., Source Nodes

### Community 83 - "Q: Daily bug scan: scan commits since 2026-08-06T15:54:14.481Z for concrete defects"
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Daily bug scan: scan commits since 2026-08-06T15:54:14.481Z for concrete defects, Source Nodes

### Community 85 - "Q: merge, delete branch, sync. then continue with next steps."
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: merge, delete branch, sync. then continue with next steps., Source Nodes

### Community 86 - "Q: Daily bug scan: scan recent commits for authorization, suppression, refresh, peer, lease, delivery, mailbox, cache, loading, error, account, and provider defects."
Cohesion: 0.40
Nodes (4): Answer, Outcome, Q: Daily bug scan: scan recent commits for authorization, suppression, refresh, peer, lease, delivery, mailbox, cache, loading, error, account, and provider defects., Source Nodes

### Community 87 - "MailboxRefreshFetcher"
Cohesion: 0.50
Nodes (4): bool, Func, Guid, MailboxRefreshFetcher

## Knowledge Gaps
- **107 isolated node(s):** `InfoBar`, `ProgressRing`, `TextBox`, `net10.0-windows10.0.26100.0`, `Microsoft.Identity.Client` (+102 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **10 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Work-memory lessons

**Preferred sources** — corroborated by past sessions; start here.
- `DisclosureTombstoneStore` (2× useful, score=1.736981712)
- `StateCommitCoordinator` (2× useful, score=1.735731355)
- `Phase 0 evidence report` (2× useful, score=1.733950809)

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `IOperationalLogger` connect `IOperationalLogger` to `.RefreshAsync`, `.Read`, `.ReadAsync`, `DisclosureTombstoneStore`, `TokenAcquisitionResult`, `CoordinationPaths`, `SilentAuthProbe`, `ActiveRefreshTimer`, `AuthenticationConfigurationTests`, `RefreshPresentation`, `ProtectedCache`, `.Record`, `DeliveryWorker`, `WidgetInstanceRegistry`, `WidgetProvider`, `OutlookLauncher`, `CompanionLauncher`, `SettingsChangeCoordinator`, `MutationLock`, `SelectedAccountStore`, `StateChangeListener`?**
  _High betweenness centrality (0.112) - this node is a cross-community bridge._
- **Why does `CoordinationPaths` connect `CoordinationPaths` to `.SignInAsync`, `.Record`, `.Read`, `IOperationalLogger`, `DisclosureTombstoneStore`, `SettingsChangeTests`, `SelectedAccountStore`, `SelectedAccountTests`, `.CreateAsync`, `SilentAuthProbe`, `SettingsChangeCoordinator`, `RefreshPresentation`, `.Raise`, `ProtectedCache`, `.Locate`?**
  _High betweenness centrality (0.073) - this node is a cross-community bridge._
- **What connects `InfoBar`, `ProgressRing`, `TextBox` to the rest of the system?**
  _107 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `.RefreshAsync` be split into smaller, more focused modules?**
  _Cohesion score 0.0662451068955134 - nodes in this community are weakly interconnected._
- **Should `OutlookWidget.Core.Refresh` be split into smaller, more focused modules?**
  _Cohesion score 0.060470324748040316 - nodes in this community are weakly interconnected._
- **Should `.SeedState` be split into smaller, more focused modules?**
  _Cohesion score 0.060470324748040316 - nodes in this community are weakly interconnected._
- **Should `.Read` be split into smaller, more focused modules?**
  _Cohesion score 0.06842105263157895 - nodes in this community are weakly interconnected._