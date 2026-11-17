using System.Net;
using System.Net.Sockets;
using DnsClient;

namespace WebErp.HostSync.Platform;

/// <summary>Hỏi thẳng DNS công khai (mặc định 1.1.1.1), không qua DNS của máy, không cache.</summary>
public sealed class PublicDnsCheck(HostSyncOptions options) : IDnsCheck
{
    private readonly LookupClient _client = new(new LookupClientOptions(IPAddress.Parse(options.DnsServer))
    {
        UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 2, ThrowDnsErrors = false,
    });

    public async Task<DnsAnswer> QueryAsync(string host, CancellationToken ct)
    {
        try
        {
            var a = await _client.QueryAsync(host, QueryType.A, cancellationToken: ct).ConfigureAwait(false);
            if (a.HasError && a.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
                return new DnsAnswer([], [], $"DNS {options.DnsServer} trả lỗi {a.Header.ResponseCode}.");
            if (a.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
                return new DnsAnswer([], [], $"Chưa có bản ghi DNS cho domain này (kiểm qua {options.DnsServer}).");
            var aaaa = await _client.QueryAsync(host, QueryType.AAAA, cancellationToken: ct).ConfigureAwait(false);
            return new DnsAnswer(
                a.Answers.ARecords().Select(r => r.Address.ToString()).Distinct().ToList(),
                aaaa.HasError ? [] : aaaa.Answers.AaaaRecords().Select(r => r.Address.ToString()).Distinct().ToList());
        }
        catch (DnsResponseException ex)
        {
            return new DnsAnswer([], [], $"Không hỏi được DNS {options.DnsServer}: {ex.Message}");
        }
    }
}

/// <summary>
/// <c>GET https://host/path</c> nhưng nối thẳng tới 127.0.0.1:443 (như <c>curl --resolve host:443:127.0.0.1</c>). SNI và kiểm chứng chỉ
/// như trình duyệt: chứng chỉ sai / chưa gắn thì lỗi.
/// </summary>
public sealed class LocalHttpsProbe : IHttpsProbe, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectCallback = async (_, ct) =>
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await s.ConnectAsync(IPAddress.Loopback, 443, ct).ConfigureAwait(false);
                return new NetworkStream(s, ownsSocket: true);
            }
            catch
            {
                s.Dispose();
                throw;
            }
        },
    })
    {
        // Lần đầu sau khi đổi binding, app WebErp đang khởi động lại.
        Timeout = TimeSpan.FromSeconds(60),
    };

    public async Task<ProbeResult> GetAsync(string host, string path, CancellationToken ct)
    {
        try
        {
            using var r = await _http.GetAsync(new Uri($"https://{host}{path}"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return new ProbeResult((int)r.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var inner = ex.InnerException?.Message is { Length: > 0 } m ? $" ({m})" : "";
            return new ProbeResult(null, ex.Message + inner);
        }
    }

    public void Dispose() => _http.Dispose();
}
