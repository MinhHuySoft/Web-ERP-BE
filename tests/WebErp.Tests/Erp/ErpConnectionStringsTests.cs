using Microsoft.Data.SqlClient;
using WebErp.Erp.Connection;

namespace WebErp.Tests.Erp;

public sealed class ErpConnectionStringsTests
{
    private static readonly ErpOptions Options = new() { CommandTimeoutSeconds = 30, ConnectTimeoutSeconds = 15 };

    [Fact]
    public void Ssms_style_string_for_remote_server_is_made_safe_for_web()
    {
        // Kiểu chuỗi chép từ SSMS (máy chủ, tài khoản, mật khẩu đều GIẢ).
        const string raw = "Data Source=sql.example.test,14333;Initial Catalog=ErpDemo;Persist Security Info=True;User ID=u;Password=p;" +
                           "Pooling=False;MultipleActiveResultSets=False;Encrypt=False;TrustServerCertificate=True;" +
                           "Application Name=\"SQL Server Management Studio\";Command Timeout=0";
        var n = ErpConnectionStrings.Normalize(raw, Options);
        var csb = new SqlConnectionStringBuilder(n.ConnectionString);

        Assert.True(csb.Pooling);
        Assert.False(csb.PersistSecurityInfo);
        Assert.Equal(30, csb.CommandTimeout);
        Assert.Equal(15, csb.ConnectTimeout);
        Assert.Equal("WebErp", csb.ApplicationName);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, csb.Encrypt);
        Assert.True(csb.TrustServerCertificate); // giữ lựa chọn của người nhập
        Assert.Equal("ErpDemo", n.Database);
        Assert.Equal(5, n.Changes.Count);
        Assert.DoesNotContain(n.Changes, c => c.Contains("p;", StringComparison.Ordinal)); // không lộ mật khẩu
    }

    [Fact]
    public void Local_server_keeps_encryption_choice_and_clean_string_has_no_changes()
    {
        var n = ErpConnectionStrings.Normalize("Server=localhost;Database=Erp;Integrated Security=true;Encrypt=False", Options);
        Assert.Equal(SqlConnectionEncryptOption.Optional, new SqlConnectionStringBuilder(n.ConnectionString).Encrypt);
        Assert.Empty(n.Changes);
    }

    [Fact]
    public void Explicit_connect_timeout_is_kept_and_missing_database_is_reported()
    {
        var n = ErpConnectionStrings.Normalize("Server=tcp:db.example.test,1433;User ID=u;Password=p;Connect Timeout=40;Encrypt=True", Options);
        Assert.Equal(40, new SqlConnectionStringBuilder(n.ConnectionString).ConnectTimeout);
        Assert.Null(n.Database);
        Assert.Empty(n.Changes);
    }

    [Theory]
    [InlineData(".", true)]
    [InlineData("(local)", true)]
    [InlineData(@"localhost\SQLEXPRESS", true)]
    [InlineData("tcp:127.0.0.1,1433", true)]
    [InlineData(@"(localdb)\MSSQLLocalDB", true)]
    [InlineData("sql.example.test,14333", false)]
    [InlineData("10.0.0.5", false)]
    public void Detects_local_servers(string dataSource, bool local) =>
        Assert.Equal(local, ErpConnectionStrings.IsLocal(dataSource));
}
