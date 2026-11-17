using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace WebErp.HostSync.Platform;

/// <summary>
/// win-acme (<c>wacs.exe</c> 2.2.x). Cấp chứng chỉ bằng đúng lệnh đã dùng cho TinPhong:
/// <c>--source manual --host h --validation selfhosting --store certificatestore --installation iis --installationsiteid N --notaskscheduler</c>.
/// Renewal đọc từ <c>*.renewal.json</c>, hủy bằng <c>--cancel --id</c>.
/// </summary>
public sealed partial class WacsClient(HostSyncOptions options, ILogger<WacsClient> log) : IAcmeClient
{
    public async Task<AcmeResult> IssueAsync(string host, long siteId, CancellationToken ct)
    {
        if (!HostNames.IsSyncable(host)) throw new ArgumentException($"Tên miền '{host}' không hợp lệ.", nameof(host));
        if (!File.Exists(options.WacsPath)) return new AcmeResult(false, -1, $"Không thấy {options.WacsPath}.");
        string[] args =
        [
            "--source", "manual", "--host", host, "--validation", "selfhosting", "--store", "certificatestore",
            "--installation", "iis", "--installationsiteid", siteId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--notaskscheduler",
        ];
        log.LogInformation("Chạy win-acme cho {Host} (site {SiteId}).", host, siteId);
        var r = await ProcessRunner.RunAsync(options.WacsPath, args, TimeSpan.FromMinutes(options.WacsTimeoutMinutes), ct).ConfigureAwait(false);
        var summary = r.TimedOut ? $"quá {options.WacsTimeoutMinutes} phút" : Summarize(r.Output);
        if (r.ExitCode != 0 || r.TimedOut) log.LogWarning("win-acme cho {Host} lỗi (mã {Code}): {Summary}", host, r.ExitCode, summary);
        return new AcmeResult(r.ExitCode == 0 && !r.TimedOut, r.ExitCode, summary);
    }

    public IReadOnlyList<AcmeRenewal> Renewals()
    {
        var dir = options.ResolvedWacsConfigDir;
        if (!Directory.Exists(dir)) return [];
        var list = new List<AcmeRenewal>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.renewal.json"))
        {
            try
            {
                if (ParseRenewal(File.ReadAllText(file), file) is { } r) list.Add(r);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                log.LogWarning("Không đọc được renewal {File}: {Error}", Path.GetFileName(file), ex.Message);
            }
        }
        return list;
    }

    public async Task<bool> CancelAsync(AcmeRenewal renewal, CancellationToken ct)
    {
        if (!IdPattern().IsMatch(renewal.Id)) return false;
        var r = await ProcessRunner.RunAsync(options.WacsPath, ["--cancel", "--id", renewal.Id], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        var gone = !File.Exists(renewal.File);
        if (!gone) log.LogWarning("Hủy renewal {Id} chưa xong (mã {Code}): {Summary}", renewal.Id, r.ExitCode, Summarize(r.Output));
        return gone;
    }

    /// <summary>
    /// Đọc một tệp renewal: <c>Id</c>, <c>LastFriendlyName</c>, tên miền từ <c>TargetPluginOptions</c> (<c>CommonName</c>,
    /// <c>AlternativeNames</c>), site từ mọi thuộc tính <c>SiteId</c> trong <c>InstallationPluginOptions</c>.
    /// </summary>
    internal static AcmeRenewal? ParseRenewal(string json, string file)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("Id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) return null;
        var friendly = root.TryGetProperty("LastFriendlyName", out var f) ? f.GetString() : null;
        var hosts = new List<string>();
        if (root.TryGetProperty("TargetPluginOptions", out var target) && target.ValueKind == JsonValueKind.Object)
        {
            if (target.TryGetProperty("CommonName", out var cn) && cn.GetString() is { Length: > 0 } c) hosts.Add(c.ToLowerInvariant());
            if (target.TryGetProperty("AlternativeNames", out var alt) && alt.ValueKind == JsonValueKind.Array)
                hosts.AddRange(alt.EnumerateArray().Select(a => a.GetString()).OfType<string>().Select(a => a.ToLowerInvariant()));
        }
        var sites = new List<long>();
        if (root.TryGetProperty("InstallationPluginOptions", out var inst) && inst.ValueKind == JsonValueKind.Array)
            foreach (var o in inst.EnumerateArray()) CollectSiteIds(o, sites);
        return new AcmeRenewal(id, friendly, hosts.Distinct().ToList(), sites.Distinct().ToList(), file);
    }

    private static void CollectSiteIds(JsonElement e, List<long> sites)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name.Equals("SiteId", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var id)) sites.Add(id);
            else if (p.Value.ValueKind == JsonValueKind.Object) CollectSiteIds(p.Value, sites);
        }
    }

    /// <summary>Vài dòng cuối có ý nghĩa của đầu ra win-acme, che địa chỉ email.</summary>
    internal static string Summarize(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var pick = lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("fail", StringComparison.OrdinalIgnoreCase)).TakeLast(2).ToList();
        if (pick.Count == 0) pick = lines.TakeLast(1).ToList();
        var s = HostNames.MaskEmails(string.Join(" | ", pick));
        return s.Length <= 300 ? s : s[..300];
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();
}
