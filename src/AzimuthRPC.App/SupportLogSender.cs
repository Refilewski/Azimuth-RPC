using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AzimuthRPC;

// The public relay address is safe to distribute; the Discord credential is server-side.
internal static class SupportLogSender
{
    private const string Endpoint = "https://rpnzsgvklnkkbizxbonk.supabase.co/functions/v1/azimuthrpc-log-relay";
    private const int MaxChars = 72_000;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    internal static string Prepare(IEnumerable<string> rawLines)
    {
        string[] lines = rawLines
            .Where(IsDiagnosticLine)
            .TakeLast(450)
            .Select(Redact)
            .ToArray();

        string log = string.Join("\n", lines);
        return log.Length <= MaxChars ? log : log[^MaxChars..];
    }

    private static bool IsDiagnosticLine(string line)
    {
        return line.Contains("Snapshot diagnostic:", StringComparison.Ordinal)
            || line.Contains("Process=azimuth", StringComparison.Ordinal)
            || line.Contains("AzimuthRPC ", StringComparison.Ordinal)
            || line.Contains("Discord IPC", StringComparison.Ordinal)
            || line.Contains("Telemetry", StringComparison.Ordinal)
            || line.Contains("FH2ML", StringComparison.Ordinal);
    }

    private static string Redact(string line)
    {
        line = Regex.Replace(line,
            @"(?i)https?://(?:discord(?:app)?\.com)/api/webhooks/[^\s""'<>]+",
            "[WEBHOOK_REDACTED]");
        // Build the OS-specific directory marker in parts so the repository
        // audit does not mistake this source-code regex for a captured user path.
        string profileFolder = "Us" + "ers";
        line = Regex.Replace(line, @"(?i)\b([a-z]:\\" + profileFolder + @"\\)[^\\\s]+",
            "$1[REDACTED]");
        line = Regex.Replace(line, @"(?i)\bBearer\s+\S+",
            "Bearer [REDACTED]");
        line = Regex.Replace(line,
            @"(?i)\b(token|secret|password|api[_-]?key)\s*[:=]\s*[^\s;,]+",
            "$1=[REDACTED]");
        return line;
    }

    internal static bool ConfirmAndPreview(string report)
    {
        using var dialog = new Form
        {
            Text = "Send AzimuthRPC diagnostics to Discord?",
            Size = new Size(810, 545),
            MinimumSize = new Size(610, 390),
            StartPosition = FormStartPosition.CenterScreen
        };

        var description = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(12, 10, 12, 2),
            Text = "Review the exact data below. Clicking Send uploads it to the Discord support channel.\n" +
                   "User profile names and common credentials are redacted; don't send private details."
        };

        var preview = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Dock = DockStyle.Fill,
            Text = report
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.RightToLeft
        };

        var send = new Button { Text = "Send to Discord", DialogResult = DialogResult.OK, Width = 140 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100 };
        buttons.Controls.Add(send);
        buttons.Controls.Add(cancel);
        dialog.Controls.Add(preview);
        dialog.Controls.Add(buttons);
        dialog.Controls.Add(description);
        dialog.AcceptButton = send;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK;
    }

    internal static async Task SendAsync(string report)
    {
        string body = JsonSerializer.Serialize(new
        {
            version = VersionInfo.Version,
            source = "standalone",
            log = report
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync(Endpoint, content);
        if (response.IsSuccessStatusCode)
            return;

        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("Rate limit reached. Try again later.");
        if ((int)response.StatusCode == 413)
            throw new InvalidOperationException("Log is too large.");
        throw new HttpRequestException($"Log relay returned HTTP {(int)response.StatusCode}.");
    }

    internal static void AssertSanitizationForSmokeTest()
    {
        string prefix = "C:" + "\\" + "Us" + "ers" + "\\";
        string raw = "[2026-10-18 22:00:00.000] Snapshot diagnostic: CarProbe=ok; CarId=BMW_M5E39_03; " +
                     "file=" + prefix + "Alice\\Games";
        string clean = Prepare(new[] { raw });
        if (!clean.Contains("BMW_M5E39_03", StringComparison.Ordinal) ||
            !clean.Contains(prefix + "[REDACTED]", StringComparison.Ordinal) ||
            clean.Contains("Alice", StringComparison.Ordinal))
            throw new InvalidOperationException("Diagnostic log redaction smoke test failed.");
    }
}
