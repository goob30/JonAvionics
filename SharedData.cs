// file: SharedData.cs
using System.Collections.Generic;

namespace JonAvionics
{
    public record Cell(string text, string color = "white", string size = "normal");

    public record Line
    {
        public List<Cell> Left { get; init; } = new();
        public List<Cell> Center { get; init; } = new();
        public List<Cell> Right { get; init; } = new();
    }

    public record McduState
    {
        public string Title { get; init; } = "";
        public List<List<Cell>> Grid { get; init; } = new(); // FBW
        public List<Line> Lines { get; init; } = new();      // PMDG
        public List<Cell> Scratchpad { get; init; } = new();
    }
}