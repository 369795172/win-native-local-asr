using WinLocalASR.Core.Hotkeys;
using WinLocalASR.Core.Shell;
using WinLocalASR.Core.State;
using WinLocalASR.Tests.Shell;
using WinLocalASR.Tests.State;
using Xunit;

namespace WinLocalASR.Tests.Hotkeys;

/// <summary>
/// Cross-platform HotkeyManager tests over a fake Win32 wrapper (routing,
/// availability-driven Esc register/unregister, re-registration, failure
/// surfacing). Real RegisterHotKey coverage is Windows-only
/// (<see cref="RealHotkeyTests"/>, executed on windows-latest CI).
/// </summary>
public class HotkeyManagerTests
{
    private const uint ModControl = 0x0002; // MOD_*
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint VkSpace = 0x20;
    private const uint VkEscape = 0x1B;
    private const uint VkD = 0x44;
    private const uint VkF5 = 0x74;

    private readonly FakeHotkeyRegistrar _registrar = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly FakeShellLog _log = new();
    private readonly List<string> _balloons = new();
    private int _toggleCalls;
    private int _cancelCalls;
    private string _hotkeyString = AppSettingsDefault;

    private const string AppSettingsDefault = "Ctrl+Shift+Space";

    private HotkeyManager NewManager() => new(
        _registrar,
        _dispatcher,
        () => _hotkeyString,
        () => _toggleCalls++,
        () => _cancelCalls++,
        _log,
        _balloons.Add);

    // ---- QA+: settings string → exact (MOD_*, VK) registration pair ----

    [Fact]
    public void Initialize_registers_the_parsed_settings_combination_exactly()
    {
        _hotkeyString = "Ctrl+Shift+Space";
        HotkeyManager manager = NewManager();

        manager.Initialize();

        FakeHotkeyRegistrar.RegistrationCall call = Assert.Single(_registrar.Registrations);
        Assert.Equal(HotkeyManager.UserHotkeyId, call.Id);
        Assert.Equal(ModControl | ModShift, call.Modifiers); // MOD_CONTROL | MOD_SHIFT = 0x6
        Assert.Equal(VkSpace, call.VirtualKey);              // VK_SPACE = 0x20
        Assert.Equal(HotkeyRegistrationStatus.Registered, manager.Status);
    }

    [Fact]
    public void Initialize_maps_every_modifier_alias_and_key_to_win32_constants()
    {
        _hotkeyString = "Windows+Alt+D";
        HotkeyManager manager = NewManager();

        manager.Initialize();

        FakeHotkeyRegistrar.RegistrationCall call = Assert.Single(_registrar.Registrations);
        Assert.Equal(0x0008 | ModAlt, call.Modifiers); // MOD_WIN | MOD_ALT
        Assert.Equal(VkD, call.VirtualKey);
    }

    [Fact]
    public void Unparseable_settings_string_falls_back_to_the_default_combination()
    {
        _hotkeyString = "garbage+++";
        HotkeyManager manager = NewManager();

        manager.Initialize();

        FakeHotkeyRegistrar.RegistrationCall call = Assert.Single(_registrar.Registrations);
        Assert.Equal(ModControl | ModShift, call.Modifiers);
        Assert.Equal(VkSpace, call.VirtualKey);
        Assert.Contains(_log.Warnings, w => w.Contains("unparseable"));
        Assert.Equal(HotkeyRegistrationStatus.Registered, manager.Status);
    }

    // ---- QA-: registration failure must surface, never crash ----

    [Fact]
    public void Initialize_failure_raises_conflict_balloon_and_log_without_throwing()
    {
        _registrar.NextRegisterResults.Enqueue(false); // occupied by another program
        HotkeyManager manager = NewManager();
        List<HotkeyRegistrationStatus> events = new();
        manager.StatusChanged += events.Add;

        manager.Initialize();

        Assert.Equal(HotkeyRegistrationStatus.Conflict, manager.Status);
        Assert.Equal(new[] { HotkeyRegistrationStatus.Conflict }, events);
        string balloon = Assert.Single(_balloons);
        Assert.Contains("already in use", balloon);
        Assert.Contains("Ctrl+Shift+Space", balloon);
        Assert.Contains(_log.Warnings, w => w.Contains("hotkey registration failed"));
    }

    // ---- Esc scoping: registered ONLY while a recording is active ----

    [Fact]
    public void Esc_is_initially_unregistered_and_follows_availability_true_false_true()
    {
        HotkeyManager manager = NewManager();
        manager.Initialize();

        Assert.Empty(_registrar.CancelOps); // outside Recording: never registered
        Assert.False(manager.IsCancelHotkeyRegistered);

        manager.SetCancelHotkeyActive(true);
        Assert.True(manager.IsCancelHotkeyRegistered);
        manager.SetCancelHotkeyActive(false);
        Assert.False(manager.IsCancelHotkeyRegistered);
        manager.SetCancelHotkeyActive(true);
        Assert.True(manager.IsCancelHotkeyRegistered);

        Assert.Equal(
            new[] { ("register", HotkeyManager.CancelHotkeyId), ("unregister", HotkeyManager.CancelHotkeyId), ("register", HotkeyManager.CancelHotkeyId) },
            _registrar.CancelOps.Select(op => (op.Op, op.Id)));
        FakeHotkeyRegistrar.RegistrationCall esc = _registrar.Registrations.First(r => r.Id == HotkeyManager.CancelHotkeyId);
        Assert.Equal(0u, esc.Modifiers); // bare Esc
        Assert.Equal(VkEscape, esc.VirtualKey);
    }

