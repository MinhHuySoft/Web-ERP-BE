using WebErp.Core.Metadata;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;

namespace WebErp.Tests.Erp;

/// <summary>Bước 1 form cứng: danh sách lớp viết tay, kiểm cột của tệp mô tả, ghi lỗi SQL không lộ dữ liệu.</summary>
public sealed class HandCodedFormsTests
{
    [Theory]
    [InlineData("NguoiDungFrm")]            // danh sách gốc (Medcom)
    [InlineData("AR_InvoiceTinPhongMNFrm")] // chỉ có ở bản TinPhong
    [InlineData("eInvoiceSettingFrm")]
    [InlineData("SY_MakeDocumentIDFrm")]
    [InlineData("ar_invoicetinphongmnfrm")] // không phân biệt hoa thường, như WinForms
    public void Class_names_from_both_builds_are_hand_coded(string name) => Assert.Contains(name, HandCodedForms.Names);

    [Theory]
    [InlineData("BeeC_SyncCenter2Frm")] // form cấu hình (SY_FrmLstTbl), không phải lớp
    [InlineData("CustomerListFrm")]
    [InlineData("#")]
    public void Config_forms_are_not_hand_coded(string name) => Assert.DoesNotContain(name, HandCodedForms.Names);

    [Theory]
    [InlineData(207, "Invalid column name 'TenNgan'.", "SQL 207: Invalid column name 'TenNgan'.")]
    [InlineData(208, "Invalid object name 'dbo.SY_UserX'.", "SQL 208: Invalid object name 'dbo.SY_UserX'.")]
    [InlineData(4104, "The multi-part identifier \"G.UserGroupName\" could not be bound.", "SQL 4104: The multi-part identifier \"G.UserGroupName\" could not be bound.")]
    public void Schema_errors_are_logged_verbatim(int number, string message, string expected) =>
        Assert.Equal(expected, SqlErrorText.Describe(number, message));

    [Theory]
    [InlineData(245, "Conversion failed when converting the varchar value 'GIA-TRI-RIENG' to data type int.")]
    [InlineData(2627, "Violation of PRIMARY KEY constraint 'PK_X'. The duplicate key value is (KH-001).")]
    [InlineData(8114, "Error converting data type nvarchar to numeric.")]
    [InlineData(547, "The INSERT statement conflicted with the FOREIGN KEY constraint.")]
    public void Errors_that_may_contain_data_keep_only_the_number(int number, string message)
    {
        var s = SqlErrorText.Describe(number, message);
        Assert.StartsWith($"SQL {number};", s);
        Assert.DoesNotContain("GIA-TRI-RIENG", s);
        Assert.DoesNotContain("KH-001", s);
    }

    [Fact]
    public void Real_sql_exception_is_described_from_its_first_error()
    {
        Assert.StartsWith("SQL 207: ", SqlErrorText.Describe(Support.SqlErrors.Create(207)));
        Assert.Equal("SQL 245; nội dung lỗi không ghi vì có thể chứa dữ liệu", SqlErrorText.Describe(Support.SqlErrors.Create(245)));
    }

    private static SlotMeta Master(string form)
    {
        var o = FormOverlays.Get(form, null)!.Value;
        return MetadataCompiler.Compile(form, form, o.Lst, o.Rows, []).Master!;
    }

    [Fact]
    public void Overlay_columns_are_collected_per_table_with_aliases()
    {
        var req = OverlaySchema.Required(Master("NguoiDungFrm"));
        Assert.Contains("TenNgan", req["SY_User"]);
        Assert.Contains("StartupForm", req["SY_User"]);
        Assert.Contains("UserGroupID", req["SY_User"]);   // khóa nối bên trái
        Assert.Contains("BranchID", req["SY_User"]);      // FTW
        Assert.Contains("UserGroupName", req["SY_UserGroup"]); // G.UserGroupName qua bí danh
        Assert.Contains("UserGroupID", req["SY_UserGroup"]);   // khóa nối bên phải
        Assert.False(req.ContainsKey("CF_BranchTbl"));        // truy vấn con không tiền tố: bỏ qua
        Assert.DoesNotContain(req.Keys, k => k.Contains('{'));
    }

