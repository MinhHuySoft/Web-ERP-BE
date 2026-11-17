using WebErp.HostSync;

namespace WebErp.Tests.HostSync;

/// <summary>IIS giả: các site và binding trong bộ nhớ, đếm số lần commit (mỗi lần commit = app WebErp khởi động lại).</summary>
public sealed class FakeIis : IIisAdmin
{
    public List<(long Id, string Name, List<IisBinding> Bindings)> Sites { get; } = [];
    public int Commits { get; private set; }
    public Exception? FailWith { get; set; }

    public FakeIis Site(long id, string name, params IisBinding[] bindings)
    {
        Sites.Add((id, name, bindings.ToList()));
        return this;
    }

    public IisSnapshot Read() => new(Sites.Select(s => new IisSite(s.Id, s.Name, s.Bindings.ToList())).ToList());

    public void ApplyBindings(string siteName, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
    {
        if (FailWith is not null) throw FailWith;
        var site = Sites.Single(s => s.Name == siteName);
        site.Bindings.RemoveAll(b => b.IsWeb && remove.Contains(b.Host, StringComparer.OrdinalIgnoreCase));
        foreach (var h in add)
        {
            if (!site.Bindings.Any(b => b.Protocol == "http" && b.Host == h)) site.Bindings.Add(new IisBinding("http", $"*:80:{h}"));
            if (!site.Bindings.Any(b => b.Protocol == "https" && b.Host == h)) site.Bindings.Add(new IisBinding("https", $"*:443:{h}", 1));
        }
        Commits++;
    }

    public List<IisBinding> Bindings(string site) => Sites.Single(s => s.Name == site).Bindings;

    /// <summary>Gắn chứng chỉ vào binding https (như win-acme --installation iis).</summary>
    public void AttachCert(string host, string thumb)
    {
        foreach (var s in Sites)
            for (var i = 0; i < s.Bindings.Count; i++)
                if (s.Bindings[i].Protocol == "https" && s.Bindings[i].Host == host) s.Bindings[i] = s.Bindings[i] with { CertificateHash = thumb };
    }
}

public sealed class FakeSsl : ISslBindings
{
    public Dictionary<string, SslCertBinding> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Deleted { get; } = [];

    public SslCertBinding? Find(string host) => Map.GetValueOrDefault($"{host}:443");

    public IReadOnlyList<SslCertBinding> All() => Map.Values.ToList();

    public void Delete(string host)
    {
        Map.Remove($"{host}:443");
        Deleted.Add(host);
    }

    public void Add(string endpoint, string thumb) => Map[endpoint] = new SslCertBinding(endpoint, thumb, "WebHosting");
}

public sealed class FakeCerts : ICertificateStore
{
    public Dictionary<string, CertInfo> Store { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Deleted { get; } = [];

    public CertInfo? Get(string thumbprint) => Store.GetValueOrDefault(thumbprint);

    public IReadOnlyList<CertInfo> OnlyFor(string host) => Store.Values.Where(c => c.IsOnlyFor(host)).ToList();

    public void Delete(string thumbprint)
    {
        Store.Remove(thumbprint);
        Deleted.Add(thumbprint);
    }

    public void Add(string thumb, DateTimeOffset notAfter, params string[] names) => Store[thumb] = new CertInfo(thumb, notAfter, names);
}

/// <summary>win-acme giả: cấp thì tạo chứng chỉ + ánh xạ SSL + renewal, giống lệnh thật.</summary>
public sealed class FakeAcme(FakeIis iis, FakeSsl ssl, FakeCerts certs, TimeProvider time) : IAcmeClient
{
    private int _n;
    public List<AcmeRenewal> Store { get; } = [];
    public List<string> Issued { get; } = [];
    public List<string> Cancelled { get; } = [];
    public int? FailExitCode { get; set; }

    public Task<AcmeResult> IssueAsync(string host, long siteId, CancellationToken ct)
    {
        Issued.Add(host);
        if (FailExitCode is { } code) return Task.FromResult(new AcmeResult(false, code, "Authorization failed for user@example.com"));
        var thumb = $"CERT{++_n:D4}{host.GetHashCode():X8}";
        certs.Add(thumb, time.GetUtcNow().AddDays(90), host);
        ssl.Add($"{host}:443", thumb);
        iis.AttachCert(host, thumb);
        Store.RemoveAll(r => r.Hosts.SequenceEqual([host]));
        Store.Add(new AcmeRenewal("r" + _n, "[Manual] " + host, [host], [siteId], "r" + _n + ".renewal.json"));
        return Task.FromResult(new AcmeResult(true, 0, "ok"));
    }

    public IReadOnlyList<AcmeRenewal> Renewals() => Store.ToList();

    public Task<bool> CancelAsync(AcmeRenewal renewal, CancellationToken ct)
    {
        Store.Remove(renewal);
        Cancelled.Add(renewal.Id);
        return Task.FromResult(true);
    }
}

public sealed class FakeDns(string ip) : IDnsCheck
{
    public Dictionary<string, DnsAnswer> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Queries { get; } = [];

    public Task<DnsAnswer> QueryAsync(string host, CancellationToken ct)
    {
        Queries.Add(host);
        return Task.FromResult(Answers.GetValueOrDefault(host) ?? new DnsAnswer([ip], []));
    }
}

/// <summary>HTTPS giả: có ánh xạ SSL đúng domain thì trả <see cref="Status"/>, không thì lỗi TLS.</summary>
public sealed class FakeProbe(FakeSsl ssl, FakeCerts certs) : IHttpsProbe
{
    public int Status { get; set; } = 200;
    public List<string> Calls { get; } = [];

    public Task<ProbeResult> GetAsync(string host, string path, CancellationToken ct)
    {
        Calls.Add(host + path);
        var b = ssl.Find(host);
        return Task.FromResult(b is not null && certs.Get(b.CertificateHash)?.Covers(host) == true
            ? new ProbeResult(Status)
            : new ProbeResult(null, "The SSL connection could not be established"));
    }
}
