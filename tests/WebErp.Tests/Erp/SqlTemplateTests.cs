using Dapper;
using WebErp.Erp.Query;

namespace WebErp.Tests.Erp;

public sealed class SqlTemplateTests
{
    private static (bool, object?) Resolve(string t) => t switch
    {
        "BranchID" => (true, "HN"),
        "User" => (true, "admin"),
        "isManager" => (true, 1),
        _ => (false, null),
    };

    private static (string Sql, DynamicParameters P) Bind(string sql)
    {
        var p = new DynamicParameters();
        var n = 0;
        return (SqlTemplate.Bind(sql, Resolve, p, ref n), p);
    }

    [Theory]
    [InlineData("'{BranchID}' = '' or BranchID = '{BranchID}'")]
    [InlineData("StatusName like 'Update%' and Notes <> 'xóa; sửa'")]
    [InlineData("[Update Date] > 0 and DateUpdate > '2026-01-01'")]
    [InlineData("A.ItemName, A.Unit, A.HangSX")]
    [InlineData("isDelete = 0 -- ghi chú: delete")]
    public void Read_only_fragments_pass(string sql) => SqlTemplate.EnsureReadOnly(sql, "test");

    [Theory]
    [InlineData("1=1; drop table X")]
    [InlineData("1=1 exec sp_who")]
    [InlineData("UPDATE AP_PurchaseRequestTbl SET StatusID = 2")]
    [InlineData("select * into #t from X")]
    [InlineData("1=1 /* x */ ; select 1")]
    [InlineData("x in (select * from openrowset('a','b','c'))")]
    public void Write_or_multi_statement_fragments_are_rejected(string sql) =>
        Assert.Throws<UnsafeMetadataSqlException>(() => SqlTemplate.EnsureReadOnly(sql, "test"));

    [Fact]
    public void Tokens_in_string_literals_become_parameters()
    {
        var (sql, p) = Bind("'{BranchID}' = '' or BranchID = '{BranchID}'");
        Assert.Equal("(@__p0) = '' or BranchID = (@__p1)", sql);
        Assert.Equal("HN", p.Get<DbString>("@__p0").Value);
    }

    [Fact]
    public void Token_inside_longer_literal_is_concatenated_and_keeps_unicode_prefix()
    {
        var (sql, _) = Bind("Notes like N'%{User}%' and X = 'a''b'");
        Assert.Equal("Notes like (N'%' + @__p0 + N'%') and X = 'a''b'", sql);
    }

    [Fact]
    public void Bare_tokens_and_legacy_at_user_are_parameters_unknown_tokens_stay()
    {
        var (sql, p) = Bind("UserCreate = @User and {isManager} = 1 and Y = {Unknown} and @UserID = 1");
        Assert.Equal("UserCreate = @__p0 and @__p1 = 1 and Y = {Unknown} and @UserID = 1", sql);
        Assert.Equal(1, p.Get<int>("@__p1"));
    }
}
