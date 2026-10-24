using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace ValheimPlayerCards
{
    /// <summary>
    /// A small read-only HTTP/1.1 server: the built-in page, the JSON API and the stored images.
    /// It only answers GET, HEAD and OPTIONS, serves nothing outside a fixed set of paths and never
    /// touches game objects: it reads <see cref="PlayerStore.Current"/>, built on the main thread.
    /// </summary>
    internal sealed class WebServer
    {
        private const int MaxConnections = 32;
        private const int MaxRequestBytes = 8 * 1024;

        private static readonly Dictionary<string, string> PageFiles = new Dictionary<string, string>
        {
            { "index.html", "text/html; charset=utf-8" },
            { "app.js", "text/javascript; charset=utf-8" },
            { "lang.js", "text/javascript; charset=utf-8" },
            { "style.css", "text/css; charset=utf-8" }
        };

        private readonly Dictionary<string, byte[]> _builtIn = new Dictionary<string, byte[]>();
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private int _connections;
        private string _allowOrigin;
        private string _customDir;

        public void Start(string host, int port)
        {
            foreach (string name in PageFiles.Keys)
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name))
                {
                    if (s == null) continue;
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    _builtIn[name] = ms.ToArray();
                }
            }
            _allowOrigin = (Plugin.AllowOrigin.Value ?? "").Trim();
            _customDir = (Plugin.CustomPageDirectory.Value ?? "").Trim();

            IPAddress address = IPAddress.Any;
            if (!string.IsNullOrWhiteSpace(host) && host.Trim() != "*" && !IPAddress.TryParse(host.Trim(), out address))
            {
                Plugin.Log.LogError($"Web Host '{host}' is not an IP address; the page is not served.");
                return;
            }
            try
            {
                _listener = new TcpListener(address, port);
                _listener.Start();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Cannot listen on {address}:{port} ({e.Message}); the page is not served.");
                _listener = null;
                return;
            }
            _running = true;
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "ValheimPlayerCards web" };
            _thread.Start();
            Plugin.Log.LogInfo($"Player cards page on http://{(address.Equals(IPAddress.Any) ? "*" : address.ToString())}:{port}/");
        }

        public void Stop()
        {
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // Already closed.
            }
            _listener = null;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!_running) return;
                    Thread.Sleep(100);
                    continue;
                }
                if (Interlocked.Increment(ref _connections) > MaxConnections)
                {
                    Interlocked.Decrement(ref _connections);
                    client.Close();
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Handle(client);
                    }
                    catch (Exception)
                    {
                        // Client went away or sent garbage.
                    }
                    finally
                    {
                        client.Close();
                        Interlocked.Decrement(ref _connections);
                    }
                });
            }
        }

        private void Handle(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 10000;
            var stream = client.GetStream();

            string head = ReadHead(stream);
            if (head == null) return;
            string[] requestLine = head.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
            if (requestLine.Length < 3)
            {
                Send(stream, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bad request"), false, null);
                return;
            }
            string method = requestLine[0];
            string target = requestLine[1];
            bool headOnly = method == "HEAD";
            if (method == "OPTIONS")
            {
                Send(stream, 204, null, Array.Empty<byte>(), true, null);
                return;
            }
            if (method != "GET" && !headOnly)
            {
                Send(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Method not allowed"), false, null);
                return;
            }

            int q = target.IndexOfAny(new[] { '?', '#' });
            string path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target).TrimStart('/');
            if (path.Length == 0) path = "index.html";

            var snap = PlayerStore.Current;
            if (snap == null)
            {
                Send(stream, 503, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Starting"), headOnly, "no-store");
                return;
            }

            if (PageFiles.TryGetValue(path, out string pageType))
            {
                byte[] body = Custom(path) ?? (_builtIn.TryGetValue(path, out var b) ? b : null);
                if (body != null)
                {
                    Send(stream, 200, pageType, body, headOnly, "no-cache");
                    return;
                }
            }
            else if (snap.Json.TryGetValue(path, out byte[] json))
            {
                Send(stream, 200, "application/json; charset=utf-8", json, headOnly, "no-cache");
                return;
            }
            else if (TryImage(snap, path, "portraits/", snap.Portraits, out string file)
                     || TryImage(snap, path, "icons/", snap.Icons, out file))
            {
                byte[] png = File.ReadAllBytes(file);
                Send(stream, 200, "image/png", png, headOnly, "public, max-age=300");
                return;
            }
            Send(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"), headOnly, "no-cache");
        }

        /// <summary>Only names the snapshot knows, so no path from the request reaches the disk.</summary>
        private static bool TryImage(Snapshot snap, string path, string prefix, HashSet<string> known, out string file)
        {
            file = null;
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".png", StringComparison.Ordinal)) return false;
            string name = path.Substring(prefix.Length, path.Length - prefix.Length - 4);
            if (!known.Contains(name)) return false;
            file = Path.Combine(snap.Directory, prefix.TrimEnd('/'), name + ".png");
            return File.Exists(file);
        }

        private byte[] Custom(string name)
        {
            if (_customDir.Length == 0) return null;
            try
            {
                string file = Path.Combine(_customDir, name);
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadHead(NetworkStream stream)
        {
            var buffer = new byte[MaxRequestBytes];
            int length = 0;
            while (length < buffer.Length)
            {
                int n = stream.Read(buffer, length, buffer.Length - length);
                if (n <= 0) return null;
                length += n;
                string text = Encoding.ASCII.GetString(buffer, 0, length);
                int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0) return text.Substring(0, end);
            }
            return null;
        }

        private void Send(NetworkStream stream, int status, string type, byte[] body, bool headOnly, string cache)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            if (type != null) sb.Append("Content-Type: ").Append(type).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            if (cache != null) sb.Append("Cache-Control: ").Append(cache).Append("\r\n");
            if (_allowOrigin.Length > 0)
            {
                sb.Append("Access-Control-Allow-Origin: ").Append(_allowOrigin).Append("\r\n");
                sb.Append("Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n");
            }
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            sb.Append("Server: ValheimPlayerCards\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] headBytes = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (!headOnly && body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 503: return "Service Unavailable";
                default: return "Error";
            }
        }
    }
}
