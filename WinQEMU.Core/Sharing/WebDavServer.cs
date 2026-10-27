using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace WinQEMU.Core.Sharing
{
    /// <summary>
    /// Serves the shared folders to a Windows guest over WebDAV, which Windows reads with its own client
    /// (the WebClient service): nothing to install in the guest, unlike a 9P client.
    /// </summary>
    /// <remarks>
    /// Listens on loopback only. The guest reaches it through its NAT card, where 10.0.2.2 is this PC's
    /// loopback, at \\10.0.2.2@port\secret: the secret first segment keeps other machines and local
    /// programs, which can reach loopback as well, from browsing the folders. The root is virtual, one
    /// collection per shared folder, read live as the 9P server's is.
    ///
    /// Only what Windows' client asks for: OPTIONS, PROPFIND (depth 0 and 1), GET and HEAD, PUT, DELETE,
    /// MKCOL, MOVE, COPY, PROPPATCH (Windows sets file times with it and gives up on an error), and LOCK and
    /// UNLOCK, which programs saving a file use and which are acknowledged without locking anything.
    /// </remarks>
    public sealed class WebDavServer : IAsyncDisposable
    {
        private static readonly XNamespace Dav = "DAV:";
        private static readonly UTF8Encoding Utf8 = new(false);

        private readonly Func<IReadOnlyList<SharedFolderEntry>> _folders;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private Task? _accepting;

        /// <param name="folders">Read on every request, so the shared folders can change while the guest runs.</param>
        public WebDavServer(Func<IReadOnlyList<SharedFolderEntry>> folders, string? secret = null)
        {
            _folders = folders;
            Secret = secret ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        }

        /// <summary>The first path segment every request must carry.</summary>
        public string Secret { get; }

        public int Port { get; private set; }

        /// <summary>Raised for requests that fail on the host side, for the machine's journal.</summary>
        public event Action<string>? Trace;

        public void Start()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = AcceptAsync(_stop.Token);
        }

        private async Task AcceptAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
                _ = ServeConnectionAsync(client, cancellationToken);
            }
        }

        private async Task ServeConnectionAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                try
                {
                    // Keep-alive: Windows' client sends one request after another on the same connection.
                    while (await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false) is { } request)
                    {
                        var response = await HandleAsync(request, cancellationToken).ConfigureAwait(false);
                        // Whatever the handler did not read is skipped, so the next request starts where it should.
                        await request.Body.DrainAsync(cancellationToken).ConfigureAwait(false);
                        await WriteResponseAsync(stream, request, response, cancellationToken).ConfigureAwait(false);
                        if (request.Headers.TryGetValue("Connection", out var connection)
                            && connection.Equals("close", StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
                {
                    // The guest closed the connection, or sent something that is not HTTP.
                }
            }
        }

        // ---- HTTP ----

        private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, BodyReader Body);

        /// <summary>
        /// A request's body, read as it comes: Windows sends a whole file in one PUT, up to 4 GB, which must
        /// go to disk rather than to memory.
        /// </summary>
        private sealed class BodyReader
        {
            private readonly Stream _stream;
            private readonly bool _chunked;
            private long _left;
            private bool _done;

            public BodyReader(Stream stream, long length, bool chunked)
            {
                _stream = stream;
                _left = chunked ? 0 : length;
                _chunked = chunked;
                _done = !chunked && length <= 0;
            }

            /// <summary>The next piece of the body into <paramref name="buffer"/>; 0 at its end.</summary>
            public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (_done)
                    return 0;
                if (_chunked && _left == 0)
                {
                    var sizeLine = await ReadLineAsync(_stream, cancellationToken).ConfigureAwait(false);
                    _left = long.Parse(sizeLine.Split(';')[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (_left == 0)
                    {
                        while ((await ReadLineAsync(_stream, cancellationToken).ConfigureAwait(false)).Length > 0) { }
                        _done = true;
                        return 0;
                    }
                }

                var read = await _stream.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("The connection ended in the middle of a request body.");
                _left -= read;
                if (_left == 0)
                {
                    if (_chunked)
                        await ReadLineAsync(_stream, cancellationToken).ConfigureAwait(false);
                    else
                        _done = true;
                }
                return read;
            }

            public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            /// <summary>A small body (an XML request), whole.</summary>
            public async Task<byte[]> ReadAllAsync(CancellationToken cancellationToken)
            {
                using var memory = new MemoryStream();
                await CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                return memory.ToArray();
            }

            public Task DrainAsync(CancellationToken cancellationToken) => CopyToAsync(Stream.Null, cancellationToken);
        }

        private sealed class Response
        {
            public int Status { get; init; } = 200;
            public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
            public byte[] Body { get; init; } = Array.Empty<byte>();
            /// <summary>A file sent as the body, from <see cref="FileOffset"/> for <see cref="FileLength"/> bytes.</summary>
            public string? File { get; init; }
            public long FileOffset { get; init; }
            public long FileLength { get; init; }
        }

        private static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
        {
            var head = await ReadHeadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (head is null)
                return null;

            var lines = head.Split("\r\n");
            var start = lines[0].Split(' ');
            if (start.Length < 3)
                throw new InvalidDataException("Not an HTTP request line.");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            var chunked = headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);
            var length = headers.TryGetValue("Content-Length", out var value) && long.TryParse(value, out var size) ? size : 0;
            return new Request(start[0].ToUpperInvariant(), start[1], headers, new BodyReader(stream, length, chunked));
        }

        /// <summary>The request line and headers, up to the blank line; null when the connection ends first.</summary>
        private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
        {
            var bytes = new List<byte>(512);
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                    return null;
                bytes.Add(one[0]);
                if (bytes.Count > 64 * 1024)
                    throw new InvalidDataException("HTTP headers too long.");
                var n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                    return Encoding.ASCII.GetString(bytes.ToArray(), 0, n - 4);
            }
        }

        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 1)
            {
                if (one[0] == '\n')
                    break;
                if (one[0] != '\r')
                    bytes.Add(one[0]);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static async Task WriteResponseAsync(Stream stream, Request request, Response response, CancellationToken cancellationToken)
        {
            var length = response.File is not null ? response.FileLength : response.Body.Length;
            var head = new StringBuilder();
            head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n");
            head.Append("Server: WinQEMU\r\n");
            foreach (var (name, value) in response.Headers)
                head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
            head.Append(CultureInfo.InvariantCulture, $"Content-Length: {length}\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);

            if (request.Method != "HEAD")
            {
                if (response.File is { } path)
                {
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
                    file.Seek(response.FileOffset, SeekOrigin.Begin);
                    var buffer = new byte[81920];
                    var left = response.FileLength;
                    while (left > 0)
                    {
                        var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            throw new IOException("The file shrank while it was being sent.");
                        await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        left -= read;
                    }
                }
                else
                    await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
            }
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string Reason(int status) => status switch
        {
            200 => "OK", 201 => "Created", 204 => "No Content", 206 => "Partial Content", 207 => "Multi-Status",
            400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
            409 => "Conflict", 412 => "Precondition Failed", 416 => "Range Not Satisfiable",
            500 => "Internal Server Error", 507 => "Insufficient Storage", _ => "Status"
        };

        // ---- WebDAV ----

        /// <summary>Where a path leads: the virtual root, a shared folder, or something inside one.</summary>
        private sealed record Target(SharedFolderEntry? Folder, string Relative)
        {
            public bool IsRoot => Folder is null;
        }

        private async Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
        {
            if (request.Method == "OPTIONS")
                return Options();

            // The server root answers, empty, as Windows' client probes it before the share itself.
            var path = request.Path.Split('?')[0];
            if (path == "/" && request.Method == "PROPFIND")
                return MultiStatus(new[] { Entry("/", null, isCollection: true) });

            if (Resolve(path) is not { } target)
                return new Response { Status = 404 };

            try
            {
                return request.Method switch
                {
                    "PROPFIND" => PropFind(target, path, request),
                    "GET" or "HEAD" => Get(target, request),
                    "PUT" => await PutAsync(target, request, cancellationToken).ConfigureAwait(false),
                    "DELETE" => Delete(target),
                    "MKCOL" => MakeCollection(target),
                    "MOVE" or "COPY" => MoveOrCopy(target, request),
                    "PROPPATCH" => PropPatch(path, await request.Body.ReadAllAsync(cancellationToken).ConfigureAwait(false)),
                    "LOCK" => Lock(target, path, request),
                    "UNLOCK" => new Response { Status = 204 },
                    _ => new Response { Status = 405 }
                };
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException)
            {
                return new Response { Status = 403 };
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new Response { Status = 404 };
            }
            catch (IOException ex)
            {
                Trace?.Invoke($"Shared folders (WebDAV): {request.Method} {Uri.UnescapeDataString(path)}: {ex.Message}");
                return new Response { Status = ex.HResult == unchecked((int)0x80070070) ? 507 : 409 };
            }
        }

        private static Response Options()
        {
            var response = new Response();
            response.Headers["DAV"] = "1, 2";
            response.Headers["MS-Author-Via"] = "DAV";
            response.Headers["Allow"] = "OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, DELETE, MKCOL, MOVE, COPY, LOCK, UNLOCK";
            return response;
        }

        /// <summary>
        /// The target of a request path, or null when it is not under the secret, or names something that
        /// could step out of a shared folder.
        /// </summary>
        private Target? Resolve(string rawPath)
        {
            var segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
            if (segments.Count == 0 || segments[0] != Secret)
                return null;
            if (segments.Count == 1)
                return new Target(null, string.Empty);
            if (segments.Skip(1).Any(s => !IsValidName(s)))
                return null;

            var folder = _folders().FirstOrDefault(f => string.Equals(f.Name, segments[1], StringComparison.OrdinalIgnoreCase));
            return folder is null ? null : new Target(folder, Path.Combine(segments.Skip(2).ToArray()));
        }

        /// <summary>As for 9P: a name stays a name, with nothing Windows reads as a drive, stream or device.</summary>
        private static bool IsValidName(string name) =>
            name.Length is > 0 and <= 255 && name != "." && name != ".."
            && name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0
            && !name.Any(c => c < ' ') && !name.EndsWith('.') && !name.EndsWith(' ');

        private static string HostPath(Target target) =>
            target.Relative.Length == 0 ? target.Folder!.HostPath : Path.Combine(target.Folder!.HostPath, target.Relative);

        /// <summary>
        /// A 403 when the target cannot be written (the virtual root, a shared folder itself, or anything in
        /// a read-only one), null when it can.
        /// </summary>
        private static Response? Refusal(Target target) =>
            target.IsRoot || target.Folder!.ReadOnly || target.Relative.Length == 0 ? new Response { Status = 403 } : null;

        private Response PropFind(Target target, string path, Request request)
        {
            var depth = request.Headers.TryGetValue("Depth", out var d) ? d : "1";
            var self = path.EndsWith('/') ? path : path + "/";
            var entries = new List<XElement>();

            if (target.IsRoot)
            {
                entries.Add(Entry(self, null, isCollection: true));
                if (depth != "0")
                {
                    foreach (var folder in _folders())
                        entries.Add(Entry(self + Uri.EscapeDataString(folder.Name) + "/", Directory.Exists(folder.HostPath) ? new DirectoryInfo(folder.HostPath) : null, isCollection: true));
                }
                return MultiStatus(entries);
            }

            var hostPath = HostPath(target);
            if (Directory.Exists(hostPath))
            {
                var directory = new DirectoryInfo(hostPath);
                entries.Add(Entry(self, directory, isCollection: true));
                if (depth != "0")
                {
                    foreach (var child in directory.EnumerateFileSystemInfos())
                    {
                        // Hidden system files of the host (desktop.ini and the like) are the guest's business too,
                        // but a name the guest could not ask for back would only cause errors.
                        if (!IsValidName(child.Name))
                            continue;
                        var isDirectory = child is DirectoryInfo;
                        entries.Add(Entry(self + Uri.EscapeDataString(child.Name) + (isDirectory ? "/" : string.Empty), child, isDirectory));
                    }
                }
                return MultiStatus(entries);
            }

            if (File.Exists(hostPath))
                return MultiStatus(new[] { Entry(path.TrimEnd('/'), new FileInfo(hostPath), isCollection: false) });

            return new Response { Status = 404 };
        }

        private static XElement Entry(string href, FileSystemInfo? info, bool isCollection)
        {
            var properties = new XElement(Dav + "prop",
                new XElement(Dav + "resourcetype", isCollection ? new XElement(Dav + "collection") : null));
            if (info is not null)
            {
                properties.Add(
                    new XElement(Dav + "creationdate", info.CreationTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                    new XElement(Dav + "getlastmodified", info.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture)),
                    new XElement(Dav + "displayname", info.Name));
                if (info is FileInfo file)
                {
                    properties.Add(
                        new XElement(Dav + "getcontentlength", file.Length.ToString(CultureInfo.InvariantCulture)),
                        new XElement(Dav + "getcontenttype", "application/octet-stream"),
                        new XElement(Dav + "getetag", $"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\""));
                }
            }
            return new XElement(Dav + "response",
                new XElement(Dav + "href", href),
                new XElement(Dav + "propstat", properties, new XElement(Dav + "status", "HTTP/1.1 200 OK")));
        }

        private static Response MultiStatus(IEnumerable<XElement> entries)
        {
            var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement(Dav + "multistatus", new XAttribute(XNamespace.Xmlns + "D", Dav), entries));
            var response = new Response { Status = 207, Body = Utf8.GetBytes(document.Declaration + document.ToString(SaveOptions.DisableFormatting)) };
            response.Headers["Content-Type"] = "application/xml; charset=utf-8";
            return response;
        }

        private static Response Get(Target target, Request request)
        {
            if (target.IsRoot || Directory.Exists(HostPath(target)))
                return new Response { Status = 405 };

            var file = new FileInfo(HostPath(target));
            if (!file.Exists)
                return new Response { Status = 404 };

            long start = 0, length = file.Length;
            var status = 200;
            if (request.Headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
                && !range.Contains(','))
            {
                var bounds = range[6..].Split('-');
                if (bounds[0].Length > 0 && long.TryParse(bounds[0], out var first))
                {
                    var last = bounds[1].Length > 0 && long.TryParse(bounds[1], out var end) ? Math.Min(end, file.Length - 1) : file.Length - 1;
                    if (first >= file.Length || last < first)
                        return new Response { Status = 416 };
                    start = first;
                    length = last - first + 1;
                    status = 206;
                }
            }

            var response = new Response { Status = status, File = file.FullName, FileOffset = start, FileLength = length };
            response.Headers["Content-Type"] = "application/octet-stream";
            response.Headers["Last-Modified"] = file.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture);
            response.Headers["Accept-Ranges"] = "bytes";
            if (status == 206)
                response.Headers["Content-Range"] = $"bytes {start}-{start + length - 1}/{file.Length}";
            return response;
        }

        private static async Task<Response> PutAsync(Target target, Request request, CancellationToken cancellationToken)
        {
            if (Refusal(target) is { } refused)
                return refused;
            var path = HostPath(target);
            if (Directory.Exists(path))
                return new Response { Status = 405 };
            if (!Directory.Exists(Path.GetDirectoryName(path)))
                return new Response { Status = 409 };

            var existed = File.Exists(path);
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
                await request.Body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            return new Response { Status = existed ? 204 : 201 };
        }

        private static Response Delete(Target target)
        {
            if (Refusal(target) is { } refused)
                return refused;
            var path = HostPath(target);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
            else
                return new Response { Status = 404 };
            return new Response { Status = 204 };
        }

        private static Response MakeCollection(Target target)
        {
            if (Refusal(target) is { } refused)
                return refused;
            var path = HostPath(target);
            if (Directory.Exists(path) || File.Exists(path))
                return new Response { Status = 405 };
            if (!Directory.Exists(Path.GetDirectoryName(path)))
                return new Response { Status = 409 };
            Directory.CreateDirectory(path);
            return new Response { Status = 201 };
        }

        private Response MoveOrCopy(Target source, Request request)
        {
            if (!request.Headers.TryGetValue("Destination", out var destinationHeader))
                return new Response { Status = 400 };
            var destinationPath = Uri.TryCreate(destinationHeader, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : destinationHeader;
            if (Resolve(destinationPath) is not { } destination)
                return new Response { Status = 403 };

            var move = request.Method == "MOVE";
            if ((move && Refusal(source) is not null) || Refusal(destination) is not null)
                return new Response { Status = 403 };

            var from = HostPath(source);
            var to = HostPath(destination);
            var overwrite = !request.Headers.TryGetValue("Overwrite", out var flag) || !flag.Equals("F", StringComparison.OrdinalIgnoreCase);
            var exists = File.Exists(to) || Directory.Exists(to);
            if (exists && !overwrite)
                return new Response { Status = 412 };
            if (!Directory.Exists(Path.GetDirectoryName(to)))
                return new Response { Status = 409 };

            // A rename that only changes case is a move onto itself as far as Windows is concerned.
            var sameItem = string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase);
            if (exists && !sameItem)
            {
                if (Directory.Exists(to))
                    Directory.Delete(to, recursive: true);
                else
                    File.Delete(to);
            }

            if (Directory.Exists(from))
            {
                if (move)
                    Directory.Move(from, to);
                else
                    CopyDirectory(from, to);
            }
            else if (File.Exists(from))
            {
                if (move)
                    File.Move(from, to, overwrite: true);
                else
                    File.Copy(from, to, overwrite: true);
            }
            else
                return new Response { Status = 404 };

            return new Response { Status = exists && !sameItem ? 204 : 201 };
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
            foreach (var directory in Directory.EnumerateDirectories(from))
                CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
        }

        /// <summary>
        /// Every property set is reported as done. Windows sets file times this way after copying a file,
        /// and an error makes it report the copy as failed; the times themselves are the host's.
        /// </summary>
        private static Response PropPatch(string path, byte[] body)
        {
            var properties = new XElement(Dav + "prop");
            try
            {
                var document = XDocument.Parse(Utf8.GetString(body));
                foreach (var property in document.Descendants(Dav + "prop").Elements())
                    properties.Add(new XElement(property.Name));
            }
            catch (System.Xml.XmlException)
            {
                // Nothing to report back then.
            }
            return MultiStatus(new[]
            {
                new XElement(Dav + "response",
                    new XElement(Dav + "href", path),
                    new XElement(Dav + "propstat", properties, new XElement(Dav + "status", "HTTP/1.1 200 OK")))
            });
        }

        /// <summary>
        /// Acknowledged without locking: the guest is the folders' only remote writer, and programs refuse
        /// to save on a WebDAV server that does not answer LOCK. A LOCK on a missing name creates it empty,
        /// as RFC 4918 says, which is how Windows creates a file before writing it.
        /// </summary>
        private static Response Lock(Target target, string path, Request request)
        {
            if (Refusal(target) is null)
            {
                var hostPath = HostPath(target);
                if (!File.Exists(hostPath) && !Directory.Exists(hostPath) && Directory.Exists(Path.GetDirectoryName(hostPath)))
                    File.WriteAllBytes(hostPath, Array.Empty<byte>());
            }

            var token = "opaquelocktoken:" + Guid.NewGuid().ToString("D");
            var timeout = request.Headers.TryGetValue("Timeout", out var t) ? t : "Second-3600";
            var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement(Dav + "prop", new XAttribute(XNamespace.Xmlns + "D", Dav),
                    new XElement(Dav + "lockdiscovery",
                        new XElement(Dav + "activelock",
                            new XElement(Dav + "locktype", new XElement(Dav + "write")),
                            new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")),
                            new XElement(Dav + "depth", "0"),
                            new XElement(Dav + "timeout", timeout),
                            new XElement(Dav + "locktoken", new XElement(Dav + "href", token)),
                            new XElement(Dav + "lockroot", new XElement(Dav + "href", path))))));
            var response = new Response { Status = 200, Body = Utf8.GetBytes(document.Declaration + document.ToString(SaveOptions.DisableFormatting)) };
            response.Headers["Content-Type"] = "application/xml; charset=utf-8";
            response.Headers["Lock-Token"] = $"<{token}>";
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            if (_accepting is not null)
                await _accepting.ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
