using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DbDelta.App.Views.Controls;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.Controls;

public class PasswordBoxTests
{
    [AvaloniaFact]
    public void Inner_TextBox_starts_masked_with_bullet()
    {
        PasswordBox sut = new();
        TextBox inner = InnerTextBox(sut);

        inner.PasswordChar.Should().Be('•');
    }

    [AvaloniaFact]
    public void Setting_Password_propagates_to_inner_TextBox_via_TwoWay_binding()
    {
        PasswordBox sut = new() { Password = "hunter2" };
        TextBox inner = InnerTextBox(sut);

        inner.Text.Should().Be("hunter2");
    }

    [AvaloniaFact]
    public void Typing_in_the_inner_TextBox_propagates_back_to_Password()
    {
        PasswordBox sut = new();
        TextBox inner = InnerTextBox(sut);

        inner.Text = "letmein";

        sut.Password.Should().Be("letmein");
    }

    [AvaloniaFact]
    public void RevealTooltip_defaults_to_italian_hold_to_reveal_hint()
    {
        PasswordBox sut = new();
        sut.RevealTooltip.Should().Be("Tieni premuto per mostrare la password");
    }

    /// <summary>
    /// Measured live on 2026-08-18, not deduced: ValuePattern.Current.Value on
    /// this field returned the sa password in clear, to a plain UIA client with
    /// no privileges at all. PasswordChar masks the pixels; the automation tree
    /// is a second surface and it published the text.
    /// </summary>
    [AvaloniaFact]
    public void The_automation_tree_does_not_carry_the_password()
    {
        PasswordBox sut = new() { Password = "hunter2" };
        TextBox inner = InnerTextBox(sut);

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(inner);
        string? published = peer.GetProvider<IValueProvider>()?.Value;

        published.Should().NotBe("hunter2", "the automation tree is readable by any process in the session");
    }

    [AvaloniaFact]
    public void The_automation_tree_still_describes_the_field()
    {
        // The negative control. Dropping the pattern altogether would also hide
        // the password — and take the field away from a screen reader with it.
        // What it publishes is the mask, which is what the pixels already show.
        PasswordBox sut = new() { Password = "hunter2" };
        TextBox inner = InnerTextBox(sut);

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(inner);
        IValueProvider? value = peer.GetProvider<IValueProvider>();

        value.Should().NotBeNull("a password field is still an editable value to assistive tech");
        value!.Value.Should().Be("•••••••");
    }

    private static TextBox InnerTextBox(PasswordBox sut)
    {
        // Force template / content application so PART_PasswordBox is reachable.
        sut.Measure(new Avalonia.Size(200, 32));
        sut.Arrange(new Avalonia.Rect(0, 0, 200, 32));
        return sut.FindControl<TextBox>("PART_PasswordBox")!;
    }
}
