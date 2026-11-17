namespace WebErp.HostSync.Platform;

/// <summary>Ánh xạ SSL của HTTP.sys qua <c>netsh http show|delete sslcert</c>.</summary>
public sealed class NetshSslBindings : ISslBindings
{
    private static readonly string Netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public SslCertBinding? Find(string host)
    {
        var r = Run(["http", "show", "sslcert", $"hostnameport={host}:443"]);
        // Không có ánh xạ: netsh trả mã khác 0 ("The system cannot find the file specified").
        return r.ExitCode != 0 ? null : Parse(r.Output).FirstOrDefault(b => string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<SslCertBinding> All()
    {
        var r = Run(["http", "show", "sslcert"]);
        if (r.ExitCode != 0) throw new InvalidOperationException($"netsh http show sslcert lỗi (mã {r.ExitCode}).");
        return Parse(r.Output);
    }

    public void Delete(string host)
    {
        var r = Run(["http", "delete", "sslcert", $"hostnameport={host}:443"]);
        if (r.ExitCode != 0 && Find(host) is not null)
            throw new InvalidOperationException($"netsh http delete sslcert hostnameport={host}:443 lỗi (mã {r.ExitCode}).");
    }

    private static ProcessResult Run(string[] args) => ProcessRunner.RunAsync(Netsh, args, Timeout, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Đọc kết quả <c>netsh http show sslcert</c>: các khối "Khóa : giá trị" cách nhau bởi dòng trống. Khối bắt đầu bằng
    /// <c>IP:port</c>, <c>Hostname:port</c> hoặc <c>Central Certificate Store</c>.
    /// </summary>
    internal static IReadOnlyList<SslCertBinding> Parse(string output)
    {
        var list = new List<SslCertBinding>();
        string? endpoint = null, hash = null, store = null;
        void Flush()
        {
            if (endpoint is not null && !string.IsNullOrWhiteSpace(hash) && hash != "(null)") list.Add(new SslCertBinding(endpoint, hash.Trim().ToUpperInvariant(), store));
            endpoint = hash = store = null;
        }
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 3)..].Trim();
            switch (key)
            {
                case "IP:port":
                case "Hostname:port":
                case "Central Certificate Store":
                    Flush();
                    endpoint = value;
                    break;
                case "Certificate Hash":
                    hash = value;
                    break;
                case "Certificate Store Name":
                    store = value.Length == 0 || value == "(null)" ? null : value;
                    break;
            }
        }
        Flush();
        return list;
    }
}
