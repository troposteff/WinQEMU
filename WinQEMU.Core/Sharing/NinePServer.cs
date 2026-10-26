using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinQEMU.Core.Sharing
{
    /// <summary>A host folder as the guest sees it: a directory named <see cref="Name"/> under the mount.</summary>
    public sealed record SharedFolderEntry(string Name, string HostPath, bool ReadOnly);

    /// <summary>
    /// A 9P2000.L file server for the guest's own 9p client, serving host folders the way VMware's shared
    /// folders do: through a private channel between the machine and the host, with no network, account
    /// or password involved.
    /// </summary>
    /// <remarks>
    /// The QEMU build has no 9p of its own ("fsdev support is disabled"), so the protocol is served here
    /// and carried over a virtio-serial port, which the guest mounts with the kernel's fd transport
    /// (mount -t 9p -o trans=fd). The root is virtual: one directory per shared folder, read live from
    /// the configuration, so folders come and go while the guest runs, as /mnt/hgfs does.
    ///
    /// Requests are answered one at a time, in order. The kernel matches replies by tag, so ordering is
    /// never wrong, only less parallel than it could be.
    /// </remarks>
    public sealed class NinePServer
    {
        private const uint NoFid = 0xFFFFFFFF;
        private const string Dialect = "9P2000.L";
        private const uint MaxMessageSize = 1024 * 1024;
        private const uint V9fsMagic = 0x01021997;

        // Linux errno values, which is what 9P2000.L carries.
        private const uint EPERM = 1, ENOENT = 2, EIO = 5, EBADF = 9, EACCES = 13, EBUSY = 16, EEXIST = 17,
            ENOTDIR = 20, EISDIR = 21, EINVAL = 22, ENOSPC = 28, EROFS = 30, ENAMETOOLONG = 36, ENOTEMPTY = 39,
            EOPNOTSUPP = 95;

        private readonly Func<IReadOnlyList<SharedFolderEntry>> _folders;
        private readonly Dictionary<uint, Fid> _fids = new();
        private uint _messageSize = 8192;
        private uint _uid;
        private uint _gid;

        /// <param name="folders">Read on every lookup, so the shared folders can change while the guest runs.</param>
        public NinePServer(Func<IReadOnlyList<SharedFolderEntry>> folders)
        {
            _folders = folders;
        }

        /// <summary>Answers requests until the stream ends.</summary>
        public async Task ServeAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var header = new byte[4];
            try
            {
                while (await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
                {
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
                    if (size < 7 || size > MaxMessageSize + 64)
                        throw new InvalidDataException($"A 9P message of {size} bytes cannot be right.");

                    var body = new byte[size - 4];
                    if (!await ReadExactlyOrEndAsync(stream, body, cancellationToken).ConfigureAwait(false))
                        return;

                    var reply = Handle(body);
                    await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                Reset();
            }
        }

        private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    return false;
                read += count;
            }
            return true;
        }

        /// <summary>One request (type, tag and body, without the size) to its complete reply message.</summary>
        public byte[] Handle(ReadOnlySpan<byte> request)
        {
            var type = request[0];
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(request[1..]);
            var reader = new Reader(request[3..].ToArray());
            var reply = new Writer();
            try
            {
                Dispatch(type, reader, reply);
                return reply.ToMessage((byte)(type + 1), tag);
            }
            catch (Exception ex)
            {
                var error = new Writer();
                error.U32(ErrorNumber(ex));
                return error.ToMessage(7, tag);   // Rlerror
            }
        }

        private void Dispatch(byte type, Reader r, Writer w)
        {
            switch (type)
            {
                case 100: Version(r, w); break;
                case 104: Attach(r, w); break;
                case 110: Walk(r, w); break;
                case 24: GetAttr(r, w); break;
                case 26: SetAttr(r); break;
                case 12: Open(r, w); break;
                case 14: Create(r, w); break;
                case 116: Read(r, w); break;
                case 118: Write(r, w); break;
                case 40: ReadDirectory(r, w); break;
                case 72: MakeDirectory(r, w); break;
                case 76: Unlink(r); break;
                case 74: RenameAt(r); break;
                case 20: Rename(r); break;
                case 8: StatFs(r, w); break;
                case 50: Fsync(r); break;
                case 120: Clunk(r); break;
                case 122: Remove(r); break;
                case 108: break;   // Tflush: every request is already answered by the time it could arrive.
                case 52: w.U8(0); break;   // Tlock: granted. Locks are advisory and the guest is the only client.
                case 54: GetLock(r, w); break;
                default: throw new NinePException(EOPNOTSUPP);   // xattrs, links, devices, auth
            }
        }

        // ---- Session ----

        private void Version(Reader r, Writer w)
        {
            var clientSize = r.U32();
            var version = r.Str();
            Reset();
            _messageSize = Math.Min(clientSize, MaxMessageSize);
            w.U32(_messageSize);
            w.Str(version.StartsWith(Dialect, StringComparison.Ordinal) ? Dialect : "unknown");
        }

        /// <summary>The aname is "uid:gid", which files are reported as owned by: the guest user's.</summary>
        private void Attach(Reader r, Writer w)
        {
            var fid = r.U32();
            r.U32();   // afid: no authentication, the channel is private to this machine
            r.Str();   // uname
            var aname = r.Str();
            var parts = aname.Split(':');
            _uid = parts.Length == 2 && uint.TryParse(parts[0], out var uid) ? uid : 0;
            _gid = parts.Length == 2 && uint.TryParse(parts[1], out var gid) ? gid : 0;

            var root = new Fid(Node.Root);
            _fids[fid] = root;
            w.Qid(QidOf(root.Node));
        }

        private void Reset()
        {
            foreach (var fid in _fids.Values)
                fid.Close();
            _fids.Clear();
        }

        // ---- Lookup ----

        private void Walk(Reader r, Writer w)
        {
            var fid = Require(r.U32());
            var newFid = r.U32();
            var count = r.U16();
            var node = fid.Node;
            var qids = new List<Qid>();

            for (var i = 0; i < count; i++)
            {
                var name = r.Str();
                Node? next;
                try
                {
                    next = Child(node, name);
                }
                catch (NinePException) when (i > 0)
                {
                    next = null;
                }

                if (next is null || !Exists(next))
                {
                    // Only a failure on the first name is an error; after it the walk stops short.
                    if (i == 0)
                        throw new NinePException(ENOENT);
                    break;
                }
                node = next;
                qids.Add(QidOf(node));
            }

            if (qids.Count == count)
            {
                if (_fids.Remove(newFid, out var replaced) && !ReferenceEquals(replaced, fid))
                    replaced.Close();
                _fids[newFid] = new Fid(node);
            }

            w.U16((ushort)qids.Count);
            foreach (var qid in qids)
                w.Qid(qid);
        }

        private Node? Child(Node parent, string name)
        {
            if (name == "..")
                return parent.Folder is null ? Node.Root
                     : parent.Relative.Length == 0 ? Node.Root
                     : parent with { Relative = Path.GetDirectoryName(parent.Relative) ?? string.Empty };

            ValidateName(name);
            if (parent.Folder is null)
            {
                var folder = _folders().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                return folder is null ? null : new Node(folder, string.Empty);
            }
            return parent with { Relative = Path.Combine(parent.Relative, name) };
        }

        /// <summary>
        /// Names come from the guest and must stay names: no separators, nothing Windows reads as a drive,
        /// a stream (file:stream) or a device, and nothing it would silently alter.
        /// </summary>
        private static void ValidateName(string name)
        {
            if (name.Length == 0 || name == ".")
                throw new NinePException(EINVAL);
            if (name.Length > 255)
                throw new NinePException(ENAMETOOLONG);
            if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0
                || name.Any(c => c < ' ') || name.EndsWith('.') || name.EndsWith(' '))
                throw new NinePException(EINVAL);
        }

        private bool Exists(Node node) =>
            node.Folder is null || Directory.Exists(PathOf(node)) || File.Exists(PathOf(node));

        /// <summary>The folder is looked up again by name: one removed while the guest runs is gone.</summary>
        private string PathOf(Node node)
        {
            if (node.Folder is null)
                throw new NinePException(EISDIR);
            var folder = _folders().FirstOrDefault(f => f.Name == node.Folder.Name) ?? throw new NinePException(ENOENT);
            return node.Relative.Length == 0 ? folder.HostPath : Path.Combine(folder.HostPath, node.Relative);
        }

        private bool IsReadOnly(Node node) =>
            node.Folder is null || (_folders().FirstOrDefault(f => f.Name == node.Folder.Name)?.ReadOnly ?? true);

        private bool IsDirectory(Node node) => node.Folder is null || Directory.Exists(PathOf(node));

        // ---- Attributes ----

        private void GetAttr(Reader r, Writer w)
        {
            var fid = Require(r.U32());
            r.U64();   // request mask: everything basic is always returned
            var node = fid.Node;

            FileSystemInfo? info = node.Folder is null ? null : InfoOf(node);
            var directory = info is null || info is DirectoryInfo;
            var readOnly = IsReadOnly(node) || info?.Attributes.HasFlag(FileAttributes.ReadOnly) == true && !directory;
            var size = info is FileInfo file ? file.Length : 4096;

            w.U64(0x7FF);   // P9_GETATTR_BASIC
            w.Qid(QidOf(node));
            w.U32((directory ? 0x4000u : 0x8000u) | (directory ? readOnly ? 0x16Du : 0x1EDu : readOnly ? 0x124u : 0x1A4u));
            w.U32(_uid);
            w.U32(_gid);
            w.U64(directory ? 2UL : 1UL);
            w.U64(0);
            w.U64((ulong)size);
            w.U64(4096);
            w.U64((ulong)((size + 511) / 512));
            var (accessed, modified, created) = info is null
                ? (DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow)
                : (info.LastAccessTimeUtc, info.LastWriteTimeUtc, info.CreationTimeUtc);
            w.Time(accessed);
            w.Time(modified);
            w.Time(modified);   // ctime: Windows keeps no change time; the last write is the nearest
            w.Time(created);
            w.U64(0);
            w.U64(0);
        }

        private FileSystemInfo InfoOf(Node node)
        {
            var path = PathOf(node);
            if (Directory.Exists(path))
                return new DirectoryInfo(path);
            if (File.Exists(path))
                return new FileInfo(path);
            throw new NinePException(ENOENT);
        }

        private void SetAttr(Reader r)
        {
            var fid = Require(r.U32());
            var valid = r.U32();
            r.U32(); r.U32(); r.U32();   // mode, uid, gid: Windows has no equivalent; accepted and ignored
            var size = r.U64();
            var atimeSec = r.U64(); var atimeNsec = r.U64();
            var mtimeSec = r.U64(); var mtimeNsec = r.U64();

            const uint Size = 0x8, Atime = 0x10, Mtime = 0x20, AtimeSet = 0x80, MtimeSet = 0x100;
            if ((valid & (Size | Atime | Mtime)) == 0)
                return;
            RequireWritable(fid.Node);

            var path = PathOf(fid.Node);
            if ((valid & Size) != 0)
            {
                if (fid.Handle is { } handle)
                    RandomAccess.SetLength(handle, (long)size);
                else
                    using (var opened = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        RandomAccess.SetLength(opened, (long)size);
            }
            if ((valid & Mtime) != 0)
            {
                var when = (valid & MtimeSet) != 0 ? FromUnix(mtimeSec, mtimeNsec) : DateTime.UtcNow;
                if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, when); else File.SetLastWriteTimeUtc(path, when);
            }
            if ((valid & Atime) != 0)
            {
                var when = (valid & AtimeSet) != 0 ? FromUnix(atimeSec, atimeNsec) : DateTime.UtcNow;
                if (Directory.Exists(path)) Directory.SetLastAccessTimeUtc(path, when); else File.SetLastAccessTimeUtc(path, when);
            }
        }

        private void StatFs(Reader r, Writer w)
        {
            var fid = Require(r.U32());
            ulong total = 0, free = 0;
            if (fid.Node.Folder is not null)
            {
                var drive = new DriveInfo(Path.GetPathRoot(PathOf(fid.Node))!);
                total = (ulong)drive.TotalSize;
                free = (ulong)drive.AvailableFreeSpace;
            }

            w.U32(V9fsMagic);
            w.U32(4096);
            w.U64(total / 4096);
            w.U64(free / 4096);
            w.U64(free / 4096);
            w.U64(0);
            w.U64(0);
            w.U64(0);
            w.U32(255);
        }

        // ---- Files ----

        private const uint WriteOnly = 1, ReadWrite = 2, Exclusive = 0x80, Truncate = 0x200, Append = 0x400;

        private void Open(Reader r, Writer w)
        {
            var fid = Require(r.U32());
            var flags = r.U32();
            var node = fid.Node;

            if (IsDirectory(node))
            {
                fid.Listing = null;
                fid.Opened = true;
            }
            else
            {
                var access = AccessOf(flags);
                if (access != FileAccess.Read)
                    RequireWritable(node);
                fid.Handle = File.OpenHandle(PathOf(node),
                    (flags & Truncate) != 0 && access != FileAccess.Read ? FileMode.Truncate : FileMode.Open,
                    access, FileShare.ReadWrite | FileShare.Delete);
                fid.Append = (flags & Append) != 0;
                fid.Opened = true;
            }

            w.Qid(QidOf(node));
            w.U32(0);   // iounit: let the client use the message size
        }

        private void Create(Reader r, Writer w)
        {
            var fid = Require(r.U32());
            var name = r.Str();
            var flags = r.U32();
            r.U32(); r.U32();   // mode, gid
            var parent = fid.Node;
            RequireWritable(parent);

            var node = Child(parent, name) ?? throw new NinePException(ENOENT);
            var mode = (flags & Exclusive) != 0 ? FileMode.CreateNew
                     : (flags & Truncate) != 0 ? FileMode.Create
                     : FileMode.OpenOrCreate;
            var access = AccessOf(flags) == FileAccess.Read ? FileAccess.ReadWrite : AccessOf(flags);
            var handle = File.OpenHandle(PathOf(node), mode, access, FileShare.ReadWrite | FileShare.Delete);

            // Lcreate turns the directory's fid into the new file's.
            fid.Close();
            fid.Node = node;
            fid.Handle = handle;
            fid.Append = (flags & Append) != 0;
            fid.Opened = true;

            w.Qid(QidOf(node));
            w.U32(0);
        }

        private static FileAccess AccessOf(uint flags) => (flags & 3) switch
        {
            WriteOnly => FileAccess.Write,
            ReadWrite => FileAccess.ReadWrite,
            _ => FileAccess.Read
        };

        private void Read(Reader r, Writer w)
        {
            var fid = Require(r.U32());
            var offset = r.U64();
            var count = Math.Min(r.U32(), _messageSize - 11);
            var handle = fid.Handle ?? throw new NinePException(fid.Node.Folder is null || IsDirectory(fid.Node) ? EISDIR : EBADF);

            var buffer = new byte[count];
            var read = RandomAccess.Read(handle, buffer, (long)offset);
            w.U32((uint)read);
            w.Bytes(buffer.AsSpan(0, read));
        }

        private void Write(Reader r, Writer w)
        {
            var fid = Require(r.U32());
            var offset = r.U64();
            var count = r.U32();
            var data = r.Bytes((int)count);
            var handle = fid.Handle ?? throw new NinePException(EBADF);
            RequireWritable(fid.Node);

            RandomAccess.Write(handle, data, fid.Append ? RandomAccess.GetLength(handle) : (long)offset);
            w.U32(count);
        }

        private void Fsync(Reader r)
        {
            var fid = Require(r.U32());
            if (fid.Handle is { } handle)
                RandomAccess.FlushToDisk(handle);
        }

        // ---- Directories ----

        private void ReadDirectory(Reader r, Writer w)
        {
            var fid = Require(r.U32());
            var offset = r.U64();
            var count = r.U32();

            // Listed once, when the guest starts from the beginning; offsets then index into that list.
            if (fid.Listing is null || offset == 0)
                fid.Listing = List(fid.Node);

            var entries = new Writer();
            for (var index = (int)offset; index < fid.Listing.Count; index++)
            {
                var (name, node) = fid.Listing[index];
                var entry = new Writer();
                var qid = QidOf(node);
                entry.Qid(qid);
                entry.U64((ulong)index + 1);
                entry.U8(qid.Type == QidDirectory ? (byte)4 : (byte)8);   // DT_DIR / DT_REG
                entry.Str(name);
                if (entries.Length + entry.Length > count)
                    break;
                entries.Append(entry);
            }

            w.U32((uint)entries.Length);
            w.Append(entries);
        }

        private List<(string Name, Node Node)> List(Node directory)
        {
            var parent = Child(directory, "..")!;
            var list = new List<(string, Node)> { (".", directory), ("..", parent) };

            if (directory.Folder is null)
            {
                list.AddRange(_folders().Where(f => Directory.Exists(f.HostPath)).Select(f => (f.Name, new Node(f, string.Empty))));
                return list;
            }

            var path = PathOf(directory);
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                // Hidden system files (desktop.ini and the like) are Windows' business, not the guest's.
                if (entry.Attributes.HasFlag(FileAttributes.System) && entry.Attributes.HasFlag(FileAttributes.Hidden))
                    continue;
                list.Add((entry.Name, directory with { Relative = Path.Combine(directory.Relative, entry.Name) }));
            }
            return list;
        }

        private void MakeDirectory(Reader r, Writer w)
        {
            var parent = Require(r.U32()).Node;
            var name = r.Str();
            r.U32(); r.U32();   // mode, gid
            RequireWritable(parent);

            var node = Child(parent, name) ?? throw new NinePException(ENOENT);
            var path = PathOf(node);
            if (Directory.Exists(path) || File.Exists(path))
                throw new NinePException(EEXIST);
            Directory.CreateDirectory(path);
            w.Qid(QidOf(node));
        }

        private void Unlink(Reader r)
        {
            var parent = Require(r.U32()).Node;
            var name = r.Str();
            var flags = r.U32();
            RequireWritable(parent);
            Delete(Child(parent, name) ?? throw new NinePException(ENOENT), (flags & 0x200) != 0);
        }

        private void Remove(Reader r)
        {
            var number = r.U32();
            var fid = Require(number);
            try
            {
                RequireWritable(fid.Node);
                fid.Close();
                Delete(fid.Node, IsDirectory(fid.Node));
            }
            finally
            {
                _fids.Remove(number);
            }
        }

        private void Delete(Node node, bool directory)
        {
            if (node.Folder is null || node.Relative.Length == 0)
                throw new NinePException(EACCES);   // the virtual root, or a shared folder itself

            var path = PathOf(node);
            if (directory)
            {
                if (!Directory.Exists(path))
                    throw new NinePException(File.Exists(path) ? ENOTDIR : ENOENT);
                Directory.Delete(path, recursive: false);
            }
            else
            {
                if (!File.Exists(path))
                    throw new NinePException(Directory.Exists(path) ? EISDIR : ENOENT);
                File.Delete(path);
            }
        }

        private void RenameAt(Reader r)
        {
            var oldParent = Require(r.U32()).Node;
            var oldName = r.Str();
            var newParent = Require(r.U32()).Node;
            var newName = r.Str();
            Move(Child(oldParent, oldName) ?? throw new NinePException(ENOENT),
                 Child(newParent, newName) ?? throw new NinePException(ENOENT));
        }

        private void Rename(Reader r)
        {
            var fid = Require(r.U32());
            var parent = Require(r.U32()).Node;
            var target = Child(parent, r.Str()) ?? throw new NinePException(ENOENT);
            Move(fid.Node, target);
            fid.Node = target;
        }

        /// <summary>POSIX rename: replaces a file in the way, and an empty directory.</summary>
        private void Move(Node from, Node to)
        {
            if (from.Folder is null || from.Relative.Length == 0 || to.Folder is null || to.Relative.Length == 0)
                throw new NinePException(EACCES);
            RequireWritable(from);
            RequireWritable(to);

            var source = PathOf(from);
            var target = PathOf(to);
            if (Directory.Exists(source))
            {
                if (Directory.Exists(target) && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(target, recursive: false);   // fails, as POSIX does, unless it is empty
                Directory.Move(source, target);
            }
            else if (File.Exists(source))
            {
                File.Move(source, target, overwrite: true);
            }
            else
            {
                throw new NinePException(ENOENT);
            }
        }

        private void GetLock(Reader r, Writer w)
        {
            Require(r.U32());
            r.U8();
            var start = r.U64();
            var length = r.U64();
            var process = r.U32();
            var client = r.Str();
            w.U8(2);   // F_UNLCK: nothing else holds a lock
            w.U64(start);
            w.U64(length);
            w.U32(process);
            w.Str(client);
        }

        private void Clunk(Reader r)
        {
            if (_fids.Remove(r.U32(), out var fid))
                fid.Close();
        }

        // ---- Helpers ----

        private Fid Require(uint number) =>
            _fids.TryGetValue(number, out var fid) ? fid : throw new NinePException(EBADF);

        private void RequireWritable(Node node)
        {
            if (node.Folder is null)
                throw new NinePException(EACCES);
            if (IsReadOnly(node))
                throw new NinePException(EROFS);
        }

        private const byte QidDirectory = 0x80;

        private Qid QidOf(Node node)
        {
            if (node.Folder is null)
                return new Qid(QidDirectory, 1);
            var path = PathOf(node);
            return new Qid(Directory.Exists(path) ? QidDirectory : (byte)0, Hash(path));
        }

        /// <summary>FNV-1a of the lower-cased path: the same file keeps the same qid, as the client expects.</summary>
        private static ulong Hash(string path)
        {
            var hash = 14695981039346656037UL;
            foreach (var c in path.ToLowerInvariant())
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash | 2;   // never the root's 1
        }

        private static DateTime FromUnix(ulong seconds, ulong nanoseconds) =>
            DateTime.UnixEpoch.AddTicks((long)seconds * TimeSpan.TicksPerSecond + (long)nanoseconds / 100);

        private static uint ErrorNumber(Exception ex) => ex switch
        {
            NinePException nine => nine.Errno,
            FileNotFoundException or DirectoryNotFoundException => ENOENT,
            UnauthorizedAccessException => EACCES,
            PathTooLongException => ENAMETOOLONG,
            IOException io => (io.HResult & 0xFFFF) switch
            {
                0x50 or 0xB7 => EEXIST,        // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS
                0x91 => ENOTEMPTY,             // ERROR_DIR_NOT_EMPTY
                0x20 or 0x21 => EBUSY,         // sharing or lock violation
                0x70 or 0x27 => ENOSPC,        // disk full
                0x05 => EACCES,
                _ => EIO
            },
            ArgumentException => EINVAL,
            _ => EIO
        };

        private sealed record Node(SharedFolderEntry? Folder, string Relative)
        {
            public static readonly Node Root = new(null, string.Empty);
        }

        private readonly record struct Qid(byte Type, ulong Path);

        private sealed class Fid
        {
            public Fid(Node node)
            {
                Node = node;
            }

            public Node Node { get; set; }
            public SafeFileHandle? Handle { get; set; }
            public bool Append { get; set; }
            public bool Opened { get; set; }
            public List<(string Name, Node Node)>? Listing { get; set; }

            public void Close()
            {
                Handle?.Dispose();
                Handle = null;
                Listing = null;
                Opened = false;
            }
        }

        private sealed class NinePException : Exception
        {
            public NinePException(uint errno) : base($"errno {errno}")
            {
                Errno = errno;
            }

            public uint Errno { get; }
        }

        private sealed class Reader
        {
            private readonly byte[] _data;
            private int _position;

            public Reader(byte[] data) => _data = data;

            public byte U8() => _data[_position++];
            public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_position)); _position += 2; return v; }
            public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_position)); _position += 4; return v; }
            public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_position)); _position += 8; return v; }
            public string Str() { var length = U16(); var s = Encoding.UTF8.GetString(_data, _position, length); _position += length; return s; }
            public byte[] Bytes(int count) { var b = _data.AsSpan(_position, count).ToArray(); _position += count; return b; }
        }

        private sealed class Writer
        {
            private readonly MemoryStream _buffer = new();

            public int Length => (int)_buffer.Length;

            public void U8(byte value) => _buffer.WriteByte(value);
            public void U16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); _buffer.Write(b); }
            public void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); _buffer.Write(b); }
            public void U64(ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); _buffer.Write(b); }
            public void Str(string value) { var bytes = Encoding.UTF8.GetBytes(value); U16((ushort)bytes.Length); _buffer.Write(bytes); }
            public void Bytes(ReadOnlySpan<byte> value) => _buffer.Write(value);
            public void Append(Writer other) => other._buffer.WriteTo(_buffer);
            public void Qid(Qid qid) { U8(qid.Type); U32(0); U64(qid.Path); }

            public void Time(DateTime utc)
            {
                var ticks = Math.Max(0, (utc - DateTime.UnixEpoch).Ticks);
                U64((ulong)(ticks / TimeSpan.TicksPerSecond));
                U64((ulong)(ticks % TimeSpan.TicksPerSecond * 100));
            }

            public byte[] ToMessage(byte type, ushort tag)
            {
                var message = new byte[7 + _buffer.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)message.Length);
                message[4] = type;
                BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(5), tag);
                _buffer.ToArray().CopyTo(message, 7);
                return message;
            }
        }
    }
}
