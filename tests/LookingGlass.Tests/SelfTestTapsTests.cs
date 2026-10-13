using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;

namespace LookingGlass.Tests;

/// <summary>
/// The run's taps (review M1): only the run's own lines are kept from what is delivered, never a real channel's, and a
/// full tap drops its oldest, so a busy tester never loses what the run reads last.
/// </summary>
public sealed class SelfTestTapsTests {
    private const string Code = "K7Q2";
    private const string Partner = "Bob Hatter@Lich";
    private static readonly string[] TestChannels = ["test-a", "partner-p"];

    private static ReceivedLine Line(string? channel, string sender, string text, bool own = false, long senderId = 5) =>
        new(channel, senderId, sender, own, text, [], false);

    [Fact]
    public void OnlyTheRunsLinesAreKept() {
        // The partner's lines in a test channel, and in local chat.
        Assert.True(SelfTestTaps.Keeps(Line("test-a", Partner, "LG self-test K7Q2 #3 done got=2"), TestChannels, Partner, 42, Code));
        Assert.True(SelfTestTaps.Keeps(Line("test-a", Partner, "join [Looking for Party]"), TestChannels, Partner, 42, Code));
        Assert.True(SelfTestTaps.Keeps(Line(null, "x", "LG self-test K7Q2 p4, please ignore", senderId: 42), TestChannels, Partner, 42, Code));
        // One's own test and protocol lines; someone else's with the prefix (meddling) in a test channel.
        Assert.True(SelfTestTaps.Keeps(Line("test-a", "Alice Wonder@Lich", "LG self-test K7Q2 #0 hello-from-partner", own: true), TestChannels, Partner, 42, Code));
        Assert.True(SelfTestTaps.Keeps(Line("test-a", "Mallory Meddler@Lich", "LG self-test K7Q2 #1 done"), TestChannels, Partner, 42, Code));

        // Never a real channel's, whoever sent it and whatever it says; nor anyone else's ordinary lines.
        Assert.False(SelfTestTaps.Keeps(Line("sky", Partner, "LG self-test K7Q2 #3 done got=2"), TestChannels, Partner, 42, Code));
        Assert.False(SelfTestTaps.Keeps(Line("sky", "Carol Friend@Lich", "pulling at 9"), TestChannels, Partner, 42, Code));
        Assert.False(SelfTestTaps.Keeps(Line(null, "Carol Friend@Lich", "hi there"), TestChannels, Partner, 42, Code));
        Assert.False(SelfTestTaps.Keeps(Line("test-a", "Alice Wonder@Lich", "my own ordinary line", own: true), TestChannels, Partner, 42, Code));
    }

    [Fact]
    public void AFullTapDropsItsOldest() {
        var tap = new RunTap<string>();
        for (var i = 1; i <= RunTap<string>.Most + 500; i++) {
            tap.Add(i, $"line {i}");
        }

        var all = tap.Since(0);
        Assert.Equal(RunTap<string>.Most, all.Count);
        Assert.Equal("line 501", all[0]);
        Assert.Equal($"line {RunTap<string>.Most + 500}", all[^1]);
        Assert.Equal(["line 2500"], tap.Since(RunTap<string>.Most + 499));
    }

    [Fact]
    public void ABusyTesterNeverPushesOutThePartnersAnswer() {
        var tap = new RunTap<ReceivedLine>();
        var seq = 0L;
        void Deliver(ReceivedLine line) {
            if (SelfTestTaps.Keeps(line, TestChannels, Partner, 42, Code)) {
                tap.Add(++seq, line with { Seq = seq });
            }
        }

        var mark = seq;
        // Thousands of real channels' lines (a busy free company, a raid's linkshell) around the partner's answer.
        for (var i = 0; i < 5000; i++) {
            Deliver(Line(i % 2 == 0 ? "fc" : "raid", "Someone Busy@Lich", $"line {i}"));
            if (i == 2500) {
                Deliver(Line("test-a", Partner, "LG self-test K7Q2 #7 done sent=2", senderId: 42));
            }
        }

        var answer = Assert.Single(tap.Since(mark));
        Assert.IsType<ProtocolLine.Answer>(SelfTestProtocol.Parse(Code, answer.Text));
        Assert.Equal(1, tap.Count);
    }
}
