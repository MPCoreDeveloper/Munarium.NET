namespace Munarium.Store.SharpCoreDb;

using Munarium.Ledger;
using Munarium.Retrieval;

/// <summary>
/// A deployment's index instances over SharpCoreDB's indexes: one live, and the ones a build created.
/// </summary>
/// <remarks>
/// The instances are held for the life of the process, and a cutover only moves which one answers. That is deliberate:
/// the version that stopped serving has to keep answering the questions already in flight and has to stay readable so
/// an envelope issued while it was live can still be explained - a cutover that freed it would make yesterday's
/// citations unresolvable, which is the opposite of what an index version is for.
/// <para>
/// A retrieval engine is not thread-safe, so every access to the serving instance goes through one lock. The lock is
/// held for the length of a reference read and not for the length of a query: a question is asked against the instance
/// it was handed, and a cutover that happens while it runs does not move the ground under it.
/// </para>
/// </remarks>
public sealed class SharpCoreDbIndexHost : IIndexHost, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SharpCoreDbRetriever> _built = [];
    private readonly int _dimensions;
    private SharpCoreDbRetriever _serving;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharpCoreDbIndexHost"/> class.
    /// </summary>
    /// <param name="dimensions">The embedding width every instance builds with.</param>
    /// <param name="initialVersion">The version the deployment starts by serving.</param>
    /// <param name="watermark">The ledger position the initial index reflects.</param>
    /// <param name="kind">The vector engine to build every instance with.</param>
    public SharpCoreDbIndexHost(
        int dimensions,
        string initialVersion,
        SequenceNumber watermark,
        VectorIndexKind kind = VectorIndexKind.Exact)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialVersion);

        Kind = kind;
        Engine = VectorIndexEngines.Of(kind);
        _dimensions = dimensions;
        _serving = Open(initialVersion, watermark);
        _built[initialVersion] = _serving;
    }

    /// <summary>Gets the vector engine every instance was built with.</summary>
    public VectorIndexKind Kind { get; }

    /// <summary>Gets the versions built here, which is what a deployment can cut over to.</summary>
    public IReadOnlyCollection<string> Versions
    {
        get
        {
            lock (_gate)
            {
                return [.. _built.Keys];
            }
        }
    }

    /// <inheritdoc />
    public string Engine { get; }

    /// <inheritdoc />
    public string ServingVersion
    {
        get
        {
            lock (_gate)
            {
                return _serving.IndexVersion;
            }
        }
    }

    /// <inheritdoc />
    public IIndexWriter ServingWriter
    {
        get
        {
            lock (_gate)
            {
                return _serving;
            }
        }
    }

    /// <inheritdoc />
    public IRetrievalBackend ServingReader
    {
        get
        {
            lock (_gate)
            {
                return _serving;
            }
        }
    }

    /// <inheritdoc />
    public IRetrievalBackend? ReaderFor(string indexVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersion);

        lock (_gate)
        {
            return _built.TryGetValue(indexVersion, out var built) ? built : null;
        }
    }

    /// <inheritdoc />
    public IndexInstance Build(string indexVersion, SequenceNumber watermark)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersion);

        lock (_gate)
        {
            if (!_built.TryGetValue(indexVersion, out var built))
            {
                built = Open(indexVersion, watermark);
                _built[indexVersion] = built;
            }

            return new IndexInstance(indexVersion, built, built);
        }
    }

    /// <inheritdoc />
    public bool Serve(string indexVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersion);

        lock (_gate)
        {
            if (!_built.TryGetValue(indexVersion, out var instance))
            {
                return false;
            }

            _serving = instance;

            return true;
        }
    }

    /// <inheritdoc />
    public bool Discard(string indexVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexVersion);

        lock (_gate)
        {
            // Dropping what is serving would leave the deployment answering from nothing; that is a cutover to another
            // version, not a discard.
            if (string.Equals(indexVersion, _serving.IndexVersion, StringComparison.Ordinal))
            {
                return false;
            }

            if (!_built.Remove(indexVersion, out var dropped))
            {
                return false;
            }

            dropped.Dispose();

            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var instance in _built.Values)
            {
                instance.Dispose();
            }

            _built.Clear();
        }
    }

    private SharpCoreDbRetriever Open(string indexVersion, SequenceNumber watermark) =>
        new(_dimensions, indexVersion, watermark, Kind);
}
