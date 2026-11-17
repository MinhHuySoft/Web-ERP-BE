namespace WebErp.HostSync;

// Mọi việc chạm vào hệ thống (IIS, HTTP.sys, win-acme, kho chứng chỉ, DNS, HTTPS) đi qua các interface dưới đây,
// để kiểm thử được bằng bản giả (tests/WebErp.Tests/HostSync).

/// <summary>Một binding IIS. <see cref="BindingInformation"/> dạng <c>ip:port:host</c>.</summary>
public sealed record IisBinding(string Protocol, string BindingInformation, int SslFlags = 0, string? CertificateHash = null)
{
    public string Host
    {
        get
        {
            var parts = BindingInformation.Split(':');
            return parts.Length >= 3 ? string.Join(':', parts[2..]).ToLowerInvariant() : "";
        }
    }

    public bool IsWeb => Protocol is "http" or "https";
}

public sealed record IisSite(long Id, string Name, IReadOnlyList<IisBinding> Bindings)
{
    public bool Has(string protocol, string host) =>
        Bindings.Any(b => b.Protocol == protocol && string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase));
}

public sealed record IisSnapshot(IReadOnlyList<IisSite> Sites)
{
    public IisSite? Site(string name) => Sites.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

public interface IIisAdmin
{
    IisSnapshot Read();

    /// <summary>
    /// Thêm binding <c>http *:80:host</c> + <c>https *:443:host</c> (sslFlags=1) cho <paramref name="add"/> và gỡ binding http/https của
    /// <paramref name="remove"/>, chỉ trên site <paramref name="siteName"/>, trong MỘT lần commit (app chỉ khởi động lại một lần).
    /// </summary>
    void ApplyBindings(string siteName, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove);
}

/// <summary>Ánh xạ SSL của HTTP.sys (<c>netsh http show sslcert</c>).</summary>
public sealed record SslCertBinding(string Endpoint, string CertificateHash, string? StoreName)
{
    /// <summary>Host nếu là ánh xạ SNI (<c>host:443</c>), null nếu là <c>ip:port</c>.</summary>
    public string? Host => Endpoint.Contains(':') && !char.IsDigit(Endpoint[0]) && !Endpoint.StartsWith('[') ? Endpoint[..Endpoint.LastIndexOf(':')].ToLowerInvariant() : null;
}

public interface ISslBindings
{
    SslCertBinding? Find(string host);

    IReadOnlyList<SslCertBinding> All();

    void Delete(string host);
}

public sealed record AcmeResult(bool Ok, int ExitCode, string Summary);

/// <summary>Một renewal của win-acme. <see cref="Hosts"/>: mọi tên trong chứng chỉ; <see cref="SiteIds"/>: site IIS được cài.</summary>
public sealed record AcmeRenewal(string Id, string? FriendlyName, IReadOnlyList<string> Hosts, IReadOnlyList<long> SiteIds, string File);

public interface IAcmeClient
{
    Task<AcmeResult> IssueAsync(string host, long siteId, CancellationToken ct);

    IReadOnlyList<AcmeRenewal> Renewals();

    Task<bool> CancelAsync(AcmeRenewal renewal, CancellationToken ct);
}

public sealed record CertInfo(string Thumbprint, DateTimeOffset NotAfter, IReadOnlyList<string> DnsNames)
{
    public bool Covers(string host) => DnsNames.Any(n => string.Equals(n, host, StringComparison.OrdinalIgnoreCase));

    public bool IsOnlyFor(string host) => DnsNames.Count > 0 && DnsNames.All(n => string.Equals(n, host, StringComparison.OrdinalIgnoreCase));
}

public interface ICertificateStore
{
    CertInfo? Get(string thumbprint);

    /// <summary>Chứng chỉ chỉ mang đúng một tên <paramref name="host"/>.</summary>
    IReadOnlyList<CertInfo> OnlyFor(string host);

    void Delete(string thumbprint);
}

public sealed record DnsAnswer(IReadOnlyList<string> A, IReadOnlyList<string> Aaaa, string? Error = null);

public interface IDnsCheck
{
    Task<DnsAnswer> QueryAsync(string host, CancellationToken ct);
}

public sealed record ProbeResult(int? Status, string? Error = null);

public interface IHttpsProbe
{
    /// <summary>Gọi <c>https://host/path</c> nhưng nối thẳng tới 127.0.0.1:443 (như <c>curl --resolve</c>), kiểm chứng chỉ như trình duyệt.</summary>
    Task<ProbeResult> GetAsync(string host, string path, CancellationToken ct);
}
