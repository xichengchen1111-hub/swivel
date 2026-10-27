using System;
using System.Buffers;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Swivel.Core;

/// A saved browser destination, never a command or a template for file content.
/// Validate the original authority before System.Uri can canonicalize ambiguous IPs.
public static class WebAddress
{
    public const int MaxLength = 2048;
    private const string Invalid = "Enter a full HTTPS web address up to 2048 characters. HTTP is allowed only for localhost, 127.0.0.1 or [::1]. Credentials, ambiguous hosts and invisible characters are not supported.";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Normalize(string address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > MaxLength || !Visible(address, false)) throw new UserError(Invalid);
        int start = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8
            : address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
        if (start == 0) throw new UserError(Invalid);
        int end = address.IndexOfAny(new[] { '/', '?', '#' }, start);
        if (end < 0) end = address.Length;
        string authority = address[start..end];
        if (authority.Length == 0 || authority.Any(c => c > 127 || c is '@' or '%' or '\\')) throw new UserError(Invalid);
        string host;
        string port;
        if (authority[0] == '[')
        {
            int close = authority.IndexOf(']');
            if (close < 0) throw new UserError(Invalid);
            host = authority[..(close + 1)]; port = authority[(close + 1)..];
            if (!IPAddress.TryParse(host[1..^1], out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6 ||
                !host[1..^1].Equals(ip.ToString(), StringComparison.OrdinalIgnoreCase)) throw new UserError(Invalid);
        }
        else
        {
            int colon = authority.IndexOf(':');
            host = colon < 0 ? authority : authority[..colon]; port = colon < 0 ? "" : authority[colon..];
            if (host.Length is < 1 or > 253) throw new UserError(Invalid);
            if (IPAddress.TryParse(host, out var ip))
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork || host != ip.ToString()) throw new UserError(Invalid);
            }
            else
            {
                // Browser URL parsers also treat a numeric final label as an IPv4
                // candidate. Do not send a DNS-looking spelling that they reinterpret.
                string dns = host.EndsWith('.') ? host[..^1] : host;
                string[] labels = dns.Split('.');
                if (labels.Any(x => x.Length is < 1 or > 63 || !char.IsAsciiLetterOrDigit(x[0]) || !char.IsAsciiLetterOrDigit(x[^1]) || x.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) ||
                    labels[^1].All(char.IsAsciiDigit) || IsHexNumber(labels[^1])) throw new UserError(Invalid);
            }
        }
        if (port.Length > 0 && (port[0] != ':' || port.Length == 1 || !port[1..].All(char.IsAsciiDigit) ||
            !int.TryParse(port[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int portNumber) || portNumber is < 1 or > 65535)) throw new UserError(Invalid);
        if (start == 7 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1" && host != "[::1]") throw new UserError(Invalid);
        ValidateEscapes(address);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) throw new UserError(Invalid);
        string normalized = uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
        if (normalized.Length > MaxLength || !Visible(normalized, false)) throw new UserError(Invalid);
        return normalized;
    }

    private static bool IsHexNumber(string label) => label.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && label.Length >= 2 && label[2..].All(Uri.IsHexDigit);

    private static bool Visible(string text, bool escaped)
    {
        for (int position = 0; position < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(position), out Rune rune, out int used) != OperationStatus.Done) return false;
            position += used;
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value == '\\' || category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ||
                (Rune.IsWhiteSpace(rune) && !(escaped && rune.Value == ' ')) || rune.Value == 0x034f) return false;
        }
        return true;
    }

    private static void ValidateEscapes(string address)
    {
        // Accept explicitly escaped spaces/reserved characters and well-formed UTF-8,
        // but never conceal control characters, a backslash or bidi/zero-width format.
        for (int i = 0; i < address.Length; i++)
        {
            if (address[i] != '%') continue;
            var bytes = new byte[(address.Length - i) / 3]; int count = 0;
            while (i < address.Length && address[i] == '%')
            {
                if (i + 2 >= address.Length || !Uri.IsHexDigit(address[i + 1]) || !Uri.IsHexDigit(address[i + 2])) throw new UserError(Invalid);
                bytes[count++] = (byte)((Hex(address[i + 1]) << 4) | Hex(address[i + 2])); i += 3;
            }
            i--;
            try { if (!Visible(StrictUtf8.GetString(bytes, 0, count), true)) throw new UserError(Invalid); }
            catch (DecoderFallbackException) { throw new UserError(Invalid); }
        }
    }
    private static int Hex(char value) => value <= '9' ? value - '0' : char.ToUpperInvariant(value) - 'A' + 10;
}
