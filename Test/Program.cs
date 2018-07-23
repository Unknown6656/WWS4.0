using System.Threading;
using System;
using System.Net;

using WWS;


namespace Test
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            WonkyWebServer srv = new WonkyWebServer(new WWSConfiguration
            {
                ListeningPort = 1488,
                HTTPS =  null,
                ListeningPath = "",
                ServerString = "lelwank",
                CachingAge = 0,
                UseConnectionUpgrade = false
            });

            Console.WriteLine($"Listening on {srv.Configuration.ListeningPort}");

            srv.OnIncomingRequest += r => (HttpStatusCode.OK, $@"
<html>
    <head>
        <title>TITLE</title>
    </head>
    <body>
        <h1>TITLE</h1>
        text
        <br/>
        {0 / (IntPtr.Size - IntPtr.Size)}
    </body>
</html>");
            srv.Start();

            do
                Thread.Sleep(200);
            while (Console.ReadKey(true).Key != ConsoleKey.Escape);

            srv.Stop();
        }
    }
}
