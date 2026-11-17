using System.Security.Cryptography.X509Certificates;

namespace WebErp.HostSync.Platform;

/// <summary>Kho chứng chỉ máy (mặc định <c>LocalMachine\WebHosting</c>, nơi win-acme lưu).</summary>
public sealed class WindowsCertificateStore(HostSyncOptions options) : ICertificateStore
{
    public CertInfo? Get(string thumbprint)
    {
        // Ánh xạ SSL có thể trỏ tới kho khác (vd My) với các site cấu hình tay: tìm cả hai.
        foreach (var name in new[] { options.CertificateStore, "My" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var store = Open(name, OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            if (store is null) continue;
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            try
            {
                if (found.Count > 0) return ToModel(found[0]);
            }
            finally
            {
                foreach (var c in found) c.Dispose();
            }
        }
        return null;
    }

    public IReadOnlyList<CertInfo> OnlyFor(string host)
    {
        using var store = Open(options.CertificateStore, OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        if (store is null) return [];
        var list = new List<CertInfo>();
        foreach (var c in store.Certificates)
        {
            using (c)
            {
                var m = ToModel(c);
                if (m.IsOnlyFor(host)) list.Add(m);
            }
        }
        return list;
    }

    public void Delete(string thumbprint)
    {
        // Chỉ xóa trong kho của agent (WebHosting), không bao giờ ở kho My.
        using var store = Open(options.CertificateStore, OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly)
                          ?? throw new InvalidOperationException($"Không mở được kho chứng chỉ {options.CertificateStore}.");
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        foreach (var c in found)
        {
            store.Remove(c);
            c.Dispose();
        }
    }

    private static X509Store? Open(string name, OpenFlags flags)
    {
        var store = new X509Store(name, StoreLocation.LocalMachine);
        try
        {
            store.Open(flags);
            return store;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            store.Dispose();
            return null;
        }
    }

    private static CertInfo ToModel(X509Certificate2 c)
    {
        var names = new List<string>();
        foreach (var ext in c.Extensions)
            if (ext is X509SubjectAlternativeNameExtension san)
                names.AddRange(san.EnumerateDnsNames().Select(n => n.ToLowerInvariant()));
        if (names.Count == 0 && c.GetNameInfo(X509NameType.DnsName, false) is { Length: > 0 } cn) names.Add(cn.ToLowerInvariant());
        return new CertInfo(c.Thumbprint.ToUpperInvariant(), new DateTimeOffset(c.NotAfter.ToUniversalTime(), TimeSpan.Zero), names.Distinct().ToList());
    }
}
