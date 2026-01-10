// file: FbwDataProvider.cs
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WebSocketSharp;

namespace JonAvionics.providers
{
    // If these already exist elsewhere, remove duplicates here.
    public class McduState
    {
        public string Title { get; set; } = "";              // kept, but title is also emitted as Grid[0]
        public List<List<Cell>> Grid { get; set; } = new();   // Grid[0] = title row, Grid[1..12] = data rows
        public List<Cell> Scratchpad { get; set; } = new();   // 24 cells
        public List<Line> Lines { get; set; } = new();        // compatibility (unused)
    }

    public class Line
    {
        public List<Cell> Left { get; set; } = new();
        public List<Cell> Center { get; set; } = new();
        public List<Cell> Right { get; set; } = new();
    }

    public class Cell
    {
        public string text { get; set; }
        public string color { get; set; }
        public string size { get; set; }

        public Cell(string t, string c, string s)
        {
            text = t;
            color = c;
            size = s;
        }
    }

    public class FbwDataProvider : IFmsDataProvider
    {
        public event Action<string>? OnDataReceived;

        private const string SimBridgeUri = "ws://localhost:8380/interfaces/v1/mcdu";

        private const int CDU_COLS = 24;

        // Output format you asked for:
        //   Grid[0]      = title row (normal)
        //   Grid[1..12]  = 12 content rows
        //   Scratchpad   = scratch row (normal)
        private const int TITLE_ROWS = 1;
        private const int CONTENT_ROWS = 12;

        private const bool DEBUG_PRINT_ROWS = true;

        private static readonly HashSet<string> ColorTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "white","green","amber","cyan","magenta","red","yellow"
        };

        public Task Start()
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    WebSocket? ws = null;

                    try
                    {
                        ws = new WebSocket(SimBridgeUri);

                        var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                        ws.OnOpen += (_, __) =>
                        {
                            Console.WriteLine("FBW Provider: Successfully connected to SimBridge.");
                            connectedTcs.TrySetResult(true);
                        };

                        ws.OnClose += (_, e) =>
                        {
                            Console.WriteLine($"FBW Provider: WebSocket closed. Code={e.Code} Reason={e.Reason}");
                            connectedTcs.TrySetResult(false);
                        };

                        ws.OnError += (_, e) =>
                        {
                            Console.WriteLine($"FBW Provider: WebSocket error: {e.Message}");
                        };

                        ws.OnMessage += (_, e) =>
                        {
                            try
                            {
                                var jsonString = e.Data.StartsWith("update:", StringComparison.OrdinalIgnoreCase)
                                    ? e.Data.Substring(7)
                                    : e.Data;

                                var raw = JObject.Parse(jsonString);

                                // FBW usually has "left" and "right"
                                var mcduData = raw["left"];
                                if (mcduData == null) return;

                                var state = ProcessFbwData(mcduData);
                                var finalJson = JsonConvert.SerializeObject(state);
                                OnDataReceived?.Invoke(finalJson);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"FBW Provider: Error processing message: {ex.Message}");
                            }
                        };

                        Console.WriteLine($"FBW Provider: Connecting to SimBridge at {SimBridgeUri}...");
                        ws.Connect();

                        await connectedTcs.Task;

