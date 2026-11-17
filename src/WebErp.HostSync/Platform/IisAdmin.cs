using System.Runtime.Versioning;
using Microsoft.Web.Administration;

namespace WebErp.HostSync.Platform;

/// <summary>
/// IIS qua <c>Microsoft.Web.Administration</c> (cần quyền quản trị: agent chạy LocalSystem). Chỉ sửa đúng site được chỉ định,
/// mọi thay đổi trong một lần <see cref="ServerManager.CommitChanges"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IisAdmin : IIisAdmin
{
    public IisSnapshot Read()
    {
        using var sm = new ServerManager();
        return new IisSnapshot(sm.Sites.Select(s => new IisSite(s.Id, s.Name, s.Bindings.Select(ToModel).ToList())).ToList());
    }

    public void ApplyBindings(string siteName, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
    {
        foreach (var h in add.Concat(remove))
            if (!HostNames.IsSyncable(h)) throw new ArgumentException($"Tên miền '{h}' không hợp lệ.");
        using var sm = new ServerManager();
        var site = sm.Sites[siteName] ?? throw new InvalidOperationException($"Không thấy site IIS '{siteName}'.");

        foreach (var host in remove)
        foreach (var b in site.Bindings.Where(b => b.Protocol is "http" or "https" && string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase)).ToList())
            site.Bindings.Remove(b);

        foreach (var host in add)
        {
            if (!site.Bindings.Any(b => b.Protocol == "http" && string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase)))
                site.Bindings.Add($"*:80:{host}", "http");
            if (!site.Bindings.Any(b => b.Protocol == "https" && string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase)))
            {
                var https = site.Bindings.Add($"*:443:{host}", "https");
                https.SetAttributeValue("sslFlags", 1); // SNI
            }
        }
        sm.CommitChanges();
    }

    private static IisBinding ToModel(Binding b)
    {
        var ssl = 0;
        if (b.Protocol == "https")
        {
            try
            {
                ssl = Convert.ToInt32(b.GetAttributeValue("sslFlags"), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidCastException or System.Runtime.InteropServices.COMException)
            {
                ssl = 0;
            }
        }
        var hash = b.Protocol == "https" && b.CertificateHash is { Length: > 0 } h ? Convert.ToHexString(h) : null;
        return new IisBinding(b.Protocol, b.BindingInformation, ssl, hash);
    }
}
