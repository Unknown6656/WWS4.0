using System.Text;
using System;


namespace WWS
{
    /// <summary>
    /// Represents an indexing delegate which returns a reference to the indexed value
    /// </summary>
    /// <typeparam name="I">Generic index type</typeparam>
    /// <typeparam name="V">Generic value type</typeparam>
    /// <param name="arg">Index</param>
    /// <returns>Return value reference</returns>
    public delegate ref V RefFunc<in I, V>(I arg);


    /// <summary>
    /// Represents a general webserver
    /// </summary>
    /// <typeparam name="T">Generic webserver type</typeparam>
    public interface IWebServer<out T>
        where T : IWSConfiguration
    {
        /// <summary>
        /// Inidicates, whether the server is running
        /// </summary>
        bool IsRunning { get; }
        /// <summary>
        /// The webserver's configuration
        /// </summary>
        T Configuration { get; }

        /// <summary>
        /// Starts the server
        /// </summary>
        void Start();
        /// <summary>
        /// Stops the server
        /// </summary>
        void Stop();
    }

    /// <summary>
    /// Represents a general webserver configuration
    /// </summary>
    public interface IWSConfiguration
    {
        /// <summary>
        /// The optional HTTPS configuration (a value of 'null' represents a HTTP server)
        /// </summary>
        WWSHTTPSConfiguration? HTTPS { set; get; }
        /// <summary>
        /// Use the HTTP/HTTPS header value 'upgrade' instead of 'keep-alive'
        /// </summary>
        bool UseConnectionUpgrade { set; get; }
        /// <summary>
        /// The maximum client-side caching age for the server's HTML pages (default = 0)
        /// </summary>
        ushort CachingAge { set; get; }
        /// <summary>
        /// The server's string (i.e. its name)
        /// </summary>
        string ServerString { set; get; }
        /// <summary>
        /// The listening path (default is empty)
        /// </summary>
        string ListeningPath { set; get; }
        /// <summary>
        /// The TCP/UDP port, on which the server is listening
        /// </summary>
        ushort ListeningPort { set; get; }
        /// <summary>
        /// The text encoding which should be used
        /// </summary>
        Encoding TextEncoding { set; get; }
    }

    /// <summary>
    /// Represents an abstract read-only indexer
    /// </summary>
    /// <typeparam name="I">Indexing type</typeparam>
    /// <typeparam name="V">Value type</typeparam>
    public interface IReadOnly<in I, out V>
    {
        /// <summary>
        /// The underlying (read-only) value associated with the given index
        /// </summary>
        /// <param name="i">Index</param>
        /// <returns>The value associated with the given index</returns>
        V this[I i] { get; }
    }

    /// <summary>
    /// Represents an abstract write-only indexer
    /// </summary>
    /// <typeparam name="I">Indexing type</typeparam>
    /// <typeparam name="V">Value type</typeparam>
    public interface IWriteOnly<in I, in V>
    {
        /// <summary>
        /// The underlying (write-only) value associated with the given index
        /// </summary>
        /// <param name="i">Index</param>
        /// <returns>The value associated with the given index</returns>
        V this[I i] { set; }
    }

    /// <summary>
    /// Represents a read-write-indexer
    /// </summary>
    /// <typeparam name="I">Indexing type</typeparam>
    /// <typeparam name="V">Value type</typeparam>
    public sealed class Indexer<I, V>
        : IReadOnly<I, V>
        , IWriteOnly<I, V>
    {
        internal Action<I, V> Setter { get; }
        internal Func<I, V> Getter { get; }


        /// <summary>
        /// The underlying value associated with the given index
        /// </summary>
        /// <param name="i">Index</param>
        /// <returns>The value associated with the given index</returns>
        public V this[I i]
        {
            set => Setter(i, value);
            get => Getter(i);
        }

        /// <summary>
        /// Creates a new indexer with the given getter and setter functions
        /// </summary>
        /// <param name="getter">Getter function</param>
        /// <param name="setter">Setter function</param>
        public Indexer(Func<I, V> getter, Action<I, V> setter)
        {
            Setter = setter ?? throw new ArgumentException("The setter function must not be null.", nameof(setter));
            Getter = getter ?? throw new ArgumentException("The getter function must not be null.", nameof(getter));
        }
    }

    /// <summary>
    /// Represents a by-refrence read-write-indexer
    /// </summary>
    /// <typeparam name="I">Indexing type</typeparam>
    /// <typeparam name="V">Value type</typeparam>
    public sealed class ByReferenceIndexer<I, V>
    {
        internal RefFunc<I, V> Getter { get; }


        /// <summary>
        /// A reference to the underlying value associated with the given index
        /// </summary>
        /// <param name="i">Index</param>
        /// <returns>The value associated with the given index</returns>
        public ref V this[I i] => ref Getter(i);

        /// <summary>
        /// Creates a new indexer with the given by-reference getter function
        /// </summary>
        /// <param name="getter">By-reference getter function</param>
        public ByReferenceIndexer(RefFunc<I, V> getter) => Getter = getter ?? throw new ArgumentException("The getter function must not be null.", nameof(getter));

        /// <summary>
        /// Converts the given by-reference indexer to a by-value one
        /// </summary>
        /// <param name="refindexer">By-reference indexer</param>
        public static implicit operator Indexer<I, V>(ByReferenceIndexer<I, V> refindexer) => new Indexer<I, V>(i => refindexer[i], (i, v) => refindexer[i] = v);
    }

    /// <summary>
    /// Represents a read-only indexer
    /// </summary>
    /// <typeparam name="I">Indexing type</typeparam>
    /// <typeparam name="V">Value type</typeparam>
    public sealed class ReadOnlyIndexer<I, V>
        : IReadOnly<I, V>
    {
        internal Func<I, V> Getter { get; }


        /// <inheritdoc/>
        public V this[I i] => Getter(i);

        /// <summary>
        /// Creates a new read-only indexer with the given getter function
        /// </summary>
        /// <param name="getter">Getter function</param>
        public ReadOnlyIndexer(Func<I, V> getter) => Getter = getter ?? throw new ArgumentException("The getter function must not be null.", nameof(getter));
    }

    /// <summary>
    /// Represents a write-only indexer
    /// </summary>
    /// <typeparam name="I">Indexing type</typeparam>
    /// <typeparam name="V">Value type</typeparam>
    public sealed class WriteOnlyIndexer<I, V>
        : IWriteOnly<I, V>
    {
        internal Action<I, V> Setter { get; }


        /// <inheritdoc/>
        public V this[I i]
        {
            set => Setter(i, value);
        }

        /// <summary>
        /// Creates a new write-only indexer with the given getter function
        /// </summary>
        /// <param name="setter">Setter function</param>
        public WriteOnlyIndexer(Action<I, V> setter) => Setter = setter ?? throw new ArgumentException("The setter function must not be null.", nameof(setter));
    }
}
