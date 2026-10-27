using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using WinQEMU.Core.Sharing;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Shared folders for Windows guests: the WebDAV server Windows' own client reads, on a real folder.
/// </summary>
public sealed class WebDavServerTests : IAsyncLifetime
{
    private static readonly XNamespace Dav = "DAV:";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wq-webdav-" + Guid.NewGuid().ToString("N"));
    private readonly List<SharedFolderEntry> _folders = new();
    private WebDavServer _server = null!;
    private HttpClient _http = null!;

    private string Documents => Path.Combine(_root, "Documents");
    private string Locked => Path.Combine(_root, "Locked");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Documents);
        Directory.CreateDirectory(Locked);
        File.WriteAllText(Path.Combine(Documents, "note.txt"), "bonjour");
        File.WriteAllText(Path.Combine(Locked, "keep.txt"), "à garder");
        _folders.Add(new SharedFolderEntry("Documents", Documents, false));
        _folders.Add(new SharedFolderEntry("Locked", Locked, true));

        _server = new WebDavServer(() => _folders, "s3cret");
        _server.Start();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private Task<HttpResponseMessage> SendAsync(string method, string path, HttpContent? content = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return _http.SendAsync(request);
    }

    private async Task<List<string>> ListAsync(string path)
    {
        var response = await SendAsync("PROPFIND", path, null, ("Depth", "1"));
        Assert.Equal(207, (int)response.StatusCode);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.Descendants(Dav + "href").Select(h => Uri.UnescapeDataString(h.Value)).ToList();
    }

    [Fact]
    public async Task The_secret_root_lists_the_shared_folders()
    {
        var hrefs = await ListAsync("s3cret/");

        Assert.Contains("/s3cret/Documents/", hrefs);
        Assert.Contains("/s3cret/Locked/", hrefs);
    }

    [Fact]
    public async Task Without_the_secret_nothing_is_found()
    {
        // The server root answers, as Windows probes it, but lists nothing.
        Assert.Equal(new[] { "/" }, await ListAsync("/"));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("PROPFIND", "guess/Documents/", null, ("Depth", "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("Documents/note.txt")).StatusCode);
    }

    [Fact]
    public async Task A_folder_lists_its_files_with_their_size()
    {
        var response = await SendAsync("PROPFIND", "s3cret/Documents/", null, ("Depth", "1"));
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var note = document.Descendants(Dav + "response").Single(r => r.Element(Dav + "href")!.Value.EndsWith("note.txt"));

        Assert.Equal("7", note.Descendants(Dav + "getcontentlength").Single().Value);
        Assert.Empty(note.Descendants(Dav + "collection"));
    }

    [Fact]
    public async Task A_file_written_by_the_guest_lands_in_the_host_folder_and_reads_back()
    {
        var content = new byte[3 * 1024 * 1024 + 17];
        new Random(4).NextBytes(content);

        var put = await SendAsync("PUT", "s3cret/Documents/" + Uri.EscapeDataString("été 2026.bin"), new ByteArrayContent(content));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(Documents, "été 2026.bin")));

        Assert.Equal(content, await _http.GetByteArrayAsync("s3cret/Documents/" + Uri.EscapeDataString("été 2026.bin")));
    }

    [Fact]
    public async Task A_range_reads_part_of_a_file()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "s3cret/Documents/note.txt");
        request.Headers.Range = new RangeHeaderValue(3, 6);
        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("jour", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Folders_are_made_moved_copied_and_deleted()
    {
        Assert.Equal(HttpStatusCode.Created, (await SendAsync("MKCOL", "s3cret/Documents/Photos")).StatusCode);
        Assert.True(Directory.Exists(Path.Combine(Documents, "Photos")));

        var destination = new Uri(_http.BaseAddress!, "s3cret/Documents/Photos/renamed.txt").ToString();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync("MOVE", "s3cret/Documents/note.txt", null, ("Destination", destination))).StatusCode);
        Assert.False(File.Exists(Path.Combine(Documents, "note.txt")));
        Assert.Equal("bonjour", File.ReadAllText(Path.Combine(Documents, "Photos", "renamed.txt")));

        var copy = new Uri(_http.BaseAddress!, "s3cret/Documents/Copie").ToString();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync("COPY", "s3cret/Documents/Photos", null, ("Destination", copy))).StatusCode);
        Assert.Equal("bonjour", File.ReadAllText(Path.Combine(Documents, "Copie", "renamed.txt")));

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync("DELETE", "s3cret/Documents/Photos")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(Documents, "Photos")));
    }

    [Fact]
    public async Task A_read_only_folder_refuses_every_write()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("PUT", "s3cret/Locked/new.txt", new StringContent("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("DELETE", "s3cret/Locked/keep.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("MKCOL", "s3cret/Locked/sub")).StatusCode);
        var destination = new Uri(_http.BaseAddress!, "s3cret/Locked/moved.txt").ToString();
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("MOVE", "s3cret/Documents/note.txt", null, ("Destination", destination))).StatusCode);

        Assert.True(File.Exists(Path.Combine(Locked, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(Documents, "note.txt")));
        Assert.Equal("à garder", await _http.GetStringAsync("s3cret/Locked/keep.txt"));
    }

    [Fact]
    public async Task The_shared_folders_themselves_cannot_be_deleted()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("DELETE", "s3cret/Documents")).StatusCode);
        Assert.True(Directory.Exists(Documents));
    }

    [Theory]
    [InlineData("s3cret/Documents/..%2F..%2FWindows")]
    [InlineData("s3cret/Documents/note.txt%3Astream")]
    [InlineData("s3cret/Documents/..%5C..%5Cboot.ini")]
    public async Task Nothing_steps_out_of_a_shared_folder(string path)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("/s3cret/Documents/../Locked/keep.txt")]
    [InlineData("/s3cret/Documents/%2e%2e/%2e%2e/")]
    public async Task Dot_dot_sent_as_is_is_refused(string path)
    {
        // HttpClient resolves ".." before sending, so this one is written by hand, as any client could.
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"));
        var reply = await new StreamReader(stream).ReadToEndAsync();

        Assert.StartsWith("HTTP/1.1 404", reply);
    }

    [Fact]
    public async Task A_lock_on_a_new_name_creates_it_empty_and_answers_a_token()
    {
        var response = await SendAsync("LOCK", "s3cret/Documents/nouveau.docx",
            new StringContent("<?xml version=\"1.0\"?><D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockinfo>", Encoding.UTF8, "text/xml"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("<opaquelocktoken:", response.Headers.GetValues("Lock-Token").Single());
        Assert.True(File.Exists(Path.Combine(Documents, "nouveau.docx")));
    }

    [Fact]
    public async Task Setting_file_times_is_acknowledged()
    {
        // Windows reports a copy as failed when its PROPPATCH of the file times is refused.
        var body = "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:propertyupdate xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\"><D:set><D:prop>" +
                   "<Z:Win32LastModifiedTime>Tue, 27 Oct 2026 10:00:00 GMT</Z:Win32LastModifiedTime></D:prop></D:set></D:propertyupdate>";
        var response = await SendAsync("PROPPATCH", "s3cret/Documents/note.txt", new StringContent(body, Encoding.UTF8, "text/xml"));

        Assert.Equal(207, (int)response.StatusCode);
        Assert.Contains("200 OK", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Folders_shared_while_the_guest_runs_appear_at_once()
    {
        var music = Path.Combine(_root, "Music");
        Directory.CreateDirectory(music);
        _folders.Add(new SharedFolderEntry("Music", music, false));

        Assert.Contains("/s3cret/Music/", await ListAsync("s3cret/"));
    }

    [Fact]
    public async Task Several_requests_go_through_one_connection()
    {
        for (var i = 0; i < 20; i++)
            Assert.Equal("bonjour", await _http.GetStringAsync("s3cret/Documents/note.txt"));
    }
}
