using VNotch.Services.ControlCenter;
using Xunit;

namespace VNotch.Tests;

public sealed class GoveeSyncTests
{
    [Fact]
    public async Task AMissingTaskMeansNotSetUpInsteadOfAnError()
    {
        // The Task Scheduler answers a missing task with 0x80070002, which the COM binder raises as a
        // FileNotFoundException; that once ended the app whenever the home tab opened on a PC without the task.
        bool? running = await GoveeSync.IsRunningAsync($"V-Notch test task that does not exist {Guid.NewGuid():N}");
        Assert.Null(running);
    }
}
