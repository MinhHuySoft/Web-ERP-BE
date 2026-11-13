using WebErp.Core.Metadata;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;

namespace WebErp.Tests.Erp;

/// <summary>Danh sách chọn / tra cứu (SY_FrmDrdwTbl): dòng mẫu giống MedData 2026-11-13, đã rút gọn.</summary>
public sealed class LookupTests
{
    private static DropdownMeta Dd(string grid, string column, string type, string? source, string? value, string? display,
        string? dm2 = null, string? columns = null, string? paras = null) =>
        new(grid, column, type, source, value, display, dm2, columns, paras, null, false);

    [Theory]
    [InlineData("Select * From vAgency Order By AgencyID", "Select * From vAgency")]
    [InlineData("select a.X, (select top 1 y from T order by y) Y from A a order by a.X desc", "select a.X, (select top 1 y from T order by y) Y from A a")]
    [InlineData("select N'order by' as T from A", "select N'order by' as T from A")]
    [InlineData("select * from A -- order by x", "select * from A -- order by x")]
    [InlineData("select OrderByX from A", "select OrderByX from A")]
    [InlineData("SELECT * FROM vItem  Order  By ItemID, ItemName", "SELECT * FROM vItem")]
    public void Strips_only_the_outer_trailing_order_by(string sql, string expected) =>
        Assert.Equal(expected, ErpScreens.StripTrailingOrderBy(sql));

    [Fact]
    public void Source_must_be_a_plain_select()
    {
        Assert.Equal("Select * From CF_BranchTbl", ErpScreens.LookupSource(Dd("", "BranchID", "", "Select * From CF_BranchTbl order by BranchID", "BranchID", "BranchID")));
        Assert.Null(ErpScreens.LookupSource(Dd("grdChitiet", "ItemID", "DropSelect", "IV_GetItemByContractIDStp N'{0}'", "ItemID", "ItemID")));
        Assert.Null(ErpScreens.LookupSource(Dd("", "X", "", "with a as (select 1 x) select * from a", "x", "x")));
        Assert.Null(ErpScreens.LookupSource(Dd("", "X", "", "selectX from a", "x", "x")));
    }

