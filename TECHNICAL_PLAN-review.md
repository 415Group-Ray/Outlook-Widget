# Plan Review: Outlook Inbox Widget — Technical Plan (second pass)

**Plan reviewed:** `TECHNICAL_PLAN.md` (revised draft, 2026-07-27)
**Codebase:** `C:\Users\rsmalley\OneDrive - 415 Group, Inc\Documents\GitHub\Outlook Widget` — still greenfield. One commit (`69899dd`) on `main`, tracking `origin/main` at `github.com/415Group-Ray/Outlook-Widget`; only `.gitattributes` is tracked. Verification was again done against current Microsoft Learn docs for widget providers, MSAL/WAM, Graph mail, and Windows App SDK, plus the real working environment.
**Verdict:** Ready to implement, conditional on two items — record the section 0 audience decision, and fix the cache-lock scope in §8 (below).

## Summary

All 30 findings from the first review are addressed, and most are addressed properly rather than papered over — the provider lifecycle section is now materially more correct than it was, the auth design is split into two services that make the fail-closed rule structural instead of aspirational, and the overengineering trims were taken as *deletions* rather than reworded. The plan is now internally consistent about what is documented, what is gated, and what is undecided.

The revision introduces one real design defect: the cross-process mutex added to fix the coordination gap is now held across network I/O while readers also take it, which directly contradicts the 500 ms warm-activation target. That is a small, local fix, not a rethink.

**Findings: 0 blockers, 1 major, 7 minor.** The section 0 audience decision remains open, but it is now correctly modeled as an approval gate rather than an unstated assumption, so I am not counting it as a defect.

## Correctness

Re-verified against current docs; the newly added platform claims are accurate:

- The six-method `IWidgetProvider` contract and the `WidgetManager.GetDefault().GetWidgetInfos()` startup-recovery requirement (§ confirmed facts, §3) match the documented C# provider pattern, including reading back `CustomState` and exiting once the last enabled widget is deleted.
- The docs do warn that the `Activate`/`Deactivate` interval can be short and recommend a fast update path — §8's reframing to "cached-first and activation-driven, timer is opportunistic" is faithful to that.
- "MSAL can fall back to a browser when WAM is unavailable" is correct and correctly load-bearing for the provider's silent-only design.
- `NewsAndInterests/AllowNewsAndInterests` and the **Allow widgets** GPO are the right policy levers, and the taskbar/Widgets policy-settings citation added to the source list is the right one.
- `MailboxNotEnabledForRESTAPI` and `ErrorItemNotFound` are real Graph `error.code` values for these conditions, and the Graph errors doc is now cited.
- Windows App SDK 2.3.1 stable, .NET 10 LTS, Adaptive Cards 1.5, and the `ms-appx-web://microsoft.aad.brokerplugin/{client-id}` redirect URI all still check out, unchanged from the first pass.

**[Minor] The `olk.exe` citation still points at the wrong page.** The source list continues to cite the generic Office "command-line switches / lifecycle" support URL. The claim itself is now properly hedged ("Microsoft Support currently lists `olk.exe` switches... treated as a Phase 0 compatibility test"), so this is cosmetic — but the Phase 0 evidence report will need the real reference, so fix the link now while it's cheap.

**[Minor] §10's HTTP 401 row contradicts the provider's fail-closed rule.** The row reads "Discard access token result and require silent reacquisition; **then UI if required**." That is correct for the companion and wrong for the provider, which §3 and §4 step 8 now guarantee has no interactive code path at all. Split the row or qualify it as companion-only; otherwise the one table a future implementer reads under pressure contradicts the invariant the rest of the plan works hard to establish.

## Completeness

Resolved from the first pass, all confirmed present:

