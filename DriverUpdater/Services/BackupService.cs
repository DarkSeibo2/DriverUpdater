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
            var psCommand = "Enable-ComputerRestore -Drive '" + drive + "' -ErrorAction SilentlyContinue; " +
                            "Checkpoint-Computer -Description '" + description + "' -RestorePointType MODIFY_SETTINGS";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + psCommand + "\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return new InstallResult { Success = false, Message = "Failed to start restore point creation" };
            }

            await process.WaitForExitAsync(ct);
            
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
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Export-WindowsDriver -Online -Destination '{destDir}'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return new InstallResult { Success = false, Message = "Failed to start driver backup" };
            }

            // Kill the process on cancellation
            var cancelReg = ct.Register(() => { try { process.Kill(); } catch { } });
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                throw;
            }
            finally
            {
                cancelReg.Dispose();
            }
            var output = await outputTask;
            var error = await errorTask;

            if (!string.IsNullOrEmpty(output))
            {
                _logger.LogInformation("Backup output: {Output}", output);
                progress?.Report(output);
            }
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogWarning("Backup error: {Error}", error);
            }

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("Driver backup completed: {DestDir}", destDir);
                return new InstallResult { Success = true, Message = $"Drivers backed up to {destDir}", LogPath = destDir };
            }
            else
            {
                _logger.LogError("Driver backup failed with code {Code}: {Error}", process.ExitCode, error);
                return new InstallResult { Success = false, Message = $"Backup failed (code {process.ExitCode}): {error}" };
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

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return new InstallResult { Success = false, Message = "Failed to start driver restore" };
            }

            var cancelReg = ct.Register(() => { try { process.Kill(); } catch { } });
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                throw;
            }
            finally
            {
                cancelReg.Dispose();
            }
            var output = await outputTask;
            var error = await errorTask;

            if (!string.IsNullOrEmpty(output))
            {
                _logger.LogInformation("Restore output: {Output}", output);
                progress?.Report(output);
            }
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogWarning("Restore error: {Error}", error);
            }

            bool requiresReboot = process.ExitCode == 3010;
            bool success = process.ExitCode == 0 || process.ExitCode == 3010;

            return new InstallResult 
            { 
                Success = success, 
                Message = success ? "Drivers restored successfully" : $"Restore failed (code {process.ExitCode})",
                RequiresReboot = requiresReboot
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore drivers");
            return new InstallResult { Success = false, Message = ex.Message };
        }
    }

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