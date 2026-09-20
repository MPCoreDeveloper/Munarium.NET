namespace Munarium.Wire;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Munarium.Access;
using Munarium.Claims;
using Munarium.Commands;
using Munarium.Context;
using Munarium.Counters;
using Munarium.Evidence;
using Munarium.Facts;
using Munarium.Governance;
using Munarium.Idempotency;
using Munarium.Authoring;
using Munarium.Ledger;
using Munarium.Providers;
using Munarium.Promises;
using Munarium.Retrieval;
using Munarium.Runbooks;
using Munarium.Sessions;

// The kernel's runbook namespace and the runbook reader share a name, and both declare a Severity: the wire's findings
// are the claims plane's, so that one keeps the short name here.
using Severity = Munarium.Claims.Severity;
using Munarium.Shapes;
using Munarium.Sources;
using Munarium.Versions;

/// <summary>
/// The one implementation behind every transport.
/// </summary>
/// <remarks>
/// Both surfaces - the JSON/HTTP one, and the gRPC/protobuf one generated from the same specification -
/// are adapters over this class. A behaviour is therefore fixed once, and two transports can only ever
/// disagree about encoding, never about substance.
/// </remarks>
public sealed class MunariumOperations(
    IStorageBackend storage,
    ClaimLedger claims,
    CandidateLedger candidates,
    FindingsLedger findings,
    AnchorLedger anchors,
    PromiseLedger promises,
    CounterLedger counters,
    FactLedger facts,
    ShapeRegistry shapes,
    IIndexHost indexHost,
    IModelProvider embedder,
    Composer composer,
    MeshSnapshotBuilder snapshots,
    string embeddingModel,
    IngestRunner ingest,
    ISourceRegistry sources,
    IIdempotencyStore idempotency,
    IndexBuilder indexBuilder,
    IndexCatalog catalogue,
    IIndexVersionStore indexVersions,
    IEvidenceStore evidence,
    ISourceStore evidenceBytes,
    IRunbookStore runbooks,
    ISessionStore sessions,
    IAccessTokenAudit accessAudit,
    IAuthoringDraftStore authoring,
    IShapeStore? shapeStore,
    string tenant,
    IModelProvider? model = null,
    string modelId = "")
{
    /// <summary>The wire contract version this implementation speaks.</summary>
    public const string Contract = "mmp.v1";

    /// <summary>What the contract says to return when the caller does not say how many chunks it wants.</summary>
    public const int DefaultTopK = 10;

    /// <summary>The problem identifier a contended write answers with.</summary>
    public const string ContendedWriteProblem = "https://munarium.dev/problems/contended-write";

    /// <summary>The problem identifier a request that cannot be understood answers with.</summary>
    public const string InvalidRequestProblem = "https://munarium.dev/problems/invalid-request";

    /// <summary>The problem identifier a document this port cannot read answers with.</summary>
    public const string UnsupportedMediaTypeProblem = "https://munarium.dev/problems/unsupported-media-type";

    /// <summary>The problem identifier a request without a usable capability answers with.</summary>
    public const string UnauthorizedProblem = "https://munarium.dev/problems/unauthorized";

    /// <summary>The problem identifier a withdrawal naming something never issued answers with.</summary>
    public const string UnknownAccessTokenProblem = "https://munarium.dev/problems/unknown-access-token";

    /// <summary>The problem identifier an unknown application pattern answers with.</summary>
    public const string UnknownAuthoringPatternProblem = "https://munarium.dev/problems/unknown-authoring-pattern";

    /// <summary>The problem identifier a draft that is not kept here answers with.</summary>
    public const string UnknownAuthoringDraftProblem = "https://munarium.dev/problems/unknown-authoring-draft";
    /// <summary>The problem identifier a document that is not the one declared answers with.</summary>
    public const string ContentHashMismatchProblem = "https://munarium.dev/problems/content-hash-mismatch";

    /// <summary>The problem identifier a source that was never ingested answers with.</summary>
    public const string UnknownSourceProblem = "https://munarium.dev/problems/unknown-source";

    /// <summary>The problem identifier a build that could not be made answers with.</summary>
    public const string IndexBuildRefusedProblem = "https://munarium.dev/problems/index-build-refused";

    /// <summary>The problem identifier a version that was never recorded answers with.</summary>
    public const string UnknownIndexVersionProblem = "https://munarium.dev/problems/unknown-index-version";

    /// <summary>The problem identifier a version this process cannot serve answers with.</summary>
    public const string IndexNotBuiltHereProblem = "https://munarium.dev/problems/index-not-built-here";

    /// <summary>The problem identifier a citation that names no artifact answers with.</summary>
    public const string EvidenceNotFoundProblem = "https://munarium.dev/problems/evidence-not-found";

    /// <summary>The problem identifier a reader that does not dominate the artifact's class answers with.</summary>
    public const string EvidenceForbiddenProblem = "https://munarium.dev/problems/evidence-forbidden";

    /// <summary>The problem identifier an artifact whose bytes never arrived answers with.</summary>
    public const string EvidencePendingProblem = "https://munarium.dev/problems/evidence-pending";

    /// <summary>The problem identifier an artifact whose bytes retention removed answers with.</summary>
    public const string EvidenceExpiredProblem = "https://munarium.dev/problems/evidence-expired";

    /// <summary>The problem identifier a deletion a legal hold forbids answers with.</summary>
    public const string EvidenceOnHoldProblem = "https://munarium.dev/problems/evidence-on-hold";

    /// <summary>The problem identifier an upload that has not been committed answers with - or one that cannot be.</summary>
    public const string EvidenceNotCommittedProblem = "https://munarium.dev/problems/evidence-not-committed";

    /// <summary>The problem identifier a grant that is unknown, expired or spent answers with.</summary>
    public const string EvidenceGrantInvalidProblem = "https://munarium.dev/problems/evidence-grant-invalid";

    /// <summary>The problem identifier bytes over the inline cap answer with.</summary>
    public const string EvidenceTooLargeProblem = "https://munarium.dev/problems/evidence-too-large";

    /// <summary>The problem identifier bytes that are not what the manifest declares answer with.</summary>
    public const string EvidenceHashMismatchProblem = "https://munarium.dev/problems/evidence-hash-mismatch";


    private readonly IStorageBackend _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly IRunbookStore _runbooks = runbooks ?? throw new ArgumentNullException(nameof(runbooks));
    private readonly IShapeStore? _shapeStore = shapeStore;
    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IModelProvider? _model = model;
    private readonly string _modelId = modelId ?? string.Empty;

    /// <summary>
    /// The turn runner, over the seams this surface already holds.
    /// </summary>
    /// <remarks>
    /// No evidence providers are bound: this port has not ported the fact or semantic planes into a running deployment,
    /// so a profile whose layers name one refuses <c>source-not-bound</c> - which is the honest answer, and the one the
    /// hierarchy exists to give rather than answering from documents that were never declared.
    /// <para>
    /// The embedder stands in for the model when none is configured, and it is never reached in that case: the turn
    /// operation refuses any modelled step outright rather than asking a provider that cannot answer.
    /// </para>
    /// </remarks>
    private readonly SessionTurnRunner _turns = new(
        sessions,
        indexHost,
        embedder,
        model ?? embedder,
        embeddingModel,
        [],
        tenant,
        new CollectionIndexes(indexVersions, indexHost, tenant));

    /// <summary>The problem identifier a session nobody opened answers with.</summary>
    public const string SessionNotFoundProblem = "https://munarium.dev/problems/session-not-found";

    /// <summary>The problem identifier a session whose runbook was removed answers with.</summary>
    public const string SessionRunbookRemovedProblem = "https://munarium.dev/problems/session-runbook-removed";

    /// <summary>The problem identifier a clearance that cannot open a session answers with.</summary>
    public const string SessionForbiddenProblem = "https://munarium.dev/problems/session-forbidden";

    /// <summary>The problem identifier a session that is no longer open answers with.</summary>
    public const string SessionClosedProblem = "https://munarium.dev/problems/session-closed";

    /// <summary>The problem identifier a required layer that could not answer produces.</summary>
    public const string RequiredLayerProblem = "https://munarium.dev/problems/required-layer-unavailable";

    /// <summary>The problem identifier a modelled step with no model configured produces.</summary>
    public const string NoCompletionModelProblem = "https://munarium.dev/problems/no-completion-model";

    /// <summary>The problem identifier a profile nobody declared produces.</summary>
    public const string UnknownProfileProblem = "https://munarium.dev/problems/unknown-research-profile";

    /// <summary>The problem identifier a runbook nobody applied produces.</summary>
    public const string UnknownRunbookProblem = "https://munarium.dev/problems/unknown-runbook";

    /// <summary>
    /// The most turns a transcript read returns.
    /// </summary>
    /// <remarks>
    /// A bound rather than a page: a conversation is operator-scale, and a read that could return an unbounded transcript
    /// is a read that can be made to hold an unbounded response. An unbounded <em>store</em> is still the store's to
    /// bound, which is where retention belongs.
    /// </remarks>
    public const int SessionTurnLimit = 200;
    private readonly ClaimLedger _claims = claims ?? throw new ArgumentNullException(nameof(claims));
    private readonly CandidateLedger _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
    private readonly FindingsLedger _findings = findings ?? throw new ArgumentNullException(nameof(findings));
    private readonly AnchorLedger _anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));
    private readonly PromiseLedger _promises = promises ?? throw new ArgumentNullException(nameof(promises));
    private readonly CounterLedger _counters = counters ?? throw new ArgumentNullException(nameof(counters));
    private readonly FactLedger _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    private readonly ShapeRegistry _shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));
    private readonly IIndexHost _index = indexHost ?? throw new ArgumentNullException(nameof(indexHost));
    private readonly IModelProvider _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    private readonly Composer _composer = composer ?? throw new ArgumentNullException(nameof(composer));
    private readonly MeshSnapshotBuilder _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly string _embeddingModel = embeddingModel ?? throw new ArgumentNullException(nameof(embeddingModel));
    private readonly IngestRunner _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
    private readonly ISourceRegistry _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    private readonly IIdempotencyStore _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
    private readonly IndexBuilder _builder = indexBuilder ?? throw new ArgumentNullException(nameof(indexBuilder));
    private readonly IndexCatalog _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    private readonly IEvidenceStore _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    private readonly ISourceStore _evidenceBytes = evidenceBytes ?? throw new ArgumentNullException(nameof(evidenceBytes));
    private readonly IAccessTokenAudit _accessAudit = accessAudit ?? throw new ArgumentNullException(nameof(accessAudit));
    private readonly IAuthoringDraftStore _authoring = authoring ?? throw new ArgumentNullException(nameof(authoring));
    private readonly string _tenant = string.IsNullOrWhiteSpace(tenant)
        ? throw new ArgumentException("The deployment's tenant must be named.", nameof(tenant))
        : tenant;


    /// <summary>The application patterns this deployment serves.</summary>
    /// <returns>The patterns, in the catalog own order.</returns>
    public static WireAuthoringPatternList ListAuthoringPatterns() =>
        new([.. AuthoringCatalog.Patterns.Select(Pattern)]);

    /// <summary>One application pattern.</summary>
    /// <param name="id">Its identity.</param>
    /// <returns>The pattern, or a problem when this deployment does not serve it.</returns>
    public static WireAuthoringPatternResult AuthoringPattern(string id) =>
        AuthoringCatalog.Pattern(id) is { } pattern
            ? Pattern(pattern)
            : new WireProblem(
                UnknownAuthoringPatternProblem,
                $"No application pattern with the identity '{id}' is served here.",
                Status: 404,
                ExpectedHead: 0,
                ActualHead: 0);

    /// <summary>Reads a served pattern as the contract carries it.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The wire shape.</returns>
    private static WireAuthoringPattern Pattern(AuthoringPattern pattern) => new(
        pattern.Id,
        pattern.Name,
        pattern.Description,
        pattern.StartFrom,
        pattern.Guidance,
        pattern.ShapeNames,
        pattern.HasCompletion,
        pattern.DecisionNotes);

    /// <summary>Opens a draft, which is a name and optionally the pattern to start from.</summary>
    /// <param name="request">What to open.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The draft, or why it could not be opened.</returns>
    public async ValueTask<WireAuthoringDraftResult> OpenDraftAsync(
        WireAuthoringDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = (request.Name ?? string.Empty).Trim();

        // The rule the materializer applies, refused here where it can still be explained: a draft whose name is not a
        // runbook name opens and then cannot be materialized, which is a worse experience than being told now.
        if (name.Length == 0 || name.Contains('@', StringComparison.Ordinal))
        {
            return new WireProblem(
                InvalidRequestProblem,
                $"a draft name must be non-empty and must not contain '@', and '{name}' is not",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var patternId = request.PatternId is { Length: > 0 } asked ? asked : null;

        if (patternId is not null && AuthoringCatalog.Pattern(patternId) is null)
        {
            return new WireProblem(
                UnknownAuthoringPatternProblem,
                $"No application pattern with the identity '{patternId}' is served here.",
                Status: 404,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var stored = await _authoring
            .SaveAsync(new AuthoringDraft { Name = name, PatternId = patternId }, cancellationToken)
            .ConfigureAwait(false);

        return Draft(stored);
    }

    /// <summary>Lists the drafts this deployment keeps, most recently written first.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The drafts.</returns>
    public async ValueTask<WireAuthoringDraftList> ListDraftsAsync(CancellationToken cancellationToken = default)
    {
        var drafts = await _authoring.ListAsync(cancellationToken).ConfigureAwait(false);

        return new WireAuthoringDraftList([.. drafts.Select(Draft)]);
    }

    /// <summary>Reads one draft, with the questions its pattern asks and what is still open.</summary>
    /// <param name="name">The draft name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The draft, or a problem when there is none by that name.</returns>
    public async ValueTask<WireAuthoringDraftResult> ReadDraftAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var draft = await StoredAsync(name, cancellationToken).ConfigureAwait(false);

        return draft is null ? UnknownDraft(name) : Draft(draft);
    }

    /// <summary>Replaces a draft answers.</summary>
    /// <remarks>
    /// A replacement rather than a merge, because that is what a PUT is: an answer an author removes has to disappear,
    /// and a merge would keep it alive forever.
    /// </remarks>
    /// <param name="name">The draft name.</param>
    /// <param name="answers">The answers as they now stand.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The draft as stored, or a problem when there is none by that name.</returns>
    public async ValueTask<WireAuthoringDraftResult> AnswerDraftAsync(
        string name,
        WireAuthoringAnswers answers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answers);

        var draft = await StoredAsync(name, cancellationToken).ConfigureAwait(false);

        if (draft is null)
        {
            return UnknownDraft(name);
        }

        var stored = await _authoring
            .SaveAsync(draft with { Answers = Answered(answers) }, cancellationToken)
            .ConfigureAwait(false);

        return Draft(stored);
    }

    /// <summary>Reads a stored draft, or nothing when the name is blank.</summary>
    /// <param name="name">The draft name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The draft, or <see langword="null"/>.</returns>
    private async ValueTask<AuthoringDraft?> StoredAsync(string name, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : await _authoring.FindAsync(name.Trim(), cancellationToken).ConfigureAwait(false);

    /// <summary>The note an assist answers with when this deployment cannot call a model.</summary>
    public const string AssistUnavailableNote = "assist unavailable: this deployment has no model bound";

    /// <summary>The instruction an assist asks under.</summary>
    /// <remarks>
    /// One shape, stated once: the model answers with suggestions and nothing else, so a reply that is not that shape is
    /// discarded rather than half-read. A model asked for prose produces prose, and an author cannot apply prose.
    /// </remarks>
    private const string AssistSystem =
        "You review a runbook draft. Answer with JSON and nothing else: "
        + "{\"suggestions\":[{\"path\":\"<document path>\",\"note\":\"<what to change and why>\"}]}";

    /// <summary>Asks a model what it would change about a draft, and never edits the draft itself.</summary>
    /// <remarks>
    /// What this port does differently from the original, and why: there a draft holds documents, so an assist can replace
    /// them. Here a draft holds answers and its documents are derived from them, which is what keeps a draft from
    /// disagreeing with itself - so an assist suggests and reports, and the only way an answer changes is an author
    /// answering. A deployment with no model bound, or one that fails, answers with a note instead: an author asking for
    /// help must not be told their draft became invalid.
    /// </remarks>
    /// <param name="name">The draft name.</param>
    /// <param name="request">What the author asked for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What it suggests, or why the draft could not be looked at.</returns>
    public async ValueTask<WireDraftAssistResult> AssistDraftAsync(
        string name,
        WireAssistDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await StoredAsync(name, cancellationToken).ConfigureAwait(false) is not { } draft)
        {
            return UnknownDraft(name);
        }

        var (set, refused) = AuthoringMaterializer.Build(
            draft.Name,
            AuthoringCatalog.Pattern(draft.PatternId),
            draft.Answers);

        if (set is null)
        {
            return InvalidDraft(draft.Name, refused);
        }

        if (RunbookEntry(set).Yaml is not { } yaml)
        {
            return InvalidDraft(draft.Name, "the materialized set carries no runbook");
        }

        var (suggestions, note) = await AdviseAsync(request, set, yaml, cancellationToken)
            .ConfigureAwait(false);

        var (document, _) = RunbookReader.Read(yaml);

        return new WireDraftAssist(suggestions, note, Findings(document, set.Todos));
    }

    /// <summary>Asks the deployment model, degrading to a note rather than failing.</summary>
    /// <remarks>
    /// Three specific failures are caught rather than every failure: a provider that cannot be reached, one that answers
    /// with something that is not a completion, and an answer that is not the JSON that was asked for. Anything else
    /// propagates, because a bug in this deployment is not an authoring problem and turning one into a note would hide it.
    /// </remarks>
    /// <param name="request">What the author asked for.</param>
    /// <param name="set">The materialized set.</param>
    /// <param name="yaml">The runbook it would apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The suggestions, and the note when there is no help to give.</returns>
    private async ValueTask<(IReadOnlyList<WireSuggestion> Suggestions, string? Note)> AdviseAsync(
        WireAssistDraftRequest request,
        Materialized set,
        string yaml,
        CancellationToken cancellationToken)
    {
        if (_model is null)
        {
            return ([], AssistUnavailableNote);
        }

        var asked = new CompletionRequest
        {
            Model = _modelId,
            System = AssistSystem,
            Prompt = AssistPrompt(request, yaml, set.Todos),
        };

        try
        {
            var completion = await _model.CompleteAsync(asked, cancellationToken).ConfigureAwait(false);

            return (Suggestions(completion.Text), null);
        }
        catch (HttpRequestException exception)
        {
            return ([], $"assist unavailable: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return ([], $"assist unavailable: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return ([], $"assist unavailable: {exception.Message}");
        }
    }




    /// <summary>States the draft to a model: what it would apply, what it still owes, and what the author wants.</summary>
    /// <param name="request">What the author asked for.</param>
    /// <param name="yaml">The runbook it would apply.</param>
    /// <param name="todos">What the draft still owes.</param>
    /// <returns>The prompt.</returns>
    private static string AssistPrompt(
        WireAssistDraftRequest request,
        string yaml,
        IReadOnlyList<string> todos) =>
        $"This runbook is what the draft would apply:\n\n{yaml}\n\n"
        + $"Still to be answered:\n{(todos.Count == 0 ? "nothing" : string.Join("\n", todos))}\n\n"
        + (request.Description is { Length: > 0 } said ? $"The author asks: {said}\n" : string.Empty);

    /// <summary>Reads the suggestions out of a model answer, discarding anything that is not one.</summary>
    /// <remarks>
    /// The reply is not repaired into shape: a model that wrapped its JSON in prose gets its prose dropped, because an
    /// author can apply a suggestion and cannot apply a guess at what the model meant.
    /// </remarks>
    /// <param name="answer">The generated text.</param>
    /// <returns>The suggestions, empty when the answer carries none.</returns>
    private static List<WireSuggestion> Suggestions(string answer)
    {
        var suggestions = new List<WireSuggestion>();
        var start = answer.IndexOf('{', StringComparison.Ordinal);
        var end = answer.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return suggestions;
        }

        try
        {
            using var document = JsonDocument.Parse(answer[start..(end + 1)]);

            if (document.RootElement.TryGetProperty("suggestions", out var list)
                && list.ValueKind is JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (Suggestion(item) is { } suggestion)
                    {
                        suggestions.Add(suggestion);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A reply this cannot read says nothing. It is not half-read into a suggestion.
            return suggestions;
        }

        return suggestions;
    }

    /// <summary>Reads a materialized runbook as the findings a validation reports.</summary>
    /// <param name="document">The runbook, or <see langword="null"/> when it does not read at all.</param>
    /// <param name="todos">What the draft still owes, which comes from the materializer rather than from a second rule.</param>
    /// <returns>The findings.</returns>
    private static WireDraftValidation Findings(RunbookDocument? document, IReadOnlyList<string> todos)
    {
        if (document is null)
        {
            return new WireDraftValidation(true, [], todos);
        }

        var reported = RunbookValidation.Validate(document);

        return new WireDraftValidation(
            RunbookValidation.IsValid(reported),
            [.. reported.Select(Finding)],
            todos);
    }
    /// <summary>Reads one suggestion, or nothing when the entry is not one.</summary>
    /// <param name="element">The entry.</param>
    /// <returns>The suggestion.</returns>
    private static WireSuggestion? Suggestion(JsonElement element) =>
        element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty("note", out var note)
            && note.ValueKind is JsonValueKind.String
            && note.GetString() is { Length: > 0 } text
                ? new WireSuggestion(About(element), text)
                : null;

    /// <summary>Reads the document a suggestion is about, or an empty name when it says none.</summary>
    /// <param name="element">The entry.</param>
    /// <returns>The path the model named.</returns>
    private static string About(JsonElement element) =>
        element.TryGetProperty("path", out var path) && path.ValueKind is JsonValueKind.String
            ? path.GetString() ?? string.Empty
            : string.Empty;

    /// <remarks>
    /// Refused while an error finding exists, which is the same gate apply stands behind: a bundle is what an author hands
    /// to an operator, and handing over something a deployment would refuse is handing over a refusal.
    /// </remarks>
    /// <param name="name">The draft name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bundle, or why there is none.</returns>    /// <summary>Exports what a draft materializes as a hash-manifested bundle.</summary>
    public async ValueTask<WireDraftBundleResult> ExportDraftAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (await StoredAsync(name, cancellationToken).ConfigureAwait(false) is not { } draft)
        {
            return UnknownDraft(name);
        }

        var (set, refused) = AuthoringMaterializer.Build(
            draft.Name,
            AuthoringCatalog.Pattern(draft.PatternId),
            draft.Answers);

        if (set is null)
        {
            return InvalidDraft(draft.Name, refused);
        }

        if (RunbookEntry(set).Yaml is not { } yaml)
        {
            return InvalidDraft(draft.Name, "the materialized set carries no runbook");
        }

        if (RunbookReader.Read(yaml) is not ({ } document, null))
        {
            return InvalidDraft(draft.Name, "the materialized runbook does not read");
        }

        var reported = RunbookValidation.Validate(document);

        if (!RunbookValidation.IsValid(reported))
        {
            return InvalidDraft(draft.Name, "it does not validate, and a bundle that does not validate is not exported");
        }

        var bundle = AuthoringBundle.Build(
            draft.Name,
            Rfc3339(DateTimeOffset.UtcNow),
            ToolVersion,
            set,
            BundleValidation.From(reported));

        // The materializer proves its own output reads, and this proves a bundle of it agrees with itself: a bundle is what
        // leaves this deployment, and one that fails its own check on arrival is a refusal that travelled.
        return AuthoringBundle.Verify(bundle) is { } broken
            ? new WireProblem(
                BundleInconsistentProblem,
                $"The exported bundle does not agree with itself: {broken}",
                Status: 500,
                ExpectedHead: 0,
                ActualHead: 0)
            : Bundle(bundle);
    }

    /// <summary>Gets the version of this server, as a bundle reports it.</summary>
    private static string ToolVersion =>
        typeof(MunariumOperations).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Carries a bundle as the contract carries it.</summary>
    /// <param name="bundle">The bundle.</param>
    /// <returns>The wire shape.</returns>
    private static WireAuthoringBundle Bundle(AuthoringBundle bundle) => new(
        AuthoringBundle.Kind,
        AuthoringBundle.ApiVersion,
        new WireBundleTool(bundle.Tool.Name, bundle.Tool.Version),
        bundle.DraftId,
        bundle.Name,
        bundle.CreatedAt,
        bundle.Files,
        bundle.Hashes,
        bundle.ApplyOrder,
        bundle.ManifestHash,
        new WireBundleValidation(
            bundle.Validation.Valid,
            bundle.Validation.Errors,
            bundle.Validation.Warns,
            bundle.Validation.Infos));
    /// <summary>Applies what a draft would apply, to this deployment.</summary>
    /// <remarks>
    /// Shapes first, then the runbook that binds them, which is the order the contract states: a collection binding a
    /// shape this deployment does not serve would materialize nothing, so publishing the shape after the runbook would
    /// leave a window in which the runbook is live and unusable.
    /// <para>
    /// The set is validated here as well as by the validation operation, and refused on an error finding either way: an
    /// author who validated yesterday and answered a question today would otherwise apply something nobody checked.
    /// </para>
    /// </remarks>
    /// <param name="name">The draft name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What landed, or why nothing did.</returns>
    public async ValueTask<WireDraftApplyResult> ApplyDraftAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (await StoredAsync(name, cancellationToken).ConfigureAwait(false) is not { } draft)
        {
            return UnknownDraft(name);
        }

        var (set, refused) = AuthoringMaterializer.Build(
            draft.Name,
            AuthoringCatalog.Pattern(draft.PatternId),
            draft.Answers);

        if (set is null)
        {
            return InvalidDraft(draft.Name, refused);
        }

        if (RunbookEntry(set) is not ({ } runbookPath, { } yaml))
        {
            return InvalidDraft(draft.Name, "the materialized set carries no runbook");
        }

        if (RunbookReader.Read(yaml) is not ({ } document, null))
        {
            return InvalidDraft(draft.Name, "the materialized runbook does not read");
        }

        if (!RunbookValidation.IsValid(RunbookValidation.Validate(document)))
        {
            return InvalidDraft(draft.Name, "it does not validate, and nothing that does not validate is applied");
        }

        var applied = new List<WireAppliedDocument>(set.Documents.Count);

        // Shapes first, and a set whose shape cannot be published applies nothing at all: half an applied set is not an
        // applied set, and reporting one as applied would be the worst of both.
        foreach (var (path, json) in set.Documents.Where(entry => !entry.Key.EndsWith(".yaml", StringComparison.Ordinal)))
        {
            if (Shape(json) is not { } shape)
            {
                return InvalidDraft(draft.Name, $"the document at '{path}' is not a shape this deployment can read");
            }

            if (_shapeStore is null)
            {
                return InvalidDraft(
                    draft.Name,
                    "this deployment serves shapes from no directory, so it cannot publish one");
            }

            await _shapeStore.PublishAsync(shape, cancellationToken).ConfigureAwait(false);
            _ = _shapes.Publish(shape);

            applied.Add(new WireAppliedDocument(path, "Shape", shape.Name, ArtifactContent.Hash(json)));
        }

        return await ApplyRunbookAsync(new WireRunbookApply(yaml), cancellationToken).ConfigureAwait(false) switch
        {
            WireAppliedRunbook runbook => new WireAuthoringApplied(
            [
                .. applied,
                new WireAppliedDocument(runbookPath, "Runbook", runbook.RunbookRef, ArtifactContent.Hash(yaml)),
            ]),
            WireProblem problem => problem,
        };
    }

    /// <summary>Finds the one runbook a materialized set would apply, and the path it came under.</summary>
    /// <param name="set">The materialized set.</param>
    /// <returns>The path and the YAML, both <see langword="null"/> when the set carries no runbook.</returns>
    private static (string? Path, string? Yaml) RunbookEntry(Materialized set)
    {
        foreach (var (path, document) in set.Documents)
        {
            if (path.EndsWith(".yaml", StringComparison.Ordinal))
            {
                return (path, document);
            }
        }

        return (null, null);
    }

    /// <summary>Reads a document of a materialized set as a shape.</summary>
    /// <param name="json">The document.</param>
    /// <returns>The shape, or <see langword="null"/> when the document is not one this deployment reads.</returns>
    private static FactShape? Shape(string json)
    {
        try
        {
            return ShapeDocuments.Read(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Validates the documents a draft would apply, without applying anything.</summary>
    /// <remarks>
    /// The findings a deployment would refuse to apply on, read before anything is applied. An author who had to apply a
    /// document to find out what is wrong with it would be applying documents to find out, and the first thing they would
    /// find out is that their turn failed in front of a user.
    /// </remarks>
    /// <param name="name">The draft name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The findings, or why the draft could not be built at all.</returns>
    public async ValueTask<WireDraftValidationResult> ValidateDraftAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (await StoredAsync(name, cancellationToken).ConfigureAwait(false) is not { } draft)
        {
            return UnknownDraft(name);
        }

        var (set, refused) = AuthoringMaterializer.Build(
            draft.Name,
            AuthoringCatalog.Pattern(draft.PatternId),
            draft.Answers);

        if (set is null)
        {
            return InvalidDraft(draft.Name, refused);
        }

        if (Runbook(set) is not { } yaml)
        {
            return InvalidDraft(draft.Name, "the materialized set carries no runbook");
        }

        var (document, unreadable) = RunbookReader.Read(yaml);

        if (document is null)
        {
            return InvalidDraft(draft.Name, unreadable);
        }

        var reported = RunbookValidation.Validate(document);

        return new WireDraftValidation(
            RunbookValidation.IsValid(reported),
            [.. reported.Select(Finding)],
            set.Todos);
    }

    /// <summary>Removes a draft.</summary>
    /// <remarks>
    /// What an author does once the runbook is applied: a draft was a conversation, and when the document is applied the
    /// document is the authority and the conversation is spent. Nothing else refers to a draft, which is why removing one
    /// needs no confirmation - unlike a runbook version, which sessions pin by name and which therefore takes two passes.
    /// </remarks>
    /// <param name="name">The draft name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was removed, or why nothing was.</returns>
    public async ValueTask<WireDraftRemovalResult> DeleteDraftAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        var wanted = name.Trim();

        return wanted.Length > 0
            && await _authoring.RemoveAsync(wanted, cancellationToken).ConfigureAwait(false)
            ? new WireAuthoringDraftRemoved(wanted)
            : UnknownDraft(wanted);
    }

    /// <summary>The problem identifier a bundle that disagrees with itself answers with.</summary>
    /// <remarks>
    /// A server fault rather than a caller one: nothing a caller sent can make a bundle inconsistent, so this firing would
    /// mean a deployment built something it would not accept from anyone else.
    /// </remarks>
    public const string BundleInconsistentProblem = "https://munarium.dev/problems/bundle-inconsistent";
    /// <summary>The problem identifier a draft that cannot be built answers with.</summary>
    public const string InvalidAuthoringDraftProblem = "https://munarium.dev/problems/authoring-draft-invalid";

    /// <summary>The problem a draft that cannot be built answers with.</summary>
    /// <remarks>
    /// A conflict rather than a bad request: what the caller sent was a name that exists, and what refuses it is the state
    /// the document would be in - which is why the same request may succeed once the interview is answered.
    /// </remarks>
    /// <param name="name">The draft name.</param>
    /// <param name="why">Why it could not be built.</param>
    /// <returns>The problem.</returns>
    private static WireProblem InvalidDraft(string name, string? why) => new(
        InvalidAuthoringDraftProblem,
        $"The draft '{name}' could not be built: {why ?? "it names nothing that can be built"}",
        Status: 409,
        ExpectedHead: 0,
        ActualHead: 0);

    /// <summary>Finds the one runbook a materialized set would apply.</summary>
    /// <remarks>
    /// The YAML and not the shape half: a runbook is what a deployment reads, and it is the runbook document that carries
    /// what the interview was answered. A set that carries no runbook is refused by the caller rather than validated as
    /// though nothing were wrong with it.
    /// </remarks>
    /// <param name="set">The materialized set.</param>
    /// <returns>The runbook's YAML, or <see langword="null"/> when the set carries none.</returns>
    private static string? Runbook(Materialized set) => RunbookEntry(set).Yaml;

    /// <summary>Reads one finding as the contract carries it.</summary>
    /// <param name="finding">The finding.</param>
    /// <returns>The wire shape.</returns>
    private static WireValidationFinding Finding(ValidationFinding finding) => new(
        finding.Severity switch
        {
            Munarium.Runbooks.Severity.Error => "error",
            Munarium.Runbooks.Severity.Warn => "warn",
            _ => "info",
        },
        finding.Code,
        finding.Message,
        finding.Path);

    /// <summary>The problem a draft that is not kept here answers with.</summary>
    /// <param name="name">The name asked for.</param>
    /// <returns>The problem.</returns>
    private static WireProblem UnknownDraft(string name) => new(
        UnknownAuthoringDraftProblem,
        $"No draft named '{name}' is kept here.",
        Status: 404,
        ExpectedHead: 0,
        ActualHead: 0);

    /// <summary>Projects a stored draft: its answers, the questions its pattern asks, and what is still open.</summary>
    /// <remarks>
    /// The open questions come from the materializer rather than from a second set of rules: what a draft still owes is
    /// exactly the TODOs its documents would carry, so a reader cannot be told a draft is complete when it would
    /// materialize with placeholders.
    /// </remarks>
    /// <param name="draft">The stored draft.</param>
    /// <returns>The wire shape.</returns>
    private static WireAuthoringDraft Draft(AuthoringDraft draft)
    {
        var pattern = AuthoringCatalog.Pattern(draft.PatternId);
        var (set, _) = AuthoringMaterializer.Build(draft.Name, pattern, draft.Answers);

        return new WireAuthoringDraft(
            draft.Name,
            draft.PatternId,
            draft.CreatedAt,
            draft.UpdatedAt,
            Values(draft.Answers),
            [
                .. AuthoringInterview
                    .For(pattern)
                    .Select(section => new WireAuthoringDraftSection(
                        section.Id,
                        section.Title,
                        section.DocRef,
                        [.. section.Questions.Select(Question)])),
            ],
            set?.Todos ?? []);
    }

    /// <summary>Reads one question as the contract carries it, with its default as the text of a JSON value.</summary>
    /// <param name="question">The question.</param>
    /// <returns>The wire shape.</returns>
    private static WireAuthoringQuestion Question(InterviewQuestion question) => new(
        question.Id,
        question.Prompt,
        question.Guidance,
        question.Kind,
        question.Required,
        DefaultText(question.Default),
        question.Choices,
        question.MapsTo);

    /// <summary>Reads a map of stored answers as the contract carries them.</summary>
    /// <param name="fields">The answers.</param>
    /// <returns>The wire shape.</returns>
    private static Dictionary<string, WireAuthoringValue> Values(IReadOnlyDictionary<string, object?> fields)
    {
        var values = new Dictionary<string, WireAuthoringValue>(StringComparer.Ordinal);

        foreach (var (name, value) in fields)
        {
            values[name] = Value(value);
        }

        return values;
    }

    /// <summary>Renders a question default as the text of a JSON value, without a serializer.</summary>
    /// <remarks>
    /// Hand-rolled because the reflective serializer is not available to a NativeAOT publish, and because a default is
    /// one of three things: a whole number, a flag or a piece of text.
    /// </remarks>
    /// <param name="value">The default, or <see langword="null"/>.</param>
    /// <returns>The text, or <see langword="null"/> when there is no default.</returns>
    private static string? DefaultText(object? value) => value switch
    {
        null => null,
        string text => string.Concat("\"", text, "\""),
        bool flag => flag ? "true" : "false",
        long number => number.ToString(CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        var other => other.ToString(),
    };

    /// <summary>Reads one stored answer as the contract carries it.</summary>
    /// <param name="answer">The answer.</param>
    /// <returns>The wire shape.</returns>
    private static WireAuthoringValue Value(object? answer) => answer switch
    {
        null => new WireAuthoringValue(null, null, null, null, null),
        string text => new WireAuthoringValue(text, null, null, null, null),
        bool flag => new WireAuthoringValue(null, null, flag, null, null),
        long number => new WireAuthoringValue(null, number, null, null, null),
        int number => new WireAuthoringValue(null, number, null, null, null),
        IReadOnlyList<object?> items => new WireAuthoringValue(null, null, null, [.. items.Select(Value)], null),
        IReadOnlyDictionary<string, object?> fields => new WireAuthoringValue(
            null,
            null,
            null,
            null,
            Values(fields)),
        _ => throw new FormatException($"an answer of type {answer.GetType().Name} is not one this contract carries"),
    };

    /// <summary>Reads offered answers back into what a materializer and a store take.</summary>
    /// <param name="answers">The offered answers.</param>
    /// <returns>The answers.</returns>
    private static Dictionary<string, object?> Answered(WireAuthoringAnswers answers)
    {
        var read = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (name, value) in answers.Answers)
        {
            read[name] = Answered(value);
        }

        return read;
    }

    /// <summary>Reads one offered answer back.</summary>
    /// <param name="value">The offered answer.</param>
    /// <returns>The answer.</returns>
    private static object? Answered(WireAuthoringValue value) => value switch
    {
        { Text: { } text } => text,
        { Number: { } number } => number,
        { Flag: { } flag } => flag,
        { Items: { } items } => new List<object?>(items.Select(Answered)),
        { Fields: { } fields } => Fielded(fields),
        _ => null,
    };

    /// <summary>Reads a map of offered answers back.</summary>
    /// <param name="fields">The map.</param>
    /// <returns>The answers.</returns>
    private static Dictionary<string, object?> Fielded(
        IReadOnlyDictionary<string, WireAuthoringValue> fields)
    {
        var read = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (name, value) in fields)
        {
            read[name] = Answered(value);
        }

        return read;
    }

    /// <summary>Liveness.</summary>
    /// <returns>Healthy, and which contract is answering.</returns>
    public static WireHealth Health() => new("ok", Contract);

    /// <summary>The current head of a version's stream.</summary>
    /// <param name="versionId">The version to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The head.</returns>
    public async ValueTask<WireVersionHead> GetHeadAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var head = await _storage.HeadAsync(StreamId.From(versionId), cancellationToken).ConfigureAwait(false);

        return new WireVersionHead(versionId, head.Value);
    }

    /// <summary>Proposes a claim, which governance then judges.</summary>
    /// <param name="versionId">The version to write to.</param>
    /// <param name="proposal">The claim as proposed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The claim as recorded, or a problem when the write could not be made.</returns>
    public async ValueTask<WireClaimResult> ProposeClaimAsync(
        string versionId,
        WireClaimProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        // A proposal that does not carry what the contract requires is the caller's fault, and it is answered
        // as one: a 400 rather than a 500, and never a disputed claim, because nothing about the ledger
        // should be recorded for a request that was not understood.
        if (DescribeInvalid(proposal) is { } invalid)
        {
            return new WireProblem(InvalidRequestProblem, invalid, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        // A key that is not a ULID is the caller's fault, like any other malformed field - and it is answered before the
        // write, so nothing is recorded for a request that was not understood.
        var scope = IdempotencyKeys.Of(IdempotencyKeys.Claim, versionId);
        var keyed = IdempotencyKeys.TryAccept(proposal.IdempotencyKey, out var unusable);

        if (unusable is not null)
        {
            return new WireProblem(InvalidRequestProblem, unusable, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var key = (proposal.IdempotencyKey ?? string.Empty).Trim();

        if (keyed && await AnsweredAsync<WireClaimOutcome>(scope, key, WireJson.Default.WireClaimOutcome, cancellationToken)
            .ConfigureAwait(false) is { } answered)
        {
            return answered;
        }

        var outcome = await _claims.RecordAsync(
            new RecordClaimCommand
            {
                VersionId = versionId,
                ClaimId = proposal.ClaimId,
                ClaimType = ClaimTypeOf(proposal.ClaimType),
                Shape = proposal.Shape,
                Body = proposal.Body,
                Statement = proposal.Statement,
                Actor = proposal.Actor,
            },
            cancellationToken).ConfigureAwait(false);

        // Reported from the same derived identity the ledger wrote, so a caller can never be told a lineage
        // the kernel did not use.
        var lineage = _shapes.LineageOf(proposal.Shape, proposal.Body);

        WireClaimResult result = outcome switch
        {
            ClaimAsserted asserted => new WireClaimOutcome(
                versionId, proposal.ClaimId, proposal.ClaimType, lineage,
                WireClaimStatus.Accepted, string.Empty, string.Empty, asserted.Head.Value),

            ClaimRecordedAsDisputed disputed => new WireClaimOutcome(
                versionId, proposal.ClaimId, proposal.ClaimType, lineage,
                WireClaimStatus.Disputed, disputed.Gate, disputed.Reason, disputed.Head.Value),

            ClaimContended contended => new WireProblem(
                ContendedWriteProblem,
                "Every retry lost to a moving head; the write was not recorded.",
                Status: 409,
                contended.Expected.Value,
                contended.Actual.Value),

            // Target-typed: the switch is one expression, and the union is what a caller receives.
            _ => throw new InvalidOperationException("The ledger answered with an outcome this port does not know."),
        };

        // Only an answer that recorded something is remembered: a contention wrote nothing, and a retry of one has to be
        // able to reach the ledger rather than be answered forever with a failure that was transient.
        if (keyed && result is WireClaimOutcome recorded)
        {
            await RememberAsync(scope, key, recorded, WireJson.Default.WireClaimOutcome, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Proposes a batch of claims, which governance then judges as one unit.</summary>
    /// <param name="versionId">The version to write to.</param>
    /// <param name="request">The batch as proposed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The batch as recorded, or a problem when it was not recorded.</returns>
    /// <remarks>
    /// The whole unit is judged against the head the gates read and landed as one conditional append, so a
    /// batch cannot half-succeed. A blocked claim is recorded as disputed rather than dropped, which is why a
    /// 200 here can carry a disputed claim: the ledger did what it was asked and recorded the refusal with it.
    /// </remarks>
    public async ValueTask<WireClaimBatchResult> ProposeClaimBatchAsync(
        string versionId,
        WireClaimBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Claims.Count == 0)
        {
            return new WireProblem(
                InvalidRequestProblem,
                "claims must not be empty; a batch with nothing to judge is not a write.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var proposals = new List<ProposedClaim>(request.Claims.Count);

        // The batch is keyed like the single write: the scope names the operation and the version, so one caller's key
        // for one batch cannot swallow another's, and the answer is replayed rather than the unit judged twice.
        var scope = IdempotencyKeys.Of(IdempotencyKeys.ClaimBatch, versionId);
        var keyed = IdempotencyKeys.TryAccept(request.IdempotencyKey, out var unusable);

        if (unusable is not null)
        {
            return new WireProblem(InvalidRequestProblem, unusable, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var key = (request.IdempotencyKey ?? string.Empty).Trim();

        if (keyed && await AnsweredAsync<WireClaimBatchOutcome>(
                scope, key, WireJson.Default.WireClaimBatchOutcome, cancellationToken).ConfigureAwait(false)
            is { } answered)
        {
            return answered;
        }

        for (var index = 0; index < request.Claims.Count; index++)
        {
            var claim = request.Claims[index];

            // The contract marks these required, so a caller that omits one is told which - and which claim.
            if (FirstMissing((claim.Subject, "subject"), (claim.Key, "key"), (claim.Value, "value")) is { } invalid)
            {
                return new WireProblem(
                    InvalidRequestProblem,
                    $"claims[{index}]: {invalid}",
                    Status: 400,
                    ExpectedHead: 0,
                    ActualHead: 0);
            }

            proposals.Add(new ProposedClaim
            {
                ClaimType = ClaimTypeOf(claim.ClaimType),
                Subject = claim.Subject,
                Key = claim.Key,
                Value = claim.Value,
                ScopePath = claim.ScopePath.Length > 0 ? claim.ScopePath : null,
                Provenance = ProvenanceOf(claim.Provenance),
                SupersedesId = claim.SupersedesId.Length > 0 ? claim.SupersedesId : null,
            });
        }

        // Zero means "no pin": a caller that names a position asks for that position and gets a contention
        // rather than a re-gate. Positions start at one, so zero is never a real head.
        var expectedHead = request.ExpectedHead > 0 ? new SequenceNumber(request.ExpectedHead) : (SequenceNumber?)null;

        var outcome = await _candidates
            .AppendAsync(versionId, proposals, request.Text, expectedHead, cancellationToken)
            .ConfigureAwait(false);

        WireClaimBatchResult result = outcome switch
        {
            CandidateRecorded recorded => new WireClaimBatchOutcome(
                versionId,
                recorded.Head.Value,
                [
                    .. recorded.Claims.Select(claim =>
                        VerdictOf(versionId, claim, recorded.Findings, recorded.Head.Value)),
                ],
                [.. recorded.Findings.Select(FindingOf)],
                recorded.FindingsSequence?.Value ?? 0),

            CandidateContended contended => new WireProblem(
                ContendedWriteProblem,
                "Every retry lost to a moving head; the batch was not recorded.",
                Status: 409,
                contended.Expected.Value,
                contended.Actual.Value),
        };

        // A batch that was judged and recorded is remembered; a contention is not, because it recorded nothing and a
        // retry has to be able to reach the ledger.
        if (keyed && result is WireClaimBatchOutcome batch)
        {
            await RememberAsync(scope, key, batch, WireJson.Default.WireClaimBatchOutcome, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Reads the findings a version's writes produced.</summary>
    /// <param name="versionId">The version whose stream is read.</param>
    /// <param name="asOf">The position to read as of, or 0 for every finding recorded.</param>
    /// <param name="severity">The severity to select, or empty for every severity.</param>
    /// <param name="ruleId">The exact rule to select, or empty.</param>
    /// <param name="rulePrefix">A rule-id prefix to select, or empty.</param>
    /// <param name="limit">How many findings to return, or 0 for all of them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The findings, oldest first.</returns>
    /// <remarks>
    /// The findings are read out of the version's own stream, which is where the write path recorded them: they
    /// live under the same pin as the claims they judged, so this read cannot see a verdict the ledger does not
    /// hold, and a finding recorded in the same append as its claims is citable by that position.
    /// </remarks>
    public async ValueTask<WireFindingList> ListFindingsAsync(
        string versionId,
        long asOf = 0,
        string? severity = null,
        string? ruleId = null,
        string? rulePrefix = null,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var query = new FindingsQuery
        {
            AsOfSequence = asOf > 0 ? new SequenceNumber(asOf) : null,
            Severity = SeverityOf(severity),
            RuleId = Optional(ruleId),
            RulePrefix = Optional(rulePrefix),
            Limit = limit > 0 ? limit : null,
        };

        var recorded = await _findings.ReadAsync(versionId, query, cancellationToken).ConfigureAwait(false);

        return new WireFindingList([.. recorded.Select(StoredOf)]);
    }

    /// <summary>Reads everything the mesh holds at one pin.</summary>
    /// <param name="versionId">The version to read, or empty for every version.</param>
    /// <param name="asOf">The position to read as of, or 0 for the present.</param>
    /// <param name="scope">A scope prefix to read, or empty for every scope.</param>
    /// <param name="factLimit">How many facts to keep, counting from the newest, or 0 for all of them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot at that pin.</returns>
    /// <remarks>
    /// The scope filter and the fact limit belong to the builder, applied after resolution, and the digest ladder
    /// is rebuilt there too: this operation turns the question into the builder's parameters and the answer into
    /// the contract's shape, and decides nothing itself.
    /// </remarks>
    public async ValueTask<WireSnapshot> LoadSnapshotAsync(
        string versionId,
        long asOf = 0,
        string? scope = null,
        int factLimit = 0,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _snapshots
            .BuildAsync(
                versionId,
                asOf > 0 ? new SequenceNumber(asOf) : null,
                Optional(scope),
                factLimit > 0 ? factLimit : null,
                cancellationToken)
            .ConfigureAwait(false);

        return new WireSnapshot(
            snapshot.VersionId,
            snapshot.AsOfSequence?.Value ?? 0,
            snapshot.AsOfDate ?? string.Empty,
            Timestamp(snapshot.WrittenAt),
            snapshot.WrittenOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            [.. snapshot.Facts.Select(ClaimOf)],
            [.. snapshot.Anchors.Values.Select(AnchorOf)],
            [.. snapshot.Digests.Select(DigestOf)],
            [.. snapshot.Promises.Select(PromiseOf)],
            [.. snapshot.Counters.Select(CounterOf)],
            [.. snapshot.Entities.Select(EntityOf)]);
    }

    /// <summary>Locks a detail, so no claim may contradict it.</summary>
    /// <param name="versionId">The version the lock is taken in.</param>
    /// <param name="request">The lock as asked for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lock as recorded, or why it was not taken.</returns>
    public async ValueTask<WireAnchorResult> LockAnchorAsync(
        string versionId,
        WireAnchorLock request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (FirstMissing((request.Subject, "subject"), (request.Key, "key"), (request.Value, "value")) is { } invalid)
        {
            return new WireProblem(InvalidRequestProblem, invalid, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        // A key that is not a ULID is the caller's fault, like any other malformed field - and it is answered before the
        // write, so nothing is recorded for a request that was not understood.
        var scope = IdempotencyKeys.Of(IdempotencyKeys.AnchorLock, versionId);
        var keyed = IdempotencyKeys.TryAccept(request.IdempotencyKey, out var unusable);

        if (unusable is not null)
        {
            return new WireProblem(InvalidRequestProblem, unusable, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var key = (request.IdempotencyKey ?? string.Empty).Trim();

        if (keyed && await AnsweredAsync<WireAnchor>(scope, key, WireJson.Default.WireAnchor, cancellationToken)
            .ConfigureAwait(false) is { } answered)
        {
            return answered;
        }

        // The detail key is derived here rather than passed whole, so the dot that makes a lock matchable against a
        // claim is structural: the kernel refuses a key that names no property, and this cannot produce one.
        var outcome = await _anchors
            .LockAsync(
                versionId,
                string.Concat(request.Subject, ".", request.Key),
                request.Value,
                Optional(request.ScopePath),
                Optional(request.Evidence),
                cancellationToken)
            .ConfigureAwait(false);

        WireAnchorResult result = outcome switch
        {
            Anchor anchor => AnchorOf(anchor),
            WriteContended contended => Contended(contended),
        };

        // Only a lock that landed is remembered: a contention wrote nothing, and a retry of one has to be able to
        // reach the ledger rather than be answered forever with a failure that was transient.
        if (keyed && result is WireAnchor locked)
        {
            await RememberAsync(scope, key, locked, WireJson.Default.WireAnchor, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Releases a lock, if there is one.</summary>
    /// <param name="versionId">The version the release is recorded in.</param>
    /// <param name="detailKey">The locked detail, as <c>subject.key</c>.</param>
    /// <param name="idempotencyKey">The key this command is made under, or empty for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether anything was released, or why nothing was.</returns>
    /// <remarks>
    /// This route carries no body, so the key arrives as a parameter rather than in one: it is the one place the contract
    /// puts a key outside the body, and the reason is that there is nothing else for a body to carry.
    /// </remarks>
    public async ValueTask<WireReleaseResult> ReleaseAnchorAsync(
        string versionId,
        string detailKey,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (DetailKeyRefusal(detailKey) is { } refusal)
        {
            return refusal;
        }

        // The command names a detail as well as a version, so the scope names both: one caller's key for one detail
        // cannot swallow the answer for another.
        var scope = IdempotencyKeys.Of(IdempotencyKeys.AnchorRelease, string.Concat(versionId, "/", detailKey));
        var keyed = IdempotencyKeys.TryAccept(idempotencyKey, out var unusable);

        if (unusable is not null)
        {
            return new WireProblem(InvalidRequestProblem, unusable, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var key = (idempotencyKey ?? string.Empty).Trim();

        // Only a release that released something is remembered. A release of a detail nobody locked wrote nothing, and
        // answering a retry with "nothing was released" would hide a lock taken in between.
        if (keyed && await AnsweredAsync<WireAnchorRelease>(
                scope, key, WireJson.Default.WireAnchorRelease, cancellationToken).ConfigureAwait(false)
            is { } answered)
        {
            return answered;
        }

        var outcome = await _anchors
            .ReleaseAsync(versionId, detailKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        WireReleaseResult result = outcome switch
        {
            Anchor => new WireAnchorRelease(Released: true),
            AnchorNotLocked => new WireAnchorRelease(Released: false),
            WriteContended contended => Contended(contended),
        };

        if (keyed && result is WireAnchorRelease { Released: true } released)
        {
            await RememberAsync(scope, key, released, WireJson.Default.WireAnchorRelease, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Reads the locked details at a pin.</summary>
    /// <param name="versionId">The version whose locked details are read.</param>
    /// <param name="asOf">The position to read as of, or 0 for the present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The locks, later version winning and released ones absent.</returns>
    public async ValueTask<WireAnchorList> ListAnchorsAsync(
        string versionId,
        long asOf = 0,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(versionId, asOf, cancellationToken).ConfigureAwait(false);

        return new WireAnchorList([.. snapshot.Anchors.Values.Select(AnchorOf)]);
    }

    /// <summary>Registers a promise one scope owes to another.</summary>
    /// <param name="versionId">The version the promise is made in.</param>
    /// <param name="request">The promise as asked for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The promise as recorded, or why it was not registered.</returns>
    public async ValueTask<WirePromiseResult> OpenPromiseAsync(
        string versionId,
        WirePromiseRegistration request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (FirstMissing((request.Key, "key"), (request.Kind, "kind"), (request.Description, "description")) is { } invalid)
        {
            return new WireProblem(InvalidRequestProblem, invalid, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var scope = IdempotencyKeys.Of(IdempotencyKeys.Promise, versionId);
        var keyed = IdempotencyKeys.TryAccept(request.IdempotencyKey, out var unusable);

        if (unusable is not null)
        {
            return new WireProblem(InvalidRequestProblem, unusable, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var key = (request.IdempotencyKey ?? string.Empty).Trim();

        // A promise key identifies the obligation, so a retry under one key has to open one promise rather than two:
        // the answer of the first attempt is what the second one receives.
        if (keyed && await AnsweredAsync<WirePromise>(scope, key, WireJson.Default.WirePromise, cancellationToken)
            .ConfigureAwait(false) is { } answered)
        {
            return answered;
        }

        var outcome = await _promises
            .RegisterAsync(
                versionId,
                request.Key,
                request.Kind,
                request.Description,
                Optional(request.OriginScope),
                Optional(request.DueScope),
                cancellationToken)
            .ConfigureAwait(false);

        WirePromiseResult result = outcome switch
        {
            Promise promise => PromiseOf(promise),
            WriteContended contended => Contended(contended),
        };

        if (keyed && result is WirePromise opened)
        {
            await RememberAsync(scope, key, opened, WireJson.Default.WirePromise, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Fulfils the first open promise with a key.</summary>
    /// <param name="versionId">The version the promise is fulfilled in.</param>
    /// <param name="key">The coordination key.</param>
    /// <param name="idempotencyKey">The key this command is made under, or empty for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether anything was fulfilled, or why nothing was.</returns>
    /// <remarks>
    /// This route carries no body, so the key arrives as a parameter rather than in one: it is the one place the contract
    /// puts a key outside the body, and the reason is that there is nothing else for a body to carry.
    /// </remarks>
    public async ValueTask<WireFulfilResult> FulfilPromiseAsync(
        string versionId,
        string key,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new WireProblem(InvalidRequestProblem, "key is required.", Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        // The command names a promise as well as a version, so the scope names both. The key this command is made under
        // is kept apart from the promise key it names: they are two different keys and only one of them is the caller's.
        var scope = IdempotencyKeys.Of(IdempotencyKeys.PromiseFulfilment, string.Concat(versionId, "/", key));
        var keyed = IdempotencyKeys.TryAccept(idempotencyKey, out var unusable);

        if (unusable is not null)
        {
            return new WireProblem(InvalidRequestProblem, unusable, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var commandKey = (idempotencyKey ?? string.Empty).Trim();

        // Only a fulfilment that fulfilled something is remembered. A key with nothing open wrote nothing, and answering a
        // retry with "not fulfilled" would hide a promise opened in between.
        if (keyed && await AnsweredAsync<WirePromiseFulfilment>(
                scope, commandKey, WireJson.Default.WirePromiseFulfilment, cancellationToken).ConfigureAwait(false)
            is { } answered)
        {
            return answered;
        }

        var outcome = await _promises
            .FulfilAsync(versionId, key, cancellationToken)
            .ConfigureAwait(false);

        WireFulfilResult result = outcome switch
        {
            Promise => new WirePromiseFulfilment(Fulfilled: true),
            PromiseNotOpen => new WirePromiseFulfilment(Fulfilled: false),
            WriteContended contended => Contended(contended),
        };

        if (keyed && result is WirePromiseFulfilment { Fulfilled: true } fulfilled)
        {
            await RememberAsync(scope, commandKey, fulfilled, WireJson.Default.WirePromiseFulfilment, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Reads the promises at a pin, and the overdue findings when they are asked for.</summary>
    /// <param name="versionId">The version whose promises are read.</param>
    /// <param name="asOf">The position to read as of, or 0 for the present.</param>
    /// <param name="status">The status to select, or empty for every status.</param>
    /// <param name="overdueScope">The scope to compute the promise check for, or empty to skip it.</param>
    /// <param name="isFinalUnit">Whether this read is the final unit, where every open promise is overdue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The promises, and the overdue findings when they were asked for.</returns>
    /// <remarks>
    /// The overdue view is computed over the full pinned slice, before the status filter narrows it: a finding a
    /// filter could hide would be a finding nobody sees, which is the opposite of what a check is for.
    /// </remarks>
    public async ValueTask<WirePromiseList> ListPromisesAsync(
        string versionId,
        long asOf = 0,
        string? status = null,
        string? overdueScope = null,
        bool isFinalUnit = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(versionId, asOf, cancellationToken).ConfigureAwait(false);
        var scope = Optional(overdueScope);

        var overdue = scope is not null || isFinalUnit
            ? PromiseRegistry.FindOverdue(snapshot.Promises, scope, isFinalUnit)
            : [];

        var selected = PromiseStatusOf(status) is { } wanted
            ? snapshot.Promises.Where(promise => promise.Status == wanted)
            : snapshot.Promises;

        return new WirePromiseList([.. selected.Select(PromiseOf)], [.. overdue.Select(FindingOf)]);
    }

    /// <summary>Records a whole-document total for a counter.</summary>
    /// <param name="versionId">The version the count belongs to.</param>
    /// <param name="request">The count as reported.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counter as recorded, or why it was not.</returns>
    /// <remarks>
    /// The contract counts in int64 and the plane in ulong, so a negative total is refused rather than wrapped: a
    /// count of minus three is not a smaller count, it is a request that cannot be understood.
    /// </remarks>
    public async ValueTask<WireCounterResult> RecordCounterAsync(
        string versionId,
        WireCounterRecording request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return new WireProblem(InvalidRequestProblem, "key is required.", Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        if (request.Total < 0 || request.Budget < 0)
        {
            return new WireProblem(
                InvalidRequestProblem,
                "total and budget are counts, so neither can be negative.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var scope = IdempotencyKeys.Of(IdempotencyKeys.Counter, versionId);
        var keyed = IdempotencyKeys.TryAccept(request.IdempotencyKey, out var unusable);

        if (unusable is not null)
        {
            return new WireProblem(InvalidRequestProblem, unusable, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var key = (request.IdempotencyKey ?? string.Empty).Trim();

        // A total is absolute rather than a delta, so a retry has to be answered with the total that landed: recording a
        // second one under the same key would be recording the same measurement twice.
        if (keyed && await AnsweredAsync<WireCounter>(scope, key, WireJson.Default.WireCounter, cancellationToken)
            .ConfigureAwait(false) is { } answered)
        {
            return answered;
        }

        var outcome = await _counters
            .RecordAsync(
                versionId,
                request.Key,
                (ulong)request.Total,
                // Zero means "no ceiling", because a ceiling of zero would be a counter that may not be used at all -
                // which is a different statement, and one the contract has no way to make.
                request.Budget > 0 ? (ulong)request.Budget : null,
                cancellationToken)
            .ConfigureAwait(false);

        WireCounterResult result = outcome switch
        {
            CounterTotal counter => CounterOf(counter),
            WriteContended contended => Contended(contended),
        };

        if (keyed && result is WireCounter recorded)
        {
            await RememberAsync(scope, key, recorded, WireJson.Default.WireCounter, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Reads the counters at a pin, with the directives a writer would be given.</summary>
    /// <param name="versionId">The version whose counters are read.</param>
    /// <param name="asOf">The position to read as of, or 0 for the present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counters, and the directives that follow from them.</returns>
    /// <remarks>
    /// The directives are computed here rather than stored: they are a function of the totals, and a stored copy
    /// would be one more thing that can disagree with the plane it describes.
    /// </remarks>
    public async ValueTask<WireCounterList> ListCountersAsync(
        string versionId,
        long asOf = 0,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(versionId, asOf, cancellationToken).ConfigureAwait(false);

        return new WireCounterList(
            [.. snapshot.Counters.Select(CounterOf)],
            CounterBudget.Directives(snapshot.Counters));
    }

    /// <summary>Creates a version, which is itself a governed claim.</summary>
    /// <param name="request">The version to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or a problem when it could not be created.</returns>
    /// <remarks>
    /// A version is written as a claim under the <c>version</c> shape rather than into a table of its own, so
    /// it is judged by the same gates, pinned by the same feed and rebuilt from the same slice as everything
    /// else. It also makes a version id immutable by construction: claiming an existing id conflicts with the
    /// fact that already holds that lineage.
    /// </remarks>
    public async ValueTask<WireVersionResult> CreateVersionAsync(
        WireVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The version this command creates may be named by the caller or minted here, so the scope cannot name it in
        // advance: a keyed creation is therefore scoped by its own key. That is what makes a retry of a version with a
        // generated id land on the version that was created rather than on a second one - and a retry whose id the
        // caller did supply is answered from the key before the ledger is asked to hold the same id twice.
        var keyed = IdempotencyKeys.TryAccept(request.IdempotencyKey, out var unusable);

        if (unusable is not null)
        {
            return new WireProblem(InvalidRequestProblem, unusable, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        var key = (request.IdempotencyKey ?? string.Empty).Trim();
        var scope = IdempotencyKeys.Of(IdempotencyKeys.Version, key);

        if (keyed && await AnsweredAsync<WireVersion>(scope, key, WireJson.Default.WireVersion, cancellationToken)
            .ConfigureAwait(false) is { } answered)
        {
            return answered;
        }

        var versionId = request.VersionId.Trim();
        if (versionId.Length == 0)
        {
            // A generated version identity is a ULID, so the version carries the instant it was created at
            // without a field for it: see LedgerIds.
            versionId = LedgerIds.New();
        }

        var parent = request.ParentVersionId.Trim();
        var asOf = request.AsOfDate.Trim();
        var label = request.Label.Trim();

        if (asOf.Length > 0 &&
            !DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return new WireProblem(
                InvalidRequestProblem, "as_of_date must be a date as YYYY-MM-DD.", Status: 400, 0, 0);
        }

        if (parent.Length > 0)
        {
            var known = VersionRegistry.At(await PresentAsync(cancellationToken).ConfigureAwait(false));

            if (!known.TryResolve(parent, out _))
            {
                return new WireProblem(
                    InvalidRequestProblem,
                    $"no version '{parent}' exists, so '{versionId}' cannot descend from it.",
                    Status: 400,
                    0,
                    0);
            }
        }

        var statement = parent.Length > 0
            ? $"version '{versionId}' descends from '{parent}'"
            : $"version '{versionId}' starts a lineage";

        var outcome = await ProposeClaimAsync(
            versionId,
            new WireClaimProposal(
                versionId,
                WireClaimTypes.Fact,
                VersionRegistry.ShapeName,
                VersionBody(versionId, parent, asOf, label),
                statement,
                request.Actor ?? string.Empty),
            cancellationToken).ConfigureAwait(false);

        WireVersionResult result = outcome switch
        {
            WireClaimOutcome { Status: WireClaimStatus.Accepted } accepted =>
                new WireVersion(versionId, parent, asOf, label, accepted.Head),

            // A version whose own claim was refused never came into being; the refusal is in the ledger, and
            // the caller is told why rather than handed a version that governance did not accept.
            WireClaimOutcome refused => new WireProblem(
                InvalidRequestProblem,
                $"version '{versionId}' was refused by {refused.Gate}: {refused.Reason}",
                Status: 409,
                0,
                0),

            WireProblem problem => problem,
        };

        // Only a version that came into being is remembered: a refused claim wrote a refusal, not a version, and a
        // retry of one has to be able to reach the ledger again rather than be answered with the refusal forever.
        if (keyed && result is WireVersion created)
        {
            await RememberAsync(scope, key, created, WireJson.Default.WireVersion, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>The facts that were current at a pin.</summary>
    /// <param name="asOf">The global position to read as of. 0 means the present.</param>
    /// <param name="versionId">The version to read, or an empty string for every version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The slice, and a digest over exactly the facts it contains.</returns>
    public async ValueTask<WireFactSlice> SliceFactsAsync(
        long asOf,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        // The contract says 0 means the head, so that is resolved here rather than left to the caller to
        // discover the watermark.
        var pin = asOf > 0
            ? new SequenceNumber(asOf)
            : await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false);

        var slice = await _facts
            .SliceAsync(pin, versionId ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return new WireFactSlice(slice.Pin.Value, slice.Digest, [.. slice.Facts.Select(ToWire)]);
    }

    /// <summary>The path from a lineage root down to a version, inclusive.</summary>
    /// <param name="versionId">The version to read the lineage of.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The lineage, or an empty one when the version is not known at the present pin - which the transport
    /// answers as a 404, because a version that was never created is not a version with no ancestors.
    /// </returns>
    public async ValueTask<WireVersionLineage> GetLineageAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var registry = VersionRegistry.At(await PresentAsync(cancellationToken).ConfigureAwait(false));
        var versions = new List<WireVersion>();

        foreach (var version in registry.LineageOf(versionId))
        {
            versions.Add(await ToWireAsync(version, cancellationToken).ConfigureAwait(false));
        }

        return new WireVersionLineage(versions);
    }

    /// <summary>Composes the context a model would be given.</summary>
    /// <param name="request">What to compose.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The composed context, or a problem when the request could not be resolved.</returns>
    public async ValueTask<WireContextResult> ComposeContextAsync(
        WireContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (pin, problem) = await ResolvePinAsync(request, cancellationToken).ConfigureAwait(false);

        if (problem is not null)
        {
            return problem;
        }

        var composed = await _composer.ComposeAsync(
            new ContextRequest
            {
                Pin = pin,
                VersionId = request.VersionId ?? string.Empty,
                Shape = request.Shape ?? string.Empty,
                BudgetTokens = request.BudgetTokens,
                FactLimit = request.FactLimit,
            },
            cancellationToken).ConfigureAwait(false);

        return new WireComposedContext(
            [.. composed.Sections.Select(static section => new WireContextSection(section.Title, section.Body))],
            composed.Text,
            composed.EstimatedTokens,
            composed.ContentHash,
            composed.Pin.Value);
    }

    /// <summary>The problem identifier a runbook this port cannot read answers with.</summary>
    public const string RunbookInvalidProblem = "https://munarium.dev/problems/runbook-invalid";

    /// <summary>The problem identifier a version that was removed answers with.</summary>
    public const string RunbookRemovedProblem = "https://munarium.dev/problems/runbook-removed";


    /// <summary>The finding code a document that does not parse answers as.</summary>
    /// <remarks>
    /// A finding rather than a refusal, which is the original rule and the better one: an author editing a runbook wants
    /// to read what is wrong with it, and a document that does not parse has exactly one thing wrong with it.
    /// </remarks>
    public const string RunbookParseFindingCode = "parse";

    /// <summary>Validates a runbook document, without applying it.</summary>
    /// <remarks>
    /// The checks are deterministic and always run. Asking a model is separate and never changes the findings, because
    /// an advisory pass that could refuse a document would be a second validator whose rules nobody can read.
    /// </remarks>
    /// <param name="request">The document, and whether to ask a model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The findings, and what a model suggested when one was asked.</returns>
    public async ValueTask<WireRunbookValidation> ValidateRunbookAsync(
        WireRunbookValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (RunbookReader.Read(request.Yaml) is not ({ } document, null))
        {
            return new WireRunbookValidation(
                false,
                [new WireValidationFinding("error", RunbookParseFindingCode, "the document does not read as a runbook", "$")],
                [],
                null);
        }

        var reported = RunbookValidation.Validate(document);
        var (suggestions, note) = request.Suggest
            ? await AdviseRunbookAsync(request.Yaml, cancellationToken).ConfigureAwait(false)
            : ((IReadOnlyList<WireSuggestion>)[], (string?)null);

        return new WireRunbookValidation(
            RunbookValidation.IsValid(reported),
            [.. reported.Select(Finding)],
            suggestions,
            note);
    }

    /// <summary>Asks the deployment model what it would change about a runbook.</summary>
    /// <param name="yaml">The document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The suggestions, and the note when there is no help to give.</returns>
    private async ValueTask<(IReadOnlyList<WireSuggestion> Suggestions, string? Note)> AdviseRunbookAsync(
        string yaml,
        CancellationToken cancellationToken)
    {
        if (_model is null)
        {
            return ([], SuggestUnavailableNote);
        }

        var asked = new CompletionRequest
        {
            Model = _modelId,
            System = AssistSystem,
            Prompt = $"This runbook is up for review:\n\n{yaml}\n",
        };

        try
        {
            var completion = await _model.CompleteAsync(asked, cancellationToken).ConfigureAwait(false);

            return (Suggestions(completion.Text), null);
        }
        catch (HttpRequestException exception)
        {
            return ([], $"suggestions unavailable: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return ([], $"suggestions unavailable: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return ([], $"suggestions unavailable: {exception.Message}");
        }
    }

    /// <summary>The note an advisory pass answers with when this deployment cannot call a model.</summary>
    public const string SuggestUnavailableNote = "suggestions unavailable: this deployment has no model bound";

    /// <summary>Applies a runbook version.</summary>
    /// <remarks>
    /// The catalog gates the write and the store keeps it, which is the same pair the sessions plane resolves through:
    /// an unreadable document is refused with the reader's own complaint, a removed version is refused with what to do
    /// instead, and everything else becomes a row a session can pin by name.
    /// </remarks>
    /// <param name="request">The document as written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version as applied, or why it was not.</returns>
    public async ValueTask<WireApplyRunbookResult> ApplyRunbookAsync(
        WireRunbookApply request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await RunbookCatalog
            .ApplyAsync(_runbooks, _tenant, request.Yaml, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            RunbookApplied applied => Applied(applied.Record),
            RunbookRefusal refusal => Problem(refusal),
        };
    }

    /// <summary>Lists the runbook versions this deployment has applied.</summary>
    /// <param name="includeRemoved">Whether removed versions are listed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The versions, in name and then version order.</returns>
    public async ValueTask<WireRunbookList> ListRunbooksAsync(
        bool includeRemoved = false,
        CancellationToken cancellationToken = default)
    {
        var records = await _runbooks
            .ListAsync(_tenant, includeRemoved, cancellationToken)
            .ConfigureAwait(false);

        return new WireRunbookList([.. records.Select(ToWireRunbook)]);
    }

    private static WireAppliedRunbook Applied(RunbookRecord record) => new(
        record.Ref,
        record.Name,
        record.Version ?? 0,
        record.Status.ToWireName(),
        record.CreatedAt,
        record.UpdatedAt);

    private static WireRunbook ToWireRunbook(RunbookRecord record) => new(
        record.Ref,
        record.Name,
        record.Version ?? 0,
        record.Status.ToWireName(),
        record.RemovalId,
        record.RemovalRequestedAt,
        record.RemovalRequestedBy,
        record.RemovedAt,
        record.CreatedAt,
        record.UpdatedAt);

    /// <summary>Opens a session over an applied runbook.</summary>
    /// <param name="name">The runbook's name, or its reference.</param>
    /// <param name="principal">The clearance to snapshot for the conversation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session, or why it was not opened.</returns>
    public async ValueTask<WireCreateSessionResult> CreateSessionAsync(
        string name,
        EvidencePrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(principal);

        var resolved = await RunbookCatalog
            .ResolveAsync(_runbooks, _tenant, name, cancellationToken)
            .ConfigureAwait(false);

        if (resolved is null)
        {
            // Nobody applied that, and it was removed, are different answers for an operator - so the second is asked for
            // explicitly rather than reported as absence.
            var hidden = await _runbooks
                .ResolveAsync(_tenant, name, includeRemoved: true, cancellationToken)
                .ConfigureAwait(false);

            return hidden is null
                ? new WireProblem(
                    UnknownRunbookProblem,
                    $"no runbook '{name}' has been applied",
                    Status: 400,
                    ExpectedHead: 0,
                    ActualHead: 0)
                : new WireProblem(
                    SessionRunbookRemovedProblem,
                    $"runbook '{hidden.Ref}' was removed; publish a new version instead",
                    Status: 410,
                    ExpectedHead: 0,
                    ActualHead: 0);
        }

        var opening = SessionCreation.Open(
            resolved.Document,
            new AccessContext(principal.Level, principal.Compartments, principal.AllCompartments),
            principal.Uid,
            _tenant);

        return opening switch
        {
            SessionOpened opened => new WireSessionCreated(
                (await _sessions.CreateAsync(opened.Session, cancellationToken).ConfigureAwait(false)).Id,
                opened.Session.RunbookRef,
                opened.PermittedCollections),
            SessionRefusal refusal => new WireProblem(
                SessionForbiddenProblem, refusal.Message, Status: 403, ExpectedHead: 0, ActualHead: 0),
        };
    }

    /// <summary>Reads a session and the turns it recorded.</summary>
    /// <param name="sessionId">The session's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transcript, or why it could not be read.</returns>
    public async ValueTask<WireSessionResult> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = await _sessions.GetAsync(_tenant, sessionId, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return NotFoundSession(sessionId);
        }

        var turns = await _sessions
            .TurnsAsync(_tenant, sessionId, SessionTurnLimit, cancellationToken)
            .ConfigureAwait(false);

        return new WireSession(
            session.Id,
            session.Uid,
            session.RunbookRef,
            session.Access.Level,
            session.Access.Compartments,
            session.State.ToWireName(),
            session.CreatedAt,
            session.LastTurnAt,
            [.. turns.Select(ToWireSessionTurn)]);
    }

    /// <summary>Closes a session.</summary>
    /// <param name="sessionId">The session's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session as closed, or why it was not.</returns>
    public async ValueTask<WireCloseSessionResult> CloseSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (await _sessions.GetAsync(_tenant, sessionId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFoundSession(sessionId);
        }

        var closed = await _sessions.CloseAsync(_tenant, sessionId, cancellationToken).ConfigureAwait(false);

        // A replay is visible rather than silent: the honest answer to a second close is that it did not happen.
        return closed
            ? new WireSessionClosed(sessionId, SessionState.Closed.ToWireName())
            : new WireProblem(
                SessionClosedProblem,
                $"session '{sessionId}' is not open",
                Status: 409,
                ExpectedHead: 0,
                ActualHead: 0);
    }

    private static WireProblem NotFoundSession(string sessionId) => new(
        SessionNotFoundProblem,
        $"no session '{sessionId}'",
        Status: 404,
        ExpectedHead: 0,
        ActualHead: 0);

    private static WireSessionTurn ToWireSessionTurn(TurnRecord turn) => new(
        turn.Ordinal,
        turn.Query,
        turn.CollectionsSearched,
        Payload(turn.HitsJson),
        Payload(turn.EnvelopeJson),
        turn.CompletionJson is null ? null : Payload(turn.CompletionJson),
        turn.HierarchyJson is null ? null : Payload(turn.HierarchyJson),
        turn.CreatedAt);

    /// <summary>
    /// Reads a recorded payload as JSON.
    /// </summary>
    /// <remarks>
    /// The bytes are this server's own, so a payload that no longer parses is shown as null rather than taking the whole
    /// transcript down with it: a conversation is worth more than the one field that cannot be rendered.
    /// </remarks>
    /// <param name="json">The recorded payload.</param>
    /// <returns>The element.</returns>
    private static string Payload(string json)
    {
        try
        {
            return JsonElement.Parse(json).GetRawText();
        }
        catch (JsonException)
        {
            return "null";
        }
    }

    /// <summary>Runs one turn of a session.</summary>
    /// <param name="sessionId">The session's identity.</param>
    /// <param name="request">The question, and what the turn asks for.</param>
    /// <param name="onProgress">
    /// Called as the turn crosses each stage, or <see langword="null"/> to hear nothing.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the turn produced, or why nothing was.</returns>
    public async ValueTask<WireRunTurnResult> RunTurnAsync(
        string sessionId,
        WireTurnRequest request,
        Action<WireTurnEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        var session = await _sessions.GetAsync(_tenant, sessionId, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return NotFoundSession(sessionId);
        }

        // The document is resolved from the session's own pin rather than by the name it was opened with, which is what
        // makes the pin a pin: a conversation keeps reading the version it was opened over.
        var resolved = await RunbookCatalog
            .ResolveAsync(_runbooks, _tenant, session.RunbookRef, cancellationToken)
            .ConfigureAwait(false);

        if (resolved is null)
        {
            return new WireProblem(
                SessionRunbookRemovedProblem,
                $"runbook '{session.RunbookRef}' can no longer be read",
                Status: 410,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var complete = request.Complete ?? true;

        // A modelled step with no model configured is refused before anything is read: the request is well formed, the
        // deployment is not equipped, and asking a provider that cannot answer would report a bug rather than a fact.
        if (_model is null
            && ((complete && resolved.Document.Spec.Completion is not null)
                || IntentResolution.IsPinned(resolved.Document)))
        {
            return new WireProblem(
                NoCompletionModelProblem,
                "this deployment has no model configured for the runbook's modelled steps",
                Status: 503,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var run = await _turns
            .RunAsync(
                session,
                resolved.Document,
                request.Query,
                request.ResearchProfile,
                new TurnModels(_modelId, _modelId, _modelId),
                complete,
                request.TopK ?? 0,
                onProgress is null ? null : progress => onProgress(Progress(progress)),
                cancellationToken)
            .ConfigureAwait(false);

        return run switch
        {
            SessionTurnExecuted executed => ToWireTurn(executed),
            SessionRefusal refusal => new WireProblem(
                SessionClosedProblem, refusal.Message, Status: 409, ExpectedHead: 0, ActualHead: 0),
            ResearchProblem problem => new WireProblem(
                UnknownProfileProblem, problem.Detail, Status: 400, ExpectedHead: 0, ActualHead: 0),
            RequiredLayerUnavailable refused => new WireProblem(
                RequiredLayerProblem,
                $"the required layer '{refused.Layer}' could not answer ({refused.RefusalCode})",
                Status: 503,
                ExpectedHead: 0,
                ActualHead: 0),
        };
    }

    /// <summary>
    /// Turns one of the kernel's progress events into the event the wire carries.
    /// </summary>
    /// <remarks>
    /// A translation and nothing else, like the model-to-message mappings: the kernel's vocabulary and the contract's are
    /// the same events, and the only difference is that the wire's is flat - one shape per stage rather than a union of
    /// unions - because that is what the original's SSE frames are.
    /// </remarks>
    /// <param name="progress">What the kernel reported.</param>
    /// <returns>The wire event.</returns>
    private static WireTurnEvent Progress(TurnProgress progress) => progress switch
    {
        HierarchyProgress hierarchy => HierarchyProgressOf(hierarchy),
        TurnModelResolved model => new WireTurnModelEvent(model.Provider, model.Model, model.Tier, model.WasOverride),
        TurnExpanded expanded => new WireTurnExpansionEvent(
            expanded.Provider, expanded.Model, expanded.Terms, expanded.InputTokens, expanded.OutputTokens),
        TurnProbed probed => new WireTurnProbeEvent(probed.Collection, probed.Hits, probed.Skipped),
        TurnSelected selected => new WireTurnSelectionEvent(selected.Probed, selected.Selected, selected.Collections),
        TurnRetrieved retrieved => new WireTurnRetrievalEvent(retrieved.Collection, retrieved.Hits, retrieved.Skipped),
        TurnMerged merged => new WireTurnMergeEvent(merged.Hits),
        TurnComposed composed => new WireTurnComposeEvent(
            composed.LayersUsed, composed.ContextCharacters, composed.LayersDropped),
        TurnCompleted completed => new WireTurnCompletionEvent(
            completed.Attempt, completed.Provider, completed.Model, completed.InputTokens, completed.OutputTokens),
        TurnVerified verified => new WireTurnVerifyEvent(verified.Attempt, verified.Checks, verified.Violations),
    };

    /// <summary>Turns one of the hierarchy's own progress events into the event the wire carries.</summary>
    /// <param name="progress">What the hierarchy reported.</param>
    /// <returns>The wire event.</returns>
    private static WireTurnEvent HierarchyProgressOf(HierarchyProgress progress) => progress switch
    {
        ProfileResolved resolved => new WireTurnProfileEvent(
            resolved.Profile, resolved.Layers, resolved.IntentKind, resolved.IntentExplicit),
        LayerStarted started => new WireTurnLayerStartEvent(
            started.Layer, started.Role.ToWireName(), started.Requirement.ToWireName()),
        SourceBound bound => new WireTurnLayerSourceEvent(bound.Layer, bound.Source, bound.Provider),
        LayerCompleted completed => new WireTurnLayerCompleteEvent(
            completed.Layer,
            completed.Block,
            completed.SupportsCompleteness,
            completed.RefusalCode,
            completed.ElapsedMilliseconds),
        CoverageReported coverage => new WireTurnCoverageEvent(
            coverage.CompletenessAvailable, coverage.DisclosedConflicts),
    };

    private static WireTurnResponse ToWireTurn(SessionTurnExecuted executed) => new(
        executed.Ordinal,
        executed.Question,
        executed.Intent.Kind,
        executed.Intent.Explicit,
        executed.CollectionsSearched,
        [
            .. executed.Hits.Select(chunk => new WireTurnHit(
                chunk.Source.ChunkId,
                chunk.Source.SourcePath,
                chunk.Score,
                chunk.Text)),
        ],
        [
            .. executed.Envelopes.Select(envelope => new WireTurnEnvelope(
                envelope.IndexVersion,
                envelope.LedgerWatermark.Value,
                [
                    .. envelope.Sources.Select(source => new WireTurnSource(
                        source.ChunkId,
                        source.SourcePath,
                        source.ContentHash)),
                ])),
        ],
        executed.Completion is null ? null : ToWireCompletion(executed.Completion),
        executed.Decision is null ? null : ToWireDecision(executed.Decision));

    private static WireTurnCompletion ToWireCompletion(TurnOutcome outcome) => new(
        outcome.Answer,
        outcome.InputTokens,
        outcome.OutputTokens,
        outcome.Completions,
        outcome.RetriedForTruncation,
        new WireTurnVerification(
            outcome.Checks,
            outcome.Retries,
            outcome.FirstPassViolations,
            outcome.Violations));

    private static WireHierarchyDecision ToWireDecision(EvidenceHierarchyDecision decision) => new(
        decision.Profile,
        decision.IntentKind,
        decision.IntentExplicit,
        [
            .. decision.Layers.Select(layer => new WireLayerOutcome(
                layer.Layer,

                // The enums' own names are the wire names: required, optional, fallback, and the three roles.
                layer.Role.ToString().ToLowerInvariant(),
                layer.Requirement.ToString().ToLowerInvariant(),
                layer.Block,
                layer.EvidenceId,
                layer.SupportsCompleteness,
                layer.RefusalCode,
                layer.ElapsedMilliseconds)),
        ],
        decision.CompletenessAvailable,
        decision.DisclosedConflicts,
        decision.ConflictsPolicy);

    /// <summary>Answers a runbook refusal with the problem its code names, which is also its status.</summary>
    /// <param name="refusal">The refusal.</param>
    /// <returns>The problem.</returns>
    private static WireProblem Problem(RunbookRefusal refusal) => refusal.Code switch
    {
        RunbookRefusalCodes.Removed => new WireProblem(
            RunbookRemovedProblem, refusal.Message, Status: 409, ExpectedHead: 0, ActualHead: 0),
        _ => new WireProblem(RunbookInvalidProblem, refusal.Message, Status: 400, ExpectedHead: 0, ActualHead: 0),
    };

    /// <summary>Retrieves evidence for a question.</summary>
    /// <param name="query">The question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chunks, and the envelope that seals them.</returns>
    public async ValueTask<WireSearchResult> SearchAsync(
        WireSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var embedded = await _embedder.EmbedAsync(
            new EmbeddingRequest { Model = _embeddingModel, Inputs = [query.Text] },
            cancellationToken).ConfigureAwait(false);

        // The version that answers is read now rather than held: a cutover is a serving decision, and a caller that
        // captured a reader would keep asking the version that stopped serving.
        var result = await _index.ServingReader.SearchAsync(
            new RetrievalQuery
            {
                Text = query.Text,
                Embedding = embedded.Vectors[0],
                TopK = query.TopK > 0 ? query.TopK : DefaultTopK,
            },
            cancellationToken).ConfigureAwait(false);

        return new WireSearchResult(
            [.. result.Chunks.Select(chunk => new WireRetrievedChunk(ToWire(chunk.Source), chunk.Score, chunk.Text))],
            new WireProvenanceEnvelope(
                result.Envelope.IndexVersion,
                result.Envelope.LedgerWatermark.Value,
                [.. result.Envelope.Sources.Select(ToWire)]));
    }

    /// <summary>Issues a capability: authority exchanged for a short-lived, least-privilege credential.</summary>
    /// <remarks>
    /// The secret and the instant are passed in rather than read here: a deployment owns its secret, and a clock that a
    /// test cannot place would make an expiry assertion a matter of luck. Both surfaces call this, so the two cannot
    /// disagree about what an unissuable capability is.
    /// </remarks>
    /// <param name="request">What is asked for.</param>
    /// <param name="secret">The deployment secret to sign with.</param>
    /// <param name="now">The instant to issue at.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capability, or why it could not be issued.</returns>
    public async ValueTask<WireAccessTokenResult> IssueAccessTokenAsync(
        WireAccessTokenRequest request,
        ReadOnlyMemory<byte> secret,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var capability = AccessTokens.Issue(
                new AccessClaims
                {
                    Subject = request.Subject ?? string.Empty,
                    Tenant = _tenant,
                    Level = request.Level,
                    Compartments = request.Compartments ?? [],
                    Scopes = request.Scopes ?? [],
                    Runbooks = request.Runbooks,

                    // A capability names itself so that it can be withdrawn: a bearer credential cannot be recalled,
                    // so the only way to refuse one is to know which one it is.
                    TokenId = LedgerIds.New(),
                    IssuedAt = 0,
                    ExpiresAt = 0,
                },
                now,
                request.LifetimeSeconds > 0 ? TimeSpan.FromSeconds(request.LifetimeSeconds) : null);

            // Recorded before it is handed out, and deliberately not best-effort: a capability the audit does not know
            // about cannot be withdrawn, so an issuance that cannot be recorded is an issuance that failed.
            await _accessAudit
                .RecordAsync(IssuedCapability.FromClaims(capability), cancellationToken)
                .ConfigureAwait(false);

            return new WireAccessToken(
                AccessTokens.Mint(secret.Span, capability),
                capability.TokenId,
                capability.ExpiresAt);
        }
        catch (ArgumentException refused)
        {
            return new WireProblem(
                InvalidRequestProblem,
                refused.Message,
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }
    }

    /// <summary>Lists the capabilities this deployment issued, newest first.</summary>
    /// <remarks>
    /// Never a token: what is held is an identity and the claims it carried. A deployment reading this is asking who was
    /// given what, which is a question about its own records and not about governance's.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The audit.</returns>
    public async ValueTask<WireAccessTokenAuditList> ListAccessTokensAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _accessAudit.ListAsync(_tenant, cancellationToken).ConfigureAwait(false);

        return new WireAccessTokenAuditList([.. rows.Select(Audited)]);
    }

    /// <summary>Withdraws a capability, so that verification refuses it from here on.</summary>
    /// <remarks>
    /// Idempotent by the store's own rule: withdrawing one that is already withdrawn answers with the instant it first
    /// happened, because an operator acting on a stale list has done nothing new.
    /// </remarks>
    /// <param name="tokenId">The capability's identity.</param>
    /// <param name="now">The instant to withdraw at.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row as it now stands, or a problem when no such capability was issued.</returns>
    public async ValueTask<WireAccessTokenRevocationResult> RevokeAccessTokenAsync(
        string tokenId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var withdrawn = await _accessAudit
            .RevokeAsync(_tenant, tokenId ?? string.Empty, now.ToUnixTimeSeconds(), cancellationToken)
            .ConfigureAwait(false);

        return withdrawn is null
            ? new WireProblem(
                UnknownAccessTokenProblem,
                "No capability with that identity was issued by this deployment.",
                Status: 404,
                ExpectedHead: 0,
                ActualHead: 0)
            : Audited(withdrawn);
    }

    /// <summary>Reads a stored row as the shape the contract carries.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The wire shape.</returns>
    private static WireAccessTokenAudit Audited(IssuedCapability row) => new(
        row.TokenId,
        row.Subject,
        row.Tenant,
        row.Level,
        row.Compartments,
        row.Scopes,
        row.Runbooks,
        row.IssuedAt,
        row.ExpiresAt,
        row.RevokedAt);
    /// <summary>
    /// Ingests a document: the bytes are stored, the row is recorded, and the text is indexed.
    /// </summary>
    /// <remarks>
    /// The deployment's tenant is used rather than a caller-supplied one, because the contract carries no tenant yet:
    /// tenancy arrives with authorization, and the kernel's seams are already tenant-keyed, so nothing has to be
    /// reshaped when it does - only threaded. Until then a deployment is one tenant, and saying so is better than
    /// inventing a tenant per request.
    /// </remarks>
    /// <param name="request">The document as offered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The document as stored and indexed, or why nothing was.</returns>
    public async ValueTask<WireIngestResult> IngestSourceAsync(
        WireSourceIngest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = (request.Path ?? string.Empty).Trim();

        if (path.Length == 0)
        {
            return new WireProblem(
                InvalidRequestProblem,
                "path is required: it is the source's identity and the address its bytes are stored at.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        // One document, two ways to carry it, told apart by which form carries something - which is the original's own
        // idiom for the evidence bytes. A body carrying both, or neither, is malformed rather than worth guessing at:
        // guessing which one a caller meant would store bytes they did not send. Presence is non-emptiness rather than
        // non-nullness because a protobuf string field that was never set reads as empty, so the two transports would
        // otherwise disagree about the same request.
        var asText = !string.IsNullOrEmpty(request.Content);
        var asBytes = !string.IsNullOrEmpty(request.ContentBase64);

        if (asText == asBytes)
        {
            return new WireProblem(
                InvalidRequestProblem,
                asText
                    ? "a document travels as 'content' or as 'content_base64', never both: they would be two documents."
                    : "a document travels as 'content' (its text) or 'content_base64' (its bytes), and neither carries anything.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        byte[] bytes;

        if (asBytes)
        {
            try
            {
                bytes = Convert.FromBase64String(request.ContentBase64!);
            }
            catch (FormatException notBase64)
            {
                return new WireProblem(
                    InvalidRequestProblem,
                    $"content_base64 is not valid base64: {notBase64.Message}",
                    Status: 400,
                    ExpectedHead: 0,
                    ActualHead: 0);
            }
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(request.Content!);
        }

        DocumentOutcome outcome;

        try
        {
            outcome = await _ingest
                .IngestAsync(
                    _tenant,
                    path,
                    request.MediaType ?? string.Empty,
                    bytes,
                    request.ContentSha256 is { Length: > 0 } declared ? declared : null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException invalid)
        {
            // A path a source may not hold, or a media type that names nothing, is a malformed request rather than a
            // document that was refused: nothing was attempted.
            return new WireProblem(InvalidRequestProblem, invalid.Message, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        return outcome switch
        {
            IngestedDocument ingested => ToWire(ingested),
            IngestRefused refused => new WireProblem(
                UnsupportedMediaTypeProblem,
                refused.Reason,
                Status: 415,
                ExpectedHead: 0,
                ActualHead: 0),
            IngestRejected rejected => new WireProblem(
                ContentHashMismatchProblem,
                $"the declared hash {rejected.Declared} is not the hash of the document that arrived "
                    + $"({rejected.Actual}), so nothing was written.",
                Status: 422,
                ExpectedHead: 0,
                ActualHead: 0),
        };
    }

    /// <summary>
    /// Reads where a document actually went.
    /// </summary>
    /// <param name="sourceId">The source's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or why there is none.</returns>
    public async ValueTask<WireSourceResult> GetSourceAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var record = await _sources.GetAsync(_tenant, sourceId, cancellationToken).ConfigureAwait(false);

        return record is null
            ? new WireProblem(
                UnknownSourceProblem,
                $"no source '{sourceId}' was ingested.",
                Status: 404,
                ExpectedHead: 0,
                ActualHead: 0)
            : ToWire(record);
    }

    /// <summary>
    /// Builds an index version over the sources a collection binds.
    /// </summary>
    /// <remarks>
    /// The watermark is read now rather than asked for: a build reflects the state of the world it was made in, and a
    /// caller that could name a position could have a version claim to answer for a state it never read.
    /// </remarks>
    /// <param name="request">What to build, and over which sources.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version as recorded, or why nothing was built.</returns>
    public async ValueTask<WireIndexBuildResult> BuildIndexVersionAsync(
        WireIndexBuild request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var collectionId = (request.CollectionId ?? string.Empty).Trim();
        var shapeRef = (request.ShapeRef ?? string.Empty).Trim();

        if (collectionId.Length == 0 || shapeRef.Length == 0)
        {
            return new WireProblem(
                InvalidRequestProblem,
                "collection_id and shape_ref are required: a version belongs to a collection and answers for a shape.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var collectionName = (request.CollectionName ?? string.Empty).Trim() is { Length: > 0 } named
            ? named
            : collectionId;

        var outcome = await _builder
            .BuildAsync(
                new IndexBuildPlan
                {
                    Tenant = _tenant,
                    CollectionId = collectionId,
                    CollectionName = collectionName,
                    ShapeRef = shapeRef,
                    PathPrefix = string.IsNullOrWhiteSpace(request.PathPrefix) ? null : request.PathPrefix.Trim(),
                    Watermark = await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false),
                    Activate = request.Activate,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            IndexVersion version => ToWire(version),
            IndexBuildRefused refused => new WireProblem(
                IndexBuildRefusedProblem,
                refused.Reason,
                Status: 422,
                ExpectedHead: 0,
                ActualHead: 0),
        };
    }

    /// <summary>
    /// Cuts a collection over to a version it already has.
    /// </summary>
    /// <remarks>
    /// The process is moved before the record is, and in that order: a version this process never built cannot serve
    /// here, and refusing before anything changes leaves the record as it was. The other order would record a
    /// collection as live on a version nobody can answer from.
    /// </remarks>
    /// <param name="indexVersionId">The version to make live.</param>
    /// <param name="collectionId">The collection it belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activated version, or why nothing was activated.</returns>
    public async ValueTask<WireIndexResult> ActivateIndexVersionAsync(
        string indexVersionId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        if (!_index.Serve(indexVersionId))
        {
            return new WireProblem(
                IndexNotBuiltHereProblem,
                $"index version '{indexVersionId}' was not built in this process, so it cannot serve from here; build "
                    + "it again from the sources this deployment has.",
                Status: 409,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var activated = await _catalogue
            .ActivateAsync(_tenant, collectionId, indexVersionId, cancellationToken)
            .ConfigureAwait(false);

        return activated is null
            ? new WireProblem(
                UnknownIndexVersionProblem,
                $"no index version '{indexVersionId}' belongs to collection '{collectionId}'.",
                Status: 404,
                ExpectedHead: 0,
                ActualHead: 0)
            : ToWire(activated);
    }

    /// <summary>Reads an index version.</summary>
    /// <param name="indexVersionId">The version's identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or why there is none.</returns>
    public async ValueTask<WireIndexResult> GetIndexVersionAsync(
        string indexVersionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersionId);

        var version = await _catalogue.GetAsync(_tenant, indexVersionId, cancellationToken).ConfigureAwait(false);

        return version is null
            ? new WireProblem(
                UnknownIndexVersionProblem,
                $"no index version '{indexVersionId}' is recorded; an index nobody built cannot be explained.",
                Status: 404,
                ExpectedHead: 0,
                ActualHead: 0)
            : ToWire(version);
    }

    /// <summary>Reads the version a collection answers from.</summary>
    /// <param name="collectionId">The collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The live version, or why there is none.</returns>
    public async ValueTask<WireIndexResult> GetActiveIndexVersionAsync(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var version = await _catalogue.ActiveAsync(_tenant, collectionId, cancellationToken).ConfigureAwait(false);

        return version is null
            ? new WireProblem(
                UnknownIndexVersionProblem,
                $"collection '{collectionId}' has no live index version; build one and activate it.",
                Status: 404,
                ExpectedHead: 0,
                ActualHead: 0)
            : ToWire(version);
    }

    /// <summary>
    /// Resolves an answer's envelope back to the version that issued it.
    /// </summary>
    /// <remarks>
    /// This is the read that makes provenance worth carrying: it answers whether the bytes an answer cites were in the
    /// version it names, which is the part of a citation a mixed-up envelope cannot fake. A version the catalogue does
    /// not know resolves to a failure rather than to an error, because "nobody recorded that" is an answer.
    /// </remarks>
    /// <param name="query">The envelope the answer carried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolution, or why it could not be attempted.</returns>
    public async ValueTask<WireEnvelopeResult> ResolveEnvelopeAsync(
        WireEnvelopeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.IndexVersion))
        {
            return new WireProblem(
                InvalidRequestProblem,
                "index_version is required: an envelope that names no version proves nothing.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var resolution = await _catalogue
            .ResolveAsync(
                _tenant,
                new ProvenanceEnvelope(
                    query.IndexVersion,
                    new SequenceNumber(query.LedgerWatermark),
                    [.. (query.Sources ?? []).Select(
                        source => new SourceReference(
                            source.ChunkId,
                            source.SourceId,
                            source.SourcePath,
                            source.ContentHash,
                            source.ChunkOrdinal))]),
                cancellationToken)
            .ConfigureAwait(false);

        return new WireEnvelopeResolution(
            resolution.Resolved,
            resolution.Failure,
            resolution.Version is null ? null : ToWire(resolution.Version),
            resolution.UnrecordedContentHashes);
    }

    /// <summary>Reads the sources this deployment holds.</summary>
    /// <param name="pathPrefix">The prefix to select, or empty for every source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows, in path order.</returns>
    public async ValueTask<WireSourceList> ListSourcesAsync(
        string pathPrefix,
        CancellationToken cancellationToken = default)
    {
        var rows = await _sources
            .ListAsync(_tenant, string.IsNullOrWhiteSpace(pathPrefix) ? null : pathPrefix.Trim(), cancellationToken)
            .ConfigureAwait(false);

        return new WireSourceList([.. rows.Select(ToWire)]);
    }

    /// <summary>
    /// Reads a collection's versions, so an operator can see what it can be cut over to.
    /// </summary>
    /// <remarks>
    /// A collection nobody has built for answers with an empty list rather than with a problem: this port has no
    /// collection registry to tell "no such collection" from "no versions yet", and inventing one would be answering
    /// without knowing. The versions are listed with the superseded ones, because cutting back to one is a cutover.
    /// </remarks>
    /// <param name="collectionId">The collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The versions, by the position they were built against, latest first.</returns>
    public async ValueTask<WireIndexVersionList> ListIndexVersionsAsync(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var versions = await _catalogue
            .ListAsync(_tenant, collectionId, cancellationToken)
            .ConfigureAwait(false);

        return new WireIndexVersionList(collectionId, [.. versions.Select(ToWire)]);
    }

    /// <summary>The shapes this deployment understands.</summary>
    /// <returns>The registered shapes, ordered by name.</returns>
    public WireShapeList ListShapes() =>
        new([.. _shapes.Shapes.Select(
            shape => new WireShape(shape.Name, shape.Version, shape.Identity, shape.Schema))]);

    /// <summary>
    /// Seals an artifact: the bytes inline, or a single-use grant when the caller sends none.
    /// </summary>
    /// <param name="request">The manifest, and the bytes when they travel with it.</param>
    /// <param name="principal">Who is sealing, and which class they may seal into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the seal did, or why it did nothing.</returns>
    /// <remarks>
    /// Idempotent by the domain tuple rather than by an idempotency key, and deliberately so: the tuple holds across
    /// replicas and across headers, which is the stronger guarantee for a producer retrying a seal. The check happens
    /// before any bytes are written, so a retry does not re-upload a hundred megabytes to find out the seal already
    /// happened.
    /// <para>
    /// A caller may not seal above itself. Every later reader trusts the class a manifest declares, so a principal that
    /// could seal into a class it does not itself dominate would be minting evidence nobody was ever authorized to
    /// assert - sealing up is the forgery; sealing down is merely conservative.
    /// </para>
    /// </remarks>
    public async ValueTask<WireSealResult> SealEvidenceAsync(
        WireSealEvidenceRequest request,
        EvidencePrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);

        var manifest = request.Manifest;

        try
        {
            manifest.Validate();
        }
        catch (ArgumentException invalid)
        {
            return new WireProblem(InvalidRequestProblem, invalid.Message, Status: 400, ExpectedHead: 0, ActualHead: 0);
        }

        if (!string.Equals(manifest.Tenant, principal.Tenant, StringComparison.Ordinal))
        {
            return new WireProblem(
                InvalidRequestProblem,
                $"the manifest declares tenant '{manifest.Tenant}' and this caller seals for '{principal.Tenant}'.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        if (!manifest.AuthorizationClass.DominatedBy(principal.Level, principal.Compartments, principal.AllCompartments))
        {
            return new WireProblem(
                EvidenceForbiddenProblem,
                "a caller may not seal an artifact into a class it does not itself dominate.",
                Status: 403,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        // The id is the server's to assign: a caller-supplied one is dropped rather than honoured, so a client cannot
        // choose an id to collide with.
        manifest = manifest with { EvidenceId = null };

        var existing = await _evidence
            .FindByDomainKeyAsync(principal.Tenant, manifest.ComputeDomainKey(), cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // The same logical result, under the same policy, for the same class is the same seal, so the caller is
            // handed the identity that was already assigned rather than a second one.
            return new WireSealResponse(existing.EvidenceId, existing.State.ToWireName(), Created: false);
        }

        var evidenceId = EvidenceIds.New();
        var blobPath = string.Concat(EvidenceContract.PathPrefix, evidenceId);
        var now = Rfc3339(DateTimeOffset.UtcNow);
        byte[]? bytes = null;

        if (!string.IsNullOrWhiteSpace(request.BytesBase64))
        {
            try
            {
                bytes = Convert.FromBase64String(request.BytesBase64);
            }
            catch (FormatException invalid)
            {
                return new WireProblem(
                    InvalidRequestProblem,
                    $"bytes_base64 is not valid base64: {invalid.Message}",
                    Status: 400,
                    ExpectedHead: 0,
                    ActualHead: 0);
            }
        }

        return bytes is null
            ? await GrantAsync(principal, manifest, evidenceId, blobPath, now, cancellationToken).ConfigureAwait(false)
            : await SealInlineAsync(principal, manifest, evidenceId, blobPath, now, bytes, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Registers a pending artifact with a single-use grant, which is the upload path.</summary>
    /// <param name="principal">Who is sealing.</param>
    /// <param name="manifest">The manifest they derived.</param>
    /// <param name="evidenceId">The identity the server assigned.</param>
    /// <param name="blobPath">Where the bytes will live.</param>
    /// <param name="now">When the seal happened.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the seal did, or why it did nothing.</returns>
    private async ValueTask<WireSealResult> GrantAsync(
        EvidencePrincipal principal,
        EvidenceManifest manifest,
        string evidenceId,
        string blobPath,
        string now,
        CancellationToken cancellationToken)
    {
        // Short on purpose: a grant is a single-use capability to write bytes under an id the server has already
        // committed to, so its blast radius is a function of its lifetime.
        var grant = new EvidenceGrant
        {
            GrantId = EvidenceIds.NewGrant(),
            EvidenceId = evidenceId,
            Tenant = principal.Tenant,
            ExpiresAt = Rfc3339(DateTimeOffset.UtcNow.AddSeconds(EvidenceContract.GrantTtlSeconds)),
        };

        var outcome = await _evidence
            .RegisterAsync(
                new EvidenceArtifact
                {
                    EvidenceId = evidenceId,
                    Tenant = principal.Tenant,
                    State = EvidenceState.Pending,
                    Manifest = manifest,
                    BlobPath = blobPath,
                    CreatedAt = now,
                },
                grant,
                cancellationToken)
            .ConfigureAwait(false);

        return new WireSealResponse(
            outcome.EvidenceId,
            EvidenceState.Pending.ToWireName(),
            outcome.Created,
            outcome.Grant is { } issued ? new WireEvidenceGrant(issued.GrantId, issued.ExpiresAt) : null);
    }

    /// <summary>Stores the bytes and registers a committed artifact, in that order.</summary>
    /// <param name="principal">Who is sealing.</param>
    /// <param name="manifest">The manifest they derived.</param>
    /// <param name="evidenceId">The identity the server assigned.</param>
    /// <param name="blobPath">Where the bytes go.</param>
    /// <param name="now">When the seal happened.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the seal did, or why it did nothing.</returns>
    private async ValueTask<WireSealResult> SealInlineAsync(
        EvidencePrincipal principal,
        EvidenceManifest manifest,
        string evidenceId,
        string blobPath,
        string now,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (bytes.Length > EvidenceContract.InlineSealMaxBytes)
        {
            return new WireProblem(
                EvidenceTooLargeProblem,
                $"the bytes are {bytes.Length} byte(s), over the {EvidenceContract.InlineSealMaxBytes}-byte inline cap; "
                    + "take a grant and upload them",
                Status: 413,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        if (ArtifactContent.Verify(manifest, bytes) is { Verified: false } mismatch)
        {
            return Mismatch(mismatch);
        }

        // Bytes first, metadata second. The other order can leave a committed row pointing at bytes that were never
        // written - a citation that resolves to nothing. This order can leave an orphan blob, which costs storage and
        // lies to nobody.
        await _evidenceBytes
            .PutAsync(
                EvidenceKey(principal.Tenant, blobPath, manifest.ArtifactHash),
                manifest.MediaType,
                bytes,
                cancellationToken)
            .ConfigureAwait(false);

        var seal = await _evidence
            .RegisterAsync(
                new EvidenceArtifact
                {
                    EvidenceId = evidenceId,
                    Tenant = principal.Tenant,
                    State = EvidenceState.Committed,
                    Manifest = manifest,
                    BlobPath = blobPath,
                    CreatedAt = now,
                    CommittedAt = now,
                },
                grant: null,
                cancellationToken)
            .ConfigureAwait(false);

        // The state is the recorded artifact's rather than a constant: losing the domain-key race to a concurrent seal
        // hands back the winner's id, and that artifact stays pending until its own bytes are committed. Reporting
        // "committed" for it would send the caller off to cite evidence that does not resolve yet.
        var recorded = seal.Created
            ? EvidenceState.Committed
            : (await _evidence.GetAsync(principal.Tenant, seal.EvidenceId, cancellationToken).ConfigureAwait(false))
                ?.State ?? EvidenceState.Committed;

        return new WireSealResponse(seal.EvidenceId, recorded.ToWireName(), seal.Created);
    }

    /// <summary>
    /// Uploads bytes under a grant, and does not spend the grant unless they are the bytes the manifest names.
    /// </summary>
    /// <param name="principal">Who is uploading.</param>
    /// <param name="evidenceId">The artifact the grant is for.</param>
    /// <param name="grantId">The grant.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Nothing on success, or why nothing was stored.</returns>
    /// <remarks>
    /// The bytes are verified <em>before</em> the grant is spent, so a corrupt upload can be retried: burning a
    /// single-use capability on a client-side mistake would turn a recoverable error into an unrecoverable one. An
    /// unknown artifact answers what an invalid grant answers, so a caller without a valid grant learns nothing about
    /// which ids exist.
    /// </remarks>
    public async ValueTask<WireProblem?> PutEvidenceBytesAsync(
        EvidencePrincipal principal,
        string evidenceId,
        string grantId,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var artifact = await _evidence.GetAsync(principal.Tenant, evidenceId, cancellationToken).ConfigureAwait(false);

        if (artifact is null
            || !artifact.Manifest.AuthorizationClass.DominatedBy(
                principal.Level, principal.Compartments, principal.AllCompartments))
        {
            return GrantInvalid();
        }

        if (ArtifactContent.Verify(artifact.Manifest, bytes.Span) is { Verified: false } mismatch)
        {
            return Mismatch(mismatch);
        }

        var spent = await _evidence
            .ConsumeGrantAsync(
                principal.Tenant, evidenceId, grantId, Rfc3339(DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (spent is null)
        {
            return GrantInvalid();
        }

        await _evidenceBytes
            .PutAsync(
                EvidenceKey(principal.Tenant, artifact.BlobPath, artifact.Manifest.ArtifactHash),
                artifact.Manifest.MediaType,
                bytes,
                cancellationToken)
            .ConfigureAwait(false);

        return null;
    }

    /// <summary>
    /// Commits an uploaded artifact, after re-reading its bytes and checking them again.
    /// </summary>
    /// <param name="principal">Who is committing.</param>
    /// <param name="evidenceId">The artifact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, or why it did not.</returns>
    /// <remarks>
    /// This is the moment an artifact becomes citable, so it is the moment "these bytes are that hash" has to be true -
    /// not the moment an upload happened to return a success. Only absence is the caller's state to fix: a backend that
    /// failed to read is a storage error, and reporting it as "upload first" would send a caller off to re-upload bytes
    /// that are already there.
    /// </remarks>
    public async ValueTask<WireEvidenceCommitResult> CommitEvidenceAsync(
        EvidencePrincipal principal,
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // No resolve helper here, and no audit either: committing is not a read, and the four questions a read asks
        // include "is it committed", which is the one thing this operation exists to answer yes to. The original keeps
        // the two apart for the same reason, and a commit that recorded a resolution would inflate the audit with
        // events that are not resolutions.
        var artifact = await _evidence.GetAsync(principal.Tenant, evidenceId, cancellationToken).ConfigureAwait(false);

        if (artifact is null)
        {
            return NotFound(evidenceId);
        }

        if (!artifact.Manifest.AuthorizationClass.DominatedBy(
            principal.Level, principal.Compartments, principal.AllCompartments))
        {
            return new WireProblem(
                EvidenceForbiddenProblem,
                "this reader does not dominate the artifact's authorization class.",
                Status: 403,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var stored = await _evidenceBytes
            .GetAsync(EvidenceKey(principal.Tenant, artifact.BlobPath, artifact.Manifest.ArtifactHash), cancellationToken)
            .ConfigureAwait(false);

        if (stored.Length == 0 && artifact.Manifest.BytesLength > 0)
        {
            return NotCommitted(evidenceId);
        }

        if (ArtifactContent.Verify(artifact.Manifest, stored) is { Verified: false } mismatch)
        {
            return new WireProblem(
                EvidenceHashMismatchProblem,
                $"artifact_hash mismatch: the manifest declares {mismatch.ExpectedHash} over {mismatch.ExpectedLength} "
                    + $"byte(s), and the stored bytes hash to {mismatch.ActualHash} over {mismatch.ActualLength}",
                Status: 409,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var committed = await _evidence
            .CommitAsync(principal.Tenant, evidenceId, Rfc3339(DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        return new WireEvidenceCommit(evidenceId, EvidenceState.Committed.ToWireName(), committed);
    }

    /// <summary>
    /// Reads an artifact's manifest, which is what a citation resolves to.
    /// </summary>
    /// <param name="principal">Who is reading.</param>
    /// <param name="evidenceId">The artifact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The manifest, or why it does not resolve.</returns>
    /// <remarks>
    /// The manifest itself rather than a wrapper, because a 200 already means committed and the identity is stamped onto
    /// the manifest: pending answers with its own refusal and so does purged, so a wrapper could only repeat what the
    /// status already said.
    /// </remarks>
    public async ValueTask<WireEvidenceManifestResult> ReadEvidenceManifestAsync(
        EvidencePrincipal principal,
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (await ResolveAsync(principal, evidenceId, "manifest", null, null, cancellationToken).ConfigureAwait(false)
            is { } refusal)
        {
            return refusal;
        }

        var artifact = await _evidence.GetAsync(principal.Tenant, evidenceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"artifact '{evidenceId}' resolved and then could not be read; the store changed underneath a read");

        return artifact.Manifest with { EvidenceId = artifact.EvidenceId };
    }

    /// <summary>
    /// Reads a bounded window over an artifact's rows, in the order they were sealed.
    /// </summary>
    /// <param name="principal">Who is reading.</param>
    /// <param name="evidenceId">The artifact.</param>
    /// <param name="from">The first row, zero-based.</param>
    /// <param name="limit">How many rows, or 0 for the default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, or why the artifact does not resolve.</returns>
    /// <remarks>
    /// Served for the canonical CSV form only. A Parquet artifact is sealed and replayed byte for byte but not decoded
    /// here: pulling a Parquet reader into the image to paginate rows would be a large dependency for a convenience,
    /// and what matters - that the bytes are intact - holds either way. A caller wanting Parquet rows reads the
    /// artifact.
    /// </remarks>
    public async ValueTask<WireEvidenceRowsResult> ReadEvidenceRowsAsync(
        EvidencePrincipal principal,
        string evidenceId,
        long from = 0,
        long limit = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var start = Math.Max(0, from);
        var window = limit <= 0
            ? EvidenceContract.DefaultRowLimit
            : Math.Min(limit, EvidenceContract.MaxRowLimit);

        if (await ResolveAsync(principal, evidenceId, "rows", start, window, cancellationToken).ConfigureAwait(false)
            is { } refusal)
        {
            return refusal;
        }

        var artifact = await _evidence.GetAsync(principal.Tenant, evidenceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"artifact '{evidenceId}' resolved and then could not be read; the store changed underneath a read");

        if (!string.Equals(artifact.Manifest.MediaType, EvidenceContract.MediaTypeCsv, StringComparison.Ordinal))
        {
            return new WireProblem(
                InvalidRequestProblem,
                $"rows are served for '{EvidenceContract.MediaTypeCsv}' artifacts only; this artifact is "
                    + $"'{artifact.Manifest.MediaType}'. Its bytes are sealed and replayable, but this server does not "
                    + "decode them",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        var stored = await _evidenceBytes
            .GetAsync(EvidenceKey(principal.Tenant, artifact.BlobPath, artifact.Manifest.ArtifactHash), cancellationToken)
            .ConfigureAwait(false);

        var columns = artifact.Manifest.Schema.Columns.Select(column => column.Name).ToList();
        var lines = Encoding.UTF8.GetString(stored).Split('\n');
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        long total = 0;

        // The canonical form is a header row and then data. The header is dropped and the manifest's column NAMES are
        // used instead: the schema is the contract, and a header that disagreed with it would be the schema drifting
        // silently. One pass counts every data row for the total and keeps only the page's.
        foreach (var line in lines.Skip(1))
        {
            var row = line.TrimEnd('\r');

            if (row.Length == 0)
            {
                continue;
            }

            if (total >= start && rows.Count < window)
            {
                var cells = CanonicalCsv.Cells(row);
                var keyed = new Dictionary<string, string?>(StringComparer.Ordinal);

                for (var column = 0; column < columns.Count; column++)
                {
                    keyed[columns[column]] = column < cells.Count ? cells[column] : null;
                }

                rows.Add(keyed);
            }

            total++;
        }

        return new WireEvidenceRows(evidenceId, start, start + rows.Count < total, rows, total);
    }

    /// <summary>
    /// Reads the resolutions of an artifact, newest first.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="evidenceId">The artifact.</param>
    /// <param name="limit">How many, or 0 for the default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolutions.</returns>
    /// <remarks>
    /// An operator's question about the deployment rather than a participant's question about the data, which is why the
    /// route above it is management-gated rather than evidence-scoped: a service that can seal evidence has no business
    /// enumerating who read it. It reports <em>that</em> reads happened and never the rows themselves.
    /// </remarks>
    public async ValueTask<WireEvidenceAccessResult> ReadEvidenceAccessesAsync(
    string tenant,
        string evidenceId,
        long limit = 0,
        CancellationToken cancellationToken = default)
    {
        var window = limit <= 0
            ? EvidenceContract.DefaultRowLimit
            : Math.Min(limit, EvidenceContract.MaxRowLimit);

        var accesses = await _evidence
            .AccessesAsync(tenant, evidenceId, (int)window, cancellationToken)
            .ConfigureAwait(false);

        return new WireEvidenceAccessList(
            evidenceId,
            [
                .. accesses.Select(access => new WireEvidenceAccess(
                    access.Uid,
                    access.Kind,
                    access.RowFrom,
                    access.RowLimit,
                    access.Outcome,
                    access.At)),
            ]);
    }

    /// <summary>
    /// Purges one artifact's bytes now, leaving the row so citations keep resolving as expired.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="evidenceId">The artifact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, or why nothing did.</returns>
    /// <remarks>
    /// The operator-facing twin of the janitor, and the reason a hold is a reachable refusal rather than dead
    /// vocabulary: an artifact under hold refuses deletion here, which is the whole point of a hold. The order is the
    /// sweep's - bytes first, then the row.
    /// </remarks>
    public async ValueTask<WireEvidencePurgeResult> PurgeEvidenceAsync(
    string tenant,
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        var artifact = await _evidence.GetAsync(tenant, evidenceId, cancellationToken).ConfigureAwait(false);

        if (artifact is null)
        {
            return NotFound(evidenceId);
        }

        if (artifact.Manifest.Retention is { LegalHold: true })
        {
            return new WireProblem(
                EvidenceOnHoldProblem,
                $"artifact '{evidenceId}' is under a legal hold, so its bytes may not be deleted.",
                Status: 409,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        if (artifact.State == EvidenceState.Purged)
        {
            return new WireEvidencePurge(evidenceId, Purged: false, EvidenceState.Purged.ToWireName());
        }

        await _evidenceBytes
            .DeleteAsync(EvidenceKey(tenant, artifact.BlobPath, artifact.Manifest.ArtifactHash), cancellationToken)
            .ConfigureAwait(false);

        var purged = await _evidence
            .MarkPurgedAsync(tenant, evidenceId, Rfc3339(DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        return new WireEvidencePurge(evidenceId, purged, EvidenceState.Purged.ToWireName());
    }

    /// <summary>
    /// Places or lifts a legal hold.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="evidenceId">The artifact.</param>
    /// <param name="hold">Whether to hold it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Nothing, or why nothing changed.</returns>
    /// <remarks>
    /// A hold blocks deletion and nothing else. Reads stay governed by the authorization class exactly as before: an
    /// instruction to preserve evidence that also hid it would be a strange instruction.
    /// </remarks>
    public async ValueTask<WireProblem?> SetEvidenceLegalHoldAsync(
    string tenant,
        string evidenceId,
        bool hold,
        CancellationToken cancellationToken = default)
    {
        var held = await _evidence
            .SetLegalHoldAsync(tenant, evidenceId, hold, cancellationToken)
            .ConfigureAwait(false);

        return held ? null : NotFound(evidenceId);
    }

    // ---- helpers ----

    /// <summary>
    /// Answers whether an artifact resolves for a reader, and records the resolution.
    /// </summary>
    /// <param name="principal">Who is reading.</param>
    /// <param name="evidenceId">The artifact.</param>
    /// <param name="kind">What is being read: <c>manifest</c> or <c>rows</c>.</param>
    /// <param name="rowFrom">The first row asked for, or none.</param>
    /// <param name="rowLimit">How many rows asked for, or none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="null"/> when it resolves, otherwise why it does not.</returns>
    /// <remarks>
    /// One helper for both read routes, so the four questions they ask - does it exist, may this reader have it, is it
    /// committed, is it still there - cannot drift apart between them. The audit row is written on the denial paths as
    /// well as on success, which is where an audit log earns its keep; a <em>missing</em> artifact is deliberately not
    /// recorded, because there is no artifact to attach the row to and recording every miss would let a scan fill the
    /// table.
    /// </remarks>
    private async ValueTask<WireProblem?> ResolveAsync(
        EvidencePrincipal principal,
        string evidenceId,
        string kind,
        long? rowFrom,
        long? rowLimit,
        CancellationToken cancellationToken)
    {
        var artifact = await _evidence.GetAsync(principal.Tenant, evidenceId, cancellationToken).ConfigureAwait(false);

        if (artifact is null)
        {
            return NotFound(evidenceId);
        }

        // A refusal says nothing about the artifact: learning "this exists and is above you" is itself a disclosure.
        if (!artifact.Manifest.AuthorizationClass.DominatedBy(
            principal.Level, principal.Compartments, principal.AllCompartments))
        {
            await RecordAsync(principal, evidenceId, kind, rowFrom, rowLimit, "denied", cancellationToken)
                .ConfigureAwait(false);

            return new WireProblem(
                EvidenceForbiddenProblem,
                "this reader does not dominate the artifact's authorization class.",
                Status: 403,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        if (artifact.State == EvidenceState.Purged)
        {
            await RecordAsync(principal, evidenceId, kind, rowFrom, rowLimit, "expired", cancellationToken)
                .ConfigureAwait(false);

            return new WireProblem(
                EvidenceExpiredProblem,
                $"artifact '{evidenceId}' was purged under its retention policy, so its bytes are gone.",
                Status: 410,
                ExpectedHead: 0,
                ActualHead: 0);
        }

        if (artifact.State != EvidenceState.Committed)
        {
            await RecordAsync(principal, evidenceId, kind, rowFrom, rowLimit, "denied", cancellationToken)
                .ConfigureAwait(false);

            return Pending(evidenceId);
        }

        await RecordAsync(principal, evidenceId, kind, rowFrom, rowLimit, "ok", cancellationToken)
            .ConfigureAwait(false);

        return null;
    }

    private ValueTask RecordAsync(
        EvidencePrincipal principal,
        string evidenceId,
        string kind,
        long? rowFrom,
        long? rowLimit,
        string outcome,
        CancellationToken cancellationToken) =>
        _evidence.RecordAccessAsync(
            new EvidenceAccess
            {
                EvidenceId = evidenceId,
                Tenant = principal.Tenant,
                Uid = principal.Uid,
                Kind = kind,
                RowFrom = rowFrom,
                RowLimit = rowLimit,
                Outcome = outcome,
                At = Rfc3339(DateTimeOffset.UtcNow),
            },
            cancellationToken);

    private static WireProblem NotFound(string evidenceId) => new(
        EvidenceNotFoundProblem,
        $"no artifact '{evidenceId}' exists in this tenant.",
        Status: 404,
        ExpectedHead: 0,
        ActualHead: 0);

    private static WireProblem Pending(string evidenceId) => new(
        EvidencePendingProblem,
        $"artifact '{evidenceId}' is sealed and its bytes have not been committed, so nothing resolves yet.",
        Status: 409,
        ExpectedHead: 0,
        ActualHead: 0);

    private static WireProblem NotCommitted(string evidenceId) => new(
        EvidenceNotCommittedProblem,
        $"artifact '{evidenceId}' has no bytes to commit; upload them first.",
        Status: 409,
        ExpectedHead: 0,
        ActualHead: 0);

    private static WireProblem GrantInvalid() => new(
        EvidenceGrantInvalidProblem,
        "the grant is unknown, expired or already spent.",
        Status: 403,
        ExpectedHead: 0,
        ActualHead: 0);

    private static WireProblem Mismatch(ArtifactVerification mismatch) => new(
        EvidenceHashMismatchProblem,
        $"artifact_hash mismatch: the manifest declares {mismatch.ExpectedHash} over {mismatch.ExpectedLength} "
            + $"byte(s), and the bytes hash to {mismatch.ActualHash} over {mismatch.ActualLength}",
        Status: 409,
        ExpectedHead: 0,
        ActualHead: 0);

    /// <summary>Formats an instant the way the plane's own stamps are written.</summary>
    private static string Rfc3339(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the source key an artifact's bytes live under.
    /// </summary>
    /// <remarks>
    /// The object store keys blobs by content hash, and the manifest carries the <c>sha256:</c> prefix the store does not
    /// want, so the prefix comes off here. Authorization is never inferred from this path - it comes from the artifact's
    /// row - which is why the path can be this boring.
    /// </remarks>
    private static SourceKey EvidenceKey(string tenant, string blobPath, string artifactHash) =>
        SourceKey.New(
            tenant,
            blobPath,
            artifactHash.StartsWith("sha256:", StringComparison.Ordinal)
                ? artifactHash["sha256:".Length..]
                : artifactHash);

    /// <summary>
    /// Reads what a command made under a key was answered the first time, if it was.
    /// </summary>
    /// <remarks>
    /// A payload that cannot be read back throws rather than falling through to the write: answering a retry by doing the
    /// command a second time is precisely what a key exists to prevent, and a corrupted answer must not become a second
    /// claim.
    /// </remarks>
    private async ValueTask<T?> AnsweredAsync<T>(
        string scope,
        string key,
        JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
        where T : class
    {
        if (key.Length == 0)
        {
            return null;
        }

        var recorded = await _idempotency.FindAsync(_tenant, scope, key, cancellationToken).ConfigureAwait(false);

        return recorded is null
            ? null
            : JsonSerializer.Deserialize(recorded, type)
                ?? throw new InvalidOperationException(
                    $"the answer recorded for key '{key}' cannot be read back, so a retry cannot be answered with it");
    }

    private ValueTask RememberAsync<T>(
        string scope,
        string key,
        T answer,
        JsonTypeInfo<T> type,
        CancellationToken cancellationToken) =>
        _idempotency.RecordAsync(_tenant, scope, key, JsonSerializer.Serialize(answer, type), cancellationToken);

    private async ValueTask<FactSlice> PresentAsync(CancellationToken cancellationToken)
    {
        var pin = await _facts.CurrentPinAsync(cancellationToken).ConfigureAwait(false);

        return await _facts.SliceAsync(pin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the pin a context is composed at: an explicit position, or a date read through the versions.
    /// </summary>
    private async ValueTask<(SequenceNumber Pin, WireProblem? Problem)> ResolvePinAsync(
        WireContextRequest request,
        CancellationToken cancellationToken)
    {
        var asOfDate = (request.AsOfDate ?? string.Empty).Trim();

        if (asOfDate.Length == 0)
        {
            return (new SequenceNumber(request.AsOf), null);
        }

        if (!DateOnly.TryParseExact(
                asOfDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return (SequenceNumber.Zero, new WireProblem(
                InvalidRequestProblem, "as_of_date must be a date as YYYY-MM-DD.", Status: 400, 0, 0));
        }

        var registry = VersionRegistry.At(await PresentAsync(cancellationToken).ConfigureAwait(false));

        if (registry.EffectiveOn(date) is not { } effective ||
            !registry.TryPin(effective.VersionId, out var pin))
        {
            return (SequenceNumber.Zero, new WireProblem(
                "https://munarium.dev/problems/unknown-as-of-date",
                $"no version is as of {asOfDate} or earlier.",
                Status: 404,
                0,
                0));
        }

        return (pin, null);
    }

    private async ValueTask<WireVersion> ToWireAsync(VersionRecord version, CancellationToken cancellationToken)
    {
        var head = await _storage
            .HeadAsync(StreamId.From(version.VersionId), cancellationToken)
            .ConfigureAwait(false);

        return new WireVersion(
            version.VersionId,
            version.ParentVersionId,
            version.AsOfDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            version.Label,
            head.Value);
    }

    private static WireIngestedSource ToWire(IngestedDocument ingested) => new(
        ingested.Record.SourceId,
        ingested.Record.Path,
        KindOf(ingested.Kind),
        ingested.Record.MediaType,
        ingested.Record.ContentHash,
        ingested.Record.BytesLength,
        ingested.Record.BlobUri,
        ingested.Record.BackendId,
        ingested.Record.IngestedAt,
        ingested.ChunksIndexed,
        ingested.IndexVersion);

    private static WireIndexManifest ToWire(IndexManifest manifest) => new(
        manifest.CollectionId,
        manifest.CollectionName,
        manifest.ShapeRef,
        manifest.Engine,
        manifest.Chunker,
        manifest.Extractors,
        manifest.Embedder.Fingerprint,
        manifest.MaxChars,
        manifest.SourceContentHashes);

    private static WireIndexVersion ToWire(IndexVersion version) => new(
        version.Id,
        version.CollectionId,
        version.ShapeRef,
        version.Watermark.Value,
        version.Active,
        version.Superseded,
        version.ActivatedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        version.DeactivatedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ToWire(version.Manifest));

    private static WireSourceInfo ToWire(SourceRecord record) => new(
        record.SourceId,
        record.Path,
        record.MediaType,
        record.ContentHash,
        record.BytesLength,
        record.BlobUri,
        record.BackendId,
        record.IngestedAt,
        record.ExtractionStatus,
        record.ExtractionMethod);

    private static WireFact ToWire(SlicedFact sliced) => new(
        sliced.Fact.VersionId,
        sliced.Fact.ClaimId,
        ClaimTypeName(sliced.Fact.ClaimType),
        sliced.Fact.Lineage,
        sliced.Fact.Statement,
        sliced.Fact.Actor,
        sliced.Fact.IsDisputed ? WireClaimStatus.Disputed : WireClaimStatus.Accepted,
        sliced.Fact.Gate,
        sliced.Fact.Reason,
        sliced.GlobalSequence.Value);

    private static WireSourceReference ToWire(SourceReference source) => new(
        source.ChunkId,
        source.SourceId,
        source.SourcePath,
        source.ContentHash,
        source.ChunkOrdinal);

    private static string KindOf(SourceIngestKind kind) => kind switch
    {
        SourceIngestKind.Replaced => WireSourceKinds.Replaced,
        SourceIngestKind.Unchanged => WireSourceKinds.Unchanged,
        _ => WireSourceKinds.New,
    };

    private static string ClaimTypeName(ClaimType claimType) => claimType switch
    {
        ClaimType.Fact => WireClaimTypes.Fact,
        ClaimType.Update => WireClaimTypes.Update,
        ClaimType.Correction => WireClaimTypes.Correction,
        _ => WireClaimTypes.Unspecified,
    };

    // An unknown type is not a reason to refuse the claim: it is read as "the caller did not say", which is
    // what the ledger-conflict gate then treats as an assertion.
    private static ClaimType ClaimTypeOf(string? claimType) => claimType switch
    {
        WireClaimTypes.Fact => ClaimType.Fact,
        WireClaimTypes.Update => ClaimType.Update,
        WireClaimTypes.Correction => ClaimType.Correction,
        _ => ClaimType.Unspecified,
    };

    // The version body is built by hand rather than by a serialiser, because the body is ledger content: its
    // field order and escaping are part of what a digest is taken over. JsonEncodedText does the escaping so
    // nothing here has to be trusted to get a quote or a backslash right.
    private static string VersionBody(string versionId, string parent, string asOf, string label)
    {
        var body = new StringBuilder()
            .Append("{\"version_id\":\"").Append(JsonEncodedText.Encode(versionId)).Append('"');

        if (parent.Length > 0)
        {
            body.Append(",\"parent_version_id\":\"").Append(JsonEncodedText.Encode(parent)).Append('"');
        }

        if (asOf.Length > 0)
        {
            body.Append(",\"as_of\":\"").Append(JsonEncodedText.Encode(asOf)).Append('"');
        }

        if (label.Length > 0)
        {
            body.Append(",\"label\":\"").Append(JsonEncodedText.Encode(label)).Append('"');
        }

        return body.Append('}').ToString();
    }

    // The contract marks these required; a caller that omits one gets told which.
    private static string? DescribeInvalid(WireClaimProposal proposal) =>
        FirstMissing(
            (proposal.ClaimId, "claim_id"),
            (proposal.Shape, "shape"),
            (proposal.Body, "body"),
            (proposal.Statement, "statement"),
            (proposal.Actor, "actor"));

    private static string? FirstMissing(params (string? Value, string Name)[] fields)
    {
        foreach (var (value, name) in fields)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return $"{name} is required.";
            }
        }

        return null;
    }

    // The per-claim verdict, derived from the same blocked finding the write path matched. One rule, so the
    // wire and the ledger cannot disagree about why a claim is disputed.
    private static WireClaimOutcome VerdictOf(
        string versionId,
        Claim claim,
        IReadOnlyList<GateFinding> findings,
        long head)
    {
        var refusal = findings.FirstOrDefault(finding =>
            finding.Severity is Severity.Block &&
            string.Equals(finding.ClaimKey, claim.ClaimKey, StringComparison.Ordinal));

        return new WireClaimOutcome(
            versionId,
            claim.Id,
            ClaimTypeName(claim.ClaimType),
            claim.ClaimKey,
            claim.Status is ClaimStatus.Disputed ? WireClaimStatus.Disputed : WireClaimStatus.Accepted,
            refusal?.RuleId ?? string.Empty,
            refusal?.Message ?? string.Empty,
            head);
    }

    // The detail travels as the JSON text it already is: the kernel does not interpret it, so the contract
    // carries it verbatim rather than promising a shape it would then have to keep in step.
    private static WireFinding FindingOf(GateFinding finding) => new(
        finding.RuleId,
        SeverityName(finding.Severity),
        finding.Message,
        finding.ScopePath ?? string.Empty,
        finding.ClaimKey ?? string.Empty,
        finding.Detail?.ToJsonString() ?? string.Empty);

    private static WireStoredFinding StoredOf(StoredFinding stored) =>
        new(stored.Sequence.Value, FindingOf(stored.Finding));

    private async ValueTask<MeshSnapshot> SnapshotAsync(
        string versionId,
        long asOf,
        CancellationToken cancellationToken) =>
        await _snapshots
            .BuildAsync(versionId, asOf > 0 ? new SequenceNumber(asOf) : null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    private static WireProblem Contended(WriteContended contended) => new(
        ContendedWriteProblem,
        "Every retry lost to a moving head; nothing was written.",
        Status: 409,
        contended.Expected.Value,
        contended.Actual.Value);

    // The release route carries the detail key whole, so the boundary checks the same rule the kernel enforces: a
    // key that names no property could never have been locked, and a request that cannot be understood is a 400
    // rather than a server error.
    private static WireProblem? DetailKeyRefusal(string? detailKey) =>
        string.IsNullOrWhiteSpace(detailKey) || !detailKey.Contains('.', StringComparison.Ordinal)
            ? new WireProblem(
                InvalidRequestProblem,
                "detail_key is 'subject.key', and this one names no property to release.",
                Status: 400,
                ExpectedHead: 0,
                ActualHead: 0)
            : null;

    // The plane holds what was recorded, so a read can select a status it actually contains; an unrecognised word
    // selects nothing rather than everything, because the caller asked for a state.
    private static PromiseStatus? PromiseStatusOf(string? status) => status switch
    {
        WirePromiseStatuses.Open => PromiseStatus.Open,
        WirePromiseStatuses.Fulfilled => PromiseStatus.Fulfilled,
        WirePromiseStatuses.Expired => PromiseStatus.Expired,
        WirePromiseStatuses.Violated => PromiseStatus.Violated,
        _ => null,
    };

    // The instant a snapshot carries, in the format the evidence contract validates a timestamp against: one
    // spelling of a timestamp in this port rather than one per surface.
    private static string Timestamp(DateTimeOffset? instant) =>
        instant?.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string ProvenanceName(Provenance provenance) => provenance switch
    {
        Provenance.Backfilled => WireProvenances.Backfilled,
        Provenance.Repaired => WireProvenances.Repaired,
        Provenance.Emergent => WireProvenances.Emergent,
        Provenance.CoverageRepair => WireProvenances.CoverageRepair,
        _ => WireProvenances.Witnessed,
    };

    private static WireResolvedClaim ClaimOf(Claim claim) => new(
        claim.Id,
        claim.VersionId,
        claim.Sequence.Value,
        ClaimTypeName(claim.ClaimType),
        claim.Subject,
        claim.Key,
        claim.Value,
        claim.ScopePath ?? string.Empty,
        claim.Status is ClaimStatus.Disputed ? WireClaimStatus.Disputed : WireClaimStatus.Accepted,
        ProvenanceName(claim.Provenance),
        claim.SupersedesId ?? string.Empty);

    private static WireAnchor AnchorOf(Anchor anchor) => new(
        anchor.Id,
        anchor.VersionId,
        anchor.DetailKey,
        anchor.LockedValue,
        anchor.LockedAtScope ?? string.Empty,
        AnchorStatusName(anchor.Status),
        anchor.Sequence.Value,
        anchor.EvidenceJson ?? string.Empty);

    private static WireDigest DigestOf(Digest digest) => new(
        digest.VersionId,
        digest.Tier,
        digest.ScopePath,
        digest.Content,
        digest.ContentHash,
        digest.BuiltFromSequence.Value);

    private static WirePromise PromiseOf(Promise promise) => new(
        promise.Id,
        promise.VersionId,
        promise.Key,
        promise.Kind,
        promise.Description,
        promise.OriginScope ?? string.Empty,
        promise.DueScope ?? string.Empty,
        PromiseStatusName(promise.Status),
        promise.Sequence.Value,
        promise.FulfilledSequence?.Value ?? 0);

    // The contract counts in int64: a count that does not fit is not a count, and a uint64 that did would be a
    // number no reader of the answer could act on.
    private static WireCounter CounterOf(CounterTotal counter) => new(
        counter.Key,
        (long)counter.Total,
        (long)(counter.Budget ?? 0),
        counter.IsOverBudget);

    private static WireEntity EntityOf(Entity entity) => new(
        entity.Id,
        entity.VersionId,
        entity.CanonicalName,
        entity.EntityType ?? string.Empty,
        entity.Aliases,
        entity.Sequence.Value,
        entity.MergedInto ?? string.Empty);

    private static string AnchorStatusName(AnchorStatus status) => status switch
    {
        AnchorStatus.Locked => WireAnchorStatuses.Locked,
        AnchorStatus.Released => WireAnchorStatuses.Released,
        _ => WireAnchorStatuses.Unspecified,
    };

    private static string PromiseStatusName(PromiseStatus status) => status switch
    {
        PromiseStatus.Open => WirePromiseStatuses.Open,
        PromiseStatus.Fulfilled => WirePromiseStatuses.Fulfilled,
        PromiseStatus.Expired => WirePromiseStatuses.Expired,
        PromiseStatus.Violated => WirePromiseStatuses.Violated,
        _ => WirePromiseStatuses.Unspecified,
    };

    // An empty query value means "do not filter", which is a different thing from filtering for the empty string:
    // no finding has an empty rule id, so a filter the caller did not ask for would only ever hide findings.
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // An unrecognised severity is not a filter: the caller asked for something the contract does not have, and
    // the honest answer to that is everything the version holds rather than an empty list that looks like a
    // verdict.
    private static Severity? SeverityOf(string? severity) => severity switch
    {
        WireSeverities.Info => Severity.Info,
        WireSeverities.Warn => Severity.Warn,
        WireSeverities.Block => Severity.Block,
        _ => null,
    };

    private static string SeverityName(Severity severity) => severity switch
    {
        Severity.Info => WireSeverities.Info,
        Severity.Warn => WireSeverities.Warn,
        Severity.Block => WireSeverities.Block,
        _ => WireSeverities.Unspecified,
    };

    // An unrecognised word reads as "the caller did not say" rather than as a refusal: a claim is not refused
    // for the provenance it carries, and guessing at a meaning would be worse than admitting there is none.
    private static Provenance ProvenanceOf(string? provenance) => provenance switch
    {
        WireProvenances.Backfilled => Provenance.Backfilled,
        WireProvenances.Repaired => Provenance.Repaired,
        WireProvenances.Emergent => Provenance.Emergent,
        WireProvenances.CoverageRepair => Provenance.CoverageRepair,
        _ => Provenance.Witnessed,
    };
}
