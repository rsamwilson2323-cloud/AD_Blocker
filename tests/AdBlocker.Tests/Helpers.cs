using System.Net;
using System.Net.Sockets;
using AdBlocker.Core;

namespace AdBlocker.Tests;

public static class T
{
    public static byte[] Query(string name, ushort qtype = 1, ushort id = 0x1234)
    {
        var l = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.')) { l.Add((byte)label.Length); l.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
        l.Add(0); l.AddRange(new byte[] { (byte)(qtype >> 8), (byte)qtype, 0, 1 });
        return l.ToArray();
    }

    /// <summary>A valid response: copies the query, appends one A record 1.2.3.4 with the given TTL.</summary>
    public static byte[] AnswerFor(byte[] query, uint ttl = 300)
    {
        DnsMessage.TryParseQuestion(query, out var q);
        var r = new List<byte>(query.Take(q.QuestionEnd)) ;
        var a = r.ToArray(); a[2] = 0x81; a[3] = 0x80; a[7] = 1; a[8] = a[9] = a[10] = a[11] = 0;
        var o = new List<byte>(a) { 0xC0, 0x0C, 0, 1, 0, 1, (byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl, 0, 4, 1, 2, 3, 4 };
        return o.ToArray();
    }

    public static RuleEngine Engine(string[] block, string[]? allow = null)
    {
        var e = new RuleEngine();
        e.Load(new[] { ("t", RuleCategory.Advertising, (IEnumerable<string>)block) }, new[] { ("a", (IEnumerable<string>)(allow ?? Array.Empty<string>())) });
        return e;
    }

    public sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Fake upstream resolver on a loopback ephemeral port. Counts the queries it receives.</summary>
    public sealed class FakeUpstream : IDisposable
    {
        private readonly UdpClient _u = new(new IPEndPoint(IPAddress.Loopback, 0)); private readonly CancellationTokenSource _c = new();
        public int Received; public IPEndPoint Endpoint => (IPEndPoint)_u.Client.LocalEndPoint!;
        public FakeUpstream(bool respond = true)
        {
            _ = Task.Run(async () =>
            {
                while (!_c.IsCancellationRequested)
                {
                    try { var r = await _u.ReceiveAsync(_c.Token); Interlocked.Increment(ref Received); if (respond) await _u.SendAsync(AnswerFor(r.Buffer), r.RemoteEndPoint); }
                    catch { break; }
                }
            });
        }
        public void Dispose() { _c.Cancel(); _u.Dispose(); }
    }
}