    [Fact]
    public void Esc_registration_failure_is_logged_and_non_fatal()
    {
        HotkeyManager manager = NewManager();
        manager.Initialize();
        _registrar.NextRegisterResults.Enqueue(false);

        manager.SetCancelHotkeyActive(true);

        Assert.False(manager.IsCancelHotkeyRegistered);
        Assert.Contains(_log.Warnings, w => w.Contains("cancel hotkey"));
        Assert.Empty(_balloons); // no mid-recording balloon stomp

        manager.SetCancelHotkeyActive(true); // retried on the next recording entry
        Assert.True(manager.IsCancelHotkeyRegistered);
    }

    // ---- WM_HOTKEY routing through the dispatcher seam ----

    [Fact]
    public void WmHotkey_id1_routes_toggle_through_the_dispatcher_exactly_once()
    {
        HotkeyManager manager = NewManager();
        manager.Initialize();

        manager.HandleWmHotkey(HotkeyManager.UserHotkeyId);

        Action posted = Assert.Single(_dispatcher.Posted);
        Assert.Equal(0, _toggleCalls);
        posted();
        Assert.Equal(1, _toggleCalls);
        Assert.Equal(0, _cancelCalls);
    }

    [Fact]
    public void WmHotkey_id2_routes_cancel_through_the_dispatcher()
    {
        HotkeyManager manager = NewManager();

        manager.HandleWmHotkey(HotkeyManager.CancelHotkeyId);

        _dispatcher.Posted.Single()();
        Assert.Equal(1, _cancelCalls);
        Assert.Equal(0, _toggleCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(int.MaxValue)]
    public void WmHotkey_with_unknown_ids_is_ignored_without_throwing(int id)
    {
        HotkeyManager manager = NewManager();

        manager.HandleWmHotkey(id);

        Assert.Empty(_dispatcher.Posted);
        Assert.Equal(0, _toggleCalls);
        Assert.Equal(0, _cancelCalls);
    }

    // ---- Re-registration on settings change ----

    [Fact]
    public void ApplyHotkeyFromSettings_reregisters_the_new_combination()
    {
        HotkeyManager manager = NewManager();
        manager.Initialize();
        _hotkeyString = "Ctrl+Alt+D";

        manager.ApplyHotkeyFromSettings();

        Assert.Equal(
            new[] { (HotkeyManager.UserHotkeyId, ModControl | ModShift, VkSpace) },
            _registrar.Registrations.Take(1).Select(c => (c.Id, c.Modifiers, c.VirtualKey)));
        Assert.Equal(HotkeyManager.UserHotkeyId, _registrar.Unregistrations.Single().Id);
        FakeHotkeyRegistrar.RegistrationCall renewed = _registrar.Registrations[^1];
        Assert.Equal((HotkeyManager.UserHotkeyId, ModControl | ModAlt, VkD), (renewed.Id, renewed.Modifiers, renewed.VirtualKey));
        Assert.Equal(HotkeyRegistrationStatus.Registered, manager.Status);
    }

    [Fact]
    public void ApplyHotkeyFromSettings_conflict_restores_the_previous_combination()
    {
        HotkeyManager manager = NewManager();
        manager.Initialize();
        _hotkeyString = "Ctrl+F5";
        _registrar.NextRegisterResults.Enqueue(false); // the new combination is occupied

        manager.ApplyHotkeyFromSettings();

        // unregister(old), register(new)→fail, register(old)→ok
        FakeHotkeyRegistrar.RegistrationCall[] userRegisters = _registrar.Registrations
            .Where(c => c.Id == HotkeyManager.UserHotkeyId)
            .ToArray();
        Assert.Equal(3, userRegisters.Length);
        Assert.True(userRegisters[0].Succeeded);
        Assert.Equal((ModControl | ModShift, VkSpace), (userRegisters[0].Modifiers, userRegisters[0].VirtualKey));
        Assert.False(userRegisters[1].Succeeded); // the requested combination is occupied
        Assert.Equal((ModControl, VkF5), (userRegisters[1].Modifiers, userRegisters[1].VirtualKey));
        Assert.True(userRegisters[2].Succeeded); // the previous combination is live again
        Assert.Equal((ModControl | ModShift, VkSpace), (userRegisters[2].Modifiers, userRegisters[2].VirtualKey));
        Assert.Equal(HotkeyRegistrationStatus.Conflict, manager.Status);
        Assert.Single(_balloons);
        Assert.Contains(_log.Warnings, w => w.Contains("hotkey registration failed"));

        // Settings still request Ctrl+F5, so the next Apply retries it — and with the
        // occupying program gone it takes over and clears the conflict.
        manager.ApplyHotkeyFromSettings();
        FakeHotkeyRegistrar.RegistrationCall retry = _registrar.Registrations[^1];
        Assert.True(retry.Succeeded);
        Assert.Equal((ModControl, VkF5), (retry.Modifiers, retry.VirtualKey));
        Assert.Equal(HotkeyRegistrationStatus.Registered, manager.Status);
        Assert.Single(_balloons);
    }

    [Fact]
    public void ApplyHotkeyFromSettings_with_unchanged_combination_is_a_noop()
    {
        HotkeyManager manager = NewManager();
        manager.Initialize();
        int calls = _registrar.Calls.Count;

        manager.ApplyHotkeyFromSettings();

        Assert.Equal(calls, _registrar.Calls.Count);
    }

    [Fact]
    public void ApplyHotkeyFromSettings_recovers_after_a_startup_conflict()
    {
        _registrar.NextRegisterResults.Enqueue(false); // startup: occupied
        HotkeyManager manager = NewManager();
        manager.Initialize();
        Assert.Equal(HotkeyRegistrationStatus.Conflict, manager.Status);

        manager.ApplyHotkeyFromSettings(); // other program released it meanwhile

        Assert.Equal(HotkeyRegistrationStatus.Registered, manager.Status);
    }

    // ---- Disposal ----

    [Fact]
    public void Dispose_unregisters_the_live_hotkeys_once()
    {
        HotkeyManager manager = NewManager();
        manager.Initialize();
        manager.SetCancelHotkeyActive(true);

        manager.Dispose();
        manager.Dispose();

        Assert.Equal(
            new[] { HotkeyManager.CancelHotkeyId, HotkeyManager.UserHotkeyId },
            _registrar.Unregistrations.Select(u => u.Id));
    }

    // ---- Event-driven integration with the real AppController (Task 6 contract) ----

    [Fact]
    public async Task Controller_recording_lifecycle_drives_esc_registration()
    {
        ControllerHarness harness = new(configured: true);
        await harness.Controller.InitializeAsync();
        HotkeyManager manager = NewManager();
        harness.Controller.CancelHotkeyAvailabilityChanged += manager.SetCancelHotkeyActive;
        manager.Initialize();
        Assert.Empty(_registrar.CancelOps);

        harness.Controller.ToggleRecording(); // Idle → Recording: Esc becomes available
        Assert.True(manager.IsCancelHotkeyRegistered);

        harness.Controller.ToggleRecording(); // Recording → transcribe → Idle: Esc must leave
        Assert.False(manager.IsCancelHotkeyRegistered);

        harness.Controller.ToggleRecording(); // Idle → Recording again
        Assert.True(manager.IsCancelHotkeyRegistered);

        harness.Controller.CancelActiveRecording(); // Esc cancels: immediately unregistered
        Assert.False(manager.IsCancelHotkeyRegistered);
        Assert.Equal(
            new[] { "register", "unregister", "register", "unregister" },
            _registrar.CancelOps.Select(op => op.Op));
    }
}

/// <summary>Fake RegisterHotKey/UnregisterHotKey wrapper — records every call,
/// successful or not, so tests can assert exact registration sequences.</summary>
public sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public sealed record RegistrationCall(int Id, uint Modifiers, uint VirtualKey, bool Succeeded);