| First-pass finding | Status |
|---|---|
| Provider restart/state recovery (blocker) | Fixed — §3 enumerates all six callbacks with per-instance semantics, `GetWidgetInfos()` recovery on construction, `Program.cs` owning COM registration and process lifetime, plus gates 3 and 4 and a "must be proven" entry |
| Audience / New Outlook assumption (blocker) | Converted into an explicit §0 decision gate with a designed mixed-client branch and a hard stop in the final approval gate — the right handling for a decision that isn't yours to make |
| Widgets disabled by policy | Fixed — §0 success criteria, confirmed facts, §16 Intune CSP check and RMM detection output, §17 gate precondition and integration test, §19 risk row |
| Broker without a natural HWND | Fixed — `WithParentActivityOrWindow(() => IntPtr.Zero)`, silent-only `SilentAuthService`, `Microsoft.Identity.Client.Broker` pinned in §13, gate 8 |
| Broker-unavailable / silent browser fallback | Fixed — §3 "no reference or code path to `AcquireTokenInteractive`", §10 row, §17 static boundary test, and a Phase 0 no-browser proof |
| Cross-process coordination | Addressed, with a defect — see the major below |
| Mailbox-level failure state | Fixed — two `error.code` rows in §10 plus an error-code mapping test |
| OneDrive working directory | Fixed — §12 relocation preserving history, §14 step 1, §15 certificate storage rule, §19 risk row |
| Effort estimate | Fixed — Phase 0 at 3–5 days, total 15–22, with Phases 3 and 4 also raised |
| 500 ms activation target | Fixed — warm ≤ 500 ms, cold ≤ 2 s |
| Five-minute timer premise | Fixed — restated as opportunistic in the intro bullet, §8, and §19 |
| Null `from`/`sender` | Fixed — "Unknown sender" plus an integration test |
| Cache migration machinery | Fixed — deleted; version mismatch discards and refetches |
| Log-redaction subsystem | Fixed — replaced with an API that has no metadata fields, enforced by shape and a static review test |

The smaller first-pass items are also all in: `nuget.config`, `Program.cs`, Adaptive Card 1.5 pinned, count-vs-list labelling ("Inbox unread" / "Newest email messages"), absolute local-time rendering with a midnight/DST test, multi-instance rendering, concurrent GETs instead of `$batch`, two test projects, tray proof moved into the fallback branch, the trimmed doc set, the rollback runbook with its stated cost, the named Entra role owner and pre-Phase-0 consent check, the home-account-ID cache field, Phase 4 owning uninstall and consent revocation, and the §12 git-state correction.

**[Minor] Commit and push the plan before relocating the clone.** §12 correctly says to move the working clone and not reinitialize, and there is a real `origin` to verify against. But `TECHNICAL_PLAN.md` and this review are untracked — a relocation done as a fresh `git clone` elsewhere silently loses both. Commit and push them first; then the move is safe however it's performed.

## Maintainability

**[Minor] `Program.cs` is described in two places with two owners.** §3 lists it under **OutlookWidget.Package** ("`Program.cs` registers the provider factory with `CoRegisterClassObject`..."), while §12 puts `Program.cs` in `src/OutlookWidget.Provider/`. §12 is right — the COM entry point belongs to the provider executable; the Package project holds the manifest and assets. Move the bullet.

**[Minor] §18 Phase 4 still lists documents §21 deleted.** Phase 4 says "README, app registration, **privacy, deployment**, and troubleshooting documentation," but §21 and §12 both decided privacy/deployment start as README sections and no separate files exist. Align Phase 4's deliverable list with §21.

**[Minor] The card action model is unstated.** §3's `OnActionInvoked` "validate the instance and action verb" implies `Action.Execute` verbs, and §2 draws Provider → Browser for message links — but the plan never says whether a message link is an `Action.OpenUrl` in the card JSON (host opens it) or an `Action.Execute` the provider handles. It matters for two things the plan already cares about: where §11's Outlook-host allowlist validation actually runs, and whether `webLink` values are written into card JSON that the Widgets host may persist or log — §9 explicitly forbids logging those URLs. State the choice.

## Implementation Risk

