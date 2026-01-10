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
        public string Title { get; set; } = "";
        public List<List<Cell>> Grid { get; set; } = new();     // 12 rows x 24 cols
        public List<Cell> Scratchpad { get; set; } = new();     // 24 cols
        public List<Line> Lines { get; set; } = new();          // compatibility (unused)
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

        // Output contract for your HTML:
        // Title (string) + 12 rows (Grid) + 1 scratchpad row (Scratchpad)
        private const int CDU_COLS = 24;
        private const int GRID_ROWS = 12;

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

                                // FBW usually has "left" and "right" MCDUs.
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

                        // Wait until open/close happens
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
            // Title as plain text (normal)
            var title = string.Concat(
                ParseSegment(mcduData.Value<string>("title") ?? "", "normal").Select(c => c.text)
            ).Trim();

            var grid = new List<List<Cell>>(GRID_ROWS);

            var linesParts = mcduData["lines"] as JArray;

            // Always output 12 rows, even if lines missing
            if (linesParts == null)
            {
                for (int i = 0; i < GRID_ROWS; i++)
                    grid.Add(EmptyRow(RowSizeFor(i)));

                return new McduState
                {
                    Title = title,
                    Grid = grid,
                    Scratchpad = FitTo24(ParseSegment(mcduData.Value<string>("scratchpad") ?? "", "normal"), "normal"),
                    Lines = new List<Line>()
                };
            }

            // Render up to 12, then pad to 12
            var rowsToRender = Math.Min(linesParts.Count, GRID_ROWS);

            for (int rowIdx = 0; rowIdx < rowsToRender; rowIdx++)
            {
                var segments = linesParts[rowIdx]?.ToObject<List<string>>() ?? new List<string>();

                var seg0 = segments.Count > 0 ? segments[0] ?? "" : "";
                var seg1 = segments.Count > 1 ? segments[1] ?? "" : "";
                var seg2 = segments.Count > 2 ? segments[2] ?? "" : "";

                var size = RowSizeFor(rowIdx);

                bool hasSeg1 = !string.IsNullOrWhiteSpace(Plain(seg1));
                bool hasSeg2 = !string.IsNullOrWhiteSpace(Plain(seg2));

                List<Cell> row;

                // If seg1 and seg2 are empty, FBW sometimes pre-bakes the whole 24-col row in seg0 (using {sp}).
                if (!hasSeg1 && !hasSeg2)
                {
                    row = FitTo24(ParseSegment(seg0, size), size);
                }
                else
                {
                    row = EmptyRow(size);

                    var cells0 = ParseSegment(seg0, size);
                    var cells1 = ParseSegment(seg1, size);
                    var cells2 = ParseSegment(seg2, size);

                    // Your final correct mapping for FBW:
                    // seg0 = left, seg1 = right, seg2 = center
                    PlaceLeft(row, cells0, 0);
                    PlaceRightAligned(row, cells1, CDU_COLS - 1);
                    PlaceCentered(row, cells2);
                }

                // Hard guarantee: exactly 24 cells
                if (row.Count != CDU_COLS)
                    row = FitTo24(row, size);

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

            // Pad missing rows up to 12
            for (int i = grid.Count; i < GRID_ROWS; i++)
                grid.Add(EmptyRow(RowSizeFor(i)));

            // Scratchpad: force 24 wide
            var scratch = FitTo24(ParseSegment(mcduData.Value<string>("scratchpad") ?? "", "normal"), "normal");

            return new McduState
            {
                Title = title,
                Grid = grid,           // 12 rows
                Scratchpad = scratch,  // 24 cells
                Lines = new List<Line>()
            };
        }

        // Airbus convention: label rows often small; value rows big.
        private static string RowSizeFor(int rowIdx) => (rowIdx % 2 == 0) ? "small" : "normal";

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
            // Convert {sp} to visible spaces for debug readability, then strip other tags.
            s = s.Replace("{sp}", " ", StringComparison.OrdinalIgnoreCase);
            return Regex.Replace(s, @"\{[\w/]*\}", "");
        }

        private List<Cell> ParseSegment(string segment, string forceSize)
        {
            var cells = new List<Cell>();
            var currentColor = "white";
            var currentSize = forceSize;

            // Split into {tags} and text runs
            var matches = Regex.Matches(segment ?? "", @"(\{[\w/]*\})|([^\{]+)");

            foreach (Match match in matches)
            {
                if (match.Groups[1].Success)
                {
                    var tag = match.Groups[1].Value.Trim('{', '}');

                    // {sp} is a *token* in FBW strings, not literal text.
                    // It must emit a space cell.
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

                    // Normalize “boxes”
                    textRun = textRun.Replace("[]", "□");

                    foreach (var ch in textRun)
                    {
                        cells.Add(new Cell(ch.ToString(), currentColor, currentSize));
                    }
                }
            }

            return cells;
        }
    }
}
