using DbDelta.Persistence.Util;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Util;

public class ConnectionStringRedactorTests
{
    [Fact]
    public void Null_input_returns_empty() => ConnectionStringRedactor.Redact(null).Should().Be(string.Empty);

    [Fact]
    public void Lowercase_password_keyword_is_redacted()
    {
        ConnectionStringRedactor.Redact("Server=x;password=Secret123;Database=y")
            .Should().Be("Server=x;password=***;Database=y");
    }

    [Fact]
    public void Uppercase_Password_keyword_is_redacted_case_insensitive()
    {
        ConnectionStringRedactor.Redact("Server=x;Password=Hello;Database=y")
            .Should().Be("Server=x;Password=***;Database=y");
    }

    [Fact]
    public void Pwd_alias_is_redacted()
    {
        ConnectionStringRedactor.Redact("Server=x;Pwd=Hello;Database=y")
            .Should().Be("Server=x;Pwd=***;Database=y");
    }

    [Fact]
    public void Password_with_special_chars_is_redacted_to_semicolon()
    {
        // Obviously fake, and deliberately so. This line used to carry a REAL sa
        // password of a live instance, committed on 2026-05-21 and public ever
        // since — in the test of the very class that exists to keep passwords out
        // of logs. Keep sample secrets self-evidently invented.
        ConnectionStringRedactor.Redact("Server=x;Password=N0tAr3al!Pwd#Fake;Database=y")
            .Should().Be("Server=x;Password=***;Database=y");
    }

    [Fact]
    public void Connection_string_without_password_is_unchanged()
    {
        const string s = "Server=x;Database=y;Integrated Security=True";
        ConnectionStringRedactor.Redact(s).Should().Be(s);
    }

    // The three quoting forms SqlConnectionStringBuilder emits, now that the app
    // builds every string through it (2026-09-03) and a password carrying ';'
    // is connectable. Stopping at the first ';' left `Password=***;b=c"` on
    // screen — the tail of the real password — found by the 2026-09-05 review.

    [Fact]
    public void A_double_quoted_password_is_redacted_whole()
    {
        ConnectionStringRedactor.Redact("Data Source=x;Password=\"a;b=c\";Encrypt=False")
            .Should().Be("Data Source=x;Password=***;Encrypt=False");
    }

    [Fact]
    public void A_single_quoted_password_is_redacted_whole()
    {
        // The builder switches to single quotes when the value holds a '"'.
        ConnectionStringRedactor.Redact("Data Source=x;Password='a\"b;c';Encrypt=False")
            .Should().Be("Data Source=x;Password=***;Encrypt=False");
    }

    [Fact]
    public void A_doubled_inner_quote_does_not_end_the_value()
    {
        // Inside double quotes a literal '"' is written as '""'.
        ConnectionStringRedactor.Redact("Data Source=x;Password=\"a\"\"b;c\";Encrypt=False")
            .Should().Be("Data Source=x;Password=***;Encrypt=False");
    }
}
