namespace Munarium.Store.SharpCoreDb.Tests;

using Munarium.Access;
using Munarium.Sessions;
using Munarium.Store.SharpCoreDb.Tests.Support;

/// <summary>
/// Tests for <see cref="SharpCoreDbSessionStore"/>: the clearance a session keeps, the ordinals its turns are given, and
/// the one-way close.
/// </summary>
public class SharpCoreDbSessionStoreTests
{
    /// <summary>
    /// A session comes back with the runbook it pinned, the clearance it snapshotted and a creation stamp; another
    /// tenant reading the same identity sees nothing, because the tenant is half of the key.
    /// </summary>
    [Fact]
    public async Task ASessionComesBackWithTheClearanceItWasCreatedWith()
    {
        await using var fixture = SourceStoreFixture.Create();
        var session = Session("ses-00000000000000000000000001", "ada");

        var stored = await fixture.Sessions.CreateAsync(session);
        var read = await fixture.Sessions.GetAsync("acme", session.Id);

        Assert.NotNull(stored.CreatedAt);
        Assert.NotNull(read);
        Assert.Equal("northgate@3", read.RunbookRef);
        Assert.Equal("northgate", read.RunbookName);
        Assert.Equal(SessionState.Open, read.State);
        Assert.Equal(2, read.Access.Level);
        Assert.Equal(["finance", "legal"], read.Access.Compartments);
        Assert.Equal("jti-1", read.TokenJti);

        Assert.Null(await fixture.Sessions.GetAsync("other", session.Id));
        Assert.Null(await fixture.Sessions.GetAsync("acme", "ses-00000000000000000000000009"));
    }

    /// <summary>
    /// Storing a session twice leaves the first row, because a session's clearance is the one taken at creation: a
    /// second write that widened it would silently change what an ongoing conversation can see.
    /// </summary>
    [Fact]
    public async Task StoringASessionTwiceLeavesTheFirstClearance()
    {
        await using var fixture = SourceStoreFixture.Create();
        var session = await fixture.Sessions.CreateAsync(Session("ses-00000000000000000000000002", "ada"));

        var widened = await fixture.Sessions.CreateAsync(
            session with { Access = AccessContext.Unrestricted, Uid = "someone-else" });

        Assert.Equal("ada", widened.Uid);
        Assert.Equal(2, widened.Access.Level);
        Assert.Equal(session.CreatedAt, widened.CreatedAt);

        var read = await fixture.Sessions.GetAsync("acme", session.Id);

        Assert.Equal("ada", read?.Uid);
        Assert.False(read?.Access.AllCompartments ?? true);
    }

    /// <summary>
    /// The ordinal is the store's answer rather than the caller's request, and the turns come back in the order they
    /// were asked, with the session's last-turn stamp moved forward.
    /// </summary>
    [Fact]
    public async Task TurnsAreNumberedByTheStoreAndReadBackInOrder()
    {
        await using var fixture = SourceStoreFixture.Create();
        var session = await fixture.Sessions.CreateAsync(Session("ses-00000000000000000000000003", "ada"));

        Assert.Equal(1, await fixture.Sessions.AppendTurnAsync(Turn(session.Id, "first", ordinal: 99)));
        Assert.Equal(2, await fixture.Sessions.AppendTurnAsync(Turn(session.Id, "second", ordinal: 99)));
        Assert.Equal(3, await fixture.Sessions.AppendTurnAsync(Turn(session.Id, "third", ordinal: 99)));

        var turns = await fixture.Sessions.TurnsAsync("acme", session.Id, limit: 10);

        Assert.Equal(["first", "second", "third"], turns.Select(turn => turn.Query));
        Assert.Equal([1, 2, 3], turns.Select(turn => turn.Ordinal));
        Assert.Equal(["contracts", "register"], turns[0].CollectionsSearched);
        Assert.Equal("""{"hits":0}""", turns[0].HitsJson);
        Assert.Equal("""{"index@1":[]}""", turns[0].EnvelopeJson);
        Assert.Equal("""{"model":"test"}""", turns[0].CompletionJson);
        Assert.Null(turns[0].HierarchyJson);
        Assert.Equal("""{"profile":"due-diligence"}""", turns[2].HierarchyJson);

        Assert.Equal(2, (await fixture.Sessions.TurnsAsync("acme", session.Id, limit: 2)).Count);
        Assert.Empty(await fixture.Sessions.TurnsAsync("other", session.Id, limit: 10));

        var stamped = await fixture.Sessions.GetAsync("acme", session.Id);

        Assert.NotNull(stamped?.LastTurnAt);
    }

    /// <summary>
    /// Closing is one-way and visible: the first close answers true, the second false, and the clearance the session
    /// snapshotted is still there to be read.
    /// </summary>
    [Fact]
    public async Task ClosingASessionIsOneWay()
    {
        await using var fixture = SourceStoreFixture.Create();
        var session = await fixture.Sessions.CreateAsync(Session("ses-00000000000000000000000004", "ada"));

        Assert.True(await fixture.Sessions.CloseAsync("acme", session.Id));
        Assert.False(await fixture.Sessions.CloseAsync("acme", session.Id));
        Assert.False(await fixture.Sessions.CloseAsync("acme", "ses-00000000000000000000000099"));
        Assert.False(await fixture.Sessions.CloseAsync("other", session.Id));

        var closed = await fixture.Sessions.GetAsync("acme", session.Id);

        Assert.Equal(SessionState.Closed, closed?.State);
        Assert.Equal("closed", closed?.State.ToWireName());
        Assert.Equal("northgate@3", closed?.RunbookRef);
        Assert.Equal(["finance", "legal"], closed?.Access.Compartments);
    }

    /// <summary>
    /// A caller's sessions come back newest first and nobody else's are visible, which is the read a conversation list
    /// is built from.
    /// </summary>
    [Fact]
    public async Task RecentSessionsAreNewestFirstAndBelongToOneCaller()
    {
        await using var fixture = SourceStoreFixture.Create();

        await fixture.Sessions.CreateAsync(Session("ses-00000000000000000000000005", "ada"));
        await fixture.Sessions.CreateAsync(Session("ses-00000000000000000000000006", "ada"));
        await fixture.Sessions.CreateAsync(Session("ses-00000000000000000000000007", "grace"));

        var recent = await fixture.Sessions.RecentAsync("acme", "ada", limit: 10);

        Assert.Equal(
            ["ses-00000000000000000000000006", "ses-00000000000000000000000005"],
            recent.Select(session => session.Id));
        Assert.Empty(await fixture.Sessions.RecentAsync("other", "ada", limit: 10));
        Assert.Single(await fixture.Sessions.RecentAsync("acme", "ada", limit: 1));
    }

    private static SessionRecord Session(string id, string uid) => new()
    {
        Tenant = "acme",
        Id = id,
        Uid = uid,
        RunbookRef = "northgate@3",
        TokenJti = "jti-1",
        Access = new AccessContext(2, ["finance", "legal"]),
        State = SessionState.Open,
    };

    private static TurnRecord Turn(string sessionId, string query, int ordinal) => new()
    {
        Tenant = "acme",
        SessionId = sessionId,
        Ordinal = ordinal,
        Uid = "ada",
        Query = query,
        CollectionsSearched = ["contracts", "register"],
        HitsJson = """{"hits":0}""",
        EnvelopeJson = """{"index@1":[]}""",
        CompletionJson = """{"model":"test"}""",
        HierarchyJson = query == "third" ? """{"profile":"due-diligence"}""" : null,
    };
}
