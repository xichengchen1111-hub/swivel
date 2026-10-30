using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Swivel.Core;

public enum ZipEncryption { None, ZipCrypto, Aes128, Aes192, Aes256 }

/// A password typed for one extraction. Kept as UTF-8 bytes in memory only and cleared on
/// Dispose; never logged, saved or put in error text. Copies the runtime makes elsewhere
/// (the text box, managed strings) cannot be guaranteed to be erased.
public sealed class ZipPassword : IDisposable
{
    public const int MaxBytes = 1024;
    private byte[]? bytes;
    public ZipPassword(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) throw new UserError("Type the archive's password.");
        var strict = new UTF8Encoding(false, true);
        int count;
        try { count = strict.GetByteCount(text); }
        catch (EncoderFallbackException) { throw new UserError("The password contains characters that cannot be used."); }
        if (count > MaxBytes) throw new UserError("The password is too long.");
        bytes = new byte[count];
        strict.GetBytes(text, bytes);
    }
    internal ReadOnlySpan<byte> Bytes => bytes ?? throw new ObjectDisposedException(nameof(ZipPassword));
    public void Dispose()
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        bytes = null;
    }
}

/// Original decryption for ZIP extraction from the public specifications: traditional PKWARE
/// encryption (APPNOTE 6.1, "ZipCrypto"; weak, read only) and WinZip AES (AE-1/AE-2:
/// PBKDF2-HMAC-SHA1 with 1,000 iterations, AES in CTR mode with a little-endian counter
/// starting at 1, HMAC-SHA1 over the encrypted data truncated to 10 bytes).
public static class ZipDecrypt
{
    public const int ZipCryptoHeader = 12, AesVerifier = 2, AesAuthCode = 10;
    internal static int SaltLength(ZipEncryption kind) => kind switch { ZipEncryption.Aes128 => 8, ZipEncryption.Aes192 => 12, ZipEncryption.Aes256 => 16, _ => 0 };
    private static int KeyLength(ZipEncryption kind) => kind switch { ZipEncryption.Aes128 => 16, ZipEncryption.Aes192 => 24, ZipEncryption.Aes256 => 32, _ => 0 };
    /// Bytes the encryption adds to the stored data.
    internal static int Overhead(ZipEncryption kind) => kind switch
    {
        ZipEncryption.None => 0,
        ZipEncryption.ZipCrypto => ZipCryptoHeader,
        _ => SaltLength(kind) + AesVerifier + AesAuthCode,
    };

    // ---- ZipCrypto ----

    private struct Keys
    {
        private uint k0, k1, k2;
        public static Keys For(ReadOnlySpan<byte> password)
        {
            var keys = new Keys { k0 = 305419896, k1 = 591751049, k2 = 878082192 };
            foreach (byte b in password) keys.Update(b);
            return keys;
        }
        private void Update(byte plain)
        {
            k0 = Crc(k0, plain);
            k1 = (k1 + (k0 & 0xFF)) * 134775813 + 1;
            k2 = Crc(k2, (byte)(k1 >> 24));
        }
        public byte Decrypt(byte cipher)
        {
            uint temp = (k2 | 2) & 0xFFFF;
            byte plain = (byte)(cipher ^ (byte)((temp * (temp ^ 1)) >> 8));
            Update(plain); return plain;
        }
        public void Clear() { k0 = k1 = k2 = 0; }
        private static uint Crc(uint state, byte b) => ZipExtract.Crc32Byte(state, b);
    }

    // The 12th decrypted header byte is the CRC's high byte, or with a data descriptor (bit 3)
    // the high byte of the DOS time. One byte: a wrong password passes 1 time in 256 and is
    // then caught by the CRC-32.
    private static bool ZipCryptoHeaderMatches(ref Keys keys, ReadOnlySpan<byte> header, ZipMember member)
    {
        byte last = 0;
        foreach (byte b in header) last = keys.Decrypt(b);
        byte expected = (member.Flags & 8) != 0 ? (byte)(member.DosTime >> 8) : (byte)(member.Crc >> 24);
        return last == expected;
    }

    // ---- WinZip AES ----

    private static byte[] Derive(ZipEncryption kind, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt)
    {
        byte[] derived = new byte[2 * KeyLength(kind) + AesVerifier];
        Rfc2898DeriveBytes.Pbkdf2(password, salt, derived, 1000, HashAlgorithmName.SHA1);
        return derived;
    }

