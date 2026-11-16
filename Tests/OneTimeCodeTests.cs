using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class OneTimeCodeTests
{
    [Theory]
    [InlineData("123456 is your verification code", "123456")]
    [InlineData("Dein Bestätigungscode: 48 21 93", "482193")]
    [InlineData("Code G-582913", "582913")]
    [InlineData("Bestellung 4711 versendet", null)]
    public void FindsOnlyCodesWithACue(string text, string? expected) =>
        Assert.Equal(expected, OneTimeCode.Find(text, Array.Empty<string>()));

    [Fact]
    public void PrefersTheNumberNextToTheCodeWord() =>
        Assert.Equal("3391", OneTimeCode.Find("Order 99812 - your code is 3391", Array.Empty<string>()));

    [Fact]
    public void FindsACodeImmediatelyBelowTheTitle() =>
        Assert.Equal("123456", OneTimeCode.Find("Verification code", new[] { "123456" }));

    [Theory]
    [InlineData("Code: 123", null)]
    [InlineData("Code: 1234", "1234")]
    [InlineData("Code: 12345678", "12345678")]
    [InlineData("Code: 123456789", null)]
    [InlineData("Code: 1 2 3", null)]
    [InlineData("Code: 1 2 3 4", "1234")]
    [InlineData("Code: 1 2 3 4 5 6 7 8", "12345678")]
    [InlineData("Code: 1 2 3 4 5 6 7 8 9", null)]
    public void AcceptsFourToEightDigitsOnly(string text, string? expected) =>
        Assert.Equal(expected, OneTimeCode.Find("Messages", new[] { text }));

    [Theory]
    [InlineData("CODE")]
    [InlineData("oTp")]
    [InlineData("PIN")]
    [InlineData("PASSCODE")]
    [InlineData("Verification")]
    [InlineData("BESTÄTIGUNG")]
    [InlineData("Bestätigungscode")]
    [InlineData("Sicherheitscode")]
    [InlineData("Einmalcode")]
    [InlineData("TAN")]
    public void RecognizesEveryCodeWordRegardlessOfCase(string word) =>
        Assert.Equal("123456", OneTimeCode.Find("Messages", new[] { $"{word}: 123456" }));

    [Theory]
    [InlineData("1234 code 5678")]
    [InlineData("Code received. Order 1234")]
    [InlineData("Code\nOrder 1234")]
    [InlineData("Code: this number is much too far away to be certain 1234")]
    [InlineData("Barcode: 123456")]
    public void ReturnsNullWhenUnsure(string text) =>
        Assert.Null(OneTimeCode.Find(text, Array.Empty<string>()));
}
