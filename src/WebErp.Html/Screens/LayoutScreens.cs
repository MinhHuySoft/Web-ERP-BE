using WebErp.Core.Layout;
using WebErp.Core.Messages;
using WebErp.Html.Roles;

namespace WebErp.Html.Screens;

/// <param name="Self">URL màn vẽ layout (để FE nạp lại).</param>
/// <param name="Data">URL bố cục JSON <c>erp-form-layout/1</c> (<c>data-layout-src</c>).</param>
/// <param name="Save">URL lưu (POST); null = chỉ xem.</param>
/// <param name="Source">URL nguồn chép bố cục từ form khác, có <c>{fid}</c>; null = không chép được.</param>
public sealed record LayoutDesignerUrls(string Self, string? Para, string Data, string? Save, string? Source = null);

/// <summary>Màn vẽ layout form (chuẩn ERP-HTML 15.3): FE làm toàn bộ giao diện, BE gửi URL bố cục và form lưu có token.</summary>
public static class LayoutScreens
{
    public static string Designer(LayoutDesignView v, string formName, string runtimeName, string? menuId, LayoutDesignerUrls urls,
        string antiforgeryField, string antiforgeryToken)
    {
        var canSave = v.CanSave && urls.Save is not null;
        return ScreenRenderer.Render(new ScreenModel(formName, FormTypes.Custom, $"layout:{runtimeName}", $"Vẽ layout — {v.Title ?? formName}",
            Mode: canSave ? "edit" : "view", Para: urls.Para, MenuId: menuId,
            Messages: canSave ? null : [ErpMessage.Info(v.Note ?? "Bạn chỉ được xem bố cục (vẽ layout cần quyền Admin trên form, như chế độ quản trị của ERP).")]), w =>
        {
            w.Open("section", ("data-erp", "layout-designer"), ("data-form", runtimeName), ("data-layout-src", urls.Data),
                ("data-source-src", canSave ? urls.Source : null)).Line();
            if (canSave)
            {
                w.Open("form", ("method", "post"), ("action", urls.Save)).Line();
                w.Void("input", ("type", "hidden"), ("name", antiforgeryField), ("value", antiforgeryToken)).Line();
                w.Void("input", ("type", "hidden"), ("name", "Form"), ("value", runtimeName)).Line();
                if (urls.Para is not null) w.Void("input", ("type", "hidden"), ("name", "para"), ("value", urls.Para)).Line();
                w.Close("form").Line();
            }
            w.Close("section").Line();
        });
    }
}
