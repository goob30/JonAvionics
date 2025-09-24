using Microsoft.FlightSimulator.SimConnect;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace JonAvionics.providers
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct PmdgCduCell
    {
        public byte Symbol;   // PMDG encodes using its own charmap
        public byte Color;
        public byte Flags;
    }

    // Corrected structure based on SDK documentation
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct PmdgCduScreen
    {
        // SDK shows this as a 2D array: Cells[x][y] where x=columns, y=rows
        // But in C# we need to marshal it as a 1D array and access it correctly
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24 * 14)]
        public PmdgCduCell[] Cells;

        [MarshalAs(UnmanagedType.U1)]
        public bool Powered;
    }

    public class PmdgDataProvider : IFmsDataProvider
    {
        public event Action<string>? OnDataReceived;

        private SimConnect? _simConnect;
        private const int WM_USER_SIMCONNECT = 0x0402;

        private enum DEFINITIONS { PmdgCdu0 }
        private enum DATA_REQUESTS { PmdgCdu0Request }
        private enum CLIENT_DATA_ID { Cdu0 = 0x53434430 } // "SCD0"

        private static readonly Dictionary<int, string> ColorMap = new()
        {
            {0, "white"}, {1, "cyan"}, {2, "green"},
            {3, "magenta"}, {4, "amber"}, {5, "red"}
        };

        // SDK constants from the documentation
        private const byte PMDG_NG3_CDU_FLAG_SMALL_FONT = 0x01;
        private const int CDU_COLUMNS = 24;  // CDU_COLUMNS from SDK
        private const int CDU_ROWS = 14;     // CDU_ROWS from SDK

        public async System.Threading.Tasks.Task Start()
        {
            while (true)
            {
                try
                {
                    Console.WriteLine("PMDG Provider: Attempting to connect to SimConnect...");
                    _simConnect = new SimConnect("PMDG_CDU_Reader", IntPtr.Zero, WM_USER_SIMCONNECT, null, 0);

                    _simConnect.OnRecvOpen += OnRecvOpen;
                    _simConnect.OnRecvQuit += (s, e) =>
                    {
                        Console.WriteLine("PMDG Provider: MSFS has quit.");
                        _simConnect?.Dispose();
                        _simConnect = null;
                    };
                    _simConnect.OnRecvClientData += OnRecvClientData;

                    Console.WriteLine("PMDG Provider: Connected. Waiting for messages...");

                    while (_simConnect != null)
                    {
                        try { _simConnect.ReceiveMessage(); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"PMDG Provider: Error in ReceiveMessage: {ex.Message}");
                        }
                        Thread.Sleep(5);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PMDG Provider: Connection failed: {ex.Message}");
                }
                finally
                {
                    try { _simConnect?.Dispose(); } catch { }
                    _simConnect = null;
                }

                await System.Threading.Tasks.Task.Delay(5000);
            }
        }

        private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            Console.WriteLine("PMDG Provider: SimConnect Open event received.");
            try
            {
                sender.MapClientDataNameToID("PMDG_NG3_CDU_0", CLIENT_DATA_ID.Cdu0);

                sender.AddToClientDataDefinition(
                    DEFINITIONS.PmdgCdu0,
                    0,
                    (uint)Marshal.SizeOf(typeof(PmdgCduScreen)),
                    0,
                    0
                );

                sender.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, PmdgCduScreen>(DEFINITIONS.PmdgCdu0);

                sender.RequestClientData(
                    CLIENT_DATA_ID.Cdu0,
                    DATA_REQUESTS.PmdgCdu0Request,
                    DEFINITIONS.PmdgCdu0,
                    SIMCONNECT_CLIENT_DATA_PERIOD.VISUAL_FRAME,
                    SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.CHANGED,
                    0, 0, 0
                );

                Console.WriteLine("PMDG Provider: Subscribed to CDU0 data.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PMDG Provider: Failed to subscribe: {ex.Message}");
            }
        }

        private void OnRecvClientData(SimConnect sender, SIMCONNECT_RECV_CLIENT_DATA data)
        {
            if ((DATA_REQUESTS)data.dwRequestID != DATA_REQUESTS.PmdgCdu0Request) return;

            try
            {
                var screenData = (PmdgCduScreen)data.dwData[0];
                var processed = ProcessPmdgData(screenData);
                var json = JsonConvert.SerializeObject(processed);

                Console.WriteLine($"PMDG Provider: CDU JSON (first 300 chars): {json[..Math.Min(json.Length, 300)]}");
                OnDataReceived?.Invoke(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PMDG Provider: Error processing client data: {ex.Message}");
            }
        }

        // Helper method to access 2D array stored as 1D - following SDK pattern
        private PmdgCduCell GetCduCell(PmdgCduCell[] cells, int x, int y)
        {
            // SDK uses Cells[x][y] where x=column, y=row
            // In a 1D array, this translates to: index = x * CDU_ROWS + y
            int index = x * CDU_ROWS + y;
            if (index < 0 || index >= cells.Length)
                return new PmdgCduCell(); // Return empty cell if out of bounds
            return cells[index];
        }

        private McduState ProcessPmdgData(PmdgCduScreen screenData)
        {
            if (!screenData.Powered)
                return new McduState { Title = "PMDG CDU (OFF)" };

            if (screenData.Cells == null || screenData.Cells.Length < CDU_ROWS * CDU_COLUMNS)
            {
                Console.WriteLine("PMDG Provider: Invalid cell buffer in screenData.");
                return new McduState { Title = "PMDG CDU (INVALID)" };
            }

            var lines = new List<Line>();
            
            // Process each row following the SDK pattern
            for (int y = 0; y < CDU_ROWS - 1; y++) // Exclude last row (scratchpad)
            {
                var rowCells = new List<Cell>();
                var rowChars = new List<char>(); // For debug output
                
                // Process each column in this row
                for (int x = 0; x < CDU_COLUMNS; x++)
                {
                    var cduCell = GetCduCell(screenData.Cells, x, y);
                    
                    // Character mapping - keep it simple for now
                    char ch = cduCell.Symbol >= 32 && cduCell.Symbol <= 126 ? 
                             (char)cduCell.Symbol : ' ';
                    
                    string color = ColorMap.GetValueOrDefault(cduCell.Color, "white");
                    string size = (cduCell.Flags & PMDG_NG3_CDU_FLAG_SMALL_FONT) != 0 ? 
                                 "small" : "normal";

                    rowCells.Add(new Cell(ch.ToString(), color, size));
                    rowChars.Add(ch);
                }

                // Create line with full 24-character row
                lines.Add(new Line
                {
                    Left = rowCells,           // Full line in Left for compatibility
                    Center = new List<Cell>(), // Empty
                    Right = new List<Cell>()   // Empty
                });

                // Debug output to see exact row content
                var rowText = new string(rowChars.ToArray());
                Console.WriteLine($"PMDG Debug - Row {y:D2}: [{rowText}]");
            }

            // Handle scratchpad (last row)
            var scratchCells = new List<Cell>();
            var scratchChars = new List<char>();
            
            for (int x = 0; x < CDU_COLUMNS; x++)
            {
                var cduCell = GetCduCell(screenData.Cells, x, CDU_ROWS - 1);
                char ch = cduCell.Symbol >= 32 && cduCell.Symbol <= 126 ? 
                         (char)cduCell.Symbol : ' ';
                string color = ColorMap.GetValueOrDefault(cduCell.Color, "white");
                string size = (cduCell.Flags & PMDG_NG3_CDU_FLAG_SMALL_FONT) != 0 ? 
                             "small" : "normal";

                scratchCells.Add(new Cell(ch.ToString(), color, size));
                scratchChars.Add(ch);
            }

            var scratchText = new string(scratchChars.ToArray());
            Console.WriteLine($"PMDG Debug - Scratchpad: [{scratchText}]");

            return new McduState 
            { 
                Title = "PMDG CDU", 
                Lines = lines, 
                Scratchpad = scratchCells 
            };
        }
    }

    public class PmdgConfigurator
    {
        private string? FindPmdgWorkFolder()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var potentialBasePaths = new[]
            {
                Path.Combine(localAppData, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalState", "packages"),
                Path.Combine(roamingAppData, "Microsoft Flight Simulator", "Packages")
            };

            var aircraftFolders = new[] { "pmdg-aircraft-736", "pmdg-aircraft-737", "pmdg-aircraft-738", "pmdg-aircraft-739" };

            foreach (var basePath in potentialBasePaths)
                if (Directory.Exists(basePath))
                    foreach (var aircraft in aircraftFolders)
                    {
                        var workPath = Path.Combine(basePath, aircraft, "work");
                        if (Directory.Exists(workPath)) return workPath;
                    }

            return null;
        }

        public void EnsureSdkEnabled()
        {
            var workFolder = FindPmdgWorkFolder();
            if (workFolder == null) { Console.WriteLine("PMDG Config: Could not find PMDG work folder."); return; }

            var iniPath = Path.Combine(workFolder, "737_Options.ini");
            if (!File.Exists(iniPath)) { Console.WriteLine($"PMDG Config: Could not find 737_Options.ini at '{iniPath}'."); return; }

            try
            {
                var lines = File.ReadAllLines(iniPath).ToList();
                bool sdkSectionExists = lines.Any(l => l.Trim() == "[SDK]");
                bool dataBroadcast = lines.Any(l => l.Trim() == "EnableDataBroadcast=1");
                bool cduBroadcast = lines.Any(l => l.Trim() == "EnableCDUBroadcast.0=1");

                if (dataBroadcast && cduBroadcast) { Console.WriteLine("PMDG Config: SDK broadcast already enabled."); return; }

                Console.WriteLine("PMDG Config: Enabling SDK broadcast...");
                lines = lines.Where(l => !l.Trim().StartsWith("EnableDataBroadcast") && !l.Trim().StartsWith("EnableCDUBroadcast")).ToList();

                if (!sdkSectionExists) { lines.Add(""); lines.Add("[SDK]"); }
                int sdkIndex = lines.FindIndex(l => l.Trim() == "[SDK]");

                lines.Insert(sdkIndex + 1, "EnableCDUBroadcast.0=1");
                lines.Insert(sdkIndex + 1, "EnableDataBroadcast=1");

                File.WriteAllLines(iniPath, lines);
                Console.WriteLine("PMDG Config: Updated 737_Options.ini. Restart MSFS if running.");
            }
            catch (Exception ex) { Console.WriteLine($"PMDG Config: Error updating 737_Options.ini: {ex.Message}"); }
        }
    }
}