using System.Buffers.Binary;
using System.Text;
using WinQEMU.Core.Sharing;

namespace WinQEMU.Core.Tests;

/// <summary>
/// The 9P2000.L server behind shared folders, driven with the messages Linux's v9fs client sends, on a
/// real temporary folder.
/// </summary>
public sealed class NinePServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winqemu-9p-" + Guid.NewGuid().ToString("N"));
    private readonly List<SharedFolderEntry> _folders = new();
    private readonly NinePServer _server;
    private ushort _tag;

    public NinePServerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Documents", "Sous-dossier"));
        File.WriteAllText(Path.Combine(_root, "Documents", "lisez-moi.txt"), "Bonjour depuis Windows");
        Directory.CreateDirectory(Path.Combine(_root, "Musique"));
        _folders.Add(new SharedFolderEntry("Documents", Path.Combine(_root, "Documents"), ReadOnly: false));
        _folders.Add(new SharedFolderEntry("Musique", Path.Combine(_root, "Musique"), ReadOnly: true));
        _server = new NinePServer(() => _folders);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ---- Message building, as the client does ----

    private sealed class Message
    {
        private readonly MemoryStream _body = new();
        public Message U8(byte v) { _body.WriteByte(v); return this; }
        public Message U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _body.Write(b); return this; }
        public Message U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _body.Write(b); return this; }
        public Message U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _body.Write(b); return this; }
        public Message Str(string s) { var b = Encoding.UTF8.GetBytes(s); U16((ushort)b.Length); _body.Write(b); return this; }
        public Message Bytes(byte[] b) { _body.Write(b); return this; }
        public byte[] Body => _body.ToArray();
    }

    private sealed class Reply
    {
        private readonly byte[] _data;
        private int _position = 7;
        public Reply(byte[] data) => _data = data;
        public byte Type => _data[4];
        public byte U8() => _data[_position++];
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_position)); _position += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_position)); _position += 4; return v; }
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_position)); _position += 8; return v; }
        public string Str() { var n = U16(); var s = Encoding.UTF8.GetString(_data, _position, n); _position += n; return s; }
        public byte[] Bytes(int n) { var b = _data.AsSpan(_position, n).ToArray(); _position += n; return b; }
        public void Skip(int n) => _position += n;
    }

    private Reply Send(byte type, Message message)
    {
        var request = new byte[3 + message.Body.Length];
        request[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(1), ++_tag);
        message.Body.CopyTo(request, 3);
        var bytes = _server.Handle(request);
        // The client pairs replies with requests by tag alone.
        Assert.Equal(_tag, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5)));
        Assert.Equal((uint)bytes.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        return new Reply(bytes);
    }

    /// <summary>Sends, and expects success; returns the reply positioned after the header.</summary>
    private Reply Ok(byte type, Message message)
    {
        var reply = Send(type, message);
        if (reply.Type == 7)
            Assert.Fail($"T{type} failed with errno {reply.U32()}");
        Assert.Equal(type + 1, reply.Type);
        return reply;
    }

    private uint Errno(byte type, Message message)
    {
        var reply = Send(type, message);
        Assert.Equal(7, reply.Type);
        return reply.U32();
    }

    private void Mount()
    {
        var version = Ok(100, new Message().U32(262144).Str("9P2000.L"));
        Assert.Equal(262144u, version.U32());
        Assert.Equal("9P2000.L", version.Str());
        Ok(104, new Message().U32(0).U32(0xFFFFFFFF).Str("root").Str("1000:1000").U32(0));
    }

    private Reply Walk(uint fid, uint newFid, params string[] names)
    {
        var message = new Message().U32(fid).U32(newFid).U16((ushort)names.Length);
        foreach (var name in names)
            message.Str(name);
        return Send(110, message);
    }

    private List<string> ReadDirectory(uint fid)
    {
        Ok(12, new Message().U32(fid).U32(0x10000));   // O_DIRECTORY
        var reply = Ok(40, new Message().U32(fid).U64(0).U32(8192));
        var count = reply.U32();
        var names = new List<string>();
        var consumed = 0;
        while (consumed < count)
        {
            reply.Skip(13 + 8 + 1);
            var name = reply.Str();
            names.Add(name);
            consumed += 13 + 8 + 1 + 2 + Encoding.UTF8.GetByteCount(name);
        }
        return names;
    }

    // ---- Tests ----

    [Fact]
    public void The_root_lists_one_directory_per_shared_folder()
    {
        Mount();
        Ok(110, new Message().U32(0).U32(1).U16(0));   // clone the root, as the client does before opening

        Assert.Equal(new[] { ".", "..", "Documents", "Musique" }, ReadDirectory(1));
    }

    [Fact]
    public void A_folder_added_while_mounted_appears_and_a_removed_one_goes()
    {
        Mount();
        Directory.CreateDirectory(Path.Combine(_root, "Photos"));
        _folders.Add(new SharedFolderEntry("Photos", Path.Combine(_root, "Photos"), false));
        _folders.RemoveAt(1);

        Ok(110, new Message().U32(0).U32(1).U16(0));
        Assert.Equal(new[] { ".", "..", "Documents", "Photos" }, ReadDirectory(1));
        Assert.Equal(2u, Errno(110, new Message().U32(0).U32(2).U16(1).Str("Musique")));   // ENOENT
    }

    [Fact]
    public void A_host_file_reads_back_with_its_size_and_the_guest_user_as_owner()
    {
        Mount();
        var walk = Ok(110, new Message().U32(0).U32(1).U16(2).Str("Documents").Str("lisez-moi.txt"));
        Assert.Equal(2, walk.U16());

        var attributes = Ok(24, new Message().U32(1).U64(0x7FF));
        attributes.U64();
        attributes.Skip(13);
        Assert.Equal(0x81A4u, attributes.U32());   // regular file, 0644
        Assert.Equal(1000u, attributes.U32());
        Assert.Equal(1000u, attributes.U32());
        attributes.U64(); attributes.U64();
        Assert.Equal((ulong)"Bonjour depuis Windows".Length, attributes.U64());

        Ok(12, new Message().U32(1).U32(0));   // O_RDONLY
        var read = Ok(116, new Message().U32(1).U64(8).U32(100));
        var count = read.U32();
        Assert.Equal("depuis Windows", Encoding.UTF8.GetString(read.Bytes((int)count)));
    }

    [Fact]
    public void The_guest_creates_writes_renames_and_deletes_in_a_writable_folder()
    {
        Mount();
        Ok(110, new Message().U32(0).U32(1).U16(1).Str("Documents"));
        Ok(110, new Message().U32(1).U32(2).U16(0));

        // Lcreate turns fid 2 into the new file.
        Ok(14, new Message().U32(2).Str("créé dans l'invité.txt").U32(0x41).U32(0x1A4).U32(1000));   // O_WRONLY|O_CREAT
        Ok(118, new Message().U32(2).U64(0).U32(5).Bytes(Encoding.UTF8.GetBytes("salut")));
        Ok(120, new Message().U32(2));
        Assert.Equal("salut", File.ReadAllText(Path.Combine(_root, "Documents", "créé dans l'invité.txt")));

        Ok(72, new Message().U32(1).Str("Nouveau").U32(0x1ED).U32(1000));
        Ok(74, new Message().U32(1).Str("créé dans l'invité.txt").U32(1).Str("renommé.txt"));
        Assert.True(File.Exists(Path.Combine(_root, "Documents", "renommé.txt")));

        Ok(76, new Message().U32(1).Str("renommé.txt").U32(0));
        Ok(76, new Message().U32(1).Str("Nouveau").U32(0x200));   // AT_REMOVEDIR
        Assert.False(File.Exists(Path.Combine(_root, "Documents", "renommé.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Documents", "Nouveau")));
    }

    [Fact]
    public void A_truncate_through_setattr_shortens_the_host_file()
    {
        Mount();
        Ok(110, new Message().U32(0).U32(1).U16(2).Str("Documents").Str("lisez-moi.txt"));

        Ok(26, new Message().U32(1).U32(0x8).U32(0).U32(0).U32(0).U64(7).U64(0).U64(0).U64(0).U64(0));

        Assert.Equal("Bonjour", File.ReadAllText(Path.Combine(_root, "Documents", "lisez-moi.txt")));
    }

    [Fact]
    public void A_read_only_folder_refuses_every_change()
    {
        Mount();
        Ok(110, new Message().U32(0).U32(1).U16(1).Str("Musique"));
        Ok(110, new Message().U32(1).U32(2).U16(0));

        Assert.Equal(30u, Errno(14, new Message().U32(2).Str("x.txt").U32(0x41).U32(0x1A4).U32(0)));   // EROFS
        Assert.Equal(30u, Errno(72, new Message().U32(1).Str("x").U32(0x1ED).U32(0)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "Musique")));
    }

    [Fact]
    public void Names_cannot_leave_the_folder_or_reach_windows_streams()
    {
        Mount();
        Ok(110, new Message().U32(0).U32(1).U16(1).Str("Documents"));
        Ok(110, new Message().U32(1).U32(2).U16(0));

        // ".." from a shared folder leads to the virtual root, never to the host folder above it.
        var up = Ok(110, new Message().U32(1).U32(3).U16(2).Str("..").Str("..")).U16();
        Assert.Equal(2, up);
        Assert.Equal(new[] { ".", "..", "Documents", "Musique" }, ReadDirectory(3));

        Assert.Equal(22u, Errno(14, new Message().U32(2).Str("lisez-moi.txt:secret").U32(0x41).U32(0x1A4).U32(0)));   // EINVAL
        Assert.Equal(22u, Errno(110, new Message().U32(1).U32(4).U16(1).Str(@"..\..\Windows")));
    }

    [Fact]
    public void A_missing_file_is_enoent_and_a_walk_stops_short_after_the_first_name()
    {
        Mount();

        Assert.Equal(2u, Errno(110, new Message().U32(0).U32(1).U16(1).Str("Absent")));
        var partial = Ok(110, new Message().U32(0).U32(1).U16(2).Str("Documents").Str("absent.txt"));
        Assert.Equal(1, partial.U16());
    }

    [Fact]
    public async Task The_server_answers_over_a_stream_until_it_ends()
    {
        var request = new MemoryStream();
        var version = new Message().U32(65536).Str("9P2000.L").Body;
        var header = new byte[7];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(7 + version.Length));
        header[4] = 100;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), 0xFFFF);
        request.Write(header);
        request.Write(version);
        request.Position = 0;

        var duplex = new DuplexStream(request);
        await _server.ServeAsync(duplex);

        var reply = new Reply(duplex.Written.ToArray());
        Assert.Equal(101, reply.Type);
        Assert.Equal(65536u, reply.U32());
    }

    /// <summary>Reads from one buffer and writes to another, as the virtio-serial socket does in both directions.</summary>
    private sealed class DuplexStream : Stream
    {
        private readonly Stream _input;
        public DuplexStream(Stream input) => _input = input;
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }
}
