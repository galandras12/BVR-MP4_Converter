using System.Diagnostics;
using System.Text;

namespace BvrMp4Converter.Services;

public static class ProcessUtil
{
    public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Lefuttat egy programot, és visszaadja a kilépési kódot, stdout-ot és stderr-t.</summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(
        string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = CreateStartInfo(exe, args) };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
        p.Start();
        try { p.StandardInput.Close(); } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            throw;
        }
        p.WaitForExit(); // az aszinkron olvasók kiürítése
        return (p.ExitCode, so.ToString(), se.ToString());
    }

    public static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
