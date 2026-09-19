namespace Munarium.Core.Tests.Support;

using Munarium.Ledger;
using Munarium.Retrieval;

/// <summary>
/// An <see cref="IIndexHost"/> with no engine behind it, for kernel tests.
/// </summary>
/// <remarks>
/// It keeps every instance it builds and remembers which one serves, which is what lets a test prove the two things
/// the seam is for: a build that nobody activated answers nothing, and a cutover cannot name a version that was never
/// built here.
/// </remarks>
internal sealed class FakeIndexHost(string engine = "exact@1") : IIndexHost
{
    private readonly Dictionary<string, IndexInstance> _instances = [];

    /// <summary>Gets the versions built here, in build order.</summary>
    public List<string> Built { get; } = [];

    /// <inheritdoc />
    public string Engine { get; } = engine;

    /// <inheritdoc />
    public string ServingVersion { get; private set; } = "munarium@1";

    /// <inheritdoc />
    public IIndexWriter ServingWriter => _instances.TryGetValue(ServingVersion, out var instance)
        ? instance.Writer
        : throw new InvalidOperationException($"nothing is serving '{ServingVersion}'");

    /// <inheritdoc />
    public IRetrievalBackend ServingReader => _instances.TryGetValue(ServingVersion, out var instance)
        ? instance.Reader
        : throw new InvalidOperationException($"nothing is serving '{ServingVersion}'");

    /// <summary>Gets the instances built here, by version.</summary>
    public IReadOnlyDictionary<string, IndexInstance> Instances => _instances;

    /// <inheritdoc />
    public IRetrievalBackend? ReaderFor(string indexVersion) =>
        _instances.TryGetValue(indexVersion, out var instance) ? instance.Reader : null;

    /// <inheritdoc />
    public IndexInstance Build(string indexVersion, SequenceNumber watermark)
    {
        var instance = new IndexInstance(
            indexVersion,
            new RecordingIndexWriter(indexVersion),
            new RecordingRetriever(indexVersion, watermark));

        _instances[indexVersion] = instance;
        Built.Add(indexVersion);

        return instance;
    }

    /// <inheritdoc />
    public bool Serve(string indexVersion)
    {
        if (!_instances.ContainsKey(indexVersion))
        {
            return false;
        }

        ServingVersion = indexVersion;

        return true;
    }

    /// <inheritdoc />
    public bool Discard(string indexVersion)
    {
        if (string.Equals(indexVersion, ServingVersion, StringComparison.Ordinal))
        {
            return false;
        }

        Built.Remove(indexVersion);

        return _instances.Remove(indexVersion);
    }

    /// <summary>
    /// A reader that answers from the version it was built for, and remembers what was written to it.
    /// </summary>
    /// <param name="indexVersion">The version this reader serves.</param>
    /// <param name="watermark">The ledger position it reflects.</param>
    private sealed class RecordingRetriever(string indexVersion, SequenceNumber watermark) : IRetrievalBackend
    {
        /// <summary>Gets the version this reader answers from.</summary>
        public string IndexVersion { get; } = indexVersion;

        /// <summary>Gets the watermark this reader answers at.</summary>
        public SequenceNumber Watermark { get; } = watermark;

        /// <summary>Gets how many questions were asked of it.</summary>
        public int Asked { get; private set; }

        /// <inheritdoc />
        public ValueTask<RetrievalResult> SearchAsync(
            RetrievalQuery query,
            CancellationToken cancellationToken = default)
        {
            Asked++;

            return ValueTask.FromResult(
                new RetrievalResult([], new ProvenanceEnvelope(IndexVersion, Watermark, [])));
        }
    }
}
