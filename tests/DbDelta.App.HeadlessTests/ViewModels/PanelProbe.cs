using DbDelta.App.ViewModels;
using Xunit;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// Observes a <see cref="ProjectEndpointPanelViewModel"/> the way the
/// auto-connect tests need to: did a database load START, at any point after
/// the probe was attached?
/// </summary>
/// <remarks>
/// Sampling <c>IsLoadingDatabases</c> at 900 ms was the previous shape, and it
/// is a bet on the clock in both directions. A negative test wins it wrongly
/// when an arm that should not exist fires and fails fast, e.g. behind
/// SqlClient's 5 s pool blocking period; <see cref="LoadStarted"/> closes that
/// by recording the fact, not the moment. A positive test loses it under a
/// starved thread pool, and recording the fact does not help there, because the
/// fact has not happened yet: <c>TimerQueue</c> fires overdue timers
/// newest-first, so the test's own 900 ms delay — created AFTER the 450 ms
/// debounce it is waiting on — runs first and sees nothing. Measured
/// 2026-09-17 with the pool held for 3 s: test continuation at 986 ms, debounce
/// at 3019 ms. Positive tests therefore await <see cref="LoadStartedAsync"/>,
/// which completes on the fact itself. The 30 s bound is a wall-clock ceiling,
/// not a timing assertion: a pool starved longer than that fails the test
/// honestly, with a <see cref="TimeoutException"/>, instead of inverting it.
/// </remarks>
internal static class PanelProbe
{
    public static Func<bool> LoadStarted(ProjectEndpointPanelViewModel vm)
    {
        bool started = vm.IsLoadingDatabases;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectEndpointPanelViewModel.IsLoadingDatabases)
                && vm.IsLoadingDatabases)
            {
                started = true;
            }
        };
        return () => started;
    }

    public static Task LoadStartedAsync(ProjectEndpointPanelViewModel vm)
    {
        TaskCompletionSource started = new();
        if (vm.IsLoadingDatabases) { started.TrySetResult(); }
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectEndpointPanelViewModel.IsLoadingDatabases)
                && vm.IsLoadingDatabases)
            {
                started.TrySetResult();
            }
        };
        return started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}
