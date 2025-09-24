// file: HttpFileServer.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace JonAvionics
{
    public class HttpFileServer
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly string _baseFolder;
        private readonly string _defaultFile;
        private readonly Dictionary<string, string> _mimeTypeMap = new(StringComparer.InvariantCultureIgnoreCase)
        { {".html", "text/html"}, {".ttf", "font/ttf"}, };

        public HttpFileServer(string prefix, string baseFolder, string defaultFile)
        {
            if (!prefix.EndsWith("/")) prefix += "/";
            _listener.Prefixes.Add(prefix);
            _baseFolder = baseFolder;
            _defaultFile = defaultFile;
        }

        public async Task Start()
        {
            _listener.Start();
            Console.WriteLine($"Main Server: HTTP server started at {_listener.Prefixes.First()}, serving from '{_baseFolder}'");
            while (true)
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    _ = Task.Run(() => ProcessRequestContext(context));
                }
                catch (HttpListenerException) { Console.WriteLine("HTTP server is stopping."); break; }
                catch (Exception ex) { Console.WriteLine($"HTTP Server Error: {ex.Message}"); }
            }
        }

        private void ProcessRequestContext(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            try
            {
                // The Url can potentially be null, so we use the null-conditional operator '?.'
                string localPath = request.Url?.LocalPath ?? "/";
                if (localPath == "/") { localPath = "/" + _defaultFile; }
                string filePath = Path.GetFullPath(Path.Combine(_baseFolder, localPath.TrimStart('/')));
                if (!filePath.StartsWith(Path.GetFullPath(_baseFolder))) { response.StatusCode = (int)HttpStatusCode.Forbidden; }
                else if (File.Exists(filePath))
                {
                    byte[] fileBytes = File.ReadAllBytes(filePath);
                    string extension = Path.GetExtension(filePath);
                    response.ContentType = _mimeTypeMap.GetValueOrDefault(extension, "application/octet-stream");
                    response.ContentLength64 = fileBytes.Length;
                    response.OutputStream.Write(fileBytes, 0, fileBytes.Length);
                    response.StatusCode = (int)HttpStatusCode.OK;
                }
                else { Console.WriteLine($"HTTP Server: File not found - {filePath}"); response.StatusCode = (int)HttpStatusCode.NotFound; }
            }
            catch (Exception ex) { Console.WriteLine($"HTTP Request Error: {ex.Message}"); response.StatusCode = (int)HttpStatusCode.InternalServerError; }
            finally { response.OutputStream.Close(); }
        }
    }
}