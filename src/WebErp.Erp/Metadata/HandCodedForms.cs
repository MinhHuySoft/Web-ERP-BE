namespace WebErp.Erp.Metadata;

/// <summary>
/// Form viết tay của WinForms (tài liệu 30 §1, 49 form ở Medcom). Lớp viết tay luôn che dòng <c>SY_FrmLstTbl</c> cùng tên, nên
/// web không dựng chúng từ metadata (dữ liệu nạp bằng code riêng). Form có tệp mô tả (<see cref="FormOverlays"/>) thì dựng theo tệp;
/// không có thì là màn "chưa có trên web".
/// </summary>
/// <para>
/// Ngoài danh sách gốc (bản Medcom), nạp thêm <c>hand-coded-forms.txt</c>: tên mọi lớp form trong <c>ERP.NET.exe</c> + <c>Tools.dll</c>
/// của bản TinPhong. WinForms tìm lớp trùng tên TRƯỚC khi đọc <c>SY_FrmLstTbl</c> (tài liệu 30 §1), nên form trùng tên lớp mà dựng từ
/// cấu hình sẽ sai: web trả "chưa có trên web" thay vì chạy câu truy vấn dựng sai.
/// </para>
public static class HandCodedForms
{
    // Lazy: Build dùng Original khai báo phía dưới (khởi tạo static chạy theo thứ tự khai báo).
    private static readonly Lazy<IReadOnlySet<string>> All = new(Build);

    public static IReadOnlySet<string> Names => All.Value;

    private static IReadOnlySet<string> Build()
    {
        var names = new HashSet<string>(Original, StringComparer.OrdinalIgnoreCase);
        using var s = typeof(HandCodedForms).Assembly.GetManifestResourceStream("WebErp.HandCodedForms.txt");
        if (s is null) return names;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            var name = line.Trim();
            if (name.Length > 0 && !name.StartsWith('#')) names.Add(name);
        }
        return names;
    }

    /// <summary>Danh sách gốc lấy từ bản dịch ngược bản Medcom (tài liệu 30 §1).</summary>
    private static readonly string[] Original =
    [
        // GetFormAcc trong MainFrm
        "AP_PurchaseFrm", "AP_ReturnFrm", "AP_ServiceFrm", "AR_InvoiceFrm", "AR_ReturnFrm", "AR_ServiceFrm", "CS_PaymentFrm", "CS_ReceiptFrm",
        "GJ_Entryfrm", "IV_StockAdjustFrm", "IV_StockInputFrm", "IV_StockOutputFrm", "IV_StockTranferFrm", "IV_UnitCostProcessFrm",
        "PeriodSetupFrm", "SetupFrm",
        // lớp trong ERP.NET.exe
        "AP_PurchaseAllocateFrm", "AR_StockInputFrm", "AR_StockOutFrm", "EQ_AllocateFrm", "EQ_EquipFrm", "EQ_LiquidateFrm", "FA_AssetListFrm",
        "FA_DepreciateFrm", "FA_LiquidateFrm", "GJ_AllocateFrm", "GJ_AllocateSetupFrm", "GJ_BalanceAccountFrm", "GJ_BalanceItemFrm",
        "GJ_BalanceObjectFrm", "GJ_BalanceSheetSetupFrm", "GJ_CashFlowSetupFrm", "GJ_ProfitLossSetupFrm", "GJ_RefDocumentSetupfrm",
        "IV_CheckStockFrm", "SY_LockDocumentFrm", "ePostSettingViettelFrm",
        // lớp trong Tools.dll
        "BackupDataFrm", "ChangePasswordFrm", "FiscalFrm", "NguoiDungFrm", "NguoiDungNhomFrm", "NguoiDungPhanQuyenFrm",
        "NguoiDungPhanQuyenWAFrm", "RestoreDataFrm", "SystemLangSetupFrm", "UserLogFrm",
    ];
}
