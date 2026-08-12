using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Graph;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;
using OutlookWidget.Provider.Cards;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The durable authorization decision, and the restart it exists to survive.
/// </summary>
/// <remarks>
/// This was an in-memory flag on the provider's presentation state. A package upgrade or a provider
/// recycle recreated it as "not suppressed", and the recovered-instance delivery that follows a
/// restart runs before any new Graph result — so it rendered exactly the senders and subjects a 401
/// or 403 had withheld. A restart is the moment the provider knows least, and it was the moment it
/// disclosed most.
/// </remarks>
public sealed class AuthorizationSuppressionTests : IDisposable
{
    private readonly CoordinationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private CoordinationPaths Paths => _fixture.Paths;

    private AuthorizationSuppressionStore Store() => new(Paths);

    [Fact]
    public void A_fresh_install_withholds_nothing()
    {
        // Absent is unambiguous and is not a failure: the mailbox has never refused this app, and
        // there is no cached mail to withhold either.
        AuthorizationSuppressionResult result = Store().Read();

        Assert.Equal(AuthorizationSuppressionStatus.Absent, result.Status);
        Assert.False(result.DetailsWithheld);
    }

    [Fact]
    public void A_recorded_refusal_round_trips()
    {
        Assert.True(Store().Write(detailsWithheld: true));

        AuthorizationSuppressionResult result = Store().Read();

        Assert.Equal(AuthorizationSuppressionStatus.Success, result.Status);
        Assert.True(result.DetailsWithheld);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("{\"SomethingElse\":true}")]
    public void An_unusable_record_withholds(string content)
    {
        // Including {} and a document with an unrelated property: DetailsWithheld is required, so a
        // file corrupted into valid JSON cannot deserialize into "disclose everything" and be
        // reported as a known value.
        File.WriteAllText(Paths.AuthorizationSuppressionFilePath, content);

        AuthorizationSuppressionResult result = Store().Read();

        Assert.Equal(AuthorizationSuppressionStatus.Unreadable, result.Status);
        Assert.True(result.DetailsWithheld);
    }

    [Fact]
    public void A_path_that_cannot_be_read_withholds_rather_than_reading_as_absent()
    {
        // The File.Exists hole, which has now been written three times in this codebase: an
        // existence pre-check reports false for ACL damage and every other failure, so a present
        // but unreadable record would be classified absent, and absent means disclose.
        Directory.CreateDirectory(Paths.AuthorizationSuppressionFilePath);

        try
        {
            Assert.True(Store().Read().DetailsWithheld);
        }
        finally
        {
            Directory.Delete(Paths.AuthorizationSuppressionFilePath, recursive: true);
        }
    }

    [Fact]
    public void A_refusal_survives_a_provider_restart()
    {
        // The defect this whole record exists for, end to end. The first presentation records the
        // refusal; the second stands in for the process that replaces it after a recycle.
        var first = new RefreshPresentation(Store());
        first.ReportGraphStatus(GraphMailStatus.Forbidden);

        Assert.True(first.Current.AuthorizationInvalidatesDetails);

        var afterRestart = new RefreshPresentation(Store());

        Assert.True(
            afterRestart.Current.AuthorizationInvalidatesDetails,
            "A recycled provider delivers before any new Graph result. Starting from 'not "
                + "suppressed' renders the senders and subjects the refusal withheld.");
    }

    [Fact]
    public void A_successful_read_clears_the_record_for_the_next_process()
    {
        var first = new RefreshPresentation(Store());
        first.ReportGraphStatus(GraphMailStatus.Unauthorized);
        first.ReportGraphStatus(GraphMailStatus.Success);

        Assert.False(new RefreshPresentation(Store()).Current.AuthorizationInvalidatesDetails);
    }

    [Fact]
    public void An_inconclusive_result_does_not_clear_the_record_for_the_next_process()
    {
        // The durable half of the stickiness rule: a dropped connection after a 401 must not let a
        // restart disclose what the 401 withheld.
        var first = new RefreshPresentation(Store());
        first.ReportGraphStatus(GraphMailStatus.Unauthorized);
        first.ReportGraphStatus(GraphMailStatus.NetworkFailure);

        Assert.True(new RefreshPresentation(Store()).Current.AuthorizationInvalidatesDetails);
    }

    [Fact]
    public void A_committed_refresh_clears_the_record_because_it_required_an_authorized_read()
    {
        var presentation = new RefreshPresentation(Store());
        presentation.ReportGraphStatus(GraphMailStatus.Forbidden);

        presentation.Complete(
            new RefreshResult(RefreshOutcome.Committed, DeliveryRequestOutcome.Requested, 7, TimeSpan.Zero),
            presentation.Current);

        Assert.False(Store().Read().DetailsWithheld);
        Assert.False(new RefreshPresentation(Store()).Current.AuthorizationInvalidatesDetails);
    }

