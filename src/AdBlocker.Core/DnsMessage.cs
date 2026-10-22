using System.Text;

namespace AdBlocker.Core;

public sealed record DnsQuestion(ushort Id, string Name, ushort QType, ushort QClass, int QuestionEnd)
{
    public string CacheKey => $"{Name}|{QType}|{QClass}";
}

/// <summary>Minimal RFC 1035 helpers. We only parse what we need: one question, and RR TTLs for caching.</summary>
public static class DnsMessage
{
    public const ushort TypeA = 1, TypeAAAA = 28;

    public static bool TryParseQuestion(ReadOnlySpan<byte> p, out DnsQuestion q)
    {
        q = null!;
        if (p.Length < 17) return false;
        int flags = (p[2] << 8) | p[3];
        if ((flags & 0x8000) != 0) return false;            // a response, not a query
        if (((flags >> 11) & 0xF) != 0) return false;       // only standard queries (opcode 0)
        if (((p[4] << 8) | p[5]) != 1) return false;        // exactly one question

        int pos = 12;
        var sb = new StringBuilder();
        while (true)
        {
            if (pos >= p.Length) return false;
            int len = p[pos++];
            if (len == 0) break;
            if ((len & 0xC0) != 0) return false;            // compression is not legal in a query's question
            if (pos + len > p.Length) return false;
            if (sb.Length > 0) sb.Append('.');
            for (int i = 0; i < len; i++) sb.Append(char.ToLowerInvariant((char)p[pos + i]));
            pos += len;
            if (sb.Length > 253) return false;
        }
        if (pos + 4 > p.Length) return false;
        q = new DnsQuestion((ushort)((p[0] << 8) | p[1]), sb.ToString(),
                            (ushort)((p[pos] << 8) | p[pos + 1]), (ushort)((p[pos + 2] << 8) | p[pos + 3]), pos + 4);
        return true;
    }

    /// <summary>NXDOMAIN, or (nullIpMode) 0.0.0.0 / :: for A / AAAA and an empty NOERROR for other types.</summary>
    public static byte[] BuildBlocked(byte[] query, DnsQuestion q, bool nullIpMode)
    {
        bool answer = nullIpMode && (q.QType == TypeA || q.QType == TypeAAAA);
        int rdLen = q.QType == TypeA ? 4 : 16;
        var resp = new byte[q.QuestionEnd + (answer ? 12 + rdLen : 0)];
        Array.Copy(query, resp, q.QuestionEnd);
        int rd = query[2] & 0x01;
        resp[2] = (byte)(0x80 | rd);                                  // QR=1, opcode 0, RD echoed
        resp[3] = (byte)(0x80 | (nullIpMode ? 0 : 3));                // RA=1, RCODE 3 (NXDOMAIN) or 0
        resp[6] = 0; resp[7] = (byte)(answer ? 1 : 0);                // ANCOUNT
        resp[8] = resp[9] = resp[10] = resp[11] = 0;                  // NSCOUNT, ARCOUNT (EDNS dropped)
        if (answer)
        {
            int o = q.QuestionEnd;
            resp[o++] = 0xC0; resp[o++] = 0x0C;                       // name pointer to the question
            resp[o++] = (byte)(q.QType >> 8); resp[o++] = (byte)q.QType;
            resp[o++] = 0; resp[o++] = 1;                             // class IN
            resp[o++] = 0; resp[o++] = 0; resp[o++] = 0; resp[o++] = 60; // TTL 60s
            resp[o++] = 0; resp[o++] = (byte)rdLen;                   // RDATA is already zero = 0.0.0.0 / ::
        }
        return resp;
    }

    public static byte[] BuildServFail(byte[] query, DnsQuestion q)
    {
        var resp = new byte[q.QuestionEnd];
        Array.Copy(query, resp, q.QuestionEnd);
        resp[2] = (byte)(0x80 | (query[2] & 0x01));
        resp[3] = 0x82;                                               // RA=1, RCODE 2 (SERVFAIL)
        resp[6] = resp[7] = resp[8] = resp[9] = resp[10] = resp[11] = 0;
        return resp;
    }

    public static int Rcode(ReadOnlySpan<byte> r) => r.Length >= 4 ? r[3] & 0x0F : -1;
    public static bool IsTruncated(ReadOnlySpan<byte> r) => r.Length >= 4 && (r[2] & 0x02) != 0;

    private static int SkipName(ReadOnlySpan<byte> p, int pos)
    {
        while (true)
        {
            if (pos >= p.Length) return -1;
            int len = p[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2 <= p.Length ? pos + 2 : -1;
            if ((len & 0xC0) != 0) return -1;
            pos += 1 + len;
        }
    }

    /// <summary>Smallest TTL across all non-OPT records (answers + authority + additional). False if unparseable or no records.</summary>
    public static bool TryGetMinTtl(ReadOnlySpan<byte> r, out uint ttl)
    {
        ttl = 0;
        if (r.Length < 12) return false;
        int qd = (r[4] << 8) | r[5];
        int rrs = ((r[6] << 8) | r[7]) + ((r[8] << 8) | r[9]) + ((r[10] << 8) | r[11]);
        int pos = 12;
        for (int i = 0; i < qd; i++) { pos = SkipName(r, pos); if (pos < 0 || pos + 4 > r.Length) return false; pos += 4; }
        uint min = uint.MaxValue; bool any = false;
        for (int i = 0; i < rrs; i++)
        {
            pos = SkipName(r, pos);
            if (pos < 0 || pos + 10 > r.Length) return false;
            int type = (r[pos] << 8) | r[pos + 1];
            uint t = (uint)((r[pos + 4] << 24) | (r[pos + 5] << 16) | (r[pos + 6] << 8) | r[pos + 7]);
            int rdlen = (r[pos + 8] << 8) | r[pos + 9];
            if (type != 41) { any = true; if (t < min) min = t; }
            pos += 10 + rdlen;
            if (pos > r.Length) return false;
        }
        ttl = min; return any;
    }
}
