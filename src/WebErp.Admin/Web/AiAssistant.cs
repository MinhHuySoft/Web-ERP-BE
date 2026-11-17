using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using WebErp.Config;
using WebErp.Erp.Screens;

namespace WebErp.Admin.Web;

/// <summary>Tên cài đặt chung của trợ lý AI (lưu mã hóa trong <c>config.db</c>, bảng <c>setting</c>).</summary>
public static class AiSettingNames
{
    public const string Provider = "ai.provider";
    public const string Model = "ai.model";
    public const string Key = "ai.key";
}

/// <summary>Nhà cung cấp AI cho "Gợi ý bằng AI".</summary>
public static class AiProviders
{
    public const string Claude = "claude";
    public const string Gemini = "gemini";

    public static string DefaultModel(string provider) => provider == Gemini ? "gemini-2.5-flash" : "claude-opus-5-5";

    public static string Name(string provider) => provider == Gemini ? "Google Gemini" : "Anthropic Claude";
}

/// <summary>Một bảng ứng viên gửi cho AI: chỉ tên bảng và cột (không có dữ liệu).</summary>
public sealed record AiTable(string Name, IReadOnlyList<DbColumn> Columns);

/// <summary>Gợi ý của AI đã đối chiếu với DB (bảng / cột không có thật bị bỏ).</summary>
public sealed record AiSuggestion(string? Caption, QuickSlot? Master, IReadOnlyList<QuickSlot> Details, string? Note);

