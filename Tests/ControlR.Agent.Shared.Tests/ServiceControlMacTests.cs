using ControlR.Agent.Shared.Options;
using ControlR.Agent.Shared.Services.Mac;
using ControlR.Libraries.Shared.Primitives;
using ControlR.Libraries.Shared.Services.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Diagnostics;

namespace ControlR.Agent.Shared.Tests;

public class ServiceControlMacTests
{
  [Fact]
  public async Task StartAgentService_KickstartsByKillingTheExistingProcess()
  {
    var arguments = new List<string>();
    var processManager = new Mock<IProcessManager>();
    processManager
      .Setup(x => x.StartAndWaitForExit(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
      .Callback<ProcessStartInfo, TimeSpan>((psi, _) => arguments.Add(psi.Arguments))
      .ReturnsAsync(0);

    var sut = new ServiceControlMac(
      processManager.Object,
      Microsoft.Extensions.Options.Options.Create(new InstanceOptions()),
      NullLogger<ServiceControlMac>.Instance);

    await sut.StartAgentService(throwOnFailure: true);

    Assert.Contains(arguments, argument => argument.Contains("kickstart -k system/", StringComparison.Ordinal));
  }

  [Fact]
  public async Task StartAgentService_WhenTheStartTimesOut_StaysQuietWhenTheCallerDidNotAskToBeTold()
  {
    var processManager = new Mock<IProcessManager>();
    processManager
      .Setup(x => x.StartAndWaitForExit(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
      .Returns(Task.FromException<int>(new OperationCanceledException()));

    var sut = new ServiceControlMac(
      processManager.Object,
      Microsoft.Extensions.Options.Options.Create(new InstanceOptions()),
      NullLogger<ServiceControlMac>.Instance);

    await sut.StartAgentService(throwOnFailure: false);
  }

  [Fact]
  public async Task StartAgentService_WhenTheStartTimesOut_ThrowsWhenTheCallerAskedToBeTold()
  {
    var processManager = new Mock<IProcessManager>();
    processManager
      .Setup(x => x.StartAndWaitForExit(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
      .Returns(Task.FromException<int>(new OperationCanceledException()));

    var sut = new ServiceControlMac(
      processManager.Object,
      Microsoft.Extensions.Options.Options.Create(new InstanceOptions()),
      NullLogger<ServiceControlMac>.Instance);

    // A migration removes the install being replaced once the new install reports it is running, so a
    // start that timed out has to reach a caller that asked for failures instead of reading as success.
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.StartAgentService(throwOnFailure: true));
  }

  [Fact]
  public async Task StartDesktopClientService_DoesNotKillARunningJob()
  {
    var arguments = new List<string>();
    var processManager = new Mock<IProcessManager>();
    processManager
      .Setup(x => x.GetProcessOutput("who", "-u", 5000))
      .ReturnsAsync(Result.Ok("ada console Jan 1 00:00"));
    processManager
      .Setup(x => x.GetProcessOutput("id", "-u ada", 3000))
      .ReturnsAsync(Result.Ok("501\n"));
    processManager
      .Setup(x => x.StartAndWaitForExit(It.IsAny<ProcessStartInfo>(), It.IsAny<TimeSpan>()))
      .Callback<ProcessStartInfo, TimeSpan>((psi, _) => arguments.Add(psi.Arguments))
      .ReturnsAsync(0);

    var sut = new ServiceControlMac(
      processManager.Object,
      Microsoft.Extensions.Options.Options.Create(new InstanceOptions()),
      NullLogger<ServiceControlMac>.Instance);

    await sut.StartDesktopClientService(throwOnFailure: true);

    Assert.Contains(
      arguments,
      argument => argument == "launchctl kickstart gui/501/app.controlr.desktop");
    Assert.DoesNotContain(arguments, argument => argument.Contains("kickstart -k", StringComparison.Ordinal));
  }
}
