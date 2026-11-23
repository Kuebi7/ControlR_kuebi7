using ControlR.Libraries.Shared.Services.Processes;

namespace ControlR.Libraries.Shared.Tests;

public class ProcessManagerOutputTests
{
  [Fact]
  public async Task GetProcessOutput_ArgumentList_IncludesStandardErrorAndIgnoresExitCode()
  {
    var result = await new ProcessManager().GetProcessOutput(
      "/bin/sh",
      ["-c", "echo stdout-text; echo stderr-text >&2; exit 7"],
      timeoutMs: 5_000,
      includeStandardError: true);

    Assert.True(result.IsSuccess);
    Assert.Contains("stdout-text", result.Value, StringComparison.Ordinal);
    Assert.Contains("stderr-text", result.Value, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetProcessOutput_ArgumentList_TimeoutIsAFailure()
  {
    var result = await new ProcessManager().GetProcessOutput(
      "/bin/sh",
      ["-c", "sleep 5"],
      timeoutMs: 200,
      includeStandardError: true);

    Assert.False(result.IsSuccess);
    Assert.Contains("Timed out", result.Reason, StringComparison.Ordinal);
  }
}
