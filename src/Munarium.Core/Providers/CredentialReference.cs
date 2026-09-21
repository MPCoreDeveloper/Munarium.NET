namespace Munarium.Providers;

/// <summary>
/// Where a credential lives - never the credential.
/// </summary>
/// <remarks>
/// A declaration says which environment variable or which file an adapter reads at the moment of a call. That is the
/// whole of the port's answer to a plane that needs keys: the registry is a place declarations are kept, and a registry
/// that held a credential would make it the place secrets are kept - the one place this port has said they do not go.
/// <para>
/// Exactly one of the two is present. Both are read on demand and neither is ever written back: a fingerprint or a
/// <c>credential_ok</c> flag is what leaves here.
/// </para>
/// </remarks>
public sealed record CredentialReference
{
    /// <summary>Gets the environment variable the credential is read from.</summary>
    public string? EnvironmentVariable { get; init; }

    /// <summary>Gets the file the credential is read from.</summary>
    public string? FilePath { get; init; }

    /// <summary>Names an environment variable.</summary>
    /// <param name="name">The variable's name.</param>
    /// <returns>The reference.</returns>
    public static CredentialReference ForEnvironment(string name) =>
        new() { EnvironmentVariable = name };

    /// <summary>Names a file.</summary>
    /// <param name="path">The file's path.</param>
    /// <returns>The reference.</returns>
    public static CredentialReference ForFile(string path) =>
        new() { FilePath = path };

    /// <summary>Gets how a person reads where the credential comes from - never its value.</summary>
    public string Describe =>
        EnvironmentVariable is { Length: > 0 } variable
            ? $"credential environment variable '{variable}'"
            : $"credential file '{FilePath}'";
}

/// <summary>Whether a credential resolves right now, and what to say when it does not.</summary>
/// <param name="Resolved">Whether a non-empty credential was found.</param>
/// <param name="Detail">Why not, in words that name the location and never the material.</param>
public readonly record struct CredentialResolution(bool Resolved, string Detail);

/// <summary>
/// Reading a credential from where a declaration says it lives.
/// </summary>
/// <remarks>
/// A seam rather than a static read, for the same reason every other external fact here is one: a test decides what the
/// environment and the filesystem answer without setting a process-wide variable underneath the tests that run beside
/// it. <see cref="Process"/> is what a deployment is composed with.
/// </remarks>
/// <param name="environment">Reads an environment variable, or answers <see langword="null"/>.</param>
/// <param name="file">Reads a file, or answers <see langword="null"/> when it cannot be read.</param>
public sealed class ProviderCredentials(
    Func<string, string?> environment,
    Func<string, string?> file)
{
    private readonly Func<string, string?> _environment =
        environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly Func<string, string?> _file = file ?? throw new ArgumentNullException(nameof(file));

    /// <summary>The process itself: its environment and its filesystem.</summary>
    /// <returns>The resolver a deployment is composed with.</returns>
    public static ProviderCredentials Process() => new(
        Environment.GetEnvironmentVariable,
        path =>
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });

    /// <summary>Resolves a reference, so a probe can say whether the key is where the declaration says it is.</summary>
    /// <param name="reference">The reference, or <see langword="null"/> when the declaration names none.</param>
    /// <returns>Whether it resolved, and why not when it did not.</returns>
    public CredentialResolution Resolve(CredentialReference? reference)
    {
        if (reference is null)
        {
            return new CredentialResolution(false, "the declaration names no credential");
        }

        var value = reference.EnvironmentVariable is { Length: > 0 } variable
            ? _environment(variable)
            : _file(reference.FilePath!);

        return string.IsNullOrEmpty(value)
            ? new CredentialResolution(false, $"{reference.Describe} is not set")
            : new CredentialResolution(true, $"{reference.Describe} resolves");
    }
}
