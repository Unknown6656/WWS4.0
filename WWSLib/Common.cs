using System.Collections.ObjectModel;
using System.Collections.Generic;
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
    /// Represents a WWS4.0™ compatible webserver event
    /// </summary>
    /// <typeparam name="T">Generic webserver type</typeparam>
    /// <param name="server">The server raising the event</param>
    public delegate void ServerEvent<in T>(T server)
        where T : WebServer<T>;

    /// <summary>
    /// Represents a WWS4.0™ compatible webserver event
    /// </summary>
    /// <typeparam name="T">Generic webserver type</typeparam>
    /// <typeparam name="A">Generic argument type</typeparam>
    /// <param name="server">The server raising the event</param>
    /// <param name="argument">The event argument</param>
    public delegate void ServerEvent<in T, in A>(T server, A argument)
        where T : WebServer<T>;


    /// <summary>
    /// Represents an abstract webserver
    /// </summary>
    /// <typeparam name="T">Generic webserver type</typeparam>
    public abstract class WebServer<T>
        where T : WebServer<T>
    {
        /// <summary>
        /// Raised before the server is being stopped.
        /// </summary>
        public virtual event ServerEvent<T> OnStopping;
        /// <summary>
        /// Raised after the server has stopped.
        /// </summary>
        public virtual event ServerEvent<T> OnStopped;
        /// <summary>
        /// Raised before the server is being started.
        /// </summary>
        public virtual event ServerEvent<T> OnStarting;
        /// <summary>
        /// Raised after the server has started.
        /// </summary>
        public virtual event ServerEvent<T> OnStarted;

        /// <summary>
        /// Inidicates, whether the server is running
        /// </summary>
        public virtual bool IsRunning { get; protected set; }

        /// <summary>
        /// Starts the server
        /// </summary>
        public abstract void Start();
        /// <summary>
        /// Stops the server
        /// </summary>
        public abstract void Stop();

        private protected void __OnStopping(T server) => OnStopping?.Invoke(server);
        private protected void __OnStopped(T server) => OnStopped?.Invoke(server);
        private protected void __OnStarting(T server) => OnStarting?.Invoke(server);
        private protected void __OnStarted(T server) => OnStarted?.Invoke(server);
    }

    /// <summary>
    /// Represents a general webserver with a configuration
    /// </summary>
    /// <typeparam name="S">Generic webserver configuration data type</typeparam>
    public interface IConfigurableWebServer<out S>
        where S : IWSConfiguration
    {
        /// <summary>
        /// The webserver's configuration
        /// </summary>
        S Configuration { get; }
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
        /// Use the HTTP/HTTPS header value 'upgrade' to indicate a possible HTTP/1.1 -> HTTP/2 upgrade
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

    /// <summary>
    /// Represents an exception thrown due to an invalid server configuration
    /// </summary>
    public sealed class InvalidConfigurationException
        : InvalidOperationException
    {
        /// <summary>
        /// Creates a new instance containing the given message
        /// </summary>
        /// <param name="msg">Exception message</param>
        public InvalidConfigurationException(string msg)
            : base(msg)
        {
        }
    }

    /// <summary>
    /// Represents a generic dictionary which has a default value for elements which could not be found
    /// </summary>
    /// <typeparam name="K">Generic key type</typeparam>
    /// <typeparam name="V">Generic value type</typeparam>
    public sealed class DefaultDictionary<K, V>
        : Dictionary<K, V>
    {
        /// <summary>
        /// Default value
        /// </summary>
        public V DefaultValue { get; }

        /// <inheritdoc cref="Dictionary{K,V}.this"/>
        public new V this[K index]
        {
            get => TryGetValue(index, out V v) ? v : DefaultValue;
            set => base[index] = value;
        }

        /// <summary>
        /// Creates a new dictionary with the given default value
        /// </summary>
        /// <param name="default">Default value</param>
        public DefaultDictionary(V @default) => DefaultValue = @default;
    }

    /// <summary>
    /// Represents a generic read-only dictionary which has a default value for elements which could not be found
    /// </summary>
    /// <typeparam name="K">Generic key type</typeparam>
    /// <typeparam name="V">Generic value type</typeparam>
    public sealed class DefaultReadOnlyDictionary<K, V>
        : ReadOnlyDictionary<K, V>
    {
        /// <summary>
        /// Default value
        /// </summary>
        public V DefaultValue { get; }

        /// <inheritdoc cref="ReadOnlyDictionary{K,V}.this"/>
        public new V this[K index] => TryGetValue(index, out V v) ? v : DefaultValue;

        /// <summary>
        /// Creates a new read-only dictionary with the given default value
        /// </summary>
        /// <param name="dic">Base dictionary</param>
        /// <param name="default">Default value</param>
        public DefaultReadOnlyDictionary(IDictionary<K, V> dic, V @default)
            : base(dic) => DefaultValue = @default;
    }
}