    public sealed record UnregistrationCall(int Id);

    public List<(string Op, int Id)> CancelOps { get; } = new();

    public List<RegistrationCall> Registrations { get; } = new();

    public List<UnregistrationCall> Unregistrations { get; } = new();

    public List<object> Calls { get; } = new();

    /// <summary>Scripted Register results, consumed in order; empty → success.</summary>
    public Queue<bool> NextRegisterResults { get; } = new();

    public bool Register(int id, uint modifiers, uint virtualKey)
    {
        bool result = NextRegisterResults.Count > 0 ? NextRegisterResults.Dequeue() : true;
        Registrations.Add(new RegistrationCall(id, modifiers, virtualKey, result));
        Calls.Add(Registrations[^1]);
        if (id == HotkeyManager.CancelHotkeyId)
        {
            CancelOps.Add(("register", id));
        }

        return result;
    }

    public bool Unregister(int id)
    {
        Unregistrations.Add(new UnregistrationCall(id));
        Calls.Add(Unregistrations[^1]);
        if (id == HotkeyManager.CancelHotkeyId)
        {
            CancelOps.Add(("unregister", id));
        }

        return true;
    }
}

/// <summary>Captures posts without running them — proves routing goes through the seam.</summary>
public sealed class RecordingDispatcher : IDispatcher
{
    public List<Action> Posted { get; } = new();

    public void Post(Action action) => Posted.Add(action);
}
