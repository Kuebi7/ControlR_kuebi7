using System.Diagnostics;
using ControlR.Libraries.Shared.Helpers;

namespace ControlR.Libraries.Shared.Services.Processes;

public interface IProcessManager
{
  IProcess GetCurrentProcess();
  int GetCurrentSessionId();
  IProcess GetProcessById(int processId);
  IProcess[] GetProcesses();
  IProcess[] GetProcessesByName(string processName);
  Task<Result<string>> GetProcessOutput(string command, string arguments, int timeoutMs = 10_000);

  /// <summary>
  /// Runs a command with one argument per list entry, so values are not parsed by a shell.
  /// A non-zero exit is still success. When <paramref name="includeStandardError"/> is set, stderr is
  /// appended to the returned text. A timeout is a failure and the process tree is killed.
  /// </summary>
  Task<Result<string>> GetProcessOutput(
    string fileName,
    IReadOnlyList<string> arguments,
    int timeoutMs = 10_000,
    bool includeStandardError = false);

  Task<Result<string>> GetProcessOutputStrict(string command, string arguments, int timeoutMs = 10_000);
  IProcess? LaunchUri(Uri uri);
  IProcess Start(string fileName);
  IProcess Start(string fileName, string arguments);
  IProcess? Start(string fileName, string arguments, bool useShellExec);
  IProcess? Start(ProcessStartInfo startInfo);
  Task<int> StartAndWaitForExit(ProcessStartInfo startInfo, TimeSpan timeout);
  Task<int> StartAndWaitForExit(string fileName, string arguments, bool useShellExec, TimeSpan timeout);
  Task<int> StartAndWaitForExit(string fileName, string arguments, bool useShellExec, CancellationToken cancellationToken);
}

public class ProcessManager : IProcessManager
{
  private int? _currentSessionId;

  public IProcess GetCurrentProcess()
  {
    return new ProcessWrapper(Process.GetCurrentProcess());
  }

  public int GetCurrentSessionId()
  {
    return _currentSessionId ??= Process.GetCurrentProcess().SessionId;
  }

  public IProcess GetProcessById(int processId)
  {
    return new ProcessWrapper(Process.GetProcessById(processId));
  }

  public IProcess[] GetProcesses()
  {
    return [.. Process.GetProcesses().Select(p => new ProcessWrapper(p))];
  }

  public IProcess[] GetProcessesByName(string processName)
  {
    return [.. Process.GetProcessesByName(processName).Select(p => new ProcessWrapper(p))];
  }

  public async Task<Result<string>> GetProcessOutput(string command, string arguments, int timeoutMs = 10_000)
  {
    return await GetProcessOutputCore(command, arguments, timeoutMs, requireZeroExitCode: false);
  }

  public async Task<Result<string>> GetProcessOutput(
    string fileName,
    IReadOnlyList<string> arguments,
    int timeoutMs = 10_000,
    bool includeStandardError = false)
  {
    Process? process = null;
    try
    {
      var startInfo = new ProcessStartInfo
      {
        FileName = fileName,
        WindowStyle = ProcessWindowStyle.Hidden,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = includeStandardError
      };

      foreach (var argument in arguments)
      {
        startInfo.ArgumentList.Add(argument);
      }

      process = Process.Start(startInfo);
      if (process is null)
      {
        return Result.Fail<string>("Process failed to start.");
      }

      using var timeout = new CancellationTokenSource(timeoutMs);
      var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
      var stderrTask = includeStandardError
        ? process.StandardError.ReadToEndAsync(timeout.Token)
        : Task.FromResult(string.Empty);

      try
      {
        await process.WaitForExitAsync(timeout.Token);
      }
      catch (OperationCanceledException)
      {
        // launchctl print can sit forever on a gui domain. Kill it so the next check does not
        // pile another copy on top of one that will never exit.
        KillProcessTree(process);
        await DrainProcessOutput(stdoutTask, stderrTask);
        return Result.Fail<string>(
          $"Timed out while waiting for command to finish.  Command: {fileName}.  Arguments: {string.Join(' ', arguments)}");
      }

      var stdout = await stdoutTask;
      var stderr = await stderrTask;
      if (string.IsNullOrEmpty(stderr))
      {
        return Result.Ok(stdout);
      }

      if (string.IsNullOrEmpty(stdout))
      {
        return Result.Ok(stderr);
      }

      return Result.Ok(stdout + "\n" + stderr);
    }
    catch (Exception ex)
    {
      if (process is not null)
      {
        KillProcessTree(process);
      }

      return Result.Fail<string>(ex);
    }
    finally
    {
      process?.Dispose();
    }
  }

