using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using WebErp.Core.Auth;

namespace WebErp.Config;

public enum AdminLoginStatus
{
    Ok,
    Invalid,
    Locked,
}

public sealed record AdminLoginResult(AdminLoginStatus Status, DateTimeOffset? LockedUntil = null);

public sealed record AdminAccount(string UserName, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

/// <summary>
/// Tài khoản quản trị (bảng <c>admin_user</c>): KHÔNG dùng <c>SY_User</c> của ERP (QĐ-07 hệ quả 7).
/// Đăng nhập = mật khẩu PBKDF2 + mã TOTP. Sai 5 lần liên tiếp thì khóa 15 phút. Chỉ tiến trình quản trị dùng lớp này.
/// </summary>
public sealed partial class AdminAccountStore(ConfigDatabase db, SecretProtector protector, TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    [GeneratedRegex("^[A-Za-z0-9._-]{3,40}$")]
    private static partial Regex NamePattern();

    public int Count()
    {
        using var c = db.OpenReadOnly();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM admin_user");
    }

    public IReadOnlyList<AdminAccount> List()
    {
        using var c = db.OpenReadOnly();
        return c.Query<(string Name, string Created, string? Last)>("SELECT user_name, created_at, last_login_at FROM admin_user ORDER BY user_name")
            .Select(r => new AdminAccount(r.Name, SqliteTenantStore.ParseTime(r.Created)!.Value, SqliteTenantStore.ParseTime(r.Last)))
            .ToList();
    }

    public bool Exists(string userName)
    {
        using var c = db.OpenReadOnly();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM admin_user WHERE user_name = @userName", new { userName }) > 0;
    }

    /// <summary>Tạo tài khoản. <paramref name="verifiedStep"/>: bước TOTP người dùng vừa nhập đúng lúc thiết lập (không dùng lại được).</summary>
    public void Create(string userName, string password, byte[] totpSecret, long verifiedStep, string actor)
    {
        if (!NamePattern().IsMatch(userName)) throw new ArgumentException("Tên đăng nhập quản trị chỉ gồm chữ, số, '.', '_', '-', từ 3 đến 40 ký tự.");
        if (AdminPasswordHasher.Weakness(password) is { } weak) throw new ArgumentException(weak);
        var now = Iso(time.GetUtcNow());
        using var c = db.OpenReadWrite();
        using var tx = c.BeginTransaction();
        c.Execute("INSERT INTO admin_user (user_name, password_hash, totp_protected, totp_last_step, created_at) VALUES (@userName, @hash, @totp, @step, @now)",
            new { userName, hash = AdminPasswordHasher.Hash(password), totp = protector.ProtectTotp(totpSecret), step = verifiedStep, now }, tx);
        c.Execute("INSERT INTO admin_audit (at, actor, action, target) VALUES (@now, @actor, 'admin.create', @userName)", new { now, actor, userName }, tx);
        tx.Commit();
    }

    public void Remove(string userName, string actor)
    {
        var now = Iso(time.GetUtcNow());
        using var c = db.OpenReadWrite();
        using var tx = c.BeginTransaction();
        if (c.Execute("DELETE FROM admin_user WHERE user_name = @userName", new { userName }, tx) == 0)
            throw new InvalidOperationException($"Không có tài khoản quản trị '{userName}'.");
        c.Execute("INSERT INTO admin_audit (at, actor, action, target) VALUES (@now, @actor, 'admin.remove', @userName)", new { now, actor, userName }, tx);
        tx.Commit();
    }

    /// <summary>Đăng nhập: mật khẩu + mã TOTP. Mọi lần sai (kể cả tên không tồn tại) trả cùng một kết quả <c>Invalid</c>.</summary>
    public AdminLoginResult Login(string userName, string password, string? code)
    {
        var now = time.GetUtcNow();
        using var c = db.OpenReadWrite();
        var row = c.QuerySingleOrDefault<Row>(
            "SELECT user_name AS UserName, password_hash AS PasswordHash, totp_protected AS TotpProtected, totp_last_step AS TotpLastStep, " +
            "failed_count AS FailedCount, locked_until AS LockedUntil FROM admin_user WHERE user_name = @userName", new { userName });
        if (row is null)
        {
            AdminPasswordHasher.Verify(password, DummyHash); // giữ thời gian trả lời giống trường hợp có tài khoản
            return new AdminLoginResult(AdminLoginStatus.Invalid);
        }
        if (SqliteTenantStore.ParseTime(row.LockedUntil) is { } until && until > now) return new AdminLoginResult(AdminLoginStatus.Locked, until);

        var step = AdminPasswordHasher.Verify(password, row.PasswordHash)
            ? Totp.Verify(protector.UnprotectTotp(row.TotpProtected), code, now, row.TotpLastStep)
            : null;
        if (step is null)
        {
            var failures = row.FailedCount + 1;
            var lockUntil = failures >= MaxFailures ? now + LockFor : (DateTimeOffset?)null;
            c.Execute("UPDATE admin_user SET failed_count = @f, locked_until = @l WHERE user_name = @n",
                new { f = lockUntil is null ? failures : 0, l = lockUntil is null ? null : Iso(lockUntil.Value), n = row.UserName });
            return lockUntil is null ? new AdminLoginResult(AdminLoginStatus.Invalid) : new AdminLoginResult(AdminLoginStatus.Locked, lockUntil);
        }
        c.Execute("UPDATE admin_user SET failed_count = 0, locked_until = NULL, totp_last_step = @step, last_login_at = @now WHERE user_name = @n",
            new { step = step.Value, now = Iso(now), n = row.UserName });
        return new AdminLoginResult(AdminLoginStatus.Ok);
    }

    /// <summary>Xác nhận lại bằng mã TOTP trước thao tác nguy hiểm (đặt chuỗi kết nối, đăng xuất hàng loạt…).</summary>
    public bool VerifyCode(string userName, string? code)
    {
        using var c = db.OpenReadWrite();
        var row = c.QuerySingleOrDefault<(string Totp, long Last)>("SELECT totp_protected, totp_last_step FROM admin_user WHERE user_name = @userName", new { userName });
        if (row.Totp is null) return false;
        var step = Totp.Verify(protector.UnprotectTotp(row.Totp), code, time.GetUtcNow(), row.Last);
        if (step is null) return false;
        c.Execute("UPDATE admin_user SET totp_last_step = @step WHERE user_name = @userName", new { step = step.Value, userName });
        return true;
    }

    private static readonly string DummyHash = AdminPasswordHasher.Hash(Guid.NewGuid().ToString());

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed class Row
    {
        public string UserName { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string TotpProtected { get; set; } = "";
        public long TotpLastStep { get; set; }
        public long FailedCount { get; set; }
        public string? LockedUntil { get; set; }
    }
}
