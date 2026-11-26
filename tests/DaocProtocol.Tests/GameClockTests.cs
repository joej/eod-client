// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>0x7E Time and the clock the client runs from it.</summary>
public class GameClockTests
{
    private const uint Hour = 3_600_000;

    [Fact]
    public void TheRecordedTimeIsEightInTheMorningAtRate24()
    {
        ServerPacket p = CaptureFile.Login1127e().ServerPackets().Single(p => p.Code == GameTime.Code);
        GameTime t = GameTime.Parse(p.Payload);
        Assert.Equal(new GameTime(28_859_280, 24), t);
        Assert.Equal("8:00 AM", GameClock.Format(t.MillisOfDay));
        Assert.Equal(p.Payload, t.ToBytes());
    }

    [Theory]
    [InlineData(0u, "0:00 AM")]
    [InlineData(5 * 60_000u, "0:05 AM")]          // retail writes midnight's hour as 0
    [InlineData(11 * Hour + 59 * 60_000, "11:59 AM")]
    [InlineData(12 * Hour, "12:00 PM")]
    [InlineData(12 * Hour + 30 * 60_000, "12:30 PM")]
    [InlineData(13 * Hour, "1:00 PM")]
    [InlineData(23 * Hour + 59 * 60_000, "11:59 PM")]
    public void FormatsAsRetail(uint ms, string text) => Assert.Equal(text, GameClock.Format(ms));

    [Fact]
    public void DaytimeRunsAtTheIncrement()
    {
        // At rate 24, a real minute is 24 game minutes.
        Assert.Equal(8 * Hour + 24 * 60_000, GameClock.Advance(new GameTime(8 * Hour, 24), 60_000));
    }

    [Fact]
    public void NightRunsAQuarterFaster()
    {
        // From 8 PM, a real minute at rate 24 × 1.25 = 30 game minutes.
        Assert.Equal(20 * Hour + 30 * 60_000, GameClock.Advance(new GameTime(20 * Hour, 24), 60_000));
    }

    [Fact]
    public void CrossesDuskAndMidnight()
    {
        // 5 PM at rate 24: one game hour to 6 PM takes 150 s; the next 6 game hours to midnight 720 s at 30×;
        // 30 s more is 15 game minutes past midnight.
        Assert.Equal(15 * 60_000u, GameClock.Advance(new GameTime(17 * Hour, 24), 150_000 + 720_000 + 30_000));
    }

    [Fact]
    public void AWholeGameDayComesBackToTheStart()
    {
        // Rate 24: day 12 h / 24 = 30 real min, night 12 h / 30 = 24 real min; 54 real minutes in all.
        Assert.Equal(8 * Hour, GameClock.Advance(new GameTime(8 * Hour, 24), 54 * 60_000));
    }

    [Fact]
    public void TheSkyChasesAZonesFixedTimeTheShortWayRound()
    {
        // At rate 24 the chase runs 600 game seconds per real second: 10 game minutes.
        Assert.Equal(8 * Hour + 600_000, GameClock.Chase(8 * Hour, 12 * Hour, 24, 1000));
        Assert.Equal(12 * Hour, GameClock.Chase(8 * Hour, 12 * Hour, 24, 60_000));        // stops on the target
        // 23:00 to 1:00 goes forward through midnight, 1:00 to 23:00 back through it.
        Assert.Equal(23 * Hour + 600_000, GameClock.Chase(23 * Hour, 1 * Hour, 24, 1000));
        Assert.Equal(1 * Hour - 600_000, GameClock.Chase(1 * Hour, 23 * Hour, 24, 1000));
        Assert.Equal(GameClock.Day - 300_000, GameClock.Chase(300_000, 23 * Hour, 24, 1000));   // 0:05 back past midnight
        Assert.Equal(19 * Hour, GameClock.Chase(19 * Hour, 19 * Hour, 24, 1000));
    }
}
