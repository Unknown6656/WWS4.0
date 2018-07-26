using System.Threading;
using System.Net;
using System;

using WWS;


namespace Test
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            WWSConfiguration config = WWSConfiguration.DefaultHTTPSConfiguration;

            config.HTTPS = new WWSHTTPSConfiguration
            {
                PKCS12_Certificate = ("server.pfx", "s040-nb16".ToSecureString())
            };

            WonkyWebServer srv = new WonkyWebServer(config);
            

            srv.OnIncomingRequest += r => (HttpStatusCode.OK, $@"
<html>
    <head>
        <title>TITLE</title>
    </head>
    <body style=""font-family: monospace"">
        <h1>HELLO {r.Sender}!</h1>
        You requested <b>'{r.RequestedURL}'</b>.
        <br/>
        Your user agent: <b>'{r.UserAgent}'</b>
    </body>
</html>");

            Console.WriteLine($"Listening on {srv.Configuration.ListeningPort}. Press ESC to exit.");

            srv.Start();

            do
                Thread.Sleep(200);
            while (Console.ReadKey(true).Key != ConsoleKey.Escape);

            srv.Stop();
        }
    }
}
