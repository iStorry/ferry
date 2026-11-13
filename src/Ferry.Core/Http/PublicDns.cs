using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Ferry.Core.Http;

/// <summary>
/// Looks up a few named hosts with public DNS (DNS over HTTPS, asked by IP address) rather than the machine's resolver:
/// for a server whose resolver gives a site a wrong address. A seedbox's resolver has been seen giving a site an old
/// address that answered every request with 502, while public DNS gave the site as it is now.
/// </summary>
/// <remarks>
/// When public DNS can't be asked, or has no address for the host, the machine's resolver is asked as for any other
/// host: no worse than without this. Answers are kept for their TTL, between <see cref="MinKeep"/> and <see cref="MaxKeep"/>.
/// </remarks>
public sealed class PublicDns : IDisposable
{
    private static readonly TimeSpan MinKeep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxKeep = TimeSpan.FromHours(1);
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(8);

    // asked by address, so asking needs no DNS; both certificates name these addresses
    private static readonly Uri[] Resolvers = [new("https://1.1.1.1/dns-query"), new("https://8.8.8.8/resolve")];

    private readonly HashSet<string> _hosts;
    private readonly HttpClient _client = new(new SocketsHttpHandler { ConnectTimeout = AskTimeout }) { Timeout = AskTimeout };
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (IPAddress[] Addresses, DateTimeOffset Until)> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="hosts">the hosts looked up this way; any other is left to the machine's resolver</param>
    public PublicDns(IEnumerable<string> hosts) => _hosts = new(hosts, StringComparer.OrdinalIgnoreCase);

    public bool Handles(string host) => _hosts.Contains(host);

    /// <summary>The host's addresses: from public DNS when it <see cref="Handles"/> the host and can say, else the machine's.</summary>
    public async Task<IPAddress[]> AddressesAsync(string host, CancellationToken cancel)
    {
        if (Handles(host) && await AskAsync(host, cancel) is { Length: > 0 } addresses)
            return addresses;
        return await Dns.GetHostAddressesAsync(host, cancel);
    }

    private async Task<IPAddress[]?> AskAsync(string host, CancellationToken cancel)
    {
        lock (_lock)
        {
            if (_known.TryGetValue(host, out var known) && known.Until > DateTimeOffset.UtcNow)
                return known.Addresses;
        }
        foreach (var resolver in Resolvers)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(resolver) { Query = $"name={Uri.EscapeDataString(host)}&type=A" }.Uri);
                request.Headers.Accept.ParseAdd("application/dns-json");
                using var response = await _client.SendAsync(request, cancel);
                if (!response.IsSuccessStatusCode)
                    continue;
                if (Answer(await response.Content.ReadAsStringAsync(cancel)) is not { } answer)
                    continue;
                var keep = TimeSpan.FromSeconds(Math.Clamp(answer.Ttl, MinKeep.TotalSeconds, MaxKeep.TotalSeconds));
                lock (_lock)
                    _known[host] = (answer.Addresses, DateTimeOffset.UtcNow + keep);
                return answer.Addresses;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !cancel.IsCancellationRequested)
            {
                // the next resolver, then the machine's
            }
        }
        return null;
    }

    /// <summary>The IPv4 addresses in a DNS-over-HTTPS JSON answer and their shortest TTL; null for none, or not an answer.</summary>
    internal static (IPAddress[] Addresses, int Ttl)? Answer(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.Number || status.GetInt32() != 0
                || !root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                return null;
            var addresses = new List<IPAddress>();
            var ttl = int.MaxValue;
            foreach (var answer in answers.EnumerateArray())
            {
                // type 1 is A; a CNAME on the way (type 5) is followed by the resolver, and its A records follow it
                if (answer.ValueKind != JsonValueKind.Object
                    || !answer.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number || type.GetInt32() != 1
                    || !answer.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String
                    || !IPAddress.TryParse(data.GetString(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                addresses.Add(address);
                if (answer.TryGetProperty("TTL", out var seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt32(out var value))
                    ttl = Math.Min(ttl, value);
            }
            return addresses.Count == 0 ? null : ([.. addresses], ttl == int.MaxValue ? 0 : ttl);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
