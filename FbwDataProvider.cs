// file: FbwDataProvider.cs
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp;

namespace JonAvionics.providers
{
    public class FbwDataProvider : IFmsDataProvider
    {
        public event Action<string>? OnDataReceived;
        private const string SimBridgeUri = "ws://10.0.0.91:8380/interfaces/v1/mcdu";

        public Task Start()
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using (var ws = new WebSocket(SimBridgeUri))
                        {
                            var tcs = new TaskCompletionSource<bool>();
                            ws.OnOpen += (sender, e) => { Console.WriteLine("FBW Provider: Successfully connected to SimBridge."); tcs.TrySetResult(true); };
                            ws.OnMessage += (sender, e) =>
                            {
                                try
                                {
                                    var jsonString = e.Data.StartsWith("update:") ? e.Data.Substring(7) : e.Data;
                                    var rawData = JObject.Parse(jsonString);
                                    var mcduData = rawData["left"];
                                    if (mcduData != null)
                                    {
                                        var processedState = ProcessFbwData(mcduData);
                                        var finalJson = JsonConvert.SerializeObject(processedState);
                                        OnDataReceived?.Invoke(finalJson);
                                    }
                                }
                                catch (Exception ex) { Console.WriteLine($"FBW Provider: Error processing message: {ex.Message}"); }
                            };
                            ws.OnClose += (sender, e) => { Console.WriteLine("FBW Provider: WebSocket connection closed."); tcs.TrySetResult(false); };
                            Console.WriteLine($"FBW Provider: Connecting to SimBridge at {SimBridgeUri}...");
                            ws.Connect();
                            await tcs.Task;
                            while (ws.IsAlive) { await Task.Delay(1000); }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"FBW Provider: Connection failed: {ex.Message}"); }
                    Console.WriteLine("FBW Provider: Will attempt to reconnect in 5 seconds...");
                    await Task.Delay(5000);
                }
            });
        }
        
        private McduState ProcessFbwData(JToken mcduData)
        {
            var title = ParseSegment(mcduData.Value<string>("title") ?? "").Select(c => c.text).Aggregate("", (acc, s) => acc + s).Trim();
            var processedLines = new List<Line>();
            var linesParts = mcduData["lines"] as JArray;
            if (linesParts == null) return new McduState(); // Guard against null
            for (int i = 0; i < linesParts.Count; i++)
            {
                if (i >= 13) continue;
                var segments = linesParts[i]?.ToObject<List<string>>();
                if (segments == null) continue; // Guard against null
                var leftStr = segments.Count > 0 ? segments[0] : "";
                var centerStr = segments.Count > 1 ? segments[1] : "";
                var rightStr = segments.Count > 2 ? segments[2] : "";
                if (leftStr.Contains('|') && string.IsNullOrEmpty(centerStr) && string.IsNullOrEmpty(rightStr))
                {
                    var parts = leftStr.Split('|');
                    leftStr = parts[0];
                    if (parts.Length > 1) centerStr = parts[1];
                    if (parts.Length > 2) rightStr = parts[2];
                }
                else if (Regex.IsMatch(leftStr, @"(\s|{sp}){3,}") && string.IsNullOrEmpty(centerStr) && string.IsNullOrEmpty(rightStr))
                {
                    var parts = Regex.Split(leftStr, @"(?:\s|{sp}){3,}").Where(p => !string.IsNullOrEmpty(p.Trim())).ToList();
                    if (parts.Count == 2) { leftStr = parts[0]; rightStr = parts[1]; centerStr = ""; }
                    else if (parts.Count >= 3) { leftStr = parts[0]; centerStr = parts[1]; rightStr = string.Join(" ", parts.Skip(2)); }
                }
                var forceSmall = i % 2 == 0;
                processedLines.Add(new Line { Left = ParseSegment(leftStr, forceSmall ? "small" : "normal"), Center = ParseSegment(centerStr, forceSmall ? "small" : "normal"), Right = ParseSegment(rightStr, forceSmall ? "small" : "normal") });
            }
            return new McduState { Title = title, Lines = processedLines, Scratchpad = ParseSegment(mcduData.Value<string>("scratchpad") ?? "") };
        }

        private List<Cell> ParseSegment(string segment, string forceSize = "normal")
        {
            var parts = new List<Cell>();
            var currentColor = "white";
            var currentSize = forceSize;
            var matches = Regex.Matches(segment, @"(\{[\w/]*\})|([^\{]+)");
            foreach (Match match in matches)
            {
                if (match.Groups[1].Success)
                {
                    var tag = match.Groups[1].Value.Trim('{', '}');
                    if (tag == "end") { currentColor = "white"; currentSize = forceSize; } 
                    else if (tag == "small") { currentSize = "small"; }
                    else if (new[] { "white", "green", "amber", "cyan", "magenta", "red" }.Contains(tag)) { currentColor = tag; }
                }
                else if (match.Groups[2].Success)
                {
                    var text = match.Groups[2].Value;
                    if (text.Contains("[]")) { parts.Add(new Cell(text.Replace("[]", "□"), "amber", "normal")); }
                    else { parts.Add(new Cell(text, currentColor, currentSize)); }
                }
            }
            return parts;
        }
    }
}