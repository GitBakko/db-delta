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

    private static TextBox InnerTextBox(PasswordBox sut)
    {
        // Force template / content application so PART_PasswordBox is reachable.
        sut.Measure(new Avalonia.Size(200, 32));
        sut.Arrange(new Avalonia.Rect(0, 0, 200, 32));
        return sut.FindControl<TextBox>("PART_PasswordBox")!;
    }
}
