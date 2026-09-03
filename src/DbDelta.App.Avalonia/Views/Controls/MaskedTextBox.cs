using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace DbDelta.App.Views.Controls;

/// <summary>
/// A <see cref="TextBox"/> that keeps its text out of the UI Automation tree.
/// </summary>
/// <remarks>
/// <para>
/// <c>PasswordChar</c> masks the pixels and nothing else. The automation tree
/// is a second surface over the same control, and the stock peer publishes
/// <c>Text</c> through <see cref="IValueProvider"/> — measured on 2026-08-18 by
/// driving the real app: <c>ValuePattern.Current.Value</c> on the password
/// field returned the <c>sa</c> password in clear, to an ordinary UIA client
/// holding no privilege beyond running in the same desktop session.
/// </para>
/// <para>
/// The pattern is kept rather than removed. A screen reader uses it to tell the
/// user what kind of field this is and whether it holds anything; taking it
/// away would trade a leak for an accessibility hole. What it returns is the
/// mask the user sees, which is what the pixels already say.
/// </para>
/// </remarks>
internal sealed class MaskedTextBox : TextBox
{
    /// <summary>
    /// Style key of the base type, not of this one.
    /// </summary>
    /// <remarks>
    /// A templated control resolves its <c>ControlTheme</c> — and with it its
    /// template — by its style key, which defaults to its own type; style
    /// SELECTORS match on the same key. FluentTheme keys the TextBox theme on
    /// <c>{x:Type TextBox}</c> and <c>Styles/AppStyles.axaml</c> keys the 32-px
    /// monoline height on the same name, so the default key matched neither:
    /// no template and no MinHeight, i.e. a password field that is not on
    /// screen at all. Measured, not deduced, on 2026-09-03 from the installed
    /// v1.1.0: with the default key <c>Template</c> is <see langword="null"/>
    /// and <c>MinHeight</c> is 0 once the control is in a shown Window; a plain
    /// TextBox in the same host is the control that says the harness is fine.
    /// </remarks>
    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override AutomationPeer OnCreateAutomationPeer() => new MaskedPeer(this);

    private sealed class MaskedPeer(TextBox owner) : TextBoxAutomationPeer(owner), IValueProvider
    {
        bool IValueProvider.IsReadOnly => Owner.IsReadOnly;

        /// <summary>What the pixels show, never what they stand for.</summary>
        string? IValueProvider.Value =>
            new(Owner.PasswordChar == '\0' ? '•' : Owner.PasswordChar, Owner.Text?.Length ?? 0);

        void IValueProvider.SetValue(string? value) => Owner.Text = value;
    }
}
