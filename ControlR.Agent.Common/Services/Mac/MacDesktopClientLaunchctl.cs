namespace ControlR.Agent.Common.Services.Mac;

internal enum MacDesktopClientLaunchctlState
{
  Running,
  NotRunning,
  NotLoaded,
  TimedOut,
  Unavailable
}

/// <summary>
/// What <c>launchctl print</c> actually said. A timeout or any other failure is not "not loaded":
/// the only missing-service signal is launchctl's own "Could not find service" text.
/// </summary>
internal readonly record struct MacDesktopClientLaunchctlResult(
  MacDesktopClientLaunchctlState State,
  string Detail)
{
  public bool ConfirmsServiceIsDown =>
    State is MacDesktopClientLaunchctlState.NotRunning or MacDesktopClientLaunchctlState.NotLoaded;

  public static MacDesktopClientLaunchctlResult Parse(Result<string> result)
  {
    if (!result.IsSuccess)
    {
      var detail = result.Reason ?? string.Empty;
      if (IsTimeout(result))
      {
        return new MacDesktopClientLaunchctlResult(MacDesktopClientLaunchctlState.TimedOut, detail);
      }

      return new MacDesktopClientLaunchctlResult(MacDesktopClientLaunchctlState.Unavailable, detail);
    }

    var output = result.Value ?? string.Empty;
    if (HasLivePid(output) || HasState(output, "running"))
    {
      return new MacDesktopClientLaunchctlResult(MacDesktopClientLaunchctlState.Running, string.Empty);
    }

    if (output.Contains("Could not find service", StringComparison.Ordinal))
    {
      return new MacDesktopClientLaunchctlResult(MacDesktopClientLaunchctlState.NotLoaded, output.Trim());
    }

    // A printed job with some other state (not running, waiting, spawn scheduled) is loaded
    // and has no live process. Other stderr, including "Could not find domain", is not that.
    if (HasStateLine(output))
    {
      return new MacDesktopClientLaunchctlResult(MacDesktopClientLaunchctlState.NotRunning, string.Empty);
    }

    return new MacDesktopClientLaunchctlResult(
      MacDesktopClientLaunchctlState.Unavailable,
      string.IsNullOrWhiteSpace(output) ? "launchctl print returned no status." : output.Trim());
  }

  private static bool HasLivePid(string output)
  {
    foreach (var rawLine in output.Split('\n'))
    {
      var line = rawLine.Trim();
      if (!line.StartsWith("pid =", StringComparison.Ordinal))
      {
        continue;
      }

      var value = line["pid =".Length..].Trim();
      var token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
      if (int.TryParse(token, out var pid) && pid > 0)
      {
        return true;
      }
    }

    return false;
  }

  private static bool HasState(string output, string state)
  {
    foreach (var rawLine in output.Split('\n'))
    {
      if (rawLine.Trim().Equals($"state = {state}", StringComparison.Ordinal))
      {
        return true;
      }
    }

    return false;
  }

  private static bool HasStateLine(string output)
  {
    foreach (var rawLine in output.Split('\n'))
    {
      if (rawLine.Trim().StartsWith("state =", StringComparison.Ordinal))
      {
        return true;
      }
    }

    return false;
  }

  private static bool IsTimeout(Result<string> result)
  {
    if (result.Exception is OperationCanceledException)
    {
      return true;
    }

    var reason = result.Reason ?? string.Empty;
    return reason.Contains("Timed out", StringComparison.OrdinalIgnoreCase)
      || reason.Contains("task was canceled", StringComparison.OrdinalIgnoreCase)
      || reason.Contains("task was cancelled", StringComparison.OrdinalIgnoreCase);
  }
}