    [Fact]
    public void Value_list_splits_by_column_count()
    {
        var d = Dd("", "Operation", "ValueList", "MUL;Phép nhân;DIV; Phép chia ;X", "Operation", "OperationName", columns: "Operation;OperationName");
        var rows = ErpScreens.ValueListRows(d);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("DIV", "Phép chia"), (rows[1]["Operation"], rows[1]["OperationName"]));
    }

    [Fact]
    public void Name_column_only_when_display_differs_and_type_is_a_list()
    {
        Assert.Equal("StatusName", Dd("grdListDoc", "StatusID", "Dropdown", "select StatusID, StatusName from T", "StatusID", "StatusName").NameColumn);
        Assert.Null(Dd("grdChitiet", "ItemID", "Dropdown", "Select * From vItem", "ItemID", "ItemID").NameColumn);
        Assert.Null(Dd("grdChitiet", "ObjectName", "DropSelect", "Select * from CF_ObjectTbl", "ObjectID", "ObjectName").NameColumn);
        // Ô chứng từ: cột hiển thị thứ hai của ComboX.
        Assert.Equal("BranchName", Dd("", "BranchID", "", "SELECT * FROM CF_BranchTbl", "BranchID", "BranchID", dm2: "BranchName").TextColumn);
        Assert.Equal([("BranchID", true), ("ItemID", false)], Dd("grdChitiet", "Lot", "DropSelect", "x", "Lot", "Lot", paras: "Master.BranchID; ItemID").Paras);
    }

    [Fact]
    public void Grid_names_follow_the_winforms_forms_and_GN_overrides()
    {
        var form = MetadataCompiler.Compile("F", "F", new FrmLstRow("F", "EDIT", "F", null, null, null, null, null, null, null),
        [
            new("T0", "TN", "A", null), new("T0", "PK", "ID", null),
            new("T1", "TN", "B", null), new("T1", "PK", "ID", null),
            new("TA", "TN", "C", null), new("TA", "PK", "ID", null),
            new("T2", "TN", "D", null), new("T2", "PK", "ID", null), new("T2", "GN", "grdHoaDon", null),
        ], []) with
        {
            Dropdowns = MetadataCompiler.Dropdowns(
            [
                new("grdListDoc", "StatusID", "Dropdown", "select 1", "StatusID", "StatusName", null, null, null, "Tình trạng", null),
                new("grdChitiet10", "ItemID", "", "select 1", "ItemID", "ItemName", null, null, null, null, true),
                new("grdHoaDon", "VATID", "", "select 1", "VATID", "VATName", null, null, null, null, null),
                new("", "FldObjectID", "", "select 1", "ObjectID", "ObjectID", "ObjectName", null, null, null, null),
                new("", "FldObjectID", "", "select 2", "X", "X", null, null, null, null, null),
            ]),
        };
        Assert.Equal("Tình trạng", form.GridDropdown(form.Slots["T0"], "StatusID")?.Caption);
        var withCtr = MetadataCompiler.Compile("F", "F", null, [new("T0", "TN", "A", null), new("T0", "PK", "ID", null), new("G", "grdListDoc;PurposeID", "Mục đích", "CV")], []);
        Assert.Equal("Mục đích", withCtr.Captions["G:grdListDoc;PurposeID"]);
        Assert.True(form.GridDropdown(form.Slots["TA"], "ItemID")?.Invisible);
        Assert.Equal("VATName", form.GridDropdown(form.Slots["T2"], "VATID")?.NameColumn);
        Assert.Null(form.GridDropdown(form.Slots["T1"], "StatusID"));
        Assert.Equal("select 1", form.MasterDropdown("ObjectID")?.Source); // trùng (lưới, cột): dòng đầu thắng
        Assert.Null(form.MasterDropdown("StatusID"));
    }

    [Theory]
    [InlineData("( BranchID = '{BranchID}' or BranchID2 = '{BranchID}' )", "( T.[BranchID] = '{BranchID}' or T.[BranchID2] = '{BranchID}' )")]
    [InlineData("A.BranchID = {BranchID} AND COALESCE(ImpExpID,'') <> 'XG'", "A.BranchID = {BranchID} AND COALESCE(T.[ImpExpID],'') <> 'XG'")]
    [InlineData("CAST(DocumentDate AS date) = {0} and [BranchID] = 'BranchID'", "CAST(T.[DocumentDate] AS date) = {0} and [BranchID] = 'BranchID'")]
    [InlineData("Status IN (1, 2) OR Memo LIKE N'%Status%'", "T.[Status] IN (1, 2) OR T.[Memo] LIKE N'%Status%'")]
    public void Bare_source_columns_in_filter_templates_get_the_table_name(string sql, string expected) =>
        Assert.Equal(expected, ErpScreens.QualifyColumns(sql, "T", new HashSet<string>(["BranchID", "BranchID2", "ImpExpID", "DocumentDate", "Status", "Memo", "date"], StringComparer.OrdinalIgnoreCase)));

    [Theory]
    [InlineData("Password", true)]
    [InlineData("PassWord", true)]
    [InlineData("Pass", true)]
    [InlineData("OldPassword", true)]
    [InlineData("ApiKey", true)]
    [InlineData("Api_Key", true)]
    [InlineData("AccessToken", true)]
    [InlineData("ClientSecret", true)]
    [InlineData("UserName", false)]
    [InlineData("isPass", false)]
    [InlineData("PassportNo", false)]
    public void Secret_columns_are_never_sent(string column, bool secret) => Assert.Equal(secret, ErpScreens.IsSecret(column));

    [Fact]
    public void Code_key_ignores_trailing_spaces_and_is_invariant()
    {
        Assert.Equal("KH01", ViewColumn.CodeKey("KH01  "));
        Assert.Equal("1.5", ViewColumn.CodeKey(1.5m));
        var col = new ViewColumn("StatusID", "Trạng thái", "text", null, false, false, Names: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["kh01"] = "Khách 1" });
        Assert.Equal("Khách 1", col.NameOf("KH01 "));
        Assert.Null(col.NameOf(null));
    }
}
