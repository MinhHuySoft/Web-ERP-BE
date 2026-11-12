using WebErp.Core.Menu;

namespace WebErp.Tests.Core;

public sealed class MenuTreeTests
{
    private static MenuEntry E(string id, string? parent, string caption, string? form = null, string? para = null) =>
        new(id, parent, caption, form, para, false, false);

    [Fact]
    public void Builds_tree_in_MenuID_order_and_classifies_items()
    {
        var tree = MenuTree.Build([
            E("05", null, "Mua hàng"),
            E("0530", "05", "Bảng kê mua hàng", "AP_PurchaseDetailReport"),
            E("0501", "05", "Đề nghị mua hàng", "AP_PurposeRequestListFrm"),
            E("01", null, "Hệ thống"),
            E("0199", "01", "Đăng xuất", "LogOff"),
        ]);

        Assert.Equal(["01", "05"], tree.Select(n => n.Entry.MenuId));
        var purchase = tree[1];
        Assert.Equal(["0501", "0530"], purchase.Children.Select(n => n.Entry.MenuId));
        Assert.Equal(MenuKind.Form, purchase.Children[0].Kind);
        Assert.Equal(MenuKind.Report, purchase.Children[1].Kind);
        Assert.Equal(MenuKind.Command, tree[0].Children[0].Kind);
        Assert.Equal(2, purchase.Children[0].Depth);
    }

    [Fact]
    public void Drops_groups_without_any_leaf()
    {
        var tree = MenuTree.Build([
            E("01", null, "Hệ thống"),
            E("0101", "01", "Nhóm rỗng"),
            E("02", null, "Danh mục"),
            E("0201", "02", "Khách hàng", "ObjectListFrm"),
        ]);
        Assert.Single(tree);
        Assert.Equal("02", tree[0].Entry.MenuId);
    }

    [Fact]
    public void Screen_key_includes_para()
    {
        var tree = MenuTree.Build([E("09", null, "Quỹ"), E("0901", "09", "Phiếu thu NH", "CS_ReceiptFrm", "Bank")]);
        Assert.Equal("CS_ReceiptFrm_Bank", tree[0].Children[0].ScreenKey);
    }

    [Fact]
    public void Items_whose_parent_is_missing_become_roots_and_depth_is_capped()
    {
        var tree = MenuTree.Build([
            E("0901", "09", "Mồ côi", "CS_ReceiptFrm"),
            E("1", null, "c1"), E("11", "1", "c2"), E("111", "11", "c3"), E("1111", "111", "c4"), E("11111", "1111", "c5"),
            E("111111", "11111", "lá cấp 6", "Deep6Frm"),
            E("11112", "1111", "lá cấp 5", "Deep5Frm"),
        ]);
        Assert.Contains(tree, n => n.Entry.MenuId == "0901" && n.Kind == MenuKind.Form);
        var c4 = tree.Single(n => n.Entry.MenuId == "1").Children[0].Children[0].Children[0];
        Assert.Equal(["11112"], c4.Children.Select(n => n.Entry.MenuId)); // nhóm cấp 5 chỉ chứa lá cấp 6 thì bị bỏ
    }
}
