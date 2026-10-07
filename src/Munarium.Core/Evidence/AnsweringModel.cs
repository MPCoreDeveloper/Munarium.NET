namespace Munarium.Evidence;

using Munarium.Providers;

/// <summary>
/// The model a turn's completions are asked of.
/// </summary>
/// <remarks>
/// The provider and the name it is asked for are one thing - which model answers this turn - so they travel together:
/// a caller that paired one deployment's provider with another's model name would be asking a model that does not
/// exist, or worse, one nobody meant to pay for.
/// </remarks>
/// <param name="Provider">The provider that answers.</param>
/// <param name="ModelId">The model to ask for, as the provider names it.</param>
public sealed record AnsweringModel(IModelProvider Provider, string ModelId);
