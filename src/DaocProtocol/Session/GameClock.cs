// SPDX-License-Identifier: GPL-3.0-or-later
//
// The game clock between 0x7E updates: the client advances the server's time of day itself. Daytime (6 AM to 6 PM)
// runs at the day increment; night runs 1.25 times faster, as OpenDAoC measured the 1.127 client doing
// (DayNightCycleTimer.NIGHT_INCREMENT_FACTOR) and advances its own clock to match. Shown by the clock window as
// retail's "h:mm AM" (daoc-re RE/74): hours 0–11 are AM written as they are ("0:05 AM"), 12 is "12:xx PM", 13–23 are
// 1–11 PM.

using DaocProtocol.Packets;

namespace DaocProtocol.Session;

public static class GameClock
{
    public const uint Day = 24 * 60 * 60 * 1000;
    private const uint SixAm = Day / 4, SixPm = Day * 3 / 4;
    public const double NightFactor = 1.25;

    /// <summary>The time of day (game ms since midnight) <paramref name="realMs"/> real milliseconds after
    /// <paramref name="time"/> arrived.</summary>
    public static uint Advance(GameTime time, double realMs)
    {
        double t = time.MillisOfDay % Day, dayRate = Math.Max(1u, time.DayIncrement);
        while (realMs > 0)
        {
            bool night = t < SixAm || t >= SixPm;
            double rate = night ? dayRate * NightFactor : dayRate;
            double next = t < SixAm ? SixAm : t < SixPm ? SixPm : Day;
            double realToNext = (next - t) / rate;
            if (realMs < realToNext)
            {
                t += realMs * rate;
                break;
            }
            realMs -= realToNext;
            t = next % Day;
        }
        return (uint)t % Day;
    }

    /// <summary>Retail's clock text: "8:00 AM", "0:05 AM" just after midnight, "12:30 PM", "11:59 PM".</summary>
    public static string Format(uint millisOfDay)
    {
        uint minutes = millisOfDay % Day / 60000, h = minutes / 60, m = minutes % 60;
        return h < 12 ? $"{h}:{m:00} AM" : $"{(h == 12 ? 12 : h - 12)}:{m:00} PM";
    }
}
