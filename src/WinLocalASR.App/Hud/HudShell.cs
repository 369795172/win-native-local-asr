using WinLocalASR.Core.Hud;
using WinLocalASR.Core.State;

namespace WinLocalASR.App;

/// <summary>
/// The Task 9 HUD composition root: WinForms form (view) + Core
/// <see cref="HudShellPresenter"/> over the <see cref="AppController"/> HUD event
/// surface. Lives behind <see cref="IHudShellFactory"/> so the net10.0 tests never
/// touch WinForms; constructed on the UI thread by <see cref="WinTrayShellFactory"/>
/// (same lifetime as the tray; a throwing construction degrades via
/// <see cref="HudComposition.CreateDegrading"/>).
/// </summary>
internal sealed class WinHudShellFactory : IHudShellFactory
{
    public IDisposable CreateHud(AppController controller)
    {
        HudForm form = new();
        HudShellPresenter presenter = new(controller, form);
        return new HudShell(presenter, form);
    }
}

/// <summary>Disposes the presenter (event unsubscription) before the window.</summary>
internal sealed class HudShell : IDisposable
{
    private readonly HudShellPresenter _presenter;
    private readonly HudForm _form;
    private bool _disposed;

    public HudShell(HudShellPresenter presenter, HudForm form)
    {
        _presenter = presenter;
        _form = form;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _presenter.Dispose();
        _form.Dispose();
    }
}
