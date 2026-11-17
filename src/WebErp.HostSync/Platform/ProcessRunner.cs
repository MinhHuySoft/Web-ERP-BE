using System.Diagnostics;
using System.Text;

namespace WebErp.HostSync.Platform;

public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut);

/// <summary>Chạy chương trình với đối số dạng MẢNG (không ghép chuỗi lệnh, không qua shell).</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(file) ?? "",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var gate = new Lock();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) output.AppendLine(e.Data); };
        p.Start();
        // Không cho chương trình chờ nhập từ bàn phím (win-acme hỏi khi thiếu tham số): đóng stdin ngay.
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            lock (gate) return new ProcessResult(-1, output.ToString(), true);
        }
        p.WaitForExit();
        lock (gate) return new ProcessResult(p.ExitCode, output.ToString(), false);
    }
}