    [Fact]
    public void Every_overlay_slot_yields_plain_table_and_column_names()
    {
        foreach (var name in FormOverlays.Names)
        {
            var o = FormOverlays.Get(name, null)!.Value;
            var f = MetadataCompiler.Compile(name, name, o.Lst, o.Rows, []);
            foreach (var slot in f.Slots.Values)
            foreach (var (table, cols) in OverlaySchema.Required(slot))
            {
                Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", table);
                Assert.All(cols, c => Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", c));
            }
        }
    }

    [Fact]
    public async Task Missing_columns_and_tables_are_reported()
    {
        var slot = Master("NguoiDungFrm");
        var full = OverlaySchema.Required(slot);
        Task<IReadOnlySet<string>> Db(string table) => Task.FromResult<IReadOnlySet<string>>(
            table.Equals("SY_User", StringComparison.OrdinalIgnoreCase)
                ? new HashSet<string>(full["SY_User"].Where(c => c is not ("TenNgan" or "CeoID")), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // Thiếu bảng nối: không chạy.
        Assert.Equal("thiếu bảng SY_UserGroup; thiếu cột SY_User.CeoID, SY_User.TenNgan", await OverlaySchema.MissingAsync(slot, Db));

        Task<IReadOnlySet<string>> All(string table) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(full[table], StringComparer.OrdinalIgnoreCase));
        var ok = await OverlaySchema.ResolveAsync(slot, All);
        Assert.Same(slot, ok.Slot);
        Assert.Empty(ok.Dropped);
    }

    private static Func<string, Task<IReadOnlySet<string>>> DbWithout(SlotMeta slot, params string[] absent)
    {
        var full = OverlaySchema.Required(slot);
        return table => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(
            full.GetValueOrDefault(table, new HashSet<string>()).Where(c => !absent.Contains($"{table}.{c}", StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Display_only_columns_missing_in_db_are_dropped_like_winforms_select_star()
    {
        // TinPhong: SY_User thiếu vài cột của Medcom. WinForms đọc SY_User.* nên vẫn mở được; web bỏ cột thiếu.
        var slot = Master("NguoiDungFrm");
        var r = await OverlaySchema.ResolveAsync(slot, DbWithout(slot, "SY_User.TenNgan", "SY_User.CeoID", "SY_UserGroup.UserGroupName"));
        Assert.Null(r.Missing);
        Assert.Equal(["SY_User.CeoID", "SY_User.TenNgan", "SY_UserGroup.UserGroupName"], r.Dropped);
        Assert.DoesNotContain("TenNgan", r.Slot!.Select);
        Assert.DoesNotContain("CeoID", r.Slot.Select);
        Assert.Contains("SY_User.HoTen", r.Slot.Select);
        Assert.Null(r.Slot.Joins[0].Select); // G.UserGroupName là mục duy nhất của SE1
        Assert.Equal(slot.FilterWeb, r.Slot.FilterWeb);
    }

    [Fact]
    public async Task Missing_filter_join_or_sort_column_blocks_the_screen()
    {
        var slot = Master("NguoiDungFrm");
        // BranchID dùng trong điều kiện lọc chi nhánh (FTW): bỏ đi có thể lộ dữ liệu → không chạy.
        Assert.Equal("thiếu cột SY_User.BranchID", (await OverlaySchema.ResolveAsync(slot, DbWithout(slot, "SY_User.BranchID"))).Missing);
        // Khóa nối.
        Assert.Contains("SY_UserGroup.UserGroupID", (await OverlaySchema.ResolveAsync(slot, DbWithout(slot, "SY_UserGroup.UserGroupID"))).Missing);
        // Sắp xếp.
        Assert.Contains("SY_User.Disable", (await OverlaySchema.ResolveAsync(slot, DbWithout(slot, "SY_User.Disable"))).Missing);
    }

    [Fact]
    public async Task Expression_using_a_missing_column_is_dropped_as_a_whole()
    {
        // UserLogFrm T1: Note chỉ hiện khi có FormKey để che form người dùng; DB không có FormKey thì bỏ hẳn Note (an toàn).
        var o = FormOverlays.Get("UserLogFrm", null)!.Value;
        // {Mod} được thay bằng mã module trước khi biên dịch (ErpMetadata).
        var rows = o.Rows.Select(r => r with { KeyValue = r.KeyValue?.Replace("{Mod}", "SY", StringComparison.Ordinal) }).ToList();
        var f = MetadataCompiler.Compile("UserLogFrm", "UserLogFrm", o.Lst, rows, []);
        var t1 = f.Slots["T1"];
        var table = OverlaySchema.Required(t1).Keys.Single(k => k.EndsWith("_UserLogDetail", StringComparison.Ordinal));
        var r = await OverlaySchema.ResolveAsync(t1, DbWithout(t1, $"{table}.FormKey"));
        Assert.Null(r.Missing);
        Assert.DoesNotContain("Note", r.Slot!.Expression);
        Assert.Contains("AS VN", r.Slot.Expression); // mục khác của EX giữ nguyên
    }

    [Fact]
    public void Top_level_split_keeps_commas_inside_parentheses_and_quotes()
    {
        var items = OverlaySchema.SplitTopLevel("A.X, COALESCE(A.Y, 'a,b') AS Z, CASE WHEN A.W IN (1, 2) THEN 1 END AS V, [A,B].Q").ToList();
        Assert.Equal(4, items.Count);
        Assert.Equal(" COALESCE(A.Y, 'a,b') AS Z", items[1]);
    }

    [Fact]
    public void Inherited_form_uses_parent_overlay_under_its_own_key()
    {
        // AR_Invoice2Frm : AR_InvoiceFrm (lớp con rỗng) — cấu hình / quyền theo khóa AR_Invoice2Frm.
        var o = FormOverlays.Get("AR_Invoice2Frm", null)!.Value;
        Assert.Equal("AR_Invoice2Frm", o.Lst.FormID);
        Assert.Equal("EDIT", o.Lst.FormType);
        Assert.True(o.UseDbConfig);
        Assert.Equal(FormOverlays.Get("AR_InvoiceFrm", null)!.Value.Rows.Count, o.Rows.Count);
        Assert.Contains("AR_Invoice2Frm", HandCodedForms.Names);
    }

    [Fact]
    public void New_overlays_compile_and_their_sql_is_read_only()
    {
        var batch = FormOverlays.Get("GJ_DocBatchSetupfrm", null)!.Value;
        var b = MetadataCompiler.Compile("GJ_DocBatchSetupfrm", "GJ_DocBatchSetupfrm", batch.Lst, batch.Rows, []);
        Assert.True(b.IsEdit);
        Assert.Equal("DocBatchID", b.Master!.PrimaryKey);
        Assert.Equal("DocBatchID", b.Slots["T1"].DetailField);

        var log = FormOverlays.Get("UserLogFrm", null)!.Value;
        var l = MetadataCompiler.Compile("UserLogFrm", "UserLogFrm", log.Lst, log.Rows, []);
        var daily = l.Slots["T2"];
        Assert.True(daily.Independent);
        Assert.Equal(1000, daily.Top);
        WebErp.Erp.Query.SqlTemplate.EnsureReadOnly(daily.FromOverride!.Replace("{Mod}", "SY", StringComparison.Ordinal), "T2.FRM");
        Assert.Contains("THEN NULL ELSE A.Note END", daily.FromOverride, StringComparison.Ordinal); // che Note form người dùng
    }
}
