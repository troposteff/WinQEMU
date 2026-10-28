using System.Security.Cryptography;
using System.Text;

namespace WinQEMU.Core.Services
{
    /// <summary>
    /// SHA-512 crypt ("$6$salt$hash"), the password hash Linux keeps in /etc/shadow and Ubuntu's
    /// autoinstall wants: the clear password never goes into the answer file. Ulrich Drepper's
    /// specification, default 5000 rounds.
    /// </summary>
    public static class Sha512Crypt
    {
        private const string Alphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        private const int Rounds = 5000;

        /// <summary>A hash with a fresh random salt of 16 characters.</summary>
        public static string Hash(string password)
        {
            var salt = new char[16];
            for (var i = 0; i < salt.Length; i++)
                salt[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            return Hash(password, new string(salt));
        }

        public static string Hash(string password, string salt) => Hash(password, salt, Rounds);

        /// <summary>Other round counts are written into the hash ("$6$rounds=1400$salt$…"), as the specification does.</summary>
        public static string Hash(string password, string salt, int rounds)
        {
            var p = Encoding.UTF8.GetBytes(password);
            var s = Encoding.ASCII.GetBytes(salt.Length > 16 ? salt[..16] : salt);

            var b = Sha(p, s, p);

            using var a = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            a.AppendData(p);
            a.AppendData(s);
            AppendRepeated(a, b, p.Length);
            for (var length = p.Length; length > 0; length >>= 1)
                a.AppendData((length & 1) != 0 ? b : p);
            var c = a.GetHashAndReset();

            var dp = Repeated(p, p.Length);
            var pBytes = Stretch(dp, p.Length);
            var ds = Repeated(s, 16 + c[0]);
            var sBytes = Stretch(ds, s.Length);

            for (var round = 0; round < rounds; round++)
            {
                using var r = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                r.AppendData((round & 1) != 0 ? pBytes : c);
                if (round % 3 != 0)
                    r.AppendData(sBytes);
                if (round % 7 != 0)
                    r.AppendData(pBytes);
                r.AppendData((round & 1) != 0 ? c : pBytes);
                c = r.GetHashAndReset();
            }

            var text = new StringBuilder("$6$");
            if (rounds != Rounds)
                text.Append("rounds=").Append(rounds.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('$');
            text.Append(Encoding.ASCII.GetString(s)).Append('$');
            int[][] groups =
            {
                new[] { 0, 21, 42 }, new[] { 22, 43, 1 }, new[] { 44, 2, 23 }, new[] { 3, 24, 45 }, new[] { 25, 46, 4 },
                new[] { 47, 5, 26 }, new[] { 6, 27, 48 }, new[] { 28, 49, 7 }, new[] { 50, 8, 29 }, new[] { 9, 30, 51 },
                new[] { 31, 52, 10 }, new[] { 53, 11, 32 }, new[] { 12, 33, 54 }, new[] { 34, 55, 13 }, new[] { 56, 14, 35 },
                new[] { 15, 36, 57 }, new[] { 37, 58, 16 }, new[] { 59, 17, 38 }, new[] { 18, 39, 60 }, new[] { 40, 61, 19 },
                new[] { 62, 20, 41 }
            };
            foreach (var g in groups)
                Encode(text, c[g[0]], c[g[1]], c[g[2]], 4);
            Encode(text, 0, 0, c[63], 2);
            return text.ToString();
        }

        private static byte[] Sha(params byte[][] parts)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            foreach (var part in parts)
                hash.AppendData(part);
            return hash.GetHashAndReset();
        }

        /// <summary>SHA-512 of <paramref name="data"/> repeated <paramref name="times"/> times.</summary>
        private static byte[] Repeated(byte[] data, int times)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            for (var i = 0; i < times; i++)
                hash.AppendData(data);
            return hash.GetHashAndReset();
        }

        private static void AppendRepeated(IncrementalHash hash, byte[] digest, int length)
        {
            for (; length > 64; length -= 64)
                hash.AppendData(digest);
            hash.AppendData(digest, 0, length);
        }

        /// <summary>The digest repeated, then cut, to <paramref name="length"/> bytes.</summary>
        private static byte[] Stretch(byte[] digest, int length)
        {
            var result = new byte[length];
            for (var i = 0; i < length; i++)
                result[i] = digest[i % 64];
            return result;
        }

        private static void Encode(StringBuilder text, byte b2, byte b1, byte b0, int count)
        {
            var w = (b2 << 16) | (b1 << 8) | b0;
            for (var i = 0; i < count; i++)
            {
                text.Append(Alphabet[w & 0x3f]);
                w >>= 6;
            }
        }
    }
}
