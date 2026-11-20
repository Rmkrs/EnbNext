using System.Text;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class Net7AuthLoginResponseTests
{
    [Fact]
    public void ExactValidTrueAndTicketAreRecognizedCaseSensitively()
    {
        Net7AuthLoginResponse response = Decode("Valid=TRUE\nTicket=pilot-rest\n");

        Assert.True(response.IsValid);
        Assert.Equal("TRUE", response.RawValid);
        Assert.Equal("pilot-rest", response.Ticket);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("FALSE")]
    [InlineData("")]
    public void OnlyExactUppercaseTrueIsValid(string value)
    {
        Net7AuthLoginResponse response = Decode($"Valid={value}\nTicket=t\n");
        Assert.False(response.IsValid);
    }

    [Fact]
    public void KeysAreCaseSensitiveAndFirstExactDuplicateWins()
    {
        Net7AuthLoginResponse response = Decode(
            "valid=TRUE\nValid=FALSE\nValid=TRUE\nTicket=first\nTicket=second\n");

        Assert.False(response.IsValid);
        Assert.Equal("first", response.Ticket);
        Assert.Equal("TRUE", response.Fields["valid"]);
        Assert.Equal("FALSE", response.Fields["Valid"]);
    }

    [Fact]
    public void ParserDoesNotTruncateAtNulAndSplitsOnlyOnLf()
    {
        byte[] bytes = Encoding.Latin1.GetBytes("Valid=TRUE\0tail\nTicket=after-nul\n");
        Net7AuthLoginResponse response = Net7AuthLoginResponse.Decode(bytes);

        Assert.False(response.IsValid); // exact value is "TRUE\0tail"
        Assert.Equal("after-nul", response.Ticket);
    }

    [Fact]
    public void TrimSetCommentsAndSeparatorPriorityMatchCurrentParser()
    {
        Net7AuthLoginResponse response = Decode(
            " \t# ignored=1\r\n" +
            "Ticket ticket-by-space=ignored-as-separator\r\n" +
            "reasoncode,42\r\n" +
            "reasontext\t denied \r\n" +
            "reasonurl http://example.invalid/path,tail\r\n" +
            "Valid=TRUE\r\n");

        Assert.True(response.IsValid);
        // Separator-class priority is '=' before space, even if the space occurs first.
        Assert.Equal("ignored-as-separator", response.Fields["Ticket ticket-by-space"]);
        Assert.Equal("42", response.ReasonCode);
        Assert.Equal("denied", response.ReasonText);
        Assert.Equal("http://example.invalid/path,tail", response.ReasonUrl);
    }

    [Fact]
    public void ValidTrueDoesNotRequireTicketAtAuthLoginLayer()
    {
        Net7AuthLoginResponse response = Decode("Valid=TRUE\n");
        Assert.True(response.IsValid);
        Assert.Equal(string.Empty, response.Ticket);
    }

    private static Net7AuthLoginResponse Decode(string text)
        => Net7AuthLoginResponse.Decode(Encoding.Latin1.GetBytes(text));
}
