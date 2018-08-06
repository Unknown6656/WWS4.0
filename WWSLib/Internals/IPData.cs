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



        public string Country { get; private set; }
        public string CountryCode { get; private set; }
        public string RegionCode { get; private set; }
        public string Region { get; private set; }
        public string City { get; private set; }
        public int ZipCode { get; private set; }
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        public string Timezone { get; private set; }
        public string ISPName { get; private set; }
        public string OrganizationName { get; private set; }
        public string Alias { get; private set; }
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
