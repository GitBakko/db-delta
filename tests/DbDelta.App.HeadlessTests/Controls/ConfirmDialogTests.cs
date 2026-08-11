using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbDelta.App.Views;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// This dialog stands between the user and losing unsaved work, so the one
/// thing it must never do is report a confirmation nobody gave: cancel and
/// Escape both have to come back false.
/// </summary>
public class ConfirmDialogTests
{
    private static (ConfirmDialog Dialog, Task<bool> Answer) Ask()
    {
        Window owner = new();
        owner.Show();
        ConfirmDialog dialog = new();
        dialog.FindControl<TextBlock>("HeadlineText")!.Text = "Vuoi abbandonare il progetto corrente?";
        dialog.FindControl<TextBlock>("BodyText")!.Text = "Le modifiche non salvate andranno perse.";
        dialog.FindControl<TextBlock>("ConfirmLabel")!.Text = "Abbandona e crea";
        Task<bool> answer = dialog.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();
        return (dialog, answer);
    }

    private static void Click(Control button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ConfirmingAnswersTrue()
    {
        (ConfirmDialog dialog, Task<bool> answer) = Ask();

        Click(dialog.FindControl<Button>("ConfirmButton")!);

        (await answer).Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task CancellingAnswersFalse()
    {
        (ConfirmDialog dialog, Task<bool> answer) = Ask();

        Button cancel = dialog.GetVisualDescendants()
                              .OfType<Button>()
                              .First(b => b.Name != "ConfirmButton");
        Click(cancel);

        (await answer).Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task EscapeAnswersFalse()
    {
        (ConfirmDialog dialog, Task<bool> answer) = Ask();

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        (await answer).Should().BeFalse();
    }

    [AvaloniaFact]
    public void TheConfirmButtonSaysWhatItDoes()
    {
        (ConfirmDialog dialog, _) = Ask();

        dialog.FindControl<TextBlock>("ConfirmLabel")!.Text.Should().Be("Abbandona e crea");
        dialog.FindControl<Button>("ConfirmButton")!.Classes.Should().Contain("solid-crimson");

        dialog.Close(false);
    }
}
