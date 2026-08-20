using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbDelta.App.Views;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// Every dialog is a modal the user has to get out of, and until now only one
/// of them answered Escape — by hand, in its code-behind. IsCancel and
/// IsDefault do it declaratively, on the button that already carries the
/// action, which is also why they cannot drift away from it: delete the
/// button and the key goes with it.
/// </summary>
public class DialogKeyboardTests
{
    /// <summary>
    /// Every modal window in the app. Constructed with no DataContext on
    /// purpose: these assertions are about the markup, and a binding that
    /// resolves to nothing still leaves the buttons in the tree.
    /// </summary>
    private static IEnumerable<Window> AllDialogs() =>
    [
        new ConfirmDialog(),
        new ConfirmExecuteDialog(),
        new SaveProjectDialog(),
        new LoadProjectDialog(),
        new BackfillDialog(),
        new ConnectionEditDialog(),
        new ConnectionManagerDialog(),
        new ProjectSetupDialog(),
        new LastRunDialog(),
    ];

    private static IReadOnlyList<Button> ButtonsOf(Window w)
    {
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return [.. w.GetVisualDescendants().OfType<Button>()];
    }

    [AvaloniaFact]
    public void Every_dialog_gives_Escape_a_button_to_press()
    {
        foreach (Window dialog in AllDialogs())
        {
            // Not "exactly one": ConfirmExecuteDialog has two phases, Annulla
            // and Chiudi, mutually exclusive by IsVisible — and both call
            // Close(null), so there is nothing to be ambiguous about.
            ButtonsOf(dialog).Where(b => b.IsCancel).Should().NotBeEmpty(
                $"{dialog.GetType().Name} is a modal and Escape has to leave it");
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void No_dialog_gives_Enter_more_than_one_button_to_press()
    {
        // The negative control on the rule above: "one for Escape" must not
        // turn into "a default on everything". A dialog whose confirming
        // action destroys something is allowed to have NO default at all.
        foreach (Window dialog in AllDialogs())
        {
            ButtonsOf(dialog).Count(b => b.IsDefault).Should().BeLessThanOrEqualTo(
                1, $"{dialog.GetType().Name} cannot have two answers to the same key");
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void The_destructive_confirmations_keep_Enter_to_themselves()
    {
        // Escape out of a confirmation is free; Enter into one runs it. These
        // two are the dialogs standing in front of dropped objects and a live
        // deploy, so neither may hand the crimson button to a stray keypress.
        foreach (Window dialog in new Window[] { new ConfirmDialog(), new ConfirmExecuteDialog() })
        {
            ButtonsOf(dialog).Where(b => b.IsDefault).Should().BeEmpty(
                $"{dialog.GetType().Name} confirms something irreversible");
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task Escape_leaves_the_load_dialog_without_a_project()
    {
        Window owner = new();
        owner.Show();
        LoadProjectDialog dialog = new();
        Task<string?> answer = dialog.ShowDialog<string?>(owner);
        Dispatcher.UIThread.RunJobs();

        try
        {
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            // Asserted before awaiting: a dialog that ignores Escape leaves
            // this task pending forever, and `await` would hang the run
            // instead of failing it.
            answer.IsCompleted.Should().BeTrue("Escape has to close the dialog");
            (await answer).Should().BeNull();
        }
        finally
        {
            if (!answer.IsCompleted) { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }

    [AvaloniaFact]
    public async Task Enter_accepts_the_name_typed_in_the_save_dialog()
    {
        Window owner = new();
        owner.Show();
        SaveProjectDialog dialog = new();
        Task<string?> answer = dialog.ShowDialog<string?>(owner);
        Dispatcher.UIThread.RunJobs();
        dialog.SetInitialName("Progetto di prova");

        try
        {
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            answer.IsCompleted.Should().BeTrue("Invio has to accept the typed name");
            (await answer).Should().Be("Progetto di prova");
        }
        finally
        {
            if (!answer.IsCompleted) { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }
}
