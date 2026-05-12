using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace remote_operations.Controllers;

[ApiController]
[Route("[controller]")]
public class RebootController(ILogger<RebootController> logger) : ControllerBase
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
            logger.LogError(ex, "GetBootEntries failed");
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
                if (resolvedId != null)
                {
                    // Resolve the stored numeric GrubTitle so grub2-reboot receives an
                    // index ("5") rather than the raw title string which may contain
                    // parentheses that bash would misinterpret.
                    var linuxEntries = GetLinuxBootEntries();
                    var grubTarget = linuxEntries
                        .FirstOrDefault(e => e.Id.Equals(resolvedId, StringComparison.OrdinalIgnoreCase))
                        ?.GrubTitle ?? resolvedId;
                    RunCommand("grub2-reboot", grubTarget);
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
            logger.LogError(ex, "Reboot failed (targetEntryId={TargetEntryId})", request?.TargetEntryId);
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
            logger.LogError(ex, "Shutdown failed");
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
        string? grubCfg = new[] { "/boot/grub2/grub.cfg", "/boot/grub/grub.cfg" }
            .FirstOrDefault(System.IO.File.Exists)
            ?? throw new FileNotFoundException("GRUB config not found");

        var cfgText  = System.IO.File.ReadAllText(grubCfg);
        var cfgLines = cfgText.Split('\n');
        // Modern Fedora/RHEL use BLS: Linux kernels are injected into the GRUB menu at
        // runtime by blscfg and never appear as menuentry blocks in grub.cfg.
        bool isBls = cfgText.Contains("blscfg");

        var entries = new List<BootEntry>();

        string? savedDefault = null;
        foreach (var line in cfgLines)
        {
            var sm = Regex.Match(line, @"set default=""?(\d+)""?");
            if (sm.Success) { savedDefault = sm.Groups[1].Value; break; }
        }
        try
        {
            var env = RunCommand("grub2-editenv", "list");
            var em  = Regex.Match(env, @"saved_entry=(\S+)");
            if (em.Success) savedDefault = em.Groups[1].Value.Trim();
        }
        catch { }

        // How many BLS entries precede the traditional menuentry blocks in the GRUB menu.
        // Always count .conf files so the offset is correct even if grubby only returns one entry.
        int blsCount = 0;
        if (isBls)
        {
            const string blsDir = "/boot/loader/entries";
            if (System.IO.Directory.Exists(blsDir))
                blsCount = System.IO.Directory.GetFiles(blsDir, "*.conf").Length;

            // Add only the default (index 0) kernel — older kernels and rescue variants
            // are not useful boot targets from the remote-operations perspective.
            try
            {
                var grubbyOut = RunCommand("grubby", "--info=DEFAULT");
                var idxM      = Regex.Match(grubbyOut, @"^index=(-?\d+)",  RegexOptions.Multiline);
                var titleM    = Regex.Match(grubbyOut, @"^title=(.+)",     RegexOptions.Multiline);
                if (idxM.Success && titleM.Success && idxM.Groups[1].Value != "-1")
                {
                    var rawTitle = titleM.Groups[1].Value.Trim().Trim('"');
                    entries.Add(new BootEntry
                    {
                        Id          = rawTitle,
                        Description = NormalizeDistroId(rawTitle),
                        IsDefault   = true,
                        IsOsEntry   = true,
                        GrubTitle   = idxM.Groups[1].Value.Trim()
                    });
                }
            }
            catch { /* grubby unavailable */ }
        }

        var menuPattern = new Regex(@"^\s*menuentry\s+['""]([^'""]+)['""]");
        int cfgIdx = 0;
        foreach (var line in cfgLines)
        {
            var m = menuPattern.Match(line);
            if (!m.Success) continue;

            var title   = m.Groups[1].Value.Trim();
            var grubIdx = (blsCount + cfgIdx).ToString();
            cfgIdx++; // increment for every matched menuentry to keep indices accurate

            if (Regex.IsMatch(title, @"UEFI Firmware|Firmware Settings", RegexOptions.IgnoreCase))
                continue;

            bool isOs = !Regex.IsMatch(title,
                @"0-rescue-|rescue\b|memtest|diagnostic", RegexOptions.IgnoreCase);

            entries.Add(new BootEntry
            {
                Id          = title,
                Description = NormalizeDistroId(title),
                IsDefault   = grubIdx == savedDefault,
                IsOsEntry   = isOs,
                GrubTitle   = grubIdx
            });
        }

        return entries;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Reduce a GRUB menuentry title to the short distro name that matches the
    // EFI NVRAM label Windows reports via bcdedit (e.g. "Fedora").
    private static string NormalizeDistroId(string title)
    {
        var stripped  = Regex.Replace(title, @"\s*\([^)]*\)", "").Trim();
        var firstWord = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(firstWord) ? title : firstWord;
    }

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
    public string  Id          { get; init; } = "";
    public string  Description { get; init; } = "";
    public bool    IsDefault   { get; init; }
    public bool    IsOsEntry   { get; init; }
    public string? GrubTitle   { get; init; }
}

public record RebootRequest(string? TargetEntryId, string? TargetDescription);