  public async Task<Result<string>> GetProcessOutputStrict(string command, string arguments, int timeoutMs = 10_000)
  {
    return await GetProcessOutputCore(command, arguments, timeoutMs, requireZeroExitCode: true);
  }

  public IProcess? LaunchUri(Uri uri)
  {
    var psi = new ProcessStartInfo()
    {
      FileName = $"{uri}",
      UseShellExecute = true
    };
    var process = Process.Start(psi);
    return process is not null ? new ProcessWrapper(process) : null;
  }

  public IProcess Start(string fileName)
  {
    var process = Process.Start(fileName);
    Guard.IsNotNull(process);
    return new ProcessWrapper(process);
  }

  public IProcess Start(string fileName, string arguments)
  {
    var process = Process.Start(fileName, arguments);
    Guard.IsNotNull(process);
    return new ProcessWrapper(process);
  }

  public IProcess? Start(ProcessStartInfo startInfo)
  {
    Guard.IsNotNull(startInfo);
    var process = Process.Start(startInfo);
    return process is not null ? new ProcessWrapper(process) : null;
  }

  public IProcess? Start(string fileName, string arguments, bool useShellExec)
  {
    var psi = new ProcessStartInfo()
    {
      FileName = fileName,
      Arguments = arguments,
      UseShellExecute = useShellExec
    };
    var process = Process.Start(psi);
    return process != null ? new ProcessWrapper(process) : null;
  }

  public async Task<int> StartAndWaitForExit(ProcessStartInfo startInfo, TimeSpan timeout)
  {
    using var process = Process.Start(startInfo);
    Guard.IsNotNull(process);

    using var cts = new CancellationTokenSource(timeout);
    await process.WaitForExitAsync(cts.Token);

    if (process.ExitCode != 0)
    {
      throw new ProcessStatusException(process.ExitCode);
    }

    return process.ExitCode;
  }

  public async Task<int> StartAndWaitForExit(string fileName, string arguments, bool useShellExec, TimeSpan timeout)
  {
    using var cts = new CancellationTokenSource(timeout);
    return await StartAndWaitForExit(fileName, arguments, useShellExec, cts.Token);
  }

  public async Task<int> StartAndWaitForExit(string fileName, string arguments, bool useShellExec, CancellationToken cancellationToken)
  {
    var psi = new ProcessStartInfo()
    {
      FileName = fileName,
      Arguments = arguments,
      UseShellExecute = useShellExec,
    };

    using var process = Process.Start(psi);
    Guard.IsNotNull(process);
    await process.WaitForExitAsync(cancellationToken);
    return process.ExitCode;
  }

  private static async Task DrainProcessOutput(Task<string> stdoutTask, Task<string> stderrTask)
  {
    try
    {
      await Task.WhenAll(stdoutTask, stderrTask);
    }
    catch (Exception)
    {
      // The timed-out process was killed. Reading its pipes can fail once they close.
    }
  }

  private static void KillProcessTree(Process process)
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
      }
    }
    catch (Exception)
    {
      // The process can exit between the check and the kill.
    }
  }

  private async Task<Result<string>> GetProcessOutputCore(
    string command,
    string arguments,
    int timeoutMs,
    bool requireZeroExitCode)
  {
    try
    {
      var psi = new ProcessStartInfo(command, arguments)
      {
        WindowStyle = ProcessWindowStyle.Hidden,
        UseShellExecute = false,
        RedirectStandardOutput = true
      };

      var proc = Process.Start(psi);

      if (proc is null)
      {
        return Result.Fail<string>("Process failed to start.");
      }

      using var cts = new CancellationTokenSource(timeoutMs);
      await proc.WaitForExitAsync(cts.Token);

      var output = await proc.StandardOutput.ReadToEndAsync();

      if (requireZeroExitCode && proc.ExitCode != 0)
      {
        return Result.Fail<string>($"'{command} {arguments}' exited with code {proc.ExitCode}.");
      }

      return Result.Ok(output);
    }
    catch (OperationCanceledException)
    {
      return Result.Fail<string>($"Timed out while waiting for command to finish.  " +
          $"Command: {command}.  Arguments: {arguments}");
    }
    catch (Exception ex)
    {
      return Result.Fail<string>(ex);
    }
  }
}
