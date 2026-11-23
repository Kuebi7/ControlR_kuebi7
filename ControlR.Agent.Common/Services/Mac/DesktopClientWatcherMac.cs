using ControlR.Agent.Common.Interfaces;
using ControlR.Libraries.Branding;
using ControlR.Libraries.Shared.Logging;
using ControlR.Libraries.Shared.Services.FileSystem;
using ControlR.Libraries.Shared.Services.Processes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ControlR.Agent.Common.Services.Mac;

internal class DesktopClientWatcherMac(
  TimeProvider timeProvider,
  IServiceControl serviceControl,
  IDesktopClientRepairCoordinator desktopClientRepairCoordinator,
  IProcessManager processManager,
  IIpcServerStore ipcServerStore,
  IFileSystem fileSystem,
  IFileSystemPathProvider fileSystemPathProvider,
  IDesktopClientFileVerifier desktopClientFileVerifier,
  ISystemEnvironment systemEnvironment,
  IOptions<InstanceOptions> instanceOptions,
  ILogger<DesktopClientWatcherMac> logger) : BackgroundService
{
  private const int LaunchctlPrintTimeoutMs = 15_000;
  private const int ProcessProbeTimeoutMs = 5_000;

  private static readonly TimeSpan _maxStartBackoff = TimeSpan.FromMinutes(5);

  private readonly IDesktopClientFileVerifier _desktopClientFileVerifier = desktopClientFileVerifier;
  private readonly IDesktopClientRepairCoordinator _desktopClientRepairCoordinator = desktopClientRepairCoordinator;
  private readonly IFileSystem _fileSystem = fileSystem;
  private readonly IFileSystemPathProvider _fileSystemPathProvider = fileSystemPathProvider;
  private readonly IOptions<InstanceOptions> _instanceOptions = instanceOptions;
  private readonly IIpcServerStore _ipcServerStore = ipcServerStore;
  private readonly ILogger<DesktopClientWatcherMac> _logger = logger;
  private readonly IProcessManager _processManager = processManager;
  private readonly IServiceControl _serviceControl = serviceControl;
  private readonly Dictionary<string, DesktopClientStartBackoff> _startBackoff = [];
  private readonly ISystemEnvironment _systemEnvironment = systemEnvironment;
  private readonly TimeProvider _timeProvider = timeProvider;

  internal async Task CheckDesktopClientForUser(string uid, CancellationToken cancellationToken)
  {
    try
    {
      var installationValidation = ValidateDesktopClientInstallation();
      if (!installationValidation.IsSuccess)
      {
        _desktopClientRepairCoordinator.ReportFailure(
          "desktop-installation",
          installationValidation.Reason ?? "Desktop client installation is invalid.",
          immediate: true);
        return;
      }

      var repairKey = GetRepairKey(uid);
      if (await IsDesktopClientAlive(uid))
      {
        // The process or its IPC connection is the thing a remote session actually needs.
        // launchctl print is allowed to time out without that meaning the client is gone.
        ClearStartBackoff(uid);
        _desktopClientRepairCoordinator.ReportHealthy(repairKey);
        _logger.LogInformationDeduped("Desktop client is running for user {UID}.", args: uid);
        return;
      }

      cancellationToken.ThrowIfCancellationRequested();

      var serviceName = GetDesktopClientServiceName();
      var status = await ReadLaunchctlStatus(uid, serviceName);
      switch (status.State)
      {
        case MacDesktopClientLaunchctlState.Running:
          ClearStartBackoff(uid);
          _desktopClientRepairCoordinator.ReportHealthy(repairKey);
          _logger.LogInformationDeduped("Desktop client service is running for user {UID}.", args: uid);
          return;
        case MacDesktopClientLaunchctlState.TimedOut:
          _logger.LogWarningDeduped(
            "Timed out checking desktop client service {ServiceName} for user {UID}. The client will not be restarted. Reason: {Reason}",
            args: [serviceName, uid, status.Detail]);
          return;
        case MacDesktopClientLaunchctlState.Unavailable:
          _logger.LogWarningDeduped(
            "Could not determine whether desktop client service {ServiceName} is running for user {UID}. The client will not be restarted. Reason: {Reason}",
            args: [serviceName, uid, status.Detail]);
          return;
        case MacDesktopClientLaunchctlState.NotRunning:
        case MacDesktopClientLaunchctlState.NotLoaded:
          await StartDesktopClientIfDue(uid, serviceName, status.State, cancellationToken);
          return;
        default:
          _logger.LogWarningDeduped(
            "Unhandled desktop client status {Status} for user {UID}. The client will not be restarted.",
            args: [status.State, uid]);
          return;
      }
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      _desktopClientRepairCoordinator.ReportFailure(
        GetRepairKey(uid),
        $"Failed to check or start the desktop client for user {uid}: {ex.Message}");
      _logger.LogWarningDeduped(
        "Failed to check/start desktop client service for user {UID}.",
        args: uid,
        exception: ex);
    }
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (_systemEnvironment.IsDebug)
    {
      _logger.LogInformation("Desktop process watcher is running in debug mode. Skipping watch.");
      return;
    }

    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _timeProvider);
    while (await timer.WaitForNextTick(throwOnCancellation: false, stoppingToken))
    {
      try
      {
        await CheckAndStartDesktopClientServices(stoppingToken);
      }
      catch (OperationCanceledException)
      {
        _logger.LogInformation("Desktop process watcher is stopping due to cancellation.");
        break;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error while checking for desktop processes.");
      }
    }

    await _serviceControl.StopDesktopClientService(throwOnFailure: false);
  }

  private static string GetRepairKey(string uid)
  {
    return $"mac-user-{uid}";
  }

  private static bool TryParseProcessLine(string line, out string uid, out string command)
  {
    uid = string.Empty;
    command = string.Empty;
    var trimmed = line.Trim();
    var separator = trimmed.IndexOf(' ');
    if (separator <= 0)
    {
      return false;
    }

    uid = trimmed[..separator];
    command = trimmed[(separator + 1)..].TrimStart();
    return command.Length > 0;
  }

  private async Task CheckAndStartDesktopClientServices(CancellationToken cancellationToken)
  {
    try
    {
      // Get all logged-in users
      var loggedInUsers = await GetLoggedInUsersAsync();
      if (loggedInUsers.Count == 0)
      {
        _logger.LogInformationDeduped("No logged-in users found.");
        return;
      }

      _logger.LogInformationDeduped("Found {UserCount} logged-in users.", args: loggedInUsers.Count);

      foreach (var uid in loggedInUsers)
      {
        await CheckDesktopClientForUser(uid, cancellationToken);
      }
    }
    catch (Exception ex)
    {
      _logger.LogErrorDeduped("Error checking desktop client services.", exception: ex);
    }
  }

  private void ClearStartBackoff(string uid)
  {
    _startBackoff.Remove(uid);
  }

  private string GetDesktopClientPlistPath()
  {
    if (string.IsNullOrWhiteSpace(_instanceOptions.Value.InstanceId))
    {
      return $"/Library/LaunchAgents/{BrandingConstants.MacServicePrefix}.desktop.plist";
    }

    return $"/Library/LaunchAgents/{BrandingConstants.MacServicePrefix}.desktop.{_instanceOptions.Value.InstanceId}.plist";
  }

  private string GetDesktopClientServiceName()
  {
    // This should match the service name from the LaunchAgent plist
    // Based on ServiceControlMac implementation
    if (string.IsNullOrWhiteSpace(_instanceOptions.Value.InstanceId))
    {
      return $"{BrandingConstants.MacServicePrefix}.desktop";
    }

    return $"{BrandingConstants.MacServicePrefix}.desktop.{_instanceOptions.Value.InstanceId}";
  }

  private async Task<List<string>> GetLoggedInUsersAsync()
  {
    try
    {
      var result = await _processManager.GetProcessOutput("who", "-u", 5000);
      var users = new List<string>();

      if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Value))
      {
        var lines = result.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
          // Parse the who output to extract usernames
          // Format: username console timestamp
          var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
          if (parts.Length >= 1)
          {
            var username = parts[0];
            // Get UID for the user
            var uidResult = await _processManager.GetProcessOutput("id", $"-u {username}", 3000);
            if (uidResult.IsSuccess &&
                !string.IsNullOrWhiteSpace(uidResult.Value) &&
                int.TryParse(uidResult.Value.Trim(), out var uid) &&
                uid >= 500) // Exclude system users (typically UID < 500)
            {
              users.Add(uidResult.Value.Trim());
            }
          }
        }
      }

      return [.. users.Distinct()];
    }
    catch (Exception ex)
    {
      _logger.LogErrorDeduped("Failed to get logged-in users. Falling back to empty list.", exception: ex);
      return [];
    }
  }

  private async Task<bool> HasConnectedIpcClient(string uid)
  {
    foreach (var server in _ipcServerStore.Servers.Values.ToArray())
    {
      try
      {
        if (server.Process.HasExited || !server.Server.IsConnected)
        {
          continue;
        }
      }
      catch (Exception ex)
      {
        _logger.LogDebug(ex, "Failed to read an IPC client while checking user {UID}.", uid);
        continue;
      }

      var uidResult = await _processManager.GetProcessOutput(
        "ps",
        $"-o uid= -p {server.Process.Id}",
        ProcessProbeTimeoutMs);
      if (!uidResult.IsSuccess || string.IsNullOrWhiteSpace(uidResult.Value))
      {
        continue;
      }

      if (string.Equals(uidResult.Value.Trim(), uid, StringComparison.Ordinal))
      {
        return true;
      }
    }

    return false;
  }

  private async Task<bool> HasDesktopClientProcess(string uid)
  {
    var executablePath = _fileSystemPathProvider.GetDesktopExecutablePath();
    if (string.IsNullOrWhiteSpace(executablePath))
    {
      return false;
    }

    var result = await _processManager.GetProcessOutput("ps", "-ax -o uid=,command=", ProcessProbeTimeoutMs);
    if (!result.IsSuccess)
    {
      // A failed process list is not evidence that the client is gone. The launchctl
      // result still has to confirm that before anything is started.
      _logger.LogWarningDeduped(
        "Could not list processes while checking the desktop client for user {UID}: {Reason}",
        args: [uid, result.Reason]);
      return false;
    }

    if (string.IsNullOrWhiteSpace(result.Value))
    {
      return false;
    }

    foreach (var line in result.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
      if (!TryParseProcessLine(line, out var processUid, out var command))
      {
        continue;
      }

      if (!string.Equals(processUid, uid, StringComparison.Ordinal))
      {
        continue;
      }

      if (command.Equals(executablePath, StringComparison.Ordinal) ||
          command.StartsWith(executablePath + " ", StringComparison.Ordinal))
      {
        return true;
      }
    }

    return false;
  }

  private async Task<bool> IsDesktopClientAlive(string uid)
  {
    if (await HasConnectedIpcClient(uid))
    {
      return true;
    }

    return await HasDesktopClientProcess(uid);
  }

  private async Task<MacDesktopClientLaunchctlResult> ReadLaunchctlStatus(string uid, string serviceName)
  {
    // stderr is part of the result. "Could not find service" is written there, and a timeout
    // comes back as a failed result instead of empty output.
    var result = await _processManager.GetProcessOutput(
      "sudo",
      ["launchctl", "print", $"gui/{uid}/{serviceName}"],
      LaunchctlPrintTimeoutMs,
      includeStandardError: true);

    return MacDesktopClientLaunchctlResult.Parse(result);
  }

  private void RecordStartAttempt(string uid)
  {
    var now = _timeProvider.GetUtcNow();
    if (!_startBackoff.TryGetValue(uid, out var backoff))
    {
      backoff = new DesktopClientStartBackoff();
      _startBackoff[uid] = backoff;
    }

    backoff.Attempts++;
    var seconds = Math.Min(_maxStartBackoff.TotalSeconds, 5 * Math.Pow(2, backoff.Attempts));
    backoff.NotBefore = now.AddSeconds(seconds);
  }

  private async Task StartDesktopClientIfDue(
    string uid,
    string serviceName,
    MacDesktopClientLaunchctlState state,
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();

    var now = _timeProvider.GetUtcNow();
    if (_startBackoff.TryGetValue(uid, out var backoff) && now < backoff.NotBefore)
    {
      _logger.LogInformationDeduped(
        "Desktop client start for user {UID} is waiting until {NotBefore} after the last attempt.",
        args: [uid, backoff.NotBefore]);
      return;
    }

    if (state == MacDesktopClientLaunchctlState.NotLoaded)
    {
      _logger.LogInformationDeduped(
        "Desktop client service {ServiceName} is not loaded for user {UID}. Starting it.",
        args: [serviceName, uid]);
    }
    else
    {
      _logger.LogInformationDeduped(
        "Desktop client service {ServiceName} is not running for user {UID}. Starting it.",
        args: [serviceName, uid]);
    }

    // A start that fails must not queue a repair. Repair boots the LaunchAgent out, which is
    // another way to end a session. kickstart itself no longer uses -k, so this cannot SIGKILL
    // a client that is actually still running.
    await _serviceControl.StartDesktopClientService(throwOnFailure: false);
    RecordStartAttempt(uid);
  }

  private Result ValidateDesktopClientInstallation()
  {
    var desktopExecutablePath = _fileSystemPathProvider.GetDesktopExecutablePath();
    if (!_fileSystem.FileExists(desktopExecutablePath))
    {
      return Result.Fail($"Desktop client executable was not found at '{desktopExecutablePath}'.");
    }

    var desktopClientPlistPath = GetDesktopClientPlistPath();
    if (!_fileSystem.FileExists(desktopClientPlistPath))
    {
      return Result.Fail($"Desktop client LaunchAgent plist was not found at '{desktopClientPlistPath}'.");
    }

    var verificationResult = _desktopClientFileVerifier.VerifyFile(desktopExecutablePath);
    if (verificationResult.IsSuccess)
    {
      _desktopClientRepairCoordinator.ReportHealthy("desktop-installation");
      return verificationResult;
    }

    return verificationResult;
  }

  private sealed class DesktopClientStartBackoff
  {
    public int Attempts { get; set; }
    public DateTimeOffset NotBefore { get; set; }
  }
}