    [Fact]
    public void An_advanced_generation_does_not_clear_the_durable_record()
    {
        // This asserted the opposite for one round, and the premise did not survive review: the
        // refresh lease deliberately holds no mutex, so a commit from anywhere — a different-account
        // sign-in clearing the prior snapshot is the clearest case — advances the generation while a
        // peer refresh is live and possibly failing. Clearing a disclosure decision on that is
        // clearing it on the strength of a write nobody attributed to anyone.
        //
        // The cost of the safe reading is one extra refresh after a peer genuinely recovers. The
        // cost of the unsafe one is rendering withheld mail because an unrelated commit moved a
        // number.
        var presentation = new RefreshPresentation(Store());
        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);

        long peerWaitId = presentation.Complete(
            new RefreshResult(
                RefreshOutcome.SkippedLeaseHeld,
                DeliveryRequestOutcome.NotRequested,
                0,
                TimeSpan.Zero,
                PeerLeaseStartingGeneration: 4),
            presentation.Current)!.Value;

        Assert.True(presentation.ResolvePeerWait(peerWaitId, stateAdvanced: true));

        Assert.True(presentation.Current.AuthorizationInvalidatesDetails);
        Assert.True(Store().Read().DetailsWithheld);
        Assert.True(new RefreshPresentation(Store()).Current.AuthorizationInvalidatesDetails);
    }

    [Fact]
    public void An_unrecordable_refusal_escalates_to_a_suppression_marker()
    {
        // The store surfaces a failed write and the caller used to discard it. This process still
        // withholds from memory, but the record is what the next one reads — so a recycle would
        // render exactly what the refusal withheld, which is the defect the record exists to
        // prevent. The escalation expresses the decision through a mechanism that does not depend on
        // that file.
        bool escalated = false;

        // Blocking the *temporary* path fails the write while leaving the record absent, which is
        // the dangerous combination: a restart reads "never refused" and discloses. Blocking the
        // record itself would make the read unreadable, and unreadable already withholds — so there
        // would be nothing to escalate, which is why the first version of this test saw none.
        Directory.CreateDirectory(Paths.AuthorizationSuppressionTempFilePath);

        try
        {
            var presentation = new RefreshPresentation(Store(), () => escalated = true);

            presentation.ReportGraphStatus(GraphMailStatus.Forbidden);

            Assert.True(presentation.Current.AuthorizationInvalidatesDetails);
            Assert.Equal(
                AuthorizationSuppressionStatus.Absent,
                Store().Read().Status);
            Assert.True(escalated, "A refusal that cannot be recorded must escalate, not be logged.");
        }
        finally
        {
            Directory.Delete(Paths.AuthorizationSuppressionTempFilePath, recursive: true);
        }
    }

    [Fact]
    public void A_failure_to_record_recovery_does_not_escalate()
    {
        // The other direction is already safe: the record still says withheld, so the next process
        // is merely more conservative than it needs to be.
        bool escalated = false;

        var presentation = new RefreshPresentation(Store(), () => escalated = true);
        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);

        Directory.CreateDirectory(Paths.AuthorizationSuppressionTempFilePath);

        try
        {
            escalated = false;
            presentation.ReportGraphStatus(GraphMailStatus.Success);

            Assert.False(escalated);
        }
        finally
        {
            Directory.Delete(Paths.AuthorizationSuppressionTempFilePath, recursive: true);
        }
    }

    [Fact]
    public void An_unresolved_peer_leaves_the_record_withheld()
    {
        // The other direction: a peer that did not commit is no evidence, so the record stands.
        var presentation = new RefreshPresentation(Store());
        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);

        long peerWaitId = presentation.Complete(
            new RefreshResult(
                RefreshOutcome.SkippedLeaseHeld,
                DeliveryRequestOutcome.NotRequested,
                0,
                TimeSpan.Zero,
                PeerLeaseStartingGeneration: 4),
            presentation.Current)!.Value;

        Assert.True(presentation.ResolvePeerWait(peerWaitId, stateAdvanced: false));

        Assert.True(Store().Read().DetailsWithheld);
    }

    [Fact]
    public void A_presentation_without_a_store_still_works()
    {
        // Every existing transition test constructs one this way. Without a store there is simply no
        // durable half — which is exactly the pre-fix behaviour, and why production always supplies
        // one.
        var presentation = new RefreshPresentation();

        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);

        Assert.True(presentation.Current.AuthorizationInvalidatesDetails);
    }
}
