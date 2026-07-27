# Plan Review: Outlook Inbox Widget — Technical Plan (third pass)

**Plan reviewed:** `TECHNICAL_PLAN.md` (revised draft, 2026-07-27)
**Codebase:** `C:\Users\rsmalley\OneDrive - 415 Group, Inc\Documents\GitHub\Outlook Widget` — still greenfield. `main` at `0b238d9` tracking `origin/main` at `github.com/415Group-Ray/Outlook-Widget`; the plan and prior review are now committed, as §12 asks. No source code yet. Verification was again done against current Microsoft Learn docs and the real working environment.
**Verdict:** Ready to implement, conditional only on recording the section 0 audience decision.

## Summary

Every second-pass finding is resolved, and two of the fixes went further than what I raised — the locking rework closed a logout-versus-in-flight-refresh race I had not named, and the message-action design closed a `webLink`-in-card-JSON exposure path I had only asked to be *clarified*. The plan is now specific enough that an implementer would have to actively ignore it to build the wrong thing.

One genuine issue remains, and it comes from the new lock-free read design: on Windows, atomic file replacement and concurrent readers are not automatically compatible the way the plan's reasoning assumes. That is a two-line fix, not a design problem. The remaining three items are undefined user-visible behaviors in the new concurrency paths.

**Findings: 0 blockers, 1 major, 3 minor.** The section 0 audience decision is still open and still correctly modeled as an approval gate, not a defect.

## Correctness

Re-verified this pass:

- **The `olk.exe` citation is now correct.** It points at "Architecture changes in new Outlook," which is a real Learn page that documents `olk.exe` directly. Worth noting as *supporting* evidence rather than a finding: that page documents only `--safe` and `--recovery`, which strengthens rather than weakens §9's position that no Inbox-selection or message-selection switch exists to depend on.
- All previously verified platform claims still hold — the six-method `IWidgetProvider` contract and `GetWidgetInfos()` recovery requirement, the short-`Activate`-interval warning, MSAL's browser fallback when WAM is unavailable, `NewsAndInterests/AllowNewsAndInterests`, the two Graph `error.code` values, Windows App SDK 2.3.1, .NET 10 LTS, Adaptive Cards 1.5, and the WAM redirect URI.
- **§10's 401 row is fixed** and now reads consistently with the provider's no-interactive-path invariant: "Provider fails closed to 'Sign in required'; only the companion may start user-initiated interactive recovery."

**[Major] On Windows, lock-free readers and atomic replace collide — the *writer* fails, not the reader.** §8 now says "Reads are lock-free: atomic replacement means a reader observes either the prior complete snapshot or the new complete snapshot." That reasoning is POSIX-shaped. On Windows, `File.Replace` / `MoveFileEx(MOVEFILE_REPLACE_EXISTING)` fails with a sharing violation if the destination file is open by another process without `FILE_SHARE_DELETE` — and a `FileStream` opened for reading defaults to `FileShare.Read`, which does *not* include `Delete`. So the intended outcome (readers never block, writers always commit) inverts into intermittent commit failures whenever a provider `Activate` read overlaps a companion commit: refreshes that silently fail under exactly the concurrency the new design was built to handle, and they will reproduce rarely enough to be blamed on the network.

Two additions make the stated guarantee true:

- Readers must open the snapshot with `FileShare.ReadWrite | FileShare.Delete` explicitly. This is what actually gives Windows the POSIX-like "reader keeps the old inode, writer swaps the name" behavior the plan is relying on.
- The commit should still retry the replace a few times with a short backoff, since any other handle on the file (indexer, AV, a debugger) can transiently block it. Retry inside the mutation mutex is fine — it is local file I/O, not network I/O, so it does not reintroduce the reader stall this design just removed.

Then state the share mode in §8's cache-protection bullets, and extend §17's existing concurrency test to assert the *writer* side too: a commit must succeed while another process holds the snapshot open for reading. The current test only asserts that readers don't block.

## Completeness

Second-pass findings, all confirmed resolved:

