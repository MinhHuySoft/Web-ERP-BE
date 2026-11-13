namespace WebErp.Erp.Metadata;

/// <summary>
/// Form viết tay của WinForms (tài liệu 30 §1, 49 form ở Medcom). Lớp viết tay luôn che dòng <c>SY_FrmLstTbl</c> cùng tên, nên
/// web không dựng chúng từ metadata (dữ liệu nạp bằng code riêng). Form có tệp mô tả (<see cref="FormOverlays"/>) thì dựng theo tệp;
/// không có thì là màn "chưa có trên web".
/// </summary>
public static class HandCodedForms
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
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
    };
}
