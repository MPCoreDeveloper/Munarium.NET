namespace Munarium.Wire;

using System.Text.Json.Serialization;

/// <summary>
/// The JSON shape of the wire contract.
/// </summary>
/// <remarks>
/// Snake case and source-generated metadata, so the JSON the server emits is the JSON the
/// specification describes (<c>claim_id</c>, <c>as_of</c>, <c>top_k</c>) and the serializer needs no
/// reflection at runtime.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WireHealth))]
[JsonSerializable(typeof(WireVersionHead))]
[JsonSerializable(typeof(WireVersion))]
[JsonSerializable(typeof(WireVersionLineage))]
[JsonSerializable(typeof(WireVersionRequest))]
[JsonSerializable(typeof(WireContextSection))]
[JsonSerializable(typeof(WireContextRequest))]
[JsonSerializable(typeof(WireComposedContext))]
[JsonSerializable(typeof(WireClaimProposal))]
[JsonSerializable(typeof(WireClaimOutcome))]
[JsonSerializable(typeof(WireClaimCandidate))]
[JsonSerializable(typeof(WireClaimBatchRequest))]
[JsonSerializable(typeof(WireClaimBatchOutcome))]
[JsonSerializable(typeof(WireFinding))]
[JsonSerializable(typeof(WireProblem))]
[JsonSerializable(typeof(WireFact))]
[JsonSerializable(typeof(WireFactSlice))]
[JsonSerializable(typeof(WireSourceReference))]
[JsonSerializable(typeof(WireProvenanceEnvelope))]
[JsonSerializable(typeof(WireRetrievedChunk))]
[JsonSerializable(typeof(WireSearchQuery))]
[JsonSerializable(typeof(WireSearchResult))]
[JsonSerializable(typeof(WireShape))]
[JsonSerializable(typeof(WireShapeList))]
public sealed partial class WireJson : JsonSerializerContext;
