namespace Munarium.Providers.Tests;

using Munarium.Providers;

/// <summary>
/// Tests for the deterministic embedder. Reproducibility is the property that matters most here:
/// an index built with it has to rebuild bit-identically.
/// </summary>
public class DeterministicEmbeddingProviderTests
{
    [Fact]
    public void TheSameTextAlwaysEmbedsToTheSameVector()
    {
        var provider = new DeterministicEmbeddingProvider(64);

        Assert.Equal(
            provider.Embed("the supplier is north"),
            provider.Embed("the supplier is north"));
    }

    [Fact]
    public void ATextEmbedsToAUnitVectorOfTheRequestedWidth()
    {
        var vector = new DeterministicEmbeddingProvider(64).Embed("north supplier policy");

        Assert.Equal(64, vector.Length);
        Assert.Equal(1.0, Dot(vector, vector), 6);
    }

    [Fact]
    public void SharedVocabularyLandsCloserThanUnrelatedText()
    {
        var provider = new DeterministicEmbeddingProvider(256);
        var query = provider.Embed("sanctioned supplier policy");
        var near = provider.Embed("the supplier appears on the sanctioned policy list");
        var far = provider.Embed("weather over the north sea and shipping lanes");

        Assert.True(Dot(query, near) > Dot(query, far));
    }

    [Fact]
    public async Task EmbedAsyncReturnsOneVectorPerInputInOrder()
    {
        var provider = new DeterministicEmbeddingProvider(32);

        var response = await provider.EmbedAsync(new EmbeddingRequest
        {
            Model = DeterministicEmbeddingProvider.ModelName,
            Inputs = ["alpha", "beta"],
        });

        Assert.Equal(2, response.Vectors.Count);
        Assert.Equal(provider.Embed("alpha"), response.Vectors[0].ToArray());
        Assert.Equal(provider.Embed("beta"), response.Vectors[1].ToArray());
    }

    [Fact]
    public async Task HealthIsObservedRatherThanAssumed()
    {
        var health = await new DeterministicEmbeddingProvider().HealthAsync();

        Assert.True(health.Healthy);
        Assert.False(string.IsNullOrWhiteSpace(health.Detail));
    }

    private static double Dot(float[] left, float[] right)
    {
        var sum = 0.0;

        for (var index = 0; index < left.Length; index++)
        {
            sum += (double)left[index] * right[index];
        }

        return sum;
    }
}
