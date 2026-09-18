namespace Munarium.Core.Tests.Evidence;

using Munarium.Evidence;

/// <summary>
/// Tests for the identity the server assigns at seal: an id a citation can rely on, and one nobody can guess their way
/// to.
/// </summary>
public class EvidenceIdsTests
{
    [Fact]
    public void AnIdentityIsThePrefixAndThirtyTwoLowercaseHexDigits()
    {
        var id = EvidenceIds.New();

        Assert.StartsWith(EvidenceIds.Prefix, id, StringComparison.Ordinal);
        Assert.Equal(35, id.Length);
        Assert.All(id[3..], character => Assert.Contains(character, "0123456789abcdef"));
    }

    /// <summary>
    /// The version nibble is what makes the value random rather than sequential: it is the fourth digit of a v4 UUID, and
    /// it is the difference between an id that leaks how many artifacts exist and one that does not.
    /// </summary>
    [Fact]
    public void AnIdentityIsRandomRatherThanSequential()
    {
        var first = EvidenceIds.New();
        var second = EvidenceIds.New();

        Assert.Equal('4', first[3 + 12]);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void IdentitiesDoNotRepeat()
    {
        var minted = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 500; i++)
        {
            Assert.True(minted.Add(EvidenceIds.New()));
        }
    }
}
