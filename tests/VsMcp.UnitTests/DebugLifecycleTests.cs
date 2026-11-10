using VsMcp.Services.Debugging;
using Xunit;

namespace VsMcp.UnitTests;

public sealed class DebugLifecycleTests
{
    [Fact]
    public void Abandonment_requires_acknowledgement_and_cannot_become_success_from_a_late_observer()
    {
        var request = new LaunchTracking();
        Assert.Throws<ArgumentException>(() => request.Abandon(false));
        Assert.True(request.IsPending);
        request.Abandon(true);
        Assert.False(request.IsPending);
        request.Confirm();
        request.Fail();
        Assert.Equal("abandoned", request.Status);
        request.Abandon(true);
        Assert.Equal("abandoned", request.Status);
    }

    [Fact]
    public void Fresh_confirmation_releases_pending_without_abandoning_success()
    {
        var request = new LaunchTracking();
        request.RequireId(request.Id);
        Assert.Throws<ArgumentException>(() => request.RequireId(new LaunchTracking().Id));
        Assert.True(request.IsPending);
        request.Confirm();
        request.Abandon(true);
        request.Fail();
        Assert.Equal("started", request.Status);
        Assert.False(request.IsPending);
    }

    [Fact]
    public void Every_resume_step_and_new_pause_invalidates_previous_selection_tokens()
    {
        var context = new DebugBreakContext();
        var paused = context.Id;
        context.Require(paused);
        context.Invalidate();
        Assert.Throws<ArgumentException>(() => context.Require(paused));
        var resumed = context.Id;
        context.Invalidate();
        Assert.Throws<ArgumentException>(() => context.Require(resumed));
        context.Require(context.Id);
    }
}
