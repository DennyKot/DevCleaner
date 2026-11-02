using System.ComponentModel;
using System.Diagnostics;

namespace DevCleaner.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    /// <returns>null when the executable is missing or timed out.</returns>
    public static ProcessResult? Run(string fileName, IEnumerable<string> args, TimeSpan timeout, string? stdin = null, System.Text.Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            StandardInputEncoding = stdin is null ? null : encoding,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            if (!p.WaitForExit(timeout))
            {
                p.Kill(entireProcessTree: true);
                return null;
            }

            return new ProcessResult(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> RunningOf(IEnumerable<string> processNames)
    {
        var running = new List<string>();
        foreach (var name in processNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0)
                running.Add(name);
            foreach (var p in procs)
                p.Dispose();
        }

        return running;
    }
}
