// file: IFmsDataProvider.cs
using System;
using System.Threading.Tasks;

namespace JonAvionics
{
    public interface IFmsDataProvider
    {
        // The event is now nullable to satisfy the compiler
        event Action<string>? OnDataReceived;
        Task Start();
    }
}