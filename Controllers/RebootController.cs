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
            // Resolve description → ID if caller used TargetDescription
            string? resolvedId = request?.TargetEntryId;
            if (resolvedId == null && request?.TargetDescription is { } desc)
            {
                var entries = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? GetWindowsBootEntries()
                    : GetLinuxBootEntries();

                resolvedId = entries
                    .FirstOrDefault(e => e.Description.Equals(desc, StringComparison.OrdinalIgnoreCase))
                    ?.Id ?? throw new InvalidOperationException($"No boot entry found with description '{desc}'");
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (resolvedId != null)
                    RunCommand("bcdedit", $"/set \"{{fwbootmgr}}\" bootsequence {resolvedId}");
                else
                    // Ensure the firmware boots back into Windows Boot Manager by default
                    RunCommand("bcdedit", "/set \"{fwbootmgr}\" bootsequence \"{bootmgr}\"");

                RunCommand("shutdown", "/r /t 5");
            }
            else
            {
                if(resolvedId != null)
                {
                    RunCommand("bash", $"-c \"grub2-reboot {resolvedId}\"");
                }
                RunCommand("bash", "-c \"(sleep 5; reboot) &\"");
            }

            return Ok(new
            {
                message = "Reboot initiated" + (resolvedId != null ? $" → {resolvedId}" : ""),
                targetEntryId = resolvedId
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Shuts the machine down.
    /// </summary>
    [HttpPost("/shutdown")]
    public IActionResult Shutdown()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                RunCommand("shutdown", "/s /t 5");
            else
                RunCommand("bash", "-c \"(sleep 5; poweroff) &\"");

            return Ok(new { message = "Shutdown initiated" });
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

        foreach (var block in blocks)
        {
            var idMatch   = Regex.Match(block, @"identifier\s+(\{[^}]+\})", RegexOptions.IgnoreCase);
            var descMatch = Regex.Match(block, @"description\s+(.+)",        RegexOptions.IgnoreCase);

            if (!idMatch.Success || !descMatch.Success) continue;

            var id          = idMatch.Groups[1].Value.Trim();
            var description = descMatch.Groups[1].Value.Trim();
            var blockType   = block.TrimStart().Split('\n')[0].Trim();

            // Skip manager / diagnostic entries by block type
            if (blockType.StartsWith("Windows Boot Manager",  StringComparison.OrdinalIgnoreCase) ||
                blockType.StartsWith("Firmware Boot Manager", StringComparison.OrdinalIgnoreCase) ||
                blockType.StartsWith("Windows Memory Tester", StringComparison.OrdinalIgnoreCase) ||
                blockType.StartsWith("Real-mode Boot Sector", StringComparison.OrdinalIgnoreCase))
                continue;

            // Windows OS entries have block type "Windows Boot Loader".
            // Linux/other OS entries appear as "Firmware Application" with a vendor EFI path;
            // PXE/network boot entries also appear as "Firmware Application" but their
            // description starts with "UEFI:" — use that to tell them apart.
            bool isOsEntry =
                blockType.StartsWith("Windows Boot Loader", StringComparison.OrdinalIgnoreCase) ||
                (blockType.StartsWith("Firmware Application", StringComparison.OrdinalIgnoreCase) &&
                 !description.StartsWith("UEFI:", StringComparison.OrdinalIgnoreCase));

            entries.Add(new BootEntry
            {
                Id          = id,
                Description = description,
                IsDefault   = id.Equals(defaultId, StringComparison.OrdinalIgnoreCase),
                IsOsEntry   = isOsEntry
            });
        }

        return entries;
    }

    // ── Linux ─────────────────────────────────────────────────────────────────

    private static List<BootEntry> GetLinuxBootEntries()
    {
        // Read GRUB config and extract menuentry titles; index == grub-reboot argument
        // Fedora/RHEL use /boot/grub2/grub.cfg; Debian/Ubuntu use /boot/grub/grub.cfg
        string? grubCfg = new[] { "/boot/grub2/grub.cfg", "/boot/grub/grub.cfg" }
            .FirstOrDefault(System.IO.File.Exists)
            ?? throw new FileNotFoundException("GRUB config not found");

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

            var title = m.Groups[1].Value.Trim();

            // Rescue kernels and diagnostics are not primary OS entries
            bool isOsEntry = !Regex.IsMatch(title, @"\(0-rescue-|rescue\b|memtest|diagnostic",
                RegexOptions.IgnoreCase);

            entries.Add(new BootEntry
            {
                Id          = title,
                Description = title,
                IsDefault   = index.ToString() == savedDefault,
                IsOsEntry   = isOsEntry
            });
            index++;
        }

        // Use grubby to get the current default OS and add it if not already listed
        try
        {
            var grubbyOutput = RunCommand("grubby", "--info DEFAULT");
            var titleMatch   = Regex.Match(grubbyOutput, @"^title=""?([^""\n]+)""?", RegexOptions.Multiline);
            if (titleMatch.Success)
            {
                var rawTitle     = titleMatch.Groups[1].Value.Trim();
            var currentTitle = Regex.Replace(rawTitle, @"\s*\([^)]*\)", "").Trim();
                if (!entries.Any(e => string.Equals(e.Id, currentTitle, StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Insert(0, new BootEntry
                    {
                        Id          = currentTitle,
                        Description = currentTitle,
                        IsDefault   = true,
                        IsOsEntry   = true
                    });
                }
            }
        }
        catch { /* grubby not available; rely on grub.cfg entries */ }

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
    public bool   IsOsEntry   { get; init; }
}

public record RebootRequest(string? TargetEntryId, string? TargetDescription);
