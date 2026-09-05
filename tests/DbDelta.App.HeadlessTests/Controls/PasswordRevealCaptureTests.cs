using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DbDelta.App.Views.Controls;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// «Tieni premuto per mostrare la password» must re-mask when the press ends,
/// including when it ends WITHOUT a release.
/// </summary>
/// <remarks>
/// <c>PasswordBox</c> reveals on <c>PointerPressed</c> and re-masks on
/// <c>PointerReleased</c> — one handler each, and nothing else. A press can end
/// another way: the pointer capture is taken away. Alt+Tab, a notification
/// stealing activation, a touch or pen contact cancelled by the system. If that
/// happens the release never arrives, and the field would stay in clear text on
/// screen with nobody holding anything.
/// <para>
/// This file exists to MEASURE that, not to assume it — the entry was the only
/// one in the backlog carrying «NON VERIFICATA», raised by the 2026-09-03 sweep
/// and never given a verdict. The probe presses with real headless input rather
/// than synthesising an event, and the negative control below is what says the
/// press happened at all.
/// </para>
/// </remarks>
public class PasswordRevealCaptureTests
{
    private sealed class Rig
    {
        public required Window Window { get; init; }
        public required Button Reveal { get; init; }
        public required TextBox Inner { get; init; }
        public IPointer? Pointer { get; set; }

        public Point RevealCentre =>
            Reveal.TranslatePoint(
                new Point(Reveal.Bounds.Width / 2, Reveal.Bounds.Height / 2), Window)
            ?? new Point(0, 0);
    }

    private static Rig Show()
    {
        PasswordBox sut = new() { Password = "hunter2" };
        Window window = new() { Content = sut, Width = 320, Height = 80 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Rig rig = new()
        {
            Window = window,
            Reveal = sut.FindControl<Button>("PART_RevealButton")!,
            Inner = sut.FindControl<TextBox>("PART_PasswordBox")!,
        };

        // Stash the live pointer so the probe can take its capture away later.
        rig.Reveal.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) => rig.Pointer = e.Pointer,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        return rig;
    }

    [AvaloniaFact]
    public void Control_a_press_really_does_reveal_and_a_release_really_does_re_mask()
    {
        // The control comes FIRST because everything below is worthless without
        // it: a probe whose press never lands would "prove" any conclusion.
        Rig rig = Show();
        rig.Inner.PasswordChar.Should().Be('•', "the field starts masked");

        rig.Window.MouseDown(rig.RevealCentre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        rig.Inner.PasswordChar.Should().Be('\0', "holding the button reveals the password");

        rig.Window.MouseUp(rig.RevealCentre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        rig.Inner.PasswordChar.Should().Be('•', "letting go re-masks it");
    }

    [AvaloniaFact]
    public void A_press_that_loses_its_capture_re_masks_too()
    {
        // The reported shape: the press ends without a PointerReleased because
        // something took the capture — Alt+Tab, a notification stealing
        // activation, a cancelled touch or pen contact.
        Rig rig = Show();

        rig.Window.MouseDown(rig.RevealCentre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        rig.Inner.PasswordChar.Should().Be('\0', "the press landed — same as the control above");

        rig.Pointer.Should().NotBeNull("the press must have handed us a live pointer to take capture from");
        rig.Pointer!.Capture(null);
        Dispatcher.UIThread.RunJobs();

        rig.Inner.PasswordChar.Should().Be(
            '•', "a password nobody is holding down must not stay on screen in clear");
    }
}
