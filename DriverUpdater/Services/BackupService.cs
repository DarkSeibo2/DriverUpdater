using Microsoft.Extensions.Logging;
using DriverUpdater.Models;

namespace DriverUpdater.Services;

public class BackupService
{
    private readonly ILogger<BackupService> _logger;
    private readonly SettingsService _settings;

    public BackupService(ILogger<BackupService> logger, SettingsService settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task<InstallResult> CreateRestorePointAsync(string description, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Creating restore point: {Description}", description);

            var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var psCommand = "Enable-ComputerRestore -Drive " + PsQuote(drive) + " -ErrorAction SilentlyContinue; " +
                            "Checkpoint-Computer -Description " + PsQuote(description) + " -RestorePointType MODIFY_SETTINGS";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + PsQuote(psCommand),
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return new InstallResult { Success = false, Message = "Failed to start restore point creation" };
            }

            var cancelReg = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;

                _logger.LogError("Restore point creation timed out after 10 min");
                return new InstallResult { Success = false, Message = "Restore point creation timed out" };
            }
            finally
            {
                cancelReg.Dispose();
            }
            
            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Restore point created successfully");
                return new InstallResult { Success = true, Message = "Restore point created" };
            }
            else
            {
                _logger.LogWarning("Restore point creation exited with code {Code}", process.ExitCode);
                return new InstallResult { Success = false, Message = $"Restore point creation failed (code {process.ExitCode})" };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create restore point");
            return new InstallResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<InstallResult> BackupDriversAsync(string backupName, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var destDir = Path.Combine(_settings.Settings.BackupDirectory, backupName);

        try
        {
            _logger.LogInformation("Backing up drivers to {DestDir}", destDir);
            progress?.Report($"Creating backup directory: {destDir}");

            Directory.CreateDirectory(destDir);

            // The app is already elevated (requireAdministrator manifest), so no
            // Verb=runas needed. UseShellExecute MUST be false to redirect output.
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Export-WindowsDriver -Online -Destination " + PsQuote(destDir) + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Export-WindowsDriver can run for a long time on a full driver
            // store; the hard cap keeps a hung PowerShell from blocking the app.
            var run = await ProcessRunner.RunAsync(psi, ct, timeoutMs: 60 * 60 * 1000, logger: _logger);

            if (run.TimedOut)
            {
                _logger.LogError("Driver backup timed out after 60 min");
                return new InstallResult { Success = false, Message = "Backup timed out after 60 min" };
            }

            if (!string.IsNullOrWhiteSpace(run.Output))
            {
                _logger.LogInformation("Backup output: {Output}", run.Output);
                progress?.Report(run.Output);
            }

            if (run.ExitCode == 0)
            {
                _logger.LogInformation("Driver backup completed: {DestDir}", destDir);
                return new InstallResult { Success = true, Message = $"Drivers backed up to {destDir}", LogPath = destDir };
            }
            else
            {
                _logger.LogError("Driver backup failed with code {Code}: {Error}", run.ExitCode, run.Output);
                return new InstallResult { Success = false, Message = $"Backup failed (code {run.ExitCode}): {run.Output}" };
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backup drivers");
            return new InstallResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<InstallResult> RestoreDriversAsync(string backupPath, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        try
        {
            _logger.LogInformation("Restoring drivers from {BackupPath}", backupPath);
            progress?.Report($"Restoring drivers from {backupPath}...");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                Arguments = $"/add-driver \"{backupPath}\\*.inf\" /subdirs /install",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var run = await ProcessRunner.RunAsync(psi, ct, timeoutMs: 30 * 60 * 1000, logger: _logger);

            if (run.TimedOut)
            {
                _logger.LogError("Driver restore timed out after 30 min");
                return new InstallResult { Success = false, Message = "Restore timed out after 30 min" };
            }

            if (!string.IsNullOrWhiteSpace(run.Output))
            {
                _logger.LogInformation("Restore output: {Output}", run.Output);
                progress?.Report(run.Output);
            }

            bool requiresReboot = run.ExitCode == 3010;
            bool success = run.ExitCode == 0 || run.ExitCode == 3010;

            return new InstallResult 
            { 
                Success = success, 
                Message = success ? "Drivers restored successfully" : $"Restore failed (code {run.ExitCode})",
                RequiresReboot = requiresReboot
            };

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore drivers");
            return new InstallResult { Success = false, Message = ex.Message };
        }
    }

    /// PowerShell single-quoted string with proper escaping (' -> '').
    /// Paths and descriptions reach PowerShell as data, never as code.
    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    public List<string> ListBackups()
    {
        var backupDir = _settings.Settings.BackupDirectory;
        if (!Directory.Exists(backupDir)) return new List<string>();
        
        return Directory.GetDirectories(backupDir)
            .Select(d => Path.GetFileName(d)!)
            .OrderByDescending(n => n)
            .ToList();
    }
}