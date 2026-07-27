# Plan Review: Outlook Inbox Widget — Technical Plan (fourth pass)

**Plan reviewed:** `TECHNICAL_PLAN.md` (revised draft, 2026-07-27, committed at `1b404e8`)
**Codebase:** `C:\Users\rsmalley\OneDrive - 415 Group, Inc\Documents\GitHub\Outlook Widget` — still greenfield; the plan and reviews are committed on `main`, no source code yet. This pass reviewed the `0b238d9..1b404e8` diff against the previous findings, plus a fresh look at the changed sections for regressions.
**Verdict:** Ready to implement, conditional only on recording the section 0 audience decision.

## Summary

All four third-pass findings are fixed, and fixed precisely — the Windows file-sharing correction was implemented with the exact share mode, a bounded retry with stated intervals, a prior-snapshot fallback, two supporting citations, and a fault-injection test. The three "nothing appears to happen" gaps now have defined states, error-table rows, and tests.

The plan has converged. What remains is three small implementation details introduced by this round of edits, none of which needs to block Phase 0 — one is a Markdown rendering defect, two are lock-lifetime notes that belong in the Phase 1 implementation of `RefreshCoordinator`. I found nothing material in the architecture, and I don't expect another pass to.

**Findings: 0 blockers, 0 major, 3 minor.** The section 0 audience decision remains the only thing gating approval.

## Correctness

No concerns. The changed sections are technically accurate:

- `FileShare.ReadWrite | FileShare.Delete` on readers is exactly the fix — that is what permits a Windows replace while a prior handle is open, and the plan now cites both `File.Replace` and the Win32 moving-and-replacing-files reference rather than asserting the behavior.
- The bounded retry (25/50/100 ms, mutex retained, prior snapshot kept, operational failure category recorded, retry on next trigger) is correctly scoped to local file I/O and does not reintroduce the reader stall.
- The stale-generation message action is handled with unusual care: "does not launch anything from either snapshot" and the test's "never launches a URL from the stale **or replacement** slot" close the subtle version of the bug, where rejecting the stale generation but then launching whatever now occupies slot 2 would open the wrong message.
- Step 7's discard branch and step 10's "from the committed snapshot" together close the wrong-account-render path.

**[Minor] The unescaped pipe in §19 breaks that table row's rendering.** The updated race row contains `` `FileShare.ReadWrite | FileShare.Delete` `` inline. In GitHub-flavored Markdown, a `|` inside a table cell splits the cell even within a code span — backticks don't protect it. That row will render with an extra column and the mitigation text will be cut in the wrong place, in the one table a reader is most likely to skim. Escape it as `\|`.

## Completeness

Third-pass findings, all confirmed resolved:

| Third-pass finding | Status |
|---|---|
| Lock-free reads vs. Windows atomic replace (major) | Fixed — explicit `FileShare.ReadWrite \| FileShare.Delete` on reads, bounded 25/50/100 ms replace retry under the mutation mutex, prior-snapshot fallback with an operational failure category, a §10 row, a §19 update, two Learn citations, and a §17 test that injects transient sharing violations and asserts a commit succeeds while another process holds the file open |
| Discard path didn't say what to render | Fixed — new step 7 renders only the committed snapshot or signed-out state and never the discarded result; step 10 re-worded to "from the committed snapshot" |
| Manual refresh silently skipped | Fixed — step 2 preserves/shows "Refresh already in progress," satisfied by the winning refresh's completion; §10 row and §17 test added |
| Stale-generation message click | Fixed — §3 bullet, §10 row, and a §17 test, with the replacement-slot case explicitly covered |

**[Minor] The mutation mutex has no stated release on the discard branch.** Step 6 acquires it; step 8 releases it "immediately" after a successful replace — but that release is inside the state-still-matches branch. Read literally, the discard path at step 7 never releases it, and step 7 also *renders* while (nominally) holding it. A leaked mutation mutex wouldn't hurt readers, since they never take it, but it would block every later logout, account switch, privacy change, and commit for the life of the process. Wrap the mutex in a `try/finally` covering both branches — mirroring what step 11 already does for the refresh lease — and do the rendering after the release, not under it.

**[Minor] Neither cross-process primitive says what happens when its owner dies.** §17 already tests provider crash, and the design now depends on two named cross-process objects. If a named mutex's owner terminates while holding it, .NET grants ownership to the next waiter but throws `AbandonedMutexException` in the process — including on a zero-timeout `WaitOne`. Unhandled, that turns a crash during refresh into a failed refresh for the next caller; and if the winner died mid-request, a "Refresh already in progress" indicator set by a losing manual click has nothing left to clear it. Specify both: catch `AbandonedMutexException`, treat the guarded state as suspect (which the plan already handles — remove the stray temp file, keep the prior snapshot), proceed with the acquired lock, and bound the in-progress indicator so it clears on its own if no completion event arrives.

## Maintainability

No concerns. Section 3, section 8, section 10, section 17, and section 19 all describe the same mechanism in the same terms this round, which is what makes the concurrency design reviewable at all.

## Implementation Risk

No new concerns. The two lock-lifetime items above are Phase 1 `RefreshCoordinator` implementation notes, not design risks — worth writing into the plan now so they don't have to be rediscovered, but they don't change what Phase 0 proves.

## Unclear Assumptions & Open Questions

Only the section 0 decision — audience/user count and Outlook-client mix. Unchanged across three passes now: named gate, designed branch on each side, first row of §19, enforced by the final approval gate. It needs an answer from you, not a plan edit.

## Edge Cases

No new concerns. The cases added this round (lease contention, stale generation including the replacement slot, blocked replace after retries) are the right ones, and each has a matching test.

## Overengineering & Scope

No concerns. This round added roughly fifteen lines of behavior specification and two citations, and removed no capability — proportionate to the defects being fixed.

---

### To close out

1. Record the audience/user-count and Outlook-client-mix decision in §0. This is the only remaining approval gate.
2. Escape the pipe in the §19 race row (`\|`) so the table renders.
3. `try/finally` the mutation mutex across both the commit and discard branches, and render after releasing it.
4. Specify `AbandonedMutexException` handling for the lease and mutation mutex, and a self-clearing bound on the "Refresh already in progress" indicator.

Items 2–4 are small enough to fold into Phase 1 rather than gate Phase 0. Nothing in the architecture, security model, or gating strategy needs further revision.
