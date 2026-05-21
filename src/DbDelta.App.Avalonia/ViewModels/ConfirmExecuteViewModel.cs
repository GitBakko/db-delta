using CommunityToolkit.Mvvm.ComponentModel;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Data context for <see cref="Views.ConfirmExecuteDialog"/>.
/// Exposes the number of selected objects and a redacted summary of
/// the target connection so the user can confirm before executing.
/// </summary>
public sealed class ConfirmExecuteViewModel(int objectCount, string targetSummary) : ObservableObject
{
    /// <summary>Number of selected objects that will be aligned.</summary>
    public int ObjectCount { get; } = objectCount;

    /// <summary>Redacted target connection string shown in the dialog header.</summary>
    public string TargetSummary { get; } = targetSummary;
}
