// file: Program.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp.Server;
using JonAvionics;
using JonAvionics.providers;

// ===================================================================
// === ALL TOP-LEVEL STATEMENTS (EXECUTABLE CODE) MUST COME FIRST ===
// ===================================================================

// --- Main Application Logic Starts Here ---

// 1. AIRCRAFT SELECTION
string aircraftType = "pmdg"; // Can be "fbw" or "pmdg"

// 2. WEBSOCKET SERVER SETUP
GlobalServer.Wssv = new WebSocketServer("ws://0.0.0.0:8381");
GlobalServer.Wssv.AddWebSocketService<McduDataBroadcaster>("/mcdu");
GlobalServer.Wssv.Start();
Console.WriteLine("Main Server: WebSocket server started on ws://localhost:8381");

// 3. HTTP SERVER SETUP
var exePath = AppDomain.CurrentDomain.BaseDirectory;
var frontendPath = Path.GetFullPath(Path.Combine(exePath, @"..\..\..\frontend"));
string defaultHtmlFile = (aircraftType == "fbw") ? "mcdu_fbw.html" : "mcdu_pmdg.html";
var httpFileServer = new HttpFileServer("http://localhost:8000/", frontendPath, defaultHtmlFile);
// Start the HTTP server as a fire-and-forget background task
Task.Run(() => httpFileServer.Start());

// 4. DATA PROVIDER SETUP
IFmsDataProvider provider;
if (aircraftType == "fbw")
{
    Console.WriteLine("Main Server: Starting provider for 'fbw'");
    provider = new FbwDataProvider();
}
else if (aircraftType == "pmdg")
{
    Console.WriteLine("Main Server: Starting provider for 'pmdg'");
    var config = new PmdgConfigurator();
    config.EnsureSdkEnabled();
    provider = new PmdgDataProvider();
}
else
{
    Console.WriteLine($"Error: Unknown aircraft type '{aircraftType}'.");
    // Wait for a key press so the user can see the error before exiting.
    Console.ReadKey();
    return;
}

// Subscribe to the provider's data event.
provider.OnDataReceived += (jsonData) => {
    // Use the ?. null-conditional operator for safety in a multithreaded environment
    GlobalServer.Wssv?.WebSocketServices["/mcdu"]?.Sessions.Broadcast(jsonData);
};
// Start the provider as a fire-and-forget background task
Task.Run(() => provider.Start()); 

// 5. KEEP APPLICATION ALIVE
Console.WriteLine("All services started. The application is now running.");
Console.WriteLine($"Navigate your browser to http://localhost:8000");
// This blocks the main thread and keeps the application running until you press a key or Ctrl+C.
Console.ReadKey();


// ============================================================================
// === ALL TYPE DECLARATIONS (CLASSES) MUST COME AT THE END OF THE FILE      ===
// ============================================================================

public static class GlobalServer
{
    public static WebSocketServer? Wssv;
}

public class McduDataBroadcaster : WebSocketBehavior
{
    // This class is intentionally empty for this implementation.
    // WebSocketSharp uses it to create and manage sessions for the "/mcdu" path.
}