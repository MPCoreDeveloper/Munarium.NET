namespace Munarium.Providers;

/// <summary>
/// One provider configuration, as an operator reads it.
/// </summary>
/// <remarks>
/// Free introspection: no provider call is made and no token is spent, which is why this is served beside the paid
/// probe. It carries what a config resolves to - the concrete model behind each tier - and whether its credential
/// resolves right now; never the credential, and never the reference's own value.
/// </remarks>
/// <param name="Name">The config name, or <c>default-&lt;family&gt;</c> for a synthesized default.</param>
/// <param name="Provider">The family it speaks for.</param>
/// <param name="Source">Whether this deployment applied it, or it was synthesized from the conventional variable.</param>
/// <param name="CredentialOk">Whether its credential resolves - or is unnecessary, for a local endpoint.</param>
/// <param name="Fast">The model the fast tier resolves to.</param>
/// <param name="Capable">The model the capable tier resolves to.</param>
/// <param name="Frontier">The model the frontier tier resolves to.</param>
public sealed record ProviderSummary(
    string Name,
    string Provider,
    string Source,
    bool CredentialOk,
    string? Fast,
    string? Capable,
    string? Frontier);

/// <summary>What a live probe of one configuration observed.</summary>
/// <param name="Healthy">Whether the provider answered.</param>
/// <param name="Family">The family that was probed.</param>
/// <param name="EndpointFingerprint">A fingerprint of the endpoint, never of the credential.</param>
/// <param name="Detail">What was observed, or why nothing could be.</param>
public readonly record struct ProviderProbe(
    bool Healthy,
    string Family,
    string EndpointFingerprint,
    string Detail);

/// <summary>One probe of the provider plane: a small live completion against one family and tier.</summary>
/// <param name="Family">The family probed.</param>
/// <param name="Tier">The tier probed.</param>
/// <param name="Model">The model id probed.</param>
/// <param name="Ok">Whether the model answered.</param>
/// <param name="Skipped">Whether the probe was skipped because no credential resolves.</param>
/// <param name="LatencyMs">How long the probe took, when it ran.</param>
/// <param name="Detail">The outcome, in words - never key material, never a response body.</param>
public sealed record ProviderProbeCheck(
    string Family,
    string Tier,
    string Model,
    bool Ok,
    bool Skipped,
    long? LatencyMs,
    string Detail);
