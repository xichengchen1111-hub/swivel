using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Swivel.Core;

internal static class ZipEncryptChecks
{
    // Strength 0 = ZipCrypto, 1–3 = AES-128/192/256. Every option breaks or varies one part.
    private sealed record Item(string Name, byte[] Data, string Password, int Strength, ushort Method = 8, ushort AesVersion = 2,
        bool Descriptor = false, ushort Flags = 0x0801, byte[]? Salt = null, bool FlipCipher = false, bool FlipVerifier = false,
        bool FlipAuth = false, bool WrongCrc = false, byte AesVendorVersion = 0, byte AesStrength = 0, bool LocalAesMismatch = false,
        bool NoAesField = false);

    private const ushort Time = 0x6A5B, Date = 0x5A21;

    // An independent writer from the public specifications, so the reader is checked against
    // separate code rather than against itself.
    private static byte[] Build(IReadOnlyList<Item> items)
    {
        var output = new MemoryStream(); var central = new MemoryStream(); var w = new BinaryWriter(output); var c = new BinaryWriter(central);
        foreach (var item in items)
        {
            byte[] packed = item.Method == 8 ? Deflate(item.Data) : item.Data;
            uint crc = Crc(item.Data); if (item.WrongCrc) crc ^= 1;
            byte[] password = Encoding.UTF8.GetBytes(item.Password);
            byte[] stored; ushort method = item.Method; byte[] extra = Array.Empty<byte>(), localExtra = Array.Empty<byte>();
            if (item.Strength == 0)
            {
                byte[] header = RandomNumberGenerator.GetBytes(12);
                header[11] = item.Descriptor ? (byte)(Time >> 8) : (byte)(crc >> 24);
                stored = ZipCryptoEncrypt(password, header.Concat(packed).ToArray());
                if (item.FlipCipher) stored[^1] ^= 1;
            }
            else
            {
                int key = 8 + 8 * item.Strength, saltLength = 4 + 4 * item.Strength;
                byte[] salt = item.Salt ?? RandomNumberGenerator.GetBytes(saltLength);
                byte[] derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1000, HashAlgorithmName.SHA1, 2 * key + 2);
                byte[] cipher = Ctr(derived[..key], packed);
                byte[] auth = HMACSHA1.HashData(derived[key..(2 * key)], cipher)[..10];
                if (item.FlipCipher) cipher[cipher.Length / 2] ^= 0x10; // after the code: tampering in transit
                if (item.FlipAuth) auth[0] ^= 1;
                byte[] verifier = derived[(2 * key)..]; if (item.FlipVerifier) verifier[1] ^= 1;
                stored = salt.Concat(verifier).Concat(cipher).Concat(auth).ToArray();
                if (item.AesVersion == 2 && !item.WrongCrc) crc = 0;
                extra = AesField(item.AesVendorVersion != 0 ? item.AesVendorVersion : (byte)item.AesVersion, item.AesStrength != 0 ? item.AesStrength : (byte)item.Strength, item.Method);
                localExtra = item.LocalAesMismatch ? AesField((byte)item.AesVersion, (byte)item.Strength, (ushort)(8 - item.Method)) : extra;
                if (item.NoAesField) extra = localExtra = Array.Empty<byte>();
                method = 99;
            }
            ushort flags = (ushort)(item.Flags | (item.Descriptor ? 8 : 0));
            ushort needed = method == 99 ? (ushort)51 : (ushort)20;
            byte[] name = Encoding.UTF8.GetBytes(item.Name); uint offset = (uint)output.Position;
            w.Write(0x04034b50u); w.Write(needed); w.Write(flags); w.Write(method); w.Write(Time); w.Write(Date);
            w.Write(item.Descriptor ? 0u : crc); w.Write(item.Descriptor ? 0u : (uint)stored.Length); w.Write(item.Descriptor ? 0u : (uint)item.Data.Length);
            w.Write((ushort)name.Length); w.Write((ushort)localExtra.Length); w.Write(name); w.Write(localExtra); w.Write(stored);
            if (item.Descriptor) { w.Write(0x08074b50u); w.Write(crc); w.Write((uint)stored.Length); w.Write((uint)item.Data.Length); }
            c.Write(0x02014b50u); c.Write((ushort)20); c.Write(needed); c.Write(flags); c.Write(method); c.Write(Time); c.Write(Date);
            c.Write(crc); c.Write((uint)stored.Length); c.Write((uint)item.Data.Length); c.Write((ushort)name.Length); c.Write((ushort)extra.Length);
            c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0); c.Write(0u); c.Write(offset); c.Write(name); c.Write(extra);
        }
        uint cdOffset = (uint)output.Position; w.Write(central.ToArray());
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)items.Count); w.Write((ushort)items.Count);
        w.Write((uint)central.Length); w.Write(cdOffset); w.Write((ushort)0);
        return output.ToArray();
    }

    private static byte[] AesField(byte version, byte strength, ushort method)
    {
        var field = new byte[11];
        BinaryPrimitives.WriteUInt16LittleEndian(field, 0x9901); BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(4), version); field[6] = (byte)'A'; field[7] = (byte)'E'; field[8] = strength;
        BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(9), method);
        return field;
    }

    private static byte[] Ctr(byte[] key, byte[] data)
    {
        using var aes = Aes.Create(); aes.Key = key;
        var result = new byte[data.Length]; var counter = new byte[16]; var pad = new byte[16];
        for (int at = 0, n = 1; at < data.Length; at += 16, n++)
        {
            Array.Clear(counter); BitConverter.GetBytes((long)n).CopyTo(counter, 0); // little-endian, from 1
            aes.EncryptEcb(counter, pad, PaddingMode.None);
            for (int i = 0; i < 16 && at + i < data.Length; i++) result[at + i] = (byte)(data[at + i] ^ pad[i]);
        }
        return result;
    }

    private static byte[] ZipCryptoEncrypt(byte[] password, byte[] plain)
    {
        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        void Update(byte b) { k0 = CrcByte(k0, b); k1 = unchecked((k1 + (k0 & 0xFF)) * 0x08088405 + 1); k2 = CrcByte(k2, (byte)(k1 >> 24)); }
        foreach (byte b in password) Update(b);
        var cipher = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            ushort t = (ushort)(k2 | 2); cipher[i] = (byte)(plain[i] ^ (byte)((t * (t ^ 1)) >> 8)); Update(plain[i]);
        }
        return cipher;
    }
    private static uint CrcByte(uint c, byte b)
    {
        c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; return c;
    }
    private static uint Crc(byte[] data) { uint c = 0xFFFFFFFF; foreach (byte b in data) c = CrcByte(c, b); return ~c; }
    private static byte[] Deflate(byte[] data) { var m = new MemoryStream(); using (var d = new DeflateStream(m, CompressionLevel.Optimal, true)) d.Write(data); return m.ToArray(); }

    private static ZipPlan Plan(byte[] zip) => ZipExtract.Read(new MemoryStream(zip), CancellationToken.None);
    private static byte[] Contents(byte[] zip, ZipMember member, string? password)
    {
        using var key = password is null ? null : new ZipPassword(password);
        var output = new MemoryStream(); ZipExtract.Extract(new MemoryStream(zip), member, output, CancellationToken.None, key); return output.ToArray();
    }
    private static bool Matches(byte[] zip, ZipMember member, string password)
    {
        using var key = new ZipPassword(password); return ZipExtract.PasswordMatches(new MemoryStream(zip), member, key, CancellationToken.None);
    }
    private static string Error(Action action)
    {
        try { action(); return ""; } catch (UserError error) { return error.Message; } catch (Exception error) { return error.GetType().Name; }
    }

    // Made on the Mac: bsdtar 3.5.3 (libarchive 3.7.4) `--options zip:encryption=aes256|aes128`
    // and `zip:encryption=traditional`, and Info-ZIP `/usr/bin/zip -e -P`. Password "secret".
    internal const string BsdAes256 = "UEsDBBQACQBjAEZ0Xl0AAAAAAAAAAAAAAAAFACsAYS50eHR1eAsAAQT1AQAABAAAAAABmQcAAQBBRQMIAFVUDQAHRR7kakUe5GpFHuRqnktowvlADEnV3pn+HXJ6CzdOpYlNIYffSS3elDhCLFMxrlyQo92IhXQUIGXuD8XLqy5UUEsHCOxhUB0zAAAAMgAAAFBLAwQUAAkAYwBGdF5dAAAAAAAAAAAAAAAABQArAGIudHh0dXgLAAEE9QEAAAQAAAAAAZkHAAIAQUUDCABVVA0AB0Ue5GpFHuRqRR7katCxwPaH2QA293QG5/ywje6yAbewAVWe32z5uN1ny1RQSwcIAAAAAB8AAAABAAAAUEsBAhQDFAAJAGMARnReXexhUB0zAAAAMgAAAAUAIwAAAAAAAAAAAKSBAAAAAGEudHh0dXgLAAEE9QEAAAQAAAAAAZkHAAEAQUUDCABVVAUAAUUe5GpQSwECFAMUAAkAYwBGdF5dAAAAAB8AAAABAAAABQAjAAAAAAAAAAAApIGRAAAAYi50eHR1eAsAAQT1AQAABAAAAAABmQcAAgBBRQMIAFVUBQABRR7kalBLBQYAAAAAAgACAKwAAAAOAQAAAAA=";
    private const string BsdAes128 = "UEsDBBQACQBjAEZ0Xl0AAAAAAAAAAAAAAAAFACsAYS50eHR1eAsAAQT1AQAABAAAAAABmQcAAQBBRQEAAFVUDQAHRR7kakUe5GpFHuRqOi+UJQOnPr8xRcNNI+QCSa1ZkrnstlbM8Y3+etuPcarPOdCSHAWZ2pFGHumxPFuyzKp7xXKiUbrIRabaTmbvSA2chtvFWVBLBwjsYVAdRgAAADIAAABQSwECFAMUAAkAYwBGdF5d7GFQHUYAAAAyAAAABQAjAAAAAAAAAAAApIEAAAAAYS50eHR1eAsAAQT1AQAABAAAAAABmQcAAQBBRQEAAFVUBQABRR7kalBLBQYAAAAAAQABAFYAAACkAAAAAAA=";
    private const string BsdZipCrypto = "UEsDBBQACQAIAEZ0Xl0AAAAAAAAAAAAAAAAFACAAYS50eHR1eAsAAQT1AQAABAAAAABVVA0AB0Ue5GpFHuRqRR7kajkcIaI5ILupeotb95vQRxlLFOanpIHdH9vr2yE5s3jao0c2UEsHCOxhUB0jAAAAMgAAAFBLAQIUAxQACQAIAEZ0Xl3sYVAdIwAAADIAAAAFABgAAAAAAAAAAACkgQAAAABhLnR4dHV4CwABBPUBAAAEAAAAAFVUBQABRR7kalBLBQYAAAAAAQABAEsAAAB2AAAAAAA=";
    // Info-ZIP: a.txt with password "one", then b.txt appended with "two".
    internal const string InfoZipMixed = "UEsDBBQACQAIAEd0Xl3sYVAdIwAAADIAAAAFABwAYS50eHRVVAkAA0Ue5GpFHuRqdXgLAAEE9QEAAAQAAAAAhHLPUpyDGFnFhcSO4pFtzEx9Q2z/5g+0W2ctbiHuwxHiInpQSwcI7GFQHSMAAAAyAAAAUEsDBAoACQAAAEd0Xl2DFtyMDQAAAAEAAAAFABwAYi50eHRVVAkAA0Ue5GpFHuRqdXgLAAEE9QEAAAQAAAAAB3h6Ac5Ce1mbyGAeQlBLBwiDFtyMDQAAAAEAAABQSwECHgMUAAkACABHdF5d7GFQHSMAAAAyAAAABQAYAAAAAAABAAAApIEAAAAAYS50eHRVVAUAA0Ue5Gp1eAsAAQT1AQAABAAAAABQSwECHgMKAAkAAABHdF5dgxbcjA0AAAABAAAABQAYAAAAAAABAAAApIFyAAAAYi50eHRVVAUAA0Ue5Gp1eAsAAQT1AQAABAAAAABQSwUGAAAAAAIAAgCWAAAAzgAAAAAA";
    internal const string InfoZipCrypto = "UEsDBBQACQAIAEd0Xl3sYVAdIwAAADIAAAAFABwAYS50eHRVVAkAA0Ue5GpFHuRqdXgLAAEE9QEAAAQAAAAAmA97ujZaqDjLlEF6H9A2TX4FSuDTEh3q65/8Od6nxvlmN6hQSwcI7GFQHSMAAAAyAAAAUEsDBAoACQAAAEd0Xl2DFtyMDQAAAAEAAAAFABwAYi50eHRVVAkAA0Ue5GpFHuRqdXgLAAEE9QEAAAQAAAAAnIQh7v2Ot9tqIV44qFBLBwiDFtyMDQAAAAEAAABQSwECHgMUAAkACABHdF5d7GFQHSMAAAAyAAAABQAYAAAAAAABAAAApIEAAAAAYS50eHRVVAUAA0Ue5Gp1eAsAAQT1AQAABAAAAABQSwECHgMKAAkAAABHdF5dgxbcjA0AAAABAAAABQAYAAAAAAABAAAApIFyAAAAYi50eHRVVAUAA0Ue5Gp1eAsAAQT1AQAABAAAAABQSwUGAAAAAAIAAgCWAAAAzgAAAAAA";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        byte[] body = Enumerable.Range(0, 70000).Select(i => (byte)(i * 13 % 241)).ToArray();
        byte[] small = Encoding.UTF8.GetBytes("秘密 🔑 note");
        const string pw = "pässwörd 🔑";

        // Every kind, stored and deflated, extracts exactly with its password.
        var kinds = new[] { (0, "ZipCrypto"), (1, "AES-128"), (2, "AES-192"), (3, "AES-256") };
        foreach (var (strength, label) in kinds)
            foreach (ushort method in new ushort[] { 0, 8 })
                foreach (bool descriptor in new[] { false, true })
                {
                    string what = label + (method == 8 ? " Deflate" : " Store") + (descriptor ? " with descriptor" : "");
                    var zip = Build(new[] { new Item("a.dat", body, pw, strength, method, Descriptor: descriptor), new Item("s.txt", small, pw, strength, method, AesVersion: 1) });
                    var plan = Plan(zip);
                    var expected = strength switch { 0 => ZipEncryption.ZipCrypto, 1 => ZipEncryption.Aes128, 2 => ZipEncryption.Aes192, _ => ZipEncryption.Aes256 };
                    check(plan.Files.All(f => f.Encryption == expected) && plan.TotalBytes == body.Length + small.Length, "ZIP plan names encryption: " + what);
                    check(Contents(zip, plan.Files[0], pw).SequenceEqual(body) && Contents(zip, plan.Files[1], pw).SequenceEqual(small), "ZIP decrypts exactly: " + what);
                    check(Matches(zip, plan.Files[0], pw) && !Matches(zip, plan.Files[0], "pässwörd 🔐"), "ZIP password check: " + what);
                    string message = Error(() => Contents(zip, plan.Files[0], "wrong"));
                    check(message == ZipDecrypt.WrongPassword || strength == 0 && message == ZipDecrypt.Damaged, "ZIP wrong password refused: " + what + " " + message);
                    reject(() => Contents(zip, plan.Files[0], null), "ZIP encrypted entry needs a password: " + what);
                }
        check(Plan(Build(new[] { new Item("x", body, pw, 3, AesVersion: 2) })).Files[0] is { AesVersion: 2, Crc: 0, AesMethod: 8, Method: 99 }, "ZIP AE-2 has no CRC and method 99 over Deflate");

        // AES integrity: tampering is caught by the authentication code before decryption.
        foreach (int strength in new[] { 1, 3 })
        {
            var tampered = Build(new[] { new Item("t", body, pw, strength, FlipCipher: true) });
            check(Error(() => Contents(tampered, Plan(tampered).Files[0], pw)) == ZipDecrypt.AuthFailed, "ZIP AES tampered ciphertext fails authentication " + strength);
            var badAuth = Build(new[] { new Item("t", body, pw, strength, Method: 0, FlipAuth: true) });
            check(Error(() => Contents(badAuth, Plan(badAuth).Files[0], pw)) == ZipDecrypt.AuthFailed, "ZIP AES altered auth code fails " + strength);
            var badVerifier = Build(new[] { new Item("t", body, pw, strength, FlipVerifier: true) });
            check(!Matches(badVerifier, Plan(badVerifier).Files[0], pw) && Error(() => Contents(badVerifier, Plan(badVerifier).Files[0], pw)) == ZipDecrypt.WrongPassword, "ZIP AES altered verifier refused " + strength);
        }
        var ae1BadCrc = Build(new[] { new Item("c", body, pw, 3, AesVersion: 1, WrongCrc: true) });
        check(Error(() => Contents(ae1BadCrc, Plan(ae1BadCrc).Files[0], pw)) == ZipDecrypt.Damaged, "ZIP AE-1 also checks CRC-32");
        var zcTampered = Build(new[] { new Item("z", body, pw, 0, Method: 0, FlipCipher: true) });
        check(Error(() => Contents(zcTampered, Plan(zcTampered).Files[0], pw)) == ZipDecrypt.Damaged, "ZIP ZipCrypto tampering caught by CRC-32");
        var zcDeflateTampered = Build(new[] { new Item("z", body, pw, 0, FlipCipher: true) });
        check(Error(() => Contents(zcDeflateTampered, Plan(zcDeflateTampered).Files[0], pw)) == ZipDecrypt.Damaged, "ZIP ZipCrypto Deflate tampering refused");
        // A wrong ZipCrypto password passing the one-byte check is still caught later.
        int caught = 0;
        for (int i = 0; i < 600 && caught < 3; i++)
        {
            var zip = Build(new[] { new Item("z", body, pw, 0, Method: 0) });
            string guess = "guess" + i;
            if (!Matches(zip, Plan(zip).Files[0], guess)) continue;
            check(Error(() => Contents(zip, Plan(zip).Files[0], guess)) == ZipDecrypt.Damaged, "ZIP ZipCrypto false header match caught by CRC"); caught++;
        }
        check(caught > 0, "ZIP ZipCrypto one-byte header collisions exercised");

        // Mixed archives: plain, AES and ZipCrypto entries with different passwords.
        var mixed = Build(new[] { new Item("zero.txt", small, "zero", 0), new Item("one.dat", body, "first", 3), new Item("two.dat", small, "second", 0, Method: 0), new Item("three.dat", body, "first", 1, Method: 0) });
        var mixedPlan = Plan(mixed);
        check(mixedPlan.Files.Select(f => f.Encryption).SequenceEqual(new[] { ZipEncryption.ZipCrypto, ZipEncryption.Aes256, ZipEncryption.ZipCrypto, ZipEncryption.Aes128 }), "ZIP mixed plan lists each method");
        check(Matches(mixed, mixedPlan.Files[1], "first") && !Matches(mixed, mixedPlan.Files[2], "first") && Matches(mixed, mixedPlan.Files[2], "second"), "ZIP mixed passwords checked per entry");
        check(Contents(mixed, mixedPlan.Files[3], "first").SequenceEqual(body), "ZIP mixed AES-128 entry extracts");
        var plainAndEncrypted = ZipExtractPlainPlus(small, body);
        var plainPlan = Plan(plainAndEncrypted);
        check(plainPlan.Files[0].Encryption == ZipEncryption.None && Contents(plainAndEncrypted, plainPlan.Files[0], null).SequenceEqual(small)
            && Contents(plainAndEncrypted, plainPlan.Files[0], "anything").SequenceEqual(small), "ZIP plain entry beside encrypted ones needs no password");
        check(ZipExtract.PasswordMatches(new MemoryStream(plainAndEncrypted), plainPlan.Files[0], new ZipPassword("x"), CancellationToken.None), "ZIP plain entry always matches");
        check(Contents(plainAndEncrypted, plainPlan.Files[1], "k").SequenceEqual(body), "ZIP encrypted entry beside a plain one");

        // Refused variants.
        reject(() => Plan(Build(new[] { new Item("s", body, pw, 3, Flags: 0x0841) })), "ZIP refuses strong encryption flag with AES");
        check(Error(() => Plan(Build(new[] { new Item("s", body, pw, 0, Flags: 0x0841) }))) == ZipExtract.StrongEncryption, "ZIP refuses PKWARE strong encryption");
        check(Error(() => Plan(Build(new[] { new Item("s", body, pw, 0, Flags: 0x2801) }))) == ZipExtract.StrongEncryption, "ZIP refuses central directory encryption (bit 13)");
        reject(() => Plan(Build(new[] { new Item("s", body, pw, 3, AesStrength: 4) })), "ZIP refuses AES strength 4");
        reject(() => Plan(Build(new[] { new Item("s", body, pw, 3, AesVendorVersion: 3) })), "ZIP refuses AE-3");
        reject(() => Plan(Build(new[] { new Item("s", body, pw, 3, Flags: 0x0800) })), "ZIP refuses AES without the encryption flag");
        reject(() => Plan(Build(new[] { new Item("s", body, pw, 3, NoAesField: true) })), "ZIP refuses method 99 without its AES field");
        reject(() => Plan(Build(new[] { new Item("s", body, pw, 3, LocalAesMismatch: true) })), "ZIP refuses local AES field differing from directory");
        reject(() => Plan(Build(new[] { new Item("s", body, pw, 3, Method: 12) })), "ZIP refuses other methods under AES");
        reject(() => new ZipPassword(""), "ZIP empty password refused");
        reject(() => new ZipPassword("a\uD800b"), "ZIP password with a lone surrogate refused");
        reject(() => new ZipPassword(new string('x', ZipPassword.MaxBytes + 1)), "ZIP password length bound");
        var disposed = new ZipPassword("x"); disposed.Dispose();
        var any = Build(new[] { new Item("d", small, "x", 3) });
        reject(() => ZipExtract.Extract(new MemoryStream(any), Plan(any).Files[0], new MemoryStream(), CancellationToken.None, disposed), "ZIP disposed password cannot be used");
        reject(() => ZipExtract.Extract(new MemoryStream(any), Plan(any).Files[0], new MemoryStream(), new CancellationToken(true), new ZipPassword("x")), "ZIP encrypted extract cancellation");
        using (var cancel = new CancellationTokenSource())
        {
            var big = Build(new[] { new Item("big", Enumerable.Range(0, 400000).Select(i => (byte)i).ToArray(), "x", 3, Method: 0) });
            var member = Plan(big).Files[0]; int reads = 0;
            var stream = new CountingStream(big, () => { if (++reads == 4) cancel.Cancel(); });
            reject(() => ZipExtract.Extract(stream, member, new MemoryStream(), cancel.Token, new ZipPassword("x")), "ZIP AES authentication pass is cancellable");
        }
        // Messages never contain the password.
        const string secret = "Sup3r-Secret!";
        var leak = Build(new[] { new Item("l", body, secret, 3, FlipCipher: true), new Item("m", body, secret, 0) });
        var leakPlan = Plan(leak);
        foreach (string message in new[] { Error(() => Contents(leak, leakPlan.Files[0], secret)), Error(() => Contents(leak, leakPlan.Files[0], secret + "x")), Error(() => Contents(leak, leakPlan.Files[1], secret + "x")) })
            check(message.Length > 0 && !message.Contains(secret, StringComparison.Ordinal), "ZIP errors never contain the password: " + message);

        foreach (string text in new[] { ZipDecrypt.WrongPassword, ZipDecrypt.AuthFailed, ZipDecrypt.Damaged, ZipExtract.StrongEncryption })
            check(Strings.Table.ContainsKey(text), "ZIP encryption message translated: " + text);

        // Cross-check with archives from other writers.
        byte[] aText = Encoding.ASCII.GetBytes("hello aes world, hello aes world, hello aes world\n");
        var bsd256 = Convert.FromBase64String(BsdAes256); var p256 = Plan(bsd256);
        check(p256.Files[0] is { Encryption: ZipEncryption.Aes256, AesVersion: 1, AesMethod: 8 } && p256.Files[1] is { Encryption: ZipEncryption.Aes256, AesVersion: 2 }, "bsdtar AES-256 plan (AE-1 and AE-2)");
        check(Contents(bsd256, p256.Files[0], "secret").SequenceEqual(aText) && Encoding.ASCII.GetString(Contents(bsd256, p256.Files[1], "secret")) == "x", "bsdtar AES-256 decrypts exactly");
        check(Error(() => Contents(bsd256, p256.Files[0], "Secret")) == ZipDecrypt.WrongPassword, "bsdtar AES-256 wrong password refused");
        var bsd128 = Convert.FromBase64String(BsdAes128); var p128 = Plan(bsd128);
        check(p128.Files[0] is { Encryption: ZipEncryption.Aes128, AesMethod: 0 } && Contents(bsd128, p128.Files[0], "secret").SequenceEqual(aText), "bsdtar AES-128 Store decrypts exactly");
        var bsdZc = Convert.FromBase64String(BsdZipCrypto); var pZc = Plan(bsdZc);
        check(pZc.Files[0].Encryption == ZipEncryption.ZipCrypto && Contents(bsdZc, pZc.Files[0], "secret").SequenceEqual(aText), "bsdtar ZipCrypto decrypts exactly");
        var info = Convert.FromBase64String(InfoZipCrypto); var pInfo = Plan(info);
        check(pInfo.Files.All(f => f.Encryption == ZipEncryption.ZipCrypto) && Contents(info, pInfo.Files[0], "secret").SequenceEqual(aText)
            && Encoding.ASCII.GetString(Contents(info, pInfo.Files[1], "secret")) == "x", "Info-ZIP ZipCrypto decrypts exactly (Deflate and Store)");
        var infoMixed = Convert.FromBase64String(InfoZipMixed); var pMixed = Plan(infoMixed);
        check(Matches(infoMixed, pMixed.Files[0], "one") && !Matches(infoMixed, pMixed.Files[1], "one") && Matches(infoMixed, pMixed.Files[1], "two")
            && Encoding.ASCII.GetString(Contents(infoMixed, pMixed.Files[1], "two")) == "x", "Info-ZIP two passwords checked per entry");
        check(!Matches(info, pInfo.Files[0], "Secret") || Error(() => Contents(info, pInfo.Files[0], "Secret")) == ZipDecrypt.Damaged, "Info-ZIP wrong password refused");
    }

    // A plain Store entry followed by an AES-256 one (password "k").
    private static byte[] ZipExtractPlainPlus(byte[] plain, byte[] secret)
    {
        var output = new MemoryStream(); var w = new BinaryWriter(output); var c = new MemoryStream(); var cw = new BinaryWriter(c);
        byte[] pName = Encoding.UTF8.GetBytes("p.txt"); uint crc = Crc(plain);
        w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write((ushort)0); w.Write(Time); w.Write(Date);
        w.Write(crc); w.Write((uint)plain.Length); w.Write((uint)plain.Length); w.Write((ushort)pName.Length); w.Write((ushort)0); w.Write(pName); w.Write(plain);
        cw.Write(0x02014b50u); cw.Write((ushort)20); cw.Write((ushort)20); cw.Write((ushort)0x0800); cw.Write((ushort)0); cw.Write(Time); cw.Write(Date);
        cw.Write(crc); cw.Write((uint)plain.Length); cw.Write((uint)plain.Length); cw.Write((ushort)pName.Length); cw.Write((ushort)0);
        cw.Write((ushort)0); cw.Write((ushort)0); cw.Write((ushort)0); cw.Write(0u); cw.Write(0u); cw.Write(pName);
        // Copy the AES entry's local record and data, then its central record with the new offset.
        var single = Build(new[] { new Item("e.dat", secret, "k", 3) });
        int cdOffset = BinaryPrimitives.ReadInt32LittleEndian(single.AsSpan(single.Length - 6));
        uint newOffset = (uint)output.Position; w.Write(single.AsSpan(0, cdOffset));
        byte[] record = single.AsSpan(cdOffset, single.Length - 22 - cdOffset).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), newOffset); cw.Write(record);
        uint start = (uint)output.Position; w.Write(c.ToArray());
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)2); w.Write((uint)c.Length); w.Write(start); w.Write((ushort)0);
        return output.ToArray();
    }

    private sealed class CountingStream(byte[] data, Action onRead) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer) { onRead(); return base.Read(buffer); }
        public override int Read(byte[] buffer, int offset, int count) { onRead(); return base.Read(buffer, offset, count); }
    }
}
