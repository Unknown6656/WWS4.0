using System.Collections.Generic;
using System.Threading.Tasks;
using System.Data.SqlClient;
using System.Dynamic;
using System.Net;
using System.IO;
using System;

using WWS.Properties;


namespace WWS.Internals
{
    using dic_sd = IDictionary<string, dynamic>;


    /// <summary>
    /// Represents a connector for an T-SQL database for a WWS4.0™-compatible webserver
    /// </summary>
    public sealed class WWSDatabaseConnector
    {
        internal const string DB = "[dbo]";
        internal const string DB_HOST = DB + ".[__WWS__RemoteHosts]";
        internal const string DB_CONN = DB + ".[__WWS__Connections]";
        internal const string DB_IPDT = DB + ".[__WWS__IPData]";

        private readonly Queue<Func<Task>> _dbtasks = new Queue<Func<Task>>();
        private SqlConnection _connection;
        private bool _running;


        /// <summary>
        /// The path to the local database file
        /// </summary>
        public FileInfo DatabasePath { set; get; }
        /// <summary>
        /// The data source type (default: '(LocalDB)\MSSQLLocalDB')
        /// </summary>
        public string DataSource { set; get; } = @"(LocalDB)\MSSQLLocalDB";
        /// <summary>
        /// Indicates whether the connection to the T-SQL database is open
        /// </summary>
        public bool IsConnected => _connection != null;

        /// <summary>
        /// The event which will be raised if a T-SQL command has been issued (directly or indirectly)
        /// </summary>
        public event EventHandler<string> OnSQLCommandIssued;


        /// <summary>
        /// Creates a new connector to the given database file
        /// </summary>
        /// <param name="path">Path pointing to the database file</param>
        public WWSDatabaseConnector(string path)
            : this(new FileInfo(path))
        {
        }

        /// <summary>
        /// Creates a new connector to the given database file
        /// </summary>
        /// <param name="path">Path pointing to the database file</param>
        public WWSDatabaseConnector(FileInfo path) => DatabasePath = path ?? throw new ArgumentNullException(nameof(path));

        /// <summary>
        /// Connects asynchroniously to the database
        /// </summary>
        public async Task ConnectAsync()
        {
            if (!DatabasePath.Exists)
                throw new FileNotFoundException("The given database path could not be found or accessed.", DatabasePath.FullName);

            Disconnect();

            _connection = new SqlConnection
            {
                ConnectionString = $@"Data Source={DataSource};
                                      AttachDbFilename={DatabasePath.FullName};
                                      Integrated Security=True;
                                      Connect Timeout=30;
                                      Trusted_Connection=Yes;"
            };

            await _connection.OpenAsync();

            PublicUtils.RunAsync(async () => await LoggerLoop());

            _running = true;
        }

        private async Task LoggerLoop()
        {
            List<Func<Task>> tasks = new List<Func<Task>>();

            while (_running)
            {
                tasks.Clear();

                lock (_dbtasks)
                    while (_dbtasks.Count > 0)
                        tasks.Add(_dbtasks.Dequeue());

                foreach (Func<Task> task in tasks)
                    await task();
            }
        }

        /// <summary>
        /// Disconnects from the database
        /// </summary>
        public void Disconnect()
        {
            if (!IsConnected)
                return;

            _running = false;
            _connection.Close();
            _connection.Dispose();
            _connection = null;
        }

