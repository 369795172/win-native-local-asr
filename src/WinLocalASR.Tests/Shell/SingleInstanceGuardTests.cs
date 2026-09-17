using WinLocalASR.Core.Shell;
using Xunit;

namespace WinLocalASR.Tests.Shell;

public class SingleInstanceGuardTests
{
    [Fact]
    public void Fresh_mutex_is_acquired_and_released_on_dispose()
    {
        FakeMutexFactory factory = new(acquire: true);

        using (SingleInstanceGuard guard = new(factory))
        {
            Assert.True(guard.IsPrimary);
        }

        Assert.Single(factory.Created);
        Assert.True(factory.Created[0].Released);
        Assert.True(factory.Created[0].Disposed);
    }

    [Fact]
    public void Already_held_mutex_is_not_acquired_and_never_released()
    {
        FakeMutexFactory factory = new(acquire: false);

        using (SingleInstanceGuard guard = new(factory))
        {
            Assert.False(guard.IsPrimary);
        }

        Assert.False(factory.Created[0].Released);
        Assert.True(factory.Created[0].Disposed);
    }

    [Fact]
    public void Abandoned_mutex_from_a_crashed_previous_instance_is_claimed()
    {
        FakeMutexFactory factory = new(acquire: false)
        {
            ThrowOnAcquire = new AbandonedMutexException(),
        };

        using (SingleInstanceGuard guard = new(factory))
        {
            Assert.True(guard.IsPrimary);
        }
    }

    [Fact]
    public void Default_name_is_the_pinned_global_mutex()
    {
        FakeMutexFactory factory = new(acquire: true);

        using SingleInstanceGuard _ = new(factory);

        Assert.Equal(@"Global\WinLocalASR", factory.LastName);
    }

    [Fact]
    public void Two_guards_on_a_real_named_mutex_are_exclusive()
    {
        // Mutex ownership is thread-reentrant, so the first holder must run on another
        // thread — this simulates the second process without spawning one.
        string name = @"Global\WinLocalASR-test-" + Guid.NewGuid().ToString("N");
        SystemMutexFactory factory = new();
        using ManualResetEventSlim acquired = new(false);
        using ManualResetEventSlim release = new(false);
        Thread holder = new(() =>
        {
            using SingleInstanceGuard first = new(factory, name);
            Assert.True(first.IsPrimary);
            acquired.Set();
            release.Wait(5000);
        });
        holder.Start();
        Assert.True(acquired.Wait(2000));

        using (SingleInstanceGuard second = new(factory, name))
        {
            Assert.False(second.IsPrimary);
        }

        release.Set();
        Assert.True(holder.Join(2000));

        using SingleInstanceGuard third = new(factory, name);
        Assert.True(third.IsPrimary); // released by the first holder's dispose
    }
}
