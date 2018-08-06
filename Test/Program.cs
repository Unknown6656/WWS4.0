using System.Threading.Tasks;
using System.Threading;
using System;

using WWS;


namespace Test
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            DodgyWebServer srv = new DodgyWebServer(DWSConfiguration.DefaultHTTPConfiguration);

            Console.WriteLine($"Listening on {srv.Configuration.ListeningPort}. Press ESC to exit.");

            await srv.StartAsync();

            do
                Thread.Sleep(200);
            while (Console.ReadKey(true).Key != ConsoleKey.Escape);

            srv.Stop();
        }
    }
}