        /// <summary>
        /// Resets the database to its WWS4.0™ defaults and clears all table data
        /// </summary>
        public async Task ResetDatabase() => await ExecuteAsync($@"
DROP TABLE {DB_HOST};
DROP TABLE {DB_CONN};
DROP TABLE {DB_IPDT};
DROP DATABASE {DB};
CREATE DATABASE {DB} ON (
Filename= '{DatabasePath}'
) FOR attach;
{Resources.dbo___WWS__RemoteHosts}
{Resources.dbo___WWS__Connections}
{Resources.dbo___WWS__IPData}
");

        /// <summary>
        /// Executes the given T-SQL command and returns its results as row-wise enumeration
        /// </summary>
        /// <param name="sql">T-SQL command string</param>
        /// <returns>Command or query result</returns>
        public dic_sd[] Execute(string sql) => PublicUtils.RunSync(async () => await ExecuteAsync(sql));

        /// <summary>
        /// Executes the given T-SQL command and returns its results as row-wise enumeration
        /// </summary>
        /// <param name="sql">T-SQL command string</param>
        /// <returns>Command or query result</returns>
        public async Task<dic_sd[]> ExecuteAsync(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return new dic_sd[0];

            if (!IsConnected)
                await ConnectAsync();

            List<dic_sd> rows = new List<dic_sd>();

            OnSQLCommandIssued?.Invoke(this, sql);

            using (SqlCommand cmd = new SqlCommand(sql, _connection))
            using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                {
                    dic_sd dyn = new ExpandoObject();

                    for (int i = 0; i < reader.FieldCount; i++)
                        dyn[reader.GetName(i)] = reader[i];

                    rows.Add(dyn);
                }

            return rows.ToArray();
        }

        /// <summary>
        /// Logs the given application to the internal database
        /// </summary>
        /// <param name="req">WWS4.0™ connection request</param>
        /// <param name="resp">WWS4.0™ connection response</param>
        public void LogConnection(WWSRequest req, WWSResponse resp)
        {
            lock(_dbtasks)
                _dbtasks.Enqueue(() => LogConnectionAsync(req, resp));
        }

        private async Task LogConnectionAsync(WWSRequest req, WWSResponse resp)
        {
            string oip = req.Sender.Address.ToString();
            string ip = SQLEscape(oip);
            string ua = SQLEscape(req.UserAgent);

            dic_sd[] res = await ExecuteAsync($@"
DECLARE @host_id BIGINT = (SELECT [ID]
							FROM {DB_HOST}
							WHERE [IPAddress] LIKE '{ip}'
							AND [UserAgent] LIKE '{ua}');

IF @host_id IS NULL
	BEGIN
		SET @host_id = (SELECT ISNULL(MAX([ID]) + 1, 0)
						FROM {DB_HOST});

		INSERT INTO {DB_HOST} (ID, IPAddress, UserAgent)
		VALUES (@host_id, '{ip}', '{ua}')
	END

DECLARE @conn_id BIGINT = (SELECT ISNULL(MAX([ID]) + 1, 0)
						   FROM {DB_CONN});

INSERT INTO {DB_CONN} (ID, HostID, TimestampUTC, RequestedURI, OriginalURI, Cookies, HTTPMethod, StatusCode, ReturnLength, RemotePort)
VALUES (
	@conn_id,
	@host_id,
	{SQLEscape(req.UTCRequestTime)},
	'{SQLEscape(req.RequestedURL.ToString())}',
	'{SQLEscape(req.OriginalURL.ToString())}',
    '{req.CookieString}',
	'{SQLEscape(req.HTTPRequestMethod)}',
    {(int)resp.StatusCode},
    {resp.Length},
    {req.Sender.Port}
);

SELECT @host_id hid, (SELECT COUNT(ipd.HostID)
                      FROM {DB_IPDT} ipd
                      WHERE ipd.HostID = @host_id
                      AND ipd.LastUpdateUTC > {SQLEscape(req.UTCRequestTime.Subtract(TimeSpan.FromMinutes(10)))}) cont;
");

            if (res[0]["cont"] == 0)
                if (await IPData.TryFetchIPDataAsync(oip) is IPData dat)
                {
                    IPHostEntry host = await Dns.GetHostEntryAsync(req.Sender.Address);
                    long uid = res[0]["hid"];

                    await ExecuteAsync($@"
INSERT INTO {DB_IPDT}
VALUES (
    {uid},
    '{SQLEscape(dat.ISPName)}',
    '{SQLEscape(dat.OrganizationName)}',
    '{dat.CountryCode}',
    '{SQLEscape(dat.Country)}',
    '{dat.RegionCode}',
    '{SQLEscape(dat.Region)}',
    '{SQLEscape(dat.City)}',
    {dat.ZipCode},
    {dat.Latitude:0.0},
    {dat.Longitude:0.0},
    '{SQLEscape(dat.Timezone)}',
    '{SQLEscape(dat.Alias)}',
    '{SQLEscape(host.HostName)}',
    {SQLEscape(DateTime.UtcNow)}
);
");
                }
        }

        /// <summary>
        /// Returns the next free ID for the given table and column name
        /// </summary>
        /// <param name="db">Table name</param>
        /// <param name="idcol">Column name (default: '[ID]')</param>
        /// <returns>Next free ID value (not guaranteed to be thread-safe after return)</returns>
        public async Task<long> GetNextIDAsync(string db, string idcol = "[ID]")
        {
            dic_sd[] res = await ExecuteAsync($"SELECT ISNULL(MAX({idcol}) + 1, 0) i FROM {db}");

            return res.Length > 0 ? (long)res[0]["i"] : 0;
        }

        /// <summary>
        /// Escapes and quotes the given <see cref="DateTime"/>-structure in a T-SQL compatible format
        /// </summary>
        /// <param name="dt">Date/Time information</param>
        /// <returns>T-SQL string</returns>
        public static string SQLEscape(DateTime dt) => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'";

        /// <summary>
        /// Converts a T-SQL compatible Date/Time representation and parses a <see cref="DateTime"/>-structure back from it
        /// </summary>
        /// <param name="res">T-SQL compatible string</param>
        /// <returns><see cref="DateTime"/>-instance</returns>
        public static DateTime FromSQLResult(string res) => DateTime.ParseExact(res, "yyyy-MM-dd HH:mm:ss.fff", null);

        /// <summary>
        /// Escapes the given string into a T-SQL comaptible format (but does _not_ surround it with quotes)
        /// </summary>
        /// <param name="s">Input string</param>
        /// <returns>Escaped (and unqouted) T-SQL string</returns>
        public static string SQLEscape(string s) => (s ?? "").Replace('\'', '"');

        /// <summary>
        /// Unescapes the given T-SQL compatible string
        /// </summary>
        /// <param name="s">Escaped string</param>
        /// <returns>Unescaped string</returns>
        public static string SQLUnescape(string s) => (s ?? "").Replace('\"', '\'');
    }
}
