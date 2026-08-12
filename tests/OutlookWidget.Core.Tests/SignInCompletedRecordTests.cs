using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The durable half of sign-in recovery.
/// </summary>
/// <remarks>
/// The event is the fast path and the record is the fallback: a raise that cannot open its handle
/// loses the fact that a sign-in happened, and without this the provider is left with a fresh
/// snapshot, sticky authorization suppression, and no reason to reach Graph.
/// </remarks>
public sealed class SignInCompletedRecordTests : IDisposable
{
    private readonly CoordinationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private CoordinationPaths Paths => _fixture.Paths;

    [Fact]
    public void No_record_means_no_evidence_of_a_sign_in()
    {
        Assert.Null(SignInCompletedRecord.Read(Paths));
    }

    [Fact]
    public void A_written_record_round_trips()
    {
        Assert.True(SignInCompletedRecord.Write(Paths));

        Assert.NotNull(SignInCompletedRecord.Read(Paths));
    }

    [Fact]
    public void Each_sign_in_produces_a_different_token()
    {
        // The provider decides "has a sign-in happened since I last looked" by comparing tokens, so
        // a repeated value would make a second sign-in invisible. Same-account sign-ins are the
        // common case and must still count.
        SignInCompletedRecord.Write(Paths);
        Guid? first = SignInCompletedRecord.Read(Paths);

        SignInCompletedRecord.Write(Paths);
        Guid? second = SignInCompletedRecord.Read(Paths);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_repeated_read_returns_the_same_token()
    {
        // The record is not consumed by reading it. The provider owns the decision about whether it
        // has already acted on a token, because a read that cleared the file would lose the
        // evidence for any other reader.
        SignInCompletedRecord.Write(Paths);

        Assert.Equal(SignInCompletedRecord.Read(Paths), SignInCompletedRecord.Read(Paths));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("{\"Token\":\"00000000-0000-0000-0000-000000000000\"}")]
    public void An_unusable_record_reads_as_no_evidence(string content)
    {
        // Answering null here is not a fail-closed decision, because this value never decides what
        // may be displayed — only whether to schedule extra work. Treating an unreadable file as
        // evidence would force a Graph attempt on every single check for as long as it stayed
        // broken. An all-zero token is rejected for the same reason a missing one is: it is the
        // default a malformed document deserializes to.
        File.WriteAllText(Paths.SignInCompletedRecordFilePath, content);

        Assert.Null(SignInCompletedRecord.Read(Paths));
    }

    [Fact]
    public void A_record_can_be_read_while_it_is_held_open_for_writing()
    {
        // Two processes: the companion may be replacing this file exactly as the provider reads it.
        SignInCompletedRecord.Write(Paths);

        using var held = new FileStream(
            Paths.SignInCompletedRecordFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.NotNull(SignInCompletedRecord.Read(Paths));
    }

    [Fact]
    public void Consuming_the_record_is_the_acknowledgement_and_it_survives_a_restart()
    {
        // The acknowledgement used to be an in-memory field, so every recycle, reboot, and package
        // upgrade started with no memory of it and treated a historical sign-in as outstanding —
        // forcing a Graph transaction over a perfectly fresh cache each time. Absence of the record
        // is the acknowledgement now, and absence survives a process boundary for free.
        SignInCompletedRecord.Write(Paths);
        Guid token = SignInCompletedRecord.Read(Paths)!.Value;

        SignInCompletedRecord.Consume(Paths, token);

        Assert.Null(SignInCompletedRecord.Read(Paths));
    }

    [Fact]
    public void Consuming_a_superseded_token_leaves_the_newer_sign_in_outstanding()
    {
        // A sign-in completed while the provider's refresh was in flight. Deleting unconditionally
        // would discard that sign-in's recovery along with the one just handled.
        SignInCompletedRecord.Write(Paths);
        Guid inFlight = SignInCompletedRecord.Read(Paths)!.Value;

        SignInCompletedRecord.Write(Paths);
        Guid newer = SignInCompletedRecord.Read(Paths)!.Value;

        SignInCompletedRecord.Consume(Paths, inFlight);

        Assert.Equal(newer, SignInCompletedRecord.Read(Paths));
    }

    [Fact]
    public void Consuming_an_absent_record_is_harmless()
    {
        SignInCompletedRecord.Consume(Paths, Guid.NewGuid());

        Assert.Null(SignInCompletedRecord.Read(Paths));
    }

    [Fact]
    public void The_record_lives_beside_the_other_coordination_state()
    {
        // Inside the package store, so uninstall removes it with everything else.
        Assert.Equal(
            Path.GetFullPath(Paths.RootDirectory),
            Path.GetFullPath(Path.GetDirectoryName(Paths.SignInCompletedRecordFilePath)!));
    }
}
