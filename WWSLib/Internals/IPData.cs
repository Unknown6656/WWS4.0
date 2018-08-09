using System.Threading.Tasks;
using System.Linq;
using System.Net;
using System;


namespace WWS.Internals
{
    /// <summary>
    /// Represents an extended information set about an IP address and/or host name
    /// </summary>
    public sealed class IPData
    {
        private const string API_URL = @"http://ip-api.com/line/";
        private const int DELAY_MS = 750;

        private static readonly IPAddress[] _locals = { System.Net.IPAddress.Any, System.Net.IPAddress.Broadcast, System.Net.IPAddress.IPv6Any, System.Net.IPAddress.IPv6Loopback, System.Net.IPAddress.Loopback, System.Net.IPAddress.None };
        private static readonly WebClient _wc = new WebClient();
        private static DateTime _lastfetch = DateTime.MinValue;


        /// <summary>
        /// The host's country
        /// </summary>
        public string Country { get; private set; }
        /// <summary>
        /// The host's country code
        /// </summary>
        public string CountryCode { get; private set; }
        /// <summary>
        /// The host's region code
        /// </summary>
        public string RegionCode { get; private set; }
        /// <summary>
        /// The host's region
        /// </summary>
        public string Region { get; private set; }
        /// <summary>
        /// The host's city
        /// </summary>
        public string City { get; private set; }
        /// <summary>
        /// The host's zip code
        /// </summary>
        public int ZipCode { get; private set; }
        /// <summary>
        /// The host's geographic latitude
        /// </summary>
        public double Latitude { get; private set; }
        /// <summary>
        /// The host's geographic longitude
        /// </summary>
        public double Longitude { get; private set; }
        /// <summary>
        /// The host's geographic timezone
        /// </summary>
        public string Timezone { get; private set; }
        /// <summary>
        /// The host's ISP name
        /// </summary>
        public string ISPName { get; private set; }
        /// <summary>
        /// The host's organization name
        /// </summary>
        public string OrganizationName { get; private set; }
        /// <summary>
        /// The host's alias name
        /// </summary>
        public string Alias { get; private set; }
        /// <summary>
        /// The host's IP address
        /// </summary>
        public string IPAddress { get; private set; }


        private IPData()
        {
        }

        /// <summary>
        /// Tries to fetch the <see cref="IPData"/> from the given host address or host name and returns the fetch result
        /// </summary>
        /// <param name="host">IP address or host name</param>
        /// <returns>Fetch result -- or 'null' if the host could not be resolved</returns>
        public static async Task<IPData> TryFetchIPDataAsync(string host)
        {
            TimeSpan tdiff = DateTime.Now - _lastfetch;
            IPHostEntry resh = await Dns.GetHostEntryAsync(host);

            if (tdiff.TotalMilliseconds < DELAY_MS)
                await Task.Delay((int)(DELAY_MS - tdiff.TotalMilliseconds));

            try
            {
                string[] res;

                if (resh.AddressList.Any(addr => addr.IsIPv6LinkLocal
                                              || addr.IsIPv6Multicast
                                              || addr.IsIPv6SiteLocal
                                              || _locals.Contains(addr)))
                    host = "";

                lock (_wc)
                    res = _wc.DownloadString(API_URL + (host ?? "")).Split('\n');

                _lastfetch = DateTime.Now;

                return new IPData
                {
                    Country = res[1],
                    CountryCode = res[2],
                    RegionCode = res[3],
                    Region = res[4],
                    City = res[5],
                    ZipCode = int.Parse(res[6]),
                    Latitude = double.Parse(res[7]),
                    Longitude = double.Parse(res[8]),
                    Timezone = res[9],
                    ISPName = res[10],
                    OrganizationName = res[11],
                    Alias = res[12],
                    IPAddress = res[13],
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