    /// Only the quick check (password verifier or header byte); reads the first bytes of the data.
    internal static bool PasswordMatches(Stream data, ZipMember member, ZipPassword password)
    {
        if (member.Encryption == ZipEncryption.ZipCrypto)
        {
            byte[] header = new byte[ZipCryptoHeader]; data.ReadExactly(header);
            var keys = Keys.For(password.Bytes);
            bool matches = ZipCryptoHeaderMatches(ref keys, header, member);
            keys.Clear(); return matches;
        }
        int saltLength = SaltLength(member.Encryption);
        byte[] start = new byte[saltLength + AesVerifier]; data.ReadExactly(start);
        byte[] derived = Derive(member.Encryption, password.Bytes, start.AsSpan(0, saltLength));
        try { return CryptographicOperations.FixedTimeEquals(derived.AsSpan(derived.Length - AesVerifier), start.AsSpan(saltLength)); }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    /// `data` is positioned at the entry's stored bytes and yields exactly CompressedSize bytes.
    /// Returns the decrypted (still compressed) data. A wrong password throws WrongPassword.
    /// AES entries are authenticated in full (one pass over the encrypted data) before any
    /// byte is decrypted; `from` opens the stored bytes again at an offset for the second pass.
    internal static Stream Open(Stream data, ZipMember member, ZipPassword password, Func<long, Stream> from, CancellationToken token)
    {
        if (member.Encryption == ZipEncryption.ZipCrypto)
        {
            byte[] header = new byte[ZipCryptoHeader]; data.ReadExactly(header);
            var keys = Keys.For(password.Bytes);
            if (!ZipCryptoHeaderMatches(ref keys, header, member)) { keys.Clear(); throw new UserError(WrongPassword); }
            return new ZipCryptoStream(data, keys);
        }
        var kind = member.Encryption;
        int saltLength = SaltLength(kind), keyLength = KeyLength(kind);
        byte[] start = new byte[saltLength + AesVerifier]; data.ReadExactly(start);
        byte[] derived = Derive(kind, password.Bytes, start.AsSpan(0, saltLength));
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(derived.AsSpan(2 * keyLength), start.AsSpan(saltLength))) throw new UserError(WrongPassword);
            long cipherLength = member.CompressedSize - saltLength - AesVerifier - AesAuthCode;
            using (var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, derived.AsSpan(keyLength, keyLength)))
            {
                byte[] buffer = new byte[65536]; long left = cipherLength;
                while (left > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int count = data.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (count == 0) throw new UserError("This ZIP archive ends early.");
                    mac.AppendData(buffer, 0, count); left -= count;
                }
                byte[] stored = new byte[AesAuthCode]; data.ReadExactly(stored);
                byte[] computed = mac.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(computed.AsSpan(0, AesAuthCode), stored)) throw new UserError(AuthFailed);
            }
            return new AesCtrStream(from(saltLength + AesVerifier), derived.AsSpan(0, keyLength), cipherLength);
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    public const string WrongPassword = "The password does not match this encrypted ZIP entry, so it was not extracted.";
    public const string AuthFailed = "An encrypted ZIP entry failed its authentication check: the archive is damaged or was altered. It was not extracted.";
    public const string Damaged = "An encrypted ZIP entry failed its integrity check (the password may be wrong, or the archive is damaged) and was not extracted.";

    private sealed class ZipCryptoStream(Stream inner, Keys keys) : Stream
    {
        private Keys keys = keys;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            for (int i = 0; i < read; i++) buffer[i] = keys.Decrypt(buffer[i]);
            return read;
        }
        protected override void Dispose(bool disposing) { keys.Clear(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // CTR: block n (from 1) is XORed with AES(n as a 16-byte little-endian number).
    private sealed class AesCtrStream : Stream
    {
        private readonly Stream inner;
        private readonly Aes aes = Aes.Create();
        private readonly byte[] counters = new byte[4096], stream = new byte[4096];
        private long left, block = 1;
        private int used = 4096;
        public AesCtrStream(Stream inner, ReadOnlySpan<byte> key, long length)
        {
            this.inner = inner; left = length;
            byte[] copy = key.ToArray(); aes.Key = copy; CryptographicOperations.ZeroMemory(copy);
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (left == 0 || buffer.IsEmpty) return 0;
            int read = inner.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
            if (read == 0) throw new UserError("This ZIP archive ends early.");
            for (int i = 0; i < read; i++)
            {
                if (used == stream.Length) Refill();
                buffer[i] ^= stream[used++];
            }
            left -= read; return read;
        }
        private void Refill()
        {
            for (int at = 0; at < counters.Length; at += 16)
            {
                // The specification's counter is 128 bits; a 2 GB entry never leaves the low 64.
                BinaryPrimitives.WriteUInt64LittleEndian(counters.AsSpan(at), (ulong)block++);
                BinaryPrimitives.WriteUInt64LittleEndian(counters.AsSpan(at + 8), 0);
            }
            aes.EncryptEcb(counters, stream, PaddingMode.None); used = 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { CryptographicOperations.ZeroMemory(stream); aes.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
