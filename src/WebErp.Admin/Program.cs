using WebErp.Admin.Web;

namespace WebErp.Admin;

/// <summary>
/// Một chương trình, hai cách chạy:
/// <list type="bullet">
/// <item><c>WebErp.Admin.exe &lt;lệnh&gt;</c> (init, tenant, admin, backup…): dòng lệnh trên VPS;</item>
/// <item>không có lệnh (IIS chạy): trang quản trị trên web.</item>
/// </list>
/// </summary>
public sealed class Program
{
    private Program()
    {
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && Cli.Commands.Contains(args[0])) return await Cli.RunAsync(args);
        await AdminWeb.Build(args).RunAsync();
        return 0;
    }
}