| Second-pass finding | Status |
|---|---|
| Mutex held across network I/O (major) | Fixed, and then some. §8 now uses a zero-timeout refresh lease for cross-process single-flight, runs token acquisition and Graph I/O entirely outside the mutation lock, takes the mutation mutex only for the commit, and releases before signalling. It also added a generation/state re-compare under that mutex, which closes a race I hadn't named: an in-flight refresh that commits *after* a logout would otherwise resurrect the previous account's mail. §17 gets a specific reader-latency test and §19's row is rewritten. |
| `olk.exe` citation | Fixed — real page, and it genuinely documents `olk.exe` |
| §10 401 row vs. fail-closed invariant | Fixed |
| `Program.cs` owner (§3 vs §12) | Fixed — now under `OutlookWidget.Provider` in both places |
| Phase 4 doc list vs §21 | Fixed — "README (including privacy, security, and deployment sections), app-registration guide, troubleshooting guide, and the Phase 0 evidence report" |
| Card action model unstated | Fixed, and improved beyond the ask. `Action.Execute` only, `Action.OpenUrl` explicitly not used; a message action carries only a bounded display slot plus snapshot generation, never the `webLink` or message ID, so those values never enter Adaptive Card JSON or `CustomState` the host may persist. The provider re-reads the snapshot, rejects a stale generation or invalid slot, and re-validates the cached HTTPS link against the Outlook-host allowlist before launching — with an automated test asserting all of it. This closes a real exposure path against a §9 rule ("never log the message URL") that the host, not the app, would otherwise have broken. |
| Commit/push before relocating | Fixed — §12 and §14 step 1 |
| Phase 0 → Phase 1 reuse dependency | Fixed — stated in §18 and in the §20 estimate table |

**[Minor] The discard path doesn't say what to render.** §8 step 6 says to discard the result if relevant state changed under the mutation mutex, and step 7 is conditioned on state still matching — but step 9 ("update every running widget instance") is not visibly conditioned on anything. Read literally in sequence, a discarded refresh could still push its result to the widgets, which is exactly the wrong-account-after-logout render the generation compare exists to prevent. Add one clause: on discard, re-render from the currently committed snapshot (or the signed-out card) and never from the discarded result.

**[Minor] A skipped manual refresh has no defined user-visible behavior.** Step 2's zero-timeout lease means a manual Refresh click during an in-flight background refresh is simply skipped. §10 already says manual refresh "must not create parallel requests," so the mechanism is right — but from the user's side the click does nothing, which reads as a broken button. Define it: show "Refresh already in progress" (or leave the existing refreshing indicator up), and let the completing refresh's update satisfy the click.

**[Minor] A stale-generation message click has no defined behavior either, and it won't be rare.** §3 correctly rejects a message action whose snapshot generation no longer matches. But note the interaction with §8: opening the Board triggers a refresh whenever the cache is older than 60 seconds, so the act of opening the Board is itself the most likely thing to invalidate the card the user is about to click. The window is small and rejecting is the safe choice, but "click does nothing" needs a defined outcome — re-render the current snapshot and surface a brief "list updated" state rather than failing silently.

## Maintainability

No concerns. The §3/§12 split is now consistent, and the component list matches the folder structure. The two-service auth split (`InteractiveAuthService` / `SilentAuthService`) plus the static boundary test is the kind of structure that keeps the fail-closed rule true after the author stops thinking about it, which is the right way to enforce an invariant like this.

## Implementation Risk

No new concerns beyond the atomic-replace item above. The estimate now carries its own assumption (Phase 1 reuses the Phase 0 skeleton) rather than leaving it implicit, and the Phase 0 preconditions in §17 are the right set of things to check before anyone writes code.

## Unclear Assumptions & Open Questions

Only the section 0 decision — audience/user count and Outlook-client mix. Unchanged from the last pass: it is a named gate with a designed branch on each side, carried as the first row of §19, and enforced by the final approval gate. It needs an answer from you, not an edit to the plan.

## Edge Cases

The concurrency edge cases introduced by the locking rework are the three minors above. Everything else I raised across the previous passes is in §17's matrix, and the new tests added this round (reader latency under in-flight refresh, message-action slot/generation validation, mutation-only mutex scope) are aimed at the right invariants rather than at implementation details.

## Overengineering & Scope

No concerns. Nothing was added back. The locking rework is a net simplification of the read path — a lock removed rather than a mechanism added — and the message-action change replaced an unstated choice with the cheaper of the two options.

---

### To close out

1. Record the audience/user-count and Outlook-client-mix decision in §0. This is the only thing standing between the plan and approval.
2. §8: specify `FileShare.ReadWrite | FileShare.Delete` on snapshot reads and a short bounded retry on the replace; extend the §17 concurrency test to assert the writer commits while a reader holds the file open.
3. §8: on a discarded commit, re-render from the committed snapshot, never from the discarded result.
4. Define the two "nothing appears to happen" cases: manual refresh skipped by the lease, and a message click rejected for a stale generation.
