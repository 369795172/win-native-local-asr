using WinLocalASR.Core.Hud;
using WinLocalASR.Core.State;

namespace WinLocalASR.Tests.Hud;

/// <summary>Records every presenter command; properties expose the latest state.</summary>
public sealed class FakeHudView : IHudView
{
    public List<HudViewState> Applies { get; } = new();

    public int Shows { get; private set; }

    public int Hides { get; private set; }

    public bool Disposed { get; private set; }

    public HudViewState? LastState => Applies.Count > 0 ? Applies[^1] : null;

    public void Apply(HudViewState state) => Applies.Add(state);

    public void ShowHud() => Shows++;

    public void HideHud() => Hides++;

    public void Dispose() => Disposed = true;
}

/// <summary>Always throws — the GUI-degrade scenario for the HUD.</summary>
public sealed class ThrowingHudFactory : IHudShellFactory
{
    public int Calls { get; private set; }

    public IDisposable CreateHud(AppController controller)
    {
        Calls++;
        throw new InvalidOperationException("no desktop window station in this session");
    }
}

public sealed class WorkingHudFactory : IHudShellFactory
{
    public List<AppController> Controllers { get; } = new();

    public IDisposable CreateHud(AppController controller)
    {
        Controllers.Add(controller);
        return new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
