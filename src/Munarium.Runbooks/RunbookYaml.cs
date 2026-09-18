namespace Munarium.Runbooks;

using System.Globalization;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Reading a YAML document's nodes, the way the JSON codecs read theirs.
/// </summary>
/// <remarks>
/// The representation model rather than deserialization: a mapping that is written out by hand can be read against the
/// original's own, and nothing about a document depends on a reflection-based binding that a later runtime could change.
/// <para>
/// Every failure is a <see cref="FormatException"/> whose message is what a person reads, because the only thing a
/// caller does with a malformed document is show it to whoever wrote it.
/// </para>
/// </remarks>
internal static class RunbookYaml
{
    /// <summary>Reads a mapping member, or <see langword="null"/> when it is absent.</summary>
    /// <param name="mapping">The mapping, or <see langword="null"/>.</param>
    /// <param name="name">The member's name.</param>
    /// <returns>The member's node, or <see langword="null"/>.</returns>
    internal static YamlNode? Member(YamlMappingNode? mapping, string name) =>
        mapping is not null && mapping.Children.TryGetValue(new YamlScalarNode(name), out var value) ? value : null;

    /// <summary>Reads a member that has to be a mapping.</summary>
    /// <param name="mapping">The mapping, or <see langword="null"/>.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <returns>The mapping, or <see langword="null"/> when the member is absent.</returns>
    internal static YamlMappingNode? Map(YamlMappingNode? mapping, string name, string path) =>
        Member(mapping, name) is { } member ? AsMapping(member, path) : null;

    /// <summary>Reads a node as a mapping.</summary>
    /// <param name="node">The node.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <returns>The mapping.</returns>
    /// <exception cref="FormatException">Thrown when the node is not a mapping.</exception>
    internal static YamlMappingNode AsMapping(YamlNode node, string path) =>
        node as YamlMappingNode ?? throw new FormatException($"{path} must be a mapping");

    /// <summary>Reads a member that has to be a sequence.</summary>
    /// <param name="mapping">The mapping, or <see langword="null"/>.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <returns>The items, or an empty list when the member is absent.</returns>
    internal static IReadOnlyList<YamlNode> Items(YamlMappingNode? mapping, string name, string path) =>
        Member(mapping, name) is { } member ? AsItems(member, path) : [];

    /// <summary>Reads a node as a sequence's items.</summary>
    /// <param name="node">The node.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <returns>The items.</returns>
    /// <exception cref="FormatException">Thrown when the node is not a sequence.</exception>
    internal static IReadOnlyList<YamlNode> AsItems(YamlNode node, string path) =>
        node is YamlSequenceNode sequence
            ? [.. sequence.Children]
            : throw new FormatException($"{path} must be a list");

    /// <summary>Reads a member that has to be a list of strings.</summary>
    /// <param name="mapping">The mapping, or <see langword="null"/>.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <returns>The values, or an empty list when the member is absent.</returns>
    internal static IReadOnlyList<string> Strings(YamlMappingNode? mapping, string name, string path) =>
        [.. Items(mapping, name, path).Select(item => Text(item, path))];

    /// <summary>Reads a node as text.</summary>
    /// <param name="node">The node, or <see langword="null"/>.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <returns>The text.</returns>
    /// <exception cref="FormatException">Thrown when the node is not a scalar.</exception>
    internal static string Text(YamlNode? node, string path) =>
        node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : throw new FormatException($"{path} must be text");

    /// <summary>Reads a member as text, or <see langword="null"/> when it is absent.</summary>
    /// <param name="mapping">The mapping, or <see langword="null"/>.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    internal static string? OptionalText(YamlMappingNode? mapping, string name, string path) =>
        Member(mapping, name) is { } member ? Text(member, path) : null;

    /// <summary>
    /// Reads a member as an integer.
    /// </summary>
    /// <param name="mapping">The mapping, or <see langword="null"/>.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <returns>The number, or <see langword="null"/> when the member is absent.</returns>
    /// <exception cref="FormatException">Thrown when the member is present and is not an integer.</exception>
    internal static long? Integer(YamlMappingNode? mapping, string name, string path) =>
        Member(mapping, name) is { } member ? IntegerOf(member, path) : null;

    private static long IntegerOf(YamlNode member, string path) =>
        long.TryParse(Text(member, path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{path} must be an integer");

    /// <summary>
    /// Reads a member as a number, integral or fractional.
    /// </summary>
    /// <param name="mapping">The mapping, or <see langword="null"/>.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <returns>The number, or <see langword="null"/> when the member is absent.</returns>
    /// <exception cref="FormatException">Thrown when the member is present and is not a number.</exception>
    internal static double? Number(YamlMappingNode? mapping, string name, string path) =>
        Member(mapping, name) is { } member ? NumberOf(member, path) : null;

    private static double NumberOf(YamlNode member, string path) =>
        double.TryParse(Text(member, path), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{path} must be a number");

    /// <summary>Reads a member as a flag.</summary>
    /// <param name="mapping">The mapping, or <see langword="null"/>.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <returns>The flag, or <see langword="null"/> when the member is absent.</returns>
    /// <exception cref="FormatException">Thrown when the member is present and is not a flag.</exception>
    internal static bool? Boolean(YamlMappingNode? mapping, string name, string path)
    {
        if (Member(mapping, name) is not { } member)
        {
            return null;
        }

        // The document is authored by hand, so the common spellings are read rather than only the two a serializer would
        // emit. A value that is none of them is refused: a flag is not a place to guess.
        return Text(member, path).ToLowerInvariant() switch
        {
            "true" or "yes" or "on" => true,
            "false" or "no" or "off" => false,
            _ => throw new FormatException($"{path} must be true or false"),
        };
    }

    /// <summary>
    /// Refuses a mapping that carries a field nobody reads.
    /// </summary>
    /// <remarks>
    /// The original denies unknown fields on the specs where a typo is most expensive, so a misspelled key fails closed
    /// instead of silently doing nothing. The specs only an executor reads stay open, exactly as they are there.
    /// </remarks>
    /// <param name="mapping">The mapping, or <see langword="null"/>.</param>
    /// <param name="path">The path to report, for the message.</param>
    /// <param name="allowed">The fields this reader reads.</param>
    /// <exception cref="FormatException">Thrown when an unknown field is present.</exception>
    internal static void DenyUnknown(YamlMappingNode? mapping, string path, params string[] allowed)
    {
        if (mapping is null)
        {
            return;
        }

        foreach (var key in mapping.Children.Keys)
        {
            var name = Text(key, path);

            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                throw new FormatException($"{path}: unknown field '{name}'");
            }
        }
    }
}
