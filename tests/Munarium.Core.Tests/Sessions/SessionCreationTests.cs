namespace Munarium.Core.Tests.Sessions;

using Munarium.Access;
using Munarium.Runbooks;
using Munarium.Sessions;

/// <summary>
/// Tests for opening a session: what it pins, what it snapshots, and the two refusals that stop a conversation nobody
/// could have.
/// </summary>
public class SessionCreationTests
{
    /// <summary>
    /// The pin is a version and the echo is the least privilege: a client is told what it can see before it asks
    /// anything, and the session carries the clearance rather than a live claim.
    /// </summary>
    [Fact]
    public void APermittedSessionPinsTheVersionAndEchoesWhatItCanSee()
    {
        var opened = Opened(SessionCreation.Open(Document(), new AccessContext(2, ["finance"]), "ada", "acme"));

        Assert.Equal("northgate@3", opened.Session.RunbookRef);
        Assert.Equal("northgate", opened.Session.RunbookName);
        Assert.Equal("ada", opened.Session.Uid);
        Assert.Equal("acme", opened.Session.Tenant);
        Assert.Equal(SessionState.Open, opened.Session.State);
        Assert.True(SessionIds.IsSessionId(opened.Session.Id));
        Assert.StartsWith(SessionIds.Prefix, opened.Session.Id, StringComparison.Ordinal);

        // `contracts` is level 2 with no compartments; `minutes` is level 4 and out of reach; `ledger` needs a
        // compartment the clearance does not carry.
        Assert.Equal(["contracts"], opened.PermittedCollections);
        Assert.Equal(2, opened.Session.Access.Level);
        Assert.Equal(["finance"], opened.Session.Access.Compartments);
    }

    /// <summary>
    /// A runbook the clearance does not name is refused, and the message names the runbook, because the caller has to be
    /// able to tell a wrong allowlist from a wrong level.
    /// </summary>
    [Fact]
    public void ARunbookTheClearanceDoesNotNameIsRefused()
    {
        var refusal = Refused(SessionCreation.Open(
            Document(),
            new AccessContext(9, [], Runbooks: ["other-runbook"]),
            "ada",
            "acme"));

        Assert.Equal(SessionRefusalCodes.RunbookNotPermitted, refusal.Code);
        Assert.Contains("does not allow runbook 'northgate'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clearance that covers the runbook but none of its collections is refused rather than opened empty: an empty
    /// session would answer every question with a confident nothing, and the caller could not tell that apart from an
    /// empty corpus.
    /// </summary>
    [Fact]
    public void ARunbookWithNothingInReachIsRefusedRatherThanOpenedEmpty()
    {
        var refusal = Refused(SessionCreation.Open(Document(), new AccessContext(0, []), "ada", "acme"));

        Assert.Equal(SessionRefusalCodes.NoVisibleCollections, refusal.Code);
        Assert.Contains("visible at your access level", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Level and compartment are two inequalities, and the compartment one is <em>all</em>: a clearance that carries one
    /// of the two tags a collection needs carries neither, and a clearance that clears every compartment clears the gate
    /// outright.
    /// </summary>
    [Fact]
    public void EveryCompartmentIsRequiredAndLevelIsAnInequality()
    {
        var clearance = new AccessContext(3, ["finance", "legal"]);

        Assert.True(clearance.Permits(3, []));
        Assert.True(clearance.Permits(0, ["finance", "legal"]));
        Assert.True(clearance.Permits(1, ["finance"]));

        // Carrying more than is asked for is fine; carrying less is not.
        Assert.False(clearance.Permits(4, []));
        Assert.False(clearance.Permits(3, ["finance", "legal", "hr"]));
        Assert.False(clearance.Permits(3, ["hr"]));

        // The unrestricted context is a value, not a special case: it clears the compartment gate without being asked to.
        Assert.True(AccessContext.Unrestricted.Permits(int.MaxValue, ["hr", "legal"]));
        Assert.True(AccessContext.Unrestricted.PermitsRunbook("anything"));

        // An allowlist is by name, so it spans a runbook's versions.
        Assert.True(new AccessContext(1, [], Runbooks: ["northgate"]).PermitsRunbook("northgate"));
        Assert.False(new AccessContext(1, [], Runbooks: ["northgate"]).PermitsRunbook("southgate"));
    }

    private static SessionOpened Opened(SessionOpening opening) =>
        opening is SessionOpened opened
            ? opened
            : throw new InvalidOperationException("The session was refused.");

    private static SessionRefusal Refused(SessionOpening opening) =>
        opening is SessionRefusal refusal
            ? refusal
            : throw new InvalidOperationException("The session was opened.");

    private static RunbookDocument Document() => new()
    {
        ApiVersion = "munarium.dev/v2",
        Kind = "Runbook",
        Metadata = new RunbookMeta { Name = "northgate", Version = 3 },
        Spec = new RunbookSpec
        {
            Collections =
            [
                new CollectionSpec { Name = "contracts", Shape = "cuad-contracts@3", AccessLevel = 2 },
                new CollectionSpec { Name = "minutes", Shape = "minutes@1", AccessLevel = 4 },
                new CollectionSpec
                {
                    Name = "ledger",
                    Shape = "ledger@1",
                    AccessLevel = 1,
                    Compartments = ["finance", "audit"],
                },
            ],
        },
    };
}
