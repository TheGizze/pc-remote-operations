using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace remote_operations.Controllers;

[ApiController]
[Route("[controller]")]
public class RebootController : ControllerBase
{
    /// <summary>
    /// Returns the available boot entries on this machine.
    /// </summary>
    [HttpGet("entries")]
    public IActionResult GetBootEntries()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Ok(GetWindowsBootEntries());

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return Ok(GetLinuxBootEntries());

            return StatusCode(501, new { error = "Unsupported OS" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reboots the machine. Optionally accepts a targetEntryId (from /reboot/entries)
    /// to boot into a specific OS on next restart.
    /// </summary>
    [HttpPost]
    public IActionResult Reboot([FromBody] RebootRequest? request)
    {
        try
        {
            if (request?.TargetEntryId is { } entryId)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    RunCommand("bcdedit", $"/bootsequence {entryId}");
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    RunCommand("grub-reboot", entryId);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                RunCommand("shutdown", "/r /t 5");
            else
                RunCommand("shutdown", "-r now");

            return Ok(new
            {
                message = "Reboot initiated" + (request?.TargetEntryId != null ? $" → {request.TargetEntryId}" : ""),
                targetEntryId = request?.TargetEntryId
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    private static List<BootEntry> GetWindowsBootEntries()
    {
        var output = RunCommand("bcdedit", "/enum all /v");
        var entries = new List<BootEntry>();

        // Normalise line endings then split into per-entry blocks
        var blocks = output
            .Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        // Discover default boot entry from the Boot Manager block
        string? defaultId = null;
        foreach (var block in blocks)
        {
            if (!block.Contains("Windows Boot Manager") && !block.Contains("{bootmgr}"))
                continue;
            var m = Regex.Match(block, @"default\s+(\{[^}]+\})", RegexOptions.IgnoreCase);
            if (m.Success) defaultId = m.Groups[1].Value.Trim();
        }

        // Skip non-bootable / manager entries
        var skipIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "{bootmgr}", "{fwbootmgr}", "{memdiag}", "{ntldr}", "{ramdiskoptions}" };

        foreach (var block in blocks)
        {
            var idMatch   = Regex.Match(block, @"identifier\s+(\{[^}]+\})", RegexOptions.IgnoreCase);
            var descMatch = Regex.Match(block, @"description\s+(.+)",        RegexOptions.IgnoreCase);

            if (!idMatch.Success || !descMatch.Success) continue;

            var id = idMatch.Groups[1].Value.Trim();
            if (skipIds.Contains(id)) continue;

            entries.Add(new BootEntry
            {
                Id          = id,
                Description = descMatch.Groups[1].Value.Trim(),
                IsDefault   = id.Equals(defaultId, StringComparison.OrdinalIgnoreCase)
            });
        }

        return entries;
    }

    // ── Linux ─────────────────────────────────────────────────────────────────

    private static List<BootEntry> GetLinuxBootEntries()
    {
        // Read GRUB config and extract menuentry titles; index == grub-reboot argument
        const string grubCfg = "/boot/grub/grub.cfg";
        if (!System.IO.File.Exists(grubCfg))
            throw new FileNotFoundException("GRUB config not found", grubCfg);

        var entries  = new List<BootEntry>();
        var lines    = System.IO.File.ReadAllLines(grubCfg);
        var pattern  = new Regex(@"^menuentry\s+'([^']+)'", RegexOptions.Multiline);

        // Also check for saved/default entry
        string? savedDefault = null;
        foreach (var line in lines)
        {
            var sm = Regex.Match(line, @"set default=""?(\d+)""?");
            if (sm.Success) { savedDefault = sm.Groups[1].Value; break; }
        }

        int index = 0;
        foreach (var line in lines)
        {
            var m = pattern.Match(line);
            if (!m.Success) continue;

            entries.Add(new BootEntry
            {
                Id          = index.ToString(),
                Description = m.Groups[1].Value.Trim(),
                IsDefault   = index.ToString() == savedDefault
            });
            index++;
        }

        return entries;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string RunCommand(string executable, string arguments)
    {
        var psi = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {executable}");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"'{executable} {arguments}' exited {process.ExitCode}: {stderr.Trim()}");

        return stdout;
    }
}

public record BootEntry
{
    public string Id          { get; init; } = "";
    public string Description { get; init; } = "";
    public bool   IsDefault   { get; init; }
}

public record RebootRequest(string? TargetEntryId);
