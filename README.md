# Wonky Webstack 4.0â„¢

A library for a simple HTTP and HTTPS webserver.

<img height="150" src="wonky-certificate.jpg"/>

The usage of this libary is fairly simple:

# WonkyWebServer™
A webserver which processes requests upon event firing.

```csharp
using WWS;


public static void Main(string[] args)
{
    var srv = new WonkyWebServer();

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
```

The webserver can be configured as follows:
```csharp
var srv = new WonkyWebServer();

srv.Configuration = WWSConfiguration.DefaultHTTPConfiguration; // use the default configuration for HTTP (listening on port 8080)
srv.srv.OnIncomingRequest += ...
srv.Start(); // start the server
```

or:
```csharp
var srv = new WonkyWebServer(new WWSConfiguration
{
    HTTPS = new WWSHTTPSConfiguration        // use HTTPS
    {
        X509_CertificatePath = "server.cer", // certificate path
    },
    ListeningPort = 2000,                    // Listen on port 2000
    CachingAge = 60,                         // This website should be cached for a maximum of 60sec
    ListeningPath = "virtual_server1/",      // Only listen on requests beginning with
                                             //  https://hostname:2000/virtual_server1/
    ServerString = "my custom server"        // The server string
});

...
```

# DodgyWebServer™
A file-based webserver with scripting capabilities (like a PHP server).