/// <summary>
/// Gợi ý bảng / cột cho "Tạo nhanh" bằng Claude (SDK Anthropic chính thức) hoặc Gemini (REST <c>generateContent</c>). Chỉ gửi tên form,
/// tên menu, tên bảng / cột / kiểu / tiêu đề cột của DB công ty; KHÔNG gửi dữ liệu. Khóa API lưu mã hóa, không ghi log, không hiện lại.
/// </summary>
public sealed class AiAssistant(SqliteTenantStore store, ILogger<AiAssistant> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<(string Provider, string Model, bool HasKey)> SettingsAsync(CancellationToken ct)
    {
        var provider = await store.GetSettingAsync(AiSettingNames.Provider, ct) ?? "";
        var model = await store.GetSettingAsync(AiSettingNames.Model, ct);
        var key = await store.GetSettingAsync(AiSettingNames.Key, ct);
        return (provider, string.IsNullOrWhiteSpace(model) ? AiProviders.DefaultModel(provider) : model, !string.IsNullOrEmpty(key));
    }

    public async Task<(AiSuggestion? Suggestion, string? Error)> SuggestAsync(string form, string? menuCaption, IReadOnlyList<AiTable> tables, CancellationToken ct)
    {
        var (provider, model, _) = await SettingsAsync(ct);
        var key = await store.GetSettingAsync(AiSettingNames.Key, ct);
        if (provider is not (AiProviders.Claude or AiProviders.Gemini) || string.IsNullOrEmpty(key))
            return (null, "Chưa cài đặt AI: vào Mô tả form cứng → Cài đặt AI, chọn Claude hoặc Gemini và nhập khóa API.");
        if (tables.Count == 0) return (null, "Không có bảng ứng viên nào để gợi ý.");
        var prompt = Prompt(form, menuCaption, tables);
        string text;
        try
        {
            text = provider == AiProviders.Claude ? await ClaudeAsync(key, model, prompt, ct) : await GeminiAsync(key, model, prompt, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AnthropicApiException or AnthropicIOException or InvalidOperationException or JsonException)
        {
            // Không ghi nội dung lỗi thô (có thể chứa phần đầu khóa trong một số thông báo của nhà cung cấp).
            log.LogWarning("Gợi ý AI ({Provider}, {Model}) lỗi: {Type}", provider, model, ex.GetType().Name);
            return (null, $"{AiProviders.Name(provider)} không trả lời được ({Describe(ex)}). Kiểm tra khóa API / tên model trong Cài đặt AI.");
        }
        var s = Parse(text, tables);
        return s is null ? (null, "AI trả lời không đúng định dạng hoặc chọn bảng không có trong DB. Thử lại hoặc chọn tay.") : (s, null);
    }

    private static string Describe(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException => "khóa API sai",
        AnthropicRateLimitException => "vượt giới hạn tần suất, thử lại sau",
        AnthropicIOException => "không kết nối được Anthropic",
        AnthropicApiException => "lỗi API của Anthropic",
        TaskCanceledException => "quá thời gian chờ",
        HttpRequestException h => h.StatusCode is { } code ? $"HTTP {(int)code}" : "không kết nối được",
        _ => "trả lời không đọc được",
    };

    private const string System = """
        You map a hand-coded ERP WinForms form to the SQL Server tables it most likely reads, so a read-only web screen can be built.
        You get the form's class name, its Vietnamese menu caption, and candidate tables with their columns (name, SQL type, primary key flag, Vietnamese caption).
        Choose ONLY tables and columns from the list. Prefer the table whose name matches the form (e.g. AR_OrderFrm -> AR_OrderTbl) and, for document forms,
        a detail table (e.g. AR_OrderDetailTbl) linked by a column holding the master's key. The web screen reads EVERY column of each table, like the
        ERP grid; "columns" only lists the ones shown by default, in a sensible order. Include every column a user of this ERP screen would normally
        look at (codes, dates, customer / item / employee codes, quantities, prices, amounts, VAT, discounts, status, notes), typically 15-30 per table;
        leave out only technical columns (row ids, sync flags, created/updated by). Sort documents by their date column, newest first.
        Code columns (ObjectID, ItemID…) get their names from catalog tables automatically; do not pick catalog tables as details.
        Answer with ONE JSON object and nothing else:
        {"caption": "screen title in Vietnamese",
         "master": {"table": "...", "primaryKey": "...", "columns": ["..."], "sortColumn": "... or null", "sortDesc": true},
         "details": [{"table": "...", "primaryKey": "...", "masterKey": "column of the master table", "detailKey": "column of this table",
                      "caption": "tab title in Vietnamese", "columns": ["..."]}],
         "note": "one short Vietnamese sentence on why, or uncertainty"}
        Use at most 3 details; use [] when the form is a simple list.
        """;

    internal static string Prompt(string form, string? caption, IReadOnlyList<AiTable> tables)
    {
        var sb = new StringBuilder();
        sb.Append("Form: ").Append(form).Append('\n');
        if (!string.IsNullOrWhiteSpace(caption)) sb.Append("Menu caption: ").Append(caption).Append('\n');
        sb.Append("Candidate tables:\n");
        foreach (var t in tables)
        {
            sb.Append("- ").Append(t.Name).Append(": ");
            sb.Append(string.Join(", ", t.Columns.Take(80).Select(c =>
                $"{c.Name} {c.Type}{(c.IsPrimaryKey ? " PK" : "")}{(string.IsNullOrWhiteSpace(c.Caption) ? "" : $" \"{c.Caption}\"")}")));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static async Task<string> ClaudeAsync(string key, string model, string prompt, CancellationToken ct)
    {
        AnthropicClient client = new() { ApiKey = key };
        // Server-side fallback (beta): nếu bộ lọc an toàn từ chối, máy chủ tự chuyển sang model dự phòng trong cùng lần gọi.
        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = System,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Messages = [new BetaUserMessageParam { Content = prompt }],
        }, ct);
        if (response.StopReason == "refusal") throw new InvalidOperationException("refusal");
        return string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
    }

    private static async Task<string> GeminiAsync(string key, string model, string prompt, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
        req.Headers.Add("x-goog-api-key", key);
        req.Content = JsonContent.Create(new
        {
            systemInstruction = new { parts = new[] { new { text = System } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new { responseMimeType = "application/json", temperature = 0.1 },
        });
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var parts = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
        return string.Concat(parts.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));
    }

    /// <summary>Đọc JSON trả lời (lấy đối tượng { … } đầu tiên), chỉ giữ bảng / cột có thật trong danh sách ứng viên.</summary>
    internal static AiSuggestion? Parse(string text, IReadOnlyList<AiTable> tables)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            var byName = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            string? S(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            QuickSlot? Slot(JsonElement e, bool detail)
            {
                if (S(e, "table") is not { } tn || !byName.TryGetValue(tn, out var t)) return null;
                var cols = t.Columns.ToDictionary(c => c.Name, c => c.Name, StringComparer.OrdinalIgnoreCase);
                string? Col(string? n) => n is not null && cols.TryGetValue(n, out var real) ? real : null;
                var pk = Col(S(e, "primaryKey")) ?? t.Columns.FirstOrDefault(c => c.IsPrimaryKey)?.Name;
                if (pk is null) return null;
                var chosen = e.TryGetProperty("columns", out var ce) && ce.ValueKind == JsonValueKind.Array
                    ? ce.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? Col(x.GetString()) : null).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : [];
                var desc = !(e.TryGetProperty("sortDesc", out var sd) && sd.ValueKind == JsonValueKind.False);
                if (!detail) return new QuickSlot(t.Name, pk, chosen, Col(S(e, "sortColumn")), desc);
                var detailKey = Col(S(e, "detailKey"));
                return detailKey is null ? null : new QuickSlot(t.Name, pk, chosen, null, true, S(e, "caption"), S(e, "masterKey"), detailKey);
            }
            var master = root.TryGetProperty("master", out var me) ? Slot(me, false) : null;
            if (master is null) return null;
            var masterCols = byName[master.Table].Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var details = new List<QuickSlot>();
            if (root.TryGetProperty("details", out var de) && de.ValueKind == JsonValueKind.Array)
                foreach (var d in de.EnumerateArray().Take(3))
                    if (Slot(d, true) is { } s && s.MasterKey is not null && masterCols.Contains(s.MasterKey)
                        && !s.Table.Equals(master.Table, StringComparison.OrdinalIgnoreCase))
                        details.Add(s);
            return new AiSuggestion(S(root, "caption"), master, details, S(root, "note"));
        }
    }
}
