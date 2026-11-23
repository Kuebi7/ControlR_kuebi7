using System.Collections.ObjectModel;
using ControlR.Agent.Common.Interfaces;
using ControlR.Agent.Common.Services;
using ControlR.Agent.Common.Services.Mac;
using ControlR.Agent.Shared.Interfaces;
using ControlR.Agent.Shared.Options;
using ControlR.Agent.Shared.Services;
using ControlR.Libraries.Ipc;
using ControlR.Libraries.Shared.Primitives;
using ControlR.Libraries.Shared.Services;
using ControlR.Libraries.Shared.Services.FileSystem;
using ControlR.Libraries.Shared.Services.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace ControlR.Agent.Common.Tests;

public class DesktopClientWatcherMacTests
{
  private const string DesktopExecutablePath = "/Applications/ControlR.app/Contents/MacOS/ControlR.DesktopClient";
  private const string Uid = "501";

  private readonly Mock<IFileSystem> _fileSystem = new();
  private readonly Mock<IFileSystemPathProvider> _fileSystemPathProvider = new();
  private readonly Mock<IDesktopClientFileVerifier> _fileVerifier = new();
  private readonly Mock<IIpcServerStore> _ipcServerStore = new();
  private readonly Mock<IProcessManager> _processManager = new();
  private readonly Mock<IDesktopClientRepairCoordinator> _repair = new();
  private readonly Mock<IServiceControl> _serviceControl = new();
  private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

