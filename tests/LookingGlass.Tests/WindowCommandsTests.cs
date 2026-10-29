using LookingGlass.Core.Client;

namespace LookingGlass.Tests;

public sealed class WindowCommandsTests {
    [Theory]
    [InlineData("/sit")]
    [InlineData("/p hello")]
    [InlineData("/lgc2 hi there")]
    [InlineData("/")]
    public void ALineStartingWithASlashIsACommand(string text) {
        Assert.True(WindowCommands.IsCommand(text));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("and/or")]
    [InlineData(":/")]
    [InlineData("")]
    public void AnyOtherLineIsAMessage(string text) {
        Assert.False(WindowCommands.IsCommand(text));
    }

    [Fact]
    public void ACommandTheGameCanTakeIsRun() {
        Assert.Null(WindowCommands.Refusal("/p hello <t>"));
        Assert.Null(WindowCommands.Refusal("/s " + new string('a', WindowCommands.MaxBytes - 3)));
        Assert.Null(WindowCommands.Refusal("/s こんにちは"));
    }

    [Fact]
    public void ACommandTooLongForGameChatIsNotRun() {
        Assert.Equal(WindowCommands.TooLong, WindowCommands.Refusal("/s " + new string('a', WindowCommands.MaxBytes - 2)));
        // Counted in bytes, as the game counts them: 170 three-byte characters are over, though only 173 characters.
        Assert.Equal(WindowCommands.TooLong, WindowCommands.Refusal("/s " + new string('あ', 170)));
    }

    [Theory]
    [InlineData(0x02)] // the game's macro start
    [InlineData(0x09)] // a tab
    [InlineData(0x202E)] // right-to-left override
    [InlineData(0x2028)] // line separator
    public void ACommandWithCharactersGameChatCantTakeIsNotRun(int character) {
        Assert.Equal(WindowCommands.NotPlain, WindowCommands.Refusal($"/s hi{char.ConvertFromUtf32(character)}there"));
    }

    [Fact]
    public void WhatTheWindowSaysIsPlain() {
        PlainLanguage.AssertPlain(WindowCommands.TooLong);
        PlainLanguage.AssertPlain(WindowCommands.NotPlain);
        PlainLanguage.AssertPlain(WindowCommands.Unavailable);
    }
}