                        while (ws.IsAlive)
                            await Task.Delay(250);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FBW Provider: Connection failed: {ex.Message}");
                    }
                    finally
                    {
                        try
                        {
                            if (ws != null && ws.IsAlive) ws.Close();
                        }
                        catch { /* ignore */ }
                    }

                    Console.WriteLine("FBW Provider: Will attempt to reconnect in 5 seconds...");
                    await Task.Delay(5000);
                }
            });
        }

        private McduState ProcessFbwData(JToken mcduData)
        {
            // Keep Title string for compatibility/debug
            var rawTitleSegment = mcduData.Value<string>("title") ?? "";
            var plainTitle = Plain(rawTitleSegment).Trim();
            var titleRow = BuildCenteredTextRow(plainTitle, "white", "normal");

            var grid = new List<List<Cell>>(TITLE_ROWS + CONTENT_ROWS)
            {
                titleRow // Grid[0] = title row
            };
            if (DEBUG_PRINT_ROWS)
            {
                Console.WriteLine($"FBW Raw Title: \"{rawTitleSegment}\"");
                Console.WriteLine($"FBW Plain Title: \"{Plain(rawTitleSegment)}\"");
            }




            var linesParts = mcduData["lines"] as JArray;
            if (linesParts == null)
            {
                // Still return title + blank rows so renderer stays stable
                for (int i = 0; i < CONTENT_ROWS; i++)
                    grid.Add(EmptyRow((i % 2 == 0) ? "small" : "normal"));

                return new McduState
                {
                    Title = plainTitle,
                    Grid = grid,
                    Scratchpad = FitTo24(ParseSegment(mcduData.Value<string>("scratchpad") ?? "", "normal"), "normal"),
                    Lines = new List<Line>()
                };
            }

            // Render exactly 12 content rows (even if SimBridge gives fewer)
            for (int rowIdx = 0; rowIdx < CONTENT_ROWS; rowIdx++)
            {
                var size = (rowIdx % 2 == 0) ? "small" : "normal";

                if (rowIdx >= linesParts.Count)
                {
                    grid.Add(EmptyRow(size));
                    continue;
                }

                var segments = linesParts[rowIdx]?.ToObject<List<string>>() ?? new List<string>();
                var seg0 = segments.Count > 0 ? segments[0] ?? "" : "";
                var seg1 = segments.Count > 1 ? segments[1] ?? "" : "";
                var seg2 = segments.Count > 2 ? segments[2] ?? "" : "";

                var hasSeg1 = !string.IsNullOrWhiteSpace(Plain(seg1));
                var hasSeg2 = !string.IsNullOrWhiteSpace(Plain(seg2));

                List<Cell> row;

                // If seg1 & seg2 are empty, FBW sometimes encodes full spacing into seg0 using {sp}
                if (!hasSeg1 && !hasSeg2)
                {
                    row = FitTo24(ParseSegment(seg0, size), size);
                }
                else
                {
                    // Your corrected mapping:
                    // seg0 = left, seg1 = right, seg2 = center (and seg2 may be blank)
                    row = EmptyRow(size);

                    var cells0 = ParseSegment(seg0, size);
                    var cells1 = ParseSegment(seg1, size);
                    var cells2 = ParseSegment(seg2, size);

                    PlaceLeft(row, cells0, 0);
                    PlaceRightAligned(row, cells1, CDU_COLS - 1);
                    PlaceCentered(row, cells2);
                }

                grid.Add(row);

                if (DEBUG_PRINT_ROWS)
                {
                    static char CellChar(Cell c)
                    {
                        var t = c?.text ?? " ";
                        return t.Length > 0 ? t[0] : ' ';
                    }

                    var rowStr = new string(row.Select(CellChar).ToArray());
                    Console.WriteLine($"FBW Row {rowIdx:00}: [{rowStr}]");
                    Console.WriteLine($"  seg0=\"{Plain(seg0)}\"");
                    Console.WriteLine($"  seg1=\"{Plain(seg1)}\"");
                    Console.WriteLine($"  seg2=\"{Plain(seg2)}\"");
                }
            }

            var scratch = FitTo24(ParseSegment(mcduData.Value<string>("scratchpad") ?? "", "normal"), "normal");

            return new McduState
            {
                Title = plainTitle,
                Grid = grid,
                Scratchpad = scratch,
                Lines = new List<Line>() // unused for FBW Grid mode
            };
        }

        private static List<Cell> EmptyRow(string size)
        {
            var row = new List<Cell>(CDU_COLS);
            for (int i = 0; i < CDU_COLS; i++)
                row.Add(new Cell(" ", "white", size));
            return row;
        }

        private static List<Cell> FitTo24(List<Cell> cells, string size)
        {
            var row = EmptyRow(size);
            if (cells == null) return row;

            for (int i = 0; i < CDU_COLS && i < cells.Count; i++)
                row[i] = cells[i];

            return row;
        }

        private static List<Cell> BuildCenteredTextRow(string text, string color, string size)
        {
            var row = EmptyRow(size);
            if (string.IsNullOrEmpty(text)) return row;

            var chars = text.ToCharArray();
            int start = (CDU_COLS - chars.Length) / 2;
            if (start < 0) start = 0;

            for (int i = 0; i < chars.Length; i++)
            {
                int col = start + i;
                if (col < 0 || col >= CDU_COLS) break;
                row[col] = new Cell(chars[i].ToString(), color, size);
            }

            return row;
        }

        private static void PlaceLeft(List<Cell> row, List<Cell> cells, int startCol)
        {
            if (cells == null || cells.Count == 0) return;

            for (int i = 0; i < cells.Count; i++)
            {
                int col = startCol + i;
                if (col < 0 || col >= CDU_COLS) break;
                row[col] = cells[i];
            }
        }

        private static void PlaceRightAligned(List<Cell> row, List<Cell> cells, int rightEdgeCol)
        {
            if (cells == null || cells.Count == 0) return;

            int start = rightEdgeCol - cells.Count + 1;
            if (start < 0) start = 0;

            for (int i = 0; i < cells.Count; i++)
            {
                int col = start + i;
                if (col < 0 || col >= CDU_COLS) break;
                row[col] = cells[i];
            }
        }

        private static void PlaceCentered(List<Cell> row, List<Cell> cells)
        {
            if (cells == null || cells.Count == 0) return;

            int start = (CDU_COLS - cells.Count) / 2;
            if (start < 0) start = 0;

            for (int i = 0; i < cells.Count; i++)
            {
                int col = start + i;
                if (col < 0 || col >= CDU_COLS) break;
                row[col] = cells[i];
            }
        }

        private static string Plain(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("{sp}", " ", StringComparison.OrdinalIgnoreCase);
            return Regex.Replace(s, @"\{[\w/]*\}", "");
        }

        private List<Cell> ParseSegment(string segment, string forceSize)
        {
            var cells = new List<Cell>();
            var currentColor = "white";
            var currentSize = forceSize;

            var matches = Regex.Matches(segment ?? "", @"(\{[\w/]*\})|([^\{]+)");

            foreach (Match match in matches)
            {
                if (match.Groups[1].Success)
                {
                    var tag = match.Groups[1].Value.Trim('{', '}');

                    // {sp} must emit an actual space cell
                    if (tag.Equals("sp", StringComparison.OrdinalIgnoreCase))
                    {
                        cells.Add(new Cell(" ", currentColor, currentSize));
                        continue;
                    }

                    if (tag.Equals("end", StringComparison.OrdinalIgnoreCase))
                    {
                        currentColor = "white";
                        currentSize = forceSize;
                    }
                    else if (tag.Equals("small", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSize = "small";
                    }
                    else if (tag.Equals("big", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSize = "normal";
                    }
                    else if (ColorTags.Contains(tag))
                    {
                        currentColor = tag.Equals("yellow", StringComparison.OrdinalIgnoreCase)
                            ? "amber"
                            : tag.ToLowerInvariant();
                    }

                    continue;
                }

                if (match.Groups[2].Success)
                {
                    var textRun = match.Groups[2].Value ?? "";

                    // Normalize placeholder boxes
                    textRun = textRun.Replace("[]", "□");

                    foreach (var ch in textRun)
                        cells.Add(new Cell(ch.ToString(), currentColor, currentSize));
                }
            }

            return cells;
        }
    }
}