  [Fact]
  public async Task Check_WhenClientBecomesHealthy_ClearsBackoff()
  {
    var sut = CreateSut();
    SetupProcessList("0 /sbin/launchd");
    SetupLaunchctl(Result.Ok("Could not find service \"app.controlr.desktop\" in domain for user gui: 501\n"));
    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    SetupLaunchctl(Result.Ok("gui/501/app.controlr.desktop = {\n\tpid = 4321\n\tstate = running\n}\n"));
    _timeProvider.Advance(TimeSpan.FromSeconds(1));
    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    SetupLaunchctl(Result.Ok("Could not find service \"app.controlr.desktop\" in domain for user gui: 501\n"));
    _timeProvider.Advance(TimeSpan.FromSeconds(1));
    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(false), Times.Exactly(2));
  }

  [Fact]
  public async Task Check_WhenDomainIsMissing_DoesNotStart()
  {
    var sut = CreateSut();
    SetupProcessList("0 /sbin/launchd");
    SetupLaunchctl(Result.Ok("Could not find domain for user gui: 501\n"));

    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(It.IsAny<bool>()), Times.Never);
  }

  [Fact]
  public async Task Check_WhenIpcClientBelongsToAnotherUser_CanStartThisUser()
  {
    var sut = CreateSut();
    var servers = new Dictionary<int, IpcServerRecord>
    {
      [42] = ConnectedClient(42)
    };
    _ipcServerStore
      .SetupGet(x => x.Servers)
      .Returns(new ReadOnlyDictionary<int, IpcServerRecord>(servers));
    _processManager
      .Setup(x => x.GetProcessOutput("ps", "-o uid= -p 42", It.IsAny<int>()))
      .ReturnsAsync(Result.Ok("502\n"));
    SetupProcessList("502 /Applications/ControlR.app/Contents/MacOS/ControlR.DesktopClient");
    SetupLaunchctl(Result.Ok("Could not find service \"app.controlr.desktop\" in domain for user gui: 501\n"));

    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(false), Times.Once);
  }

  [Fact]
  public async Task Check_WhenIpcClientIsConnectedForUser_DoesNotStart()
  {
    var sut = CreateSut();
    var servers = new Dictionary<int, IpcServerRecord>
    {
      [42] = ConnectedClient(42)
    };
    _ipcServerStore
      .SetupGet(x => x.Servers)
      .Returns(new ReadOnlyDictionary<int, IpcServerRecord>(servers));
    _processManager
      .Setup(x => x.GetProcessOutput("ps", "-o uid= -p 42", It.IsAny<int>()))
      .ReturnsAsync(Result.Ok("501\n"));
    SetupLaunchctl(Result.Ok("Could not find service \"app.controlr.desktop\" in domain for user gui: 501\n"));

    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(It.IsAny<bool>()), Times.Never);
    _processManager.Verify(
      x => x.GetProcessOutput("sudo", It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()),
      Times.Never);
  }

  [Fact]
  public async Task Check_WhenLaunchctlFails_DoesNotStart()
  {
    var sut = CreateSut();
    SetupProcessList("0 /sbin/launchd");
    SetupLaunchctl(Result.Fail<string>("Process failed to start."));

    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(It.IsAny<bool>()), Times.Never);
    _repair.Verify(x => x.ReportFailure(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
  }

  [Fact]
  public async Task Check_WhenLaunchctlShowsAPid_DoesNotStart()
  {
    var sut = CreateSut();
    SetupProcessList("0 /sbin/launchd");
    SetupLaunchctl(Result.Ok("gui/501/app.controlr.desktop = {\n\tpid = 4321\n\tstate = running\n}\n"));

    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(It.IsAny<bool>()), Times.Never);
    _repair.Verify(x => x.ReportHealthy("mac-user-501"), Times.Once);
  }

  [Fact]
  public async Task Check_WhenLaunchctlTimesOutDuringBackoff_DoesNotResetIt()
  {
    var sut = CreateSut();
    SetupProcessList("0 /sbin/launchd");
    SetupLaunchctl(Result.Ok("Could not find service \"app.controlr.desktop\" in domain for user gui: 501\n"));
    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    SetupLaunchctl(Result.Fail<string>("Timed out while waiting for command to finish."));
    _timeProvider.Advance(TimeSpan.FromSeconds(1));
    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    SetupLaunchctl(Result.Ok("Could not find service \"app.controlr.desktop\" in domain for user gui: 501\n"));
    _timeProvider.Advance(TimeSpan.FromSeconds(1));
    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(false), Times.Once);
  }

  [Fact]
  public async Task Check_WhenLaunchctlTimesOut_DoesNotStartOrRepair()
  {
    var sut = CreateSut();
    SetupProcessList("0 /sbin/launchd");
    SetupLaunchctl(Result.Fail<string>(
      "Timed out while waiting for command to finish.  Command: sudo.  Arguments: launchctl print gui/501/app.controlr.desktop"));

    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(It.IsAny<bool>()), Times.Never);
    _repair.Verify(x => x.ReportFailure(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    _processManager.Verify(
      x => x.GetProcessOutput(
        "sudo",
        It.Is<IReadOnlyList<string>>(args =>
          args.Count == 3 &&
          args[0] == "launchctl" &&
          args[1] == "print" &&
          args[2] == "gui/501/app.controlr.desktop"),
        15_000,
        true),
      Times.Once);
  }

  [Fact]
  public async Task Check_WhenProcessIsAlive_DoesNotStartEvenIfLaunchctlWouldSayMissing()
  {
    var sut = CreateSut();
    SetupProcessList($"501 {DesktopExecutablePath} --instance-id default");
    SetupLaunchctl(Result.Fail<string>("Timed out while waiting for command to finish."));

    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(It.IsAny<bool>()), Times.Never);
    _processManager.Verify(
      x => x.GetProcessOutput("sudo", It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()),
      Times.Never);
  }

  [Fact]
  public async Task Check_WhenServiceIsNotLoaded_Starts()
  {
    var sut = CreateSut();
    SetupProcessList("0 /sbin/launchd");
    SetupLaunchctl(Result.Ok("Could not find service \"app.controlr.desktop\" in domain for user gui: 501\n"));

    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(false), Times.Once);
    _repair.Verify(x => x.ReportFailure(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
  }

  [Fact]
  public async Task Check_WhenServiceIsNotRunning_Starts()
  {
    var sut = CreateSut();
    SetupProcessList("0 /sbin/launchd");
    SetupLaunchctl(Result.Ok("gui/501/app.controlr.desktop = {\n\tstate = not running\n}\n"));

    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(false), Times.Once);
  }

  [Fact]
  public async Task Check_WhenStartWasJustAttempted_WaitsOutBackoff()
  {
    var sut = CreateSut();
    SetupProcessList("0 /sbin/launchd");
    SetupLaunchctl(Result.Ok("Could not find service \"app.controlr.desktop\" in domain for user gui: 501\n"));

    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);
    _timeProvider.Advance(TimeSpan.FromSeconds(5));
    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(false), Times.Once);

    _timeProvider.Advance(TimeSpan.FromSeconds(5));
    await sut.CheckDesktopClientForUser(Uid, CancellationToken.None);

    _serviceControl.Verify(x => x.StartDesktopClientService(false), Times.Exactly(2));
  }

  [Fact]
  public void Parse_WhenJobHasAPid_IsRunning()
  {
    var result = MacDesktopClientLaunchctlResult.Parse(Result.Ok(
      "gui/501/app.controlr.desktop = {\n\tpid = 4321\n\tstate = running\n}\n"));

    Assert.Equal(MacDesktopClientLaunchctlState.Running, result.State);
    Assert.False(result.ConfirmsServiceIsDown);
  }

  [Fact]
  public void Parse_WhenJobIsPrintedWithoutAPid_IsNotRunning()
  {
    var result = MacDesktopClientLaunchctlResult.Parse(Result.Ok(
      "gui/501/app.controlr.desktop = {\n\tstate = not running\n}\n"));

    Assert.Equal(MacDesktopClientLaunchctlState.NotRunning, result.State);
    Assert.True(result.ConfirmsServiceIsDown);
  }

  [Fact]
  public void Parse_WhenLaunchctlCancels_IsATimeout()
  {
    var result = MacDesktopClientLaunchctlResult.Parse(
      Result.Fail<string>(new TaskCanceledException("A task was canceled.")));

    Assert.Equal(MacDesktopClientLaunchctlState.TimedOut, result.State);
    Assert.False(result.ConfirmsServiceIsDown);
  }

  [Fact]
  public void Parse_WhenLaunchctlTimesOut_IsNotAMissingService()
  {
    var result = MacDesktopClientLaunchctlResult.Parse(Result.Fail<string>(
      "Timed out while waiting for command to finish.  Command: sudo.  Arguments: launchctl print gui/501/app.controlr.desktop"));

    Assert.Equal(MacDesktopClientLaunchctlState.TimedOut, result.State);
    Assert.False(result.ConfirmsServiceIsDown);
  }

  [Theory]
  [InlineData("")]
  [InlineData("Bad request.\n")]
  [InlineData("Could not find domain for user gui: 501\n")]
  [InlineData("pid = 0\n")]
  public void Parse_WhenOutputDoesNotProveTheJobIsDown_IsUnavailable(string output)
  {
    var result = MacDesktopClientLaunchctlResult.Parse(Result.Ok(output));

    Assert.Equal(MacDesktopClientLaunchctlState.Unavailable, result.State);
    Assert.False(result.ConfirmsServiceIsDown);
  }

  [Fact]
  public void Parse_WhenServiceTextIsMissing_IsNotLoaded()
  {
    var result = MacDesktopClientLaunchctlResult.Parse(Result.Ok(
      "Could not find service \"app.controlr.desktop\" in domain for user gui: 501\n"));

    Assert.Equal(MacDesktopClientLaunchctlState.NotLoaded, result.State);
    Assert.True(result.ConfirmsServiceIsDown);
  }

  [Fact]
  public void Parse_WhenStateIsRunningWithoutAPidLine_IsRunning()
  {
    var result = MacDesktopClientLaunchctlResult.Parse(Result.Ok(
      "gui/501/app.controlr.desktop = {\n\tstate = running\n}\n"));

    Assert.Equal(MacDesktopClientLaunchctlState.Running, result.State);
  }

  private static IpcServerRecord ConnectedClient(int processId)
  {
    var process = new Mock<IProcess>();
    process.SetupGet(x => x.Id).Returns(processId);
    process.SetupGet(x => x.HasExited).Returns(false);
    var server = new Mock<IIpcServer>();
    server.SetupGet(x => x.IsConnected).Returns(true);
    return new IpcServerRecord(process.Object, server.Object);
  }

  private DesktopClientWatcherMac CreateSut()
  {
    _fileSystemPathProvider.Setup(x => x.GetDesktopExecutablePath()).Returns(DesktopExecutablePath);
    _fileSystem.Setup(x => x.FileExists(It.IsAny<string>())).Returns(true);
    _fileVerifier.Setup(x => x.VerifyFile(It.IsAny<string>())).Returns(Result.Ok());
    _ipcServerStore
      .SetupGet(x => x.Servers)
      .Returns(new ReadOnlyDictionary<int, IpcServerRecord>(new Dictionary<int, IpcServerRecord>()));

    return new DesktopClientWatcherMac(
      _timeProvider,
      _serviceControl.Object,
      _repair.Object,
      _processManager.Object,
      _ipcServerStore.Object,
      _fileSystem.Object,
      _fileSystemPathProvider.Object,
      _fileVerifier.Object,
      Mock.Of<ISystemEnvironment>(),
      Options.Create(new InstanceOptions()),
      NullLogger<DesktopClientWatcherMac>.Instance);
  }

  private void SetupLaunchctl(Result<string> result)
  {
    _processManager
      .Setup(x => x.GetProcessOutput(
        "sudo",
        It.IsAny<IReadOnlyList<string>>(),
        It.IsAny<int>(),
        true))
      .ReturnsAsync(result);
  }

  private void SetupProcessList(string output)
  {
    _processManager
      .Setup(x => x.GetProcessOutput("ps", "-ax -o uid=,command=", It.IsAny<int>()))
      .ReturnsAsync(Result.Ok(output));
  }
}