**[Major] The refresh mutex is held across network I/O while readers take the same mutex — this will blow the 500 ms activation target.** §8's refresh algorithm acquires the package-user-wide named mutex at step 2, then acquires a token (step 3) and issues Graph GETs with a **10-second timeout** (step 4), and only replaces the snapshot at step 6. §8's cache section also says "Readers briefly take the mutex before reading a complete snapshot." Combined: if the companion is mid-refresh when the user opens the Widgets Board, the provider's `Activate` blocks on the mutex behind up to ten seconds of network I/O before it can render *cached* content — against a stated target of 500 ms warm and 2 s cold, and against §8's own headline promise to "render the last valid cache immediately."

The fix is small and doesn't lose the guarantee the mutex was added for:

- Hold the mutex only around the mutation — token acquisition and the Graph calls happen *outside* it; acquire it just before the temp-file write plus atomic replace and the generation increment, and release immediately after. Refresh single-flight can use a separate short-held mutex acquired with a zero timeout ("someone else is already refreshing, skip") rather than one held for the duration.
- Make reads lock-free. Atomic replace already gives readers a consistent file: a reader either opens the old inode or the new one, never a torn one. The generation counter then tells the provider whether what it read is current. Taking a lock to read buys nothing here and is what creates the stall.
- Keep the mutex for the genuinely mutating, non-network operations it was intended for: logout, account switch, privacy change, cache clear.

Then re-word the §19 mitigation row and the §17 test ("named-mutex coordination and generation/event invalidation") to assert the *bound* — that a reader never blocks on an in-flight refresh — which is the property that actually needs a test.

**[Minor] Phase 0 absorbed work Phase 1 is now assumed to reuse.** Phase 0 grew to include the complete provider lifecycle skeleton, the broker-unavailable proof, policy/consent preflight, and the repo move, and went to 3–5 days; Phase 1 stayed at 2–3. That's coherent only because §12's "evolve the spike, don't build a second architecture" rule means the lifecycle skeleton is kept. Worth stating that dependency explicitly next to the Phase 1 estimate, so a Phase 0 that gets rebuilt-from-scratch doesn't silently overrun Phase 1.

## Unclear Assumptions & Open Questions

The section 0 decision — audience/user count and Outlook-client mix — is still open, and the plan is right that it blocks final approval. What's changed is that it's no longer an assumption: it's a named gate with a designed branch on each side (New-Outlook-only vs. a user-selectable client policy tested per path), and §19 carries it as the first risk. Nothing further needed from the plan; it needs an answer from you.

No other unstated assumptions found on this pass. The items I raised last time — the account-hash rationale, consent lead time and ownership, and the provider-lifetime premise behind the timer — are all now stated and justified in the text.

## Edge Cases

No new concerns. The cases I raised are all in §17's matrix now: null `from`/`sender`, the meeting-request count mismatch, midnight/DST rendering with a several-hours-old cache, multiple instances at different sizes, broker unavailable with a no-browser proof, Widgets policy-disabled vs. user-disabled, and rollback via remove-then-install with residual WAM state.

Scale remains a non-issue and §0 now says so.

## Overengineering & Scope

No concerns found, and this is where the revision is strongest — the trims were taken as removals rather than rewording. Cache schema migration is gone (delete and refetch), the redaction subsystem is gone (replaced by a logging API that has no field capable of holding metadata, which is both cheaper and more provably safe), `$batch` is deferred behind a measurement, the third test project is gone, `SECURITY.md` and three of the five docs are gone, and the tray proof only gets built if a native gate actually fails.

The one thing that grew — Phase 0 — grew for the right reason: it now proves the provider lifecycle and the fail-closed auth boundary, which were the two places where a wrong assumption would have been most expensive to discover in Phase 2.

---

### To close out

1. Record the audience/user-count and Outlook-client-mix decision in §0 (blocks approval).
2. Rescope the §8 mutex: mutation-only, reads lock-free, single-flight via a zero-timeout try-acquire; update the §17 test and §19 mitigation to assert that a reader never blocks on an in-flight refresh.
3. Sweep the four small inconsistencies: `Program.cs` owner (§3 vs §12), Phase 4's doc list (§18 vs §21), the 401 row's "then UI if required" (§10), and the card action model for message links.
4. Fix the `olk.exe` citation, and commit/push the plan and review before relocating the clone.
