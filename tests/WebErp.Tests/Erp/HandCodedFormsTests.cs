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

        var missing = await OverlaySchema.MissingAsync(slot, Db);
        Assert.Equal("thiếu cột SY_User.CeoID, SY_User.TenNgan; thiếu bảng SY_UserGroup", missing);

        Task<IReadOnlySet<string>> All(string table) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(full[table], StringComparer.OrdinalIgnoreCase));
        Assert.Null(await OverlaySchema.MissingAsync(slot, All));
    }
}
