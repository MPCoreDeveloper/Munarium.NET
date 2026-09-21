namespace Munarium.Providers;

/// <summary>
/// The three model tiers every provider family maps onto.
/// </summary>
/// <remarks>
/// A tier is how a deployment says <em>how good a model</em> without naming one: a runbook pins a task level, the level
/// resolves to a tier, and the tier resolves through the declaration the deployment applied - or through the family's
/// built-in table, which is what lets a deployment answer before it has applied anything.
/// </remarks>
public enum ModelTier
{
    /// <summary>The lesser, cheaper model - the one a small classification or an assist uses.</summary>
    Fast = 0,

    /// <summary>The stronger model, and the one a tier without a declaration falls back to.</summary>
    Capable = 1,

    /// <summary>The family's most capable - and most expensive - model.</summary>
    Frontier = 2,
}

/// <summary>
/// Reading and naming a tier the way the contract spells it.
/// </summary>
public static class ModelTiers
{
    /// <summary>Every tier, in the order the plane probes and reports them.</summary>
    public static readonly IReadOnlyList<ModelTier> All = [ModelTier.Fast, ModelTier.Capable, ModelTier.Frontier];

    /// <summary>Names a tier as the contract spells it.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The name, or the number when a value nobody declared arrives.</returns>
    public static string Name(ModelTier tier) => tier switch
    {
        ModelTier.Fast => "fast",
        ModelTier.Capable => "capable",
        ModelTier.Frontier => "frontier",
        _ => ((int)tier).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Reads a tier's name.</summary>
    /// <param name="name">The name, as a document or a caller spells it.</param>
    /// <param name="tier">The tier that name stands for.</param>
    /// <returns><see langword="true"/> when the name is one of the three.</returns>
    public static bool TryParse(string? name, out ModelTier tier)
    {
        switch (name)
        {
            case "fast":
                tier = ModelTier.Fast;
                return true;

            case "capable":
                tier = ModelTier.Capable;
                return true;

            case "frontier":
                tier = ModelTier.Frontier;
                return true;

            default:
                tier = ModelTier.Capable;
                return false;
        }
    }
}
