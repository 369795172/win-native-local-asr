namespace WinLocalASR.Core.State;

/// <summary>
/// Marshalling seam standing in for Swift's @MainActor: async continuations, timer
/// callbacks and audio callbacks are posted here so all state mutations serialize on one
/// logical thread (the WinForms UI thread in the shell; inline execution in tests).
/// </summary>
public interface IDispatcher
{
    void Post(Action action);
}
