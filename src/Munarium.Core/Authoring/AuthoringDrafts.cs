namespace Munarium.Authoring;

using System.Text;
using System.Text.Json;

/// <summary>One draft: what an author has answered, and which pattern they are starting from.</summary>
/// <remarks>
/// The answers are a flat map keyed by question id, which is what the interview produces and what materialization reads.
/// A draft carries no documents: they are built on demand from the answers, so a draft cannot disagree with itself about
/// its own content, and an author who changes one answer changes every document that depends on it.
/// <para>
/// The two instants are stamped by whoever stores the draft rather than by whoever wrote it: a clock belongs to the
/// deployment, and a draft that carried its own would make the order of two edits a matter of opinion.
/// </para>
/// </remarks>
public sealed record AuthoringDraft
{
    /// <summary>Gets the draft's name, which is also the runbook name it will materialize into.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the pattern the author is starting from, or <see langword="null"/> while none is chosen.</summary>
    public string? PatternId { get; init; }

    /// <summary>Gets the answers so far, keyed by question id.</summary>
    public IReadOnlyDictionary<string, object?> Answers { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Gets when the draft was created, as the store stamped it.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>Gets when it was last written, as the store stamped it.</summary>
    public string? UpdatedAt { get; init; }
}

/// <summary>Where drafts are kept.</summary>
/// <remarks>
/// A draft is a conversation an author is in the middle of - not ledger data and not a published document. It is kept so
/// that answering one question and coming back tomorrow is possible, and it is removed when the author is done with it. A
/// draft that was materialized into a runbook that was then applied is not the authority on anything: the runbook is.
/// </remarks>
public interface IAuthoringDraftStore
{
    /// <summary>Writes a draft, stamping when it was created and when it was last written.</summary>
    /// <param name="draft">The draft as answered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The draft as stored, with both instants filled in.</returns>
    ValueTask<AuthoringDraft> SaveAsync(AuthoringDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Reads one draft.</summary>
    /// <param name="name">Its name, which is its identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The draft, or <see langword="null"/> when there is none.</returns>
    ValueTask<AuthoringDraft?> FindAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lists the drafts, most recently written first.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The drafts.</returns>
    ValueTask<IReadOnlyList<AuthoringDraft>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes a draft.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether there was one to remove.</returns>
    ValueTask<bool> RemoveAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>The answers codec: the one place a draft's answers become text and come back.</summary>
/// <remarks>
/// Hand-rolled like every other codec here, and for the same reason: an answer is one of five things - text, a whole
/// number, a flag, a list of those, or a map of those - so writing and reading them is a loop rather than a reflection over
/// a type nobody declared. A value outside that vocabulary is refused rather than stringified, because an answer that
/// cannot be read back is worse than one that was rejected while it could still be explained.
/// </remarks>
public static class AuthoringAnswers
{
    /// <summary>Writes answers as the text a draft is stored with.</summary>
    /// <param name="answers">The answers.</param>
    /// <returns>The text.</returns>
    public static string ToJson(IReadOnlyDictionary<string, object?> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteMap(writer, answers);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads answers back from the text they were stored as.</summary>
    /// <param name="json">The text.</param>
    /// <returns>The answers.</returns>
    /// <exception cref="FormatException">Thrown when the text is not a map of answers.</exception>
    public static IReadOnlyDictionary<string, object?> FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);

        return ReadMap(document.RootElement);
    }

    private static void WriteMap(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> answers)
    {
        writer.WriteStartObject();

        foreach (var (name, value) in answers)
        {
            writer.WritePropertyName(name);
            Write(writer, value);
        }

        writer.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();

                break;
            case string text:
                writer.WriteStringValue(text);

                break;
            case bool flag:
                writer.WriteBooleanValue(flag);

                break;
            case long number:
                writer.WriteNumberValue(number);

                break;
            case int number:
                writer.WriteNumberValue(number);

                break;
            case IReadOnlyDictionary<string, object?> nested:
                WriteMap(writer, nested);

                break;
            case IReadOnlyList<object?> items:
                writer.WriteStartArray();

                foreach (var item in items)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();

                break;
            default:
                throw new FormatException(
                    $"an answer of type {value.GetType().Name} cannot be stored: text, whole numbers, flags, lists and "
                    + "maps of those are what an interview asks for");
        }
    }

    private static Dictionary<string, object?> ReadMap(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            throw new FormatException("stored answers must be a map");
        }

        var answers = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var member in element.EnumerateObject())
        {
            answers[member.Name] = Read(member.Value);
        }

        return answers;
    }

    private static object? Read(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        // Boxed explicitly, because the two arms of a conditional take their common type: without the cast a whole
        // number comes back as a double, and an answer of 3 stops equalling an answer of 3.
        JsonValueKind.Number => element.TryGetInt64(out var number) ? number : (object?)element.GetDouble(),
        JsonValueKind.Array => ReadList(element),
        JsonValueKind.Object => ReadMap(element),
        _ => null,
    };

    private static List<object?> ReadList(JsonElement element)
    {
        var items = new List<object?>();

        foreach (var item in element.EnumerateArray())
        {
            items.Add(Read(item));
        }

        return items;
    }
}
