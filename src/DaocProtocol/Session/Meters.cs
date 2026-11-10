// SPDX-License-Identifier: GPL-3.0-or-later
//
// The menu bar's three meters ("the dots"; daoc-re RE/74, 2026-11-10): each a level 0 (good), 1 or 2 (bad), worked out
// once a second. Retail's thresholds, read from the client; the performance figure's meaning is inferred.

namespace DaocProtocol.Session;

public static class Meters
{
    /// <summary>Frame rate: 11 fps or more is good, 5–10 middling, under 5 bad.</summary>
    public static int FrameRate(double fps) => fps >= 11 ? 0 : fps >= 5 ? 1 : 2;

    /// <summary>
    /// Lag from the keep-alive ping: the last round trip, or, while a ping has been out longer than 500 ms, the time
    /// waited so far (a stalled link turns the dot yellow, then red, without a reply). Under 500 ms is good,
    /// 500–1499 middling, 1500 or more (or not connected) bad.
    /// </summary>
    public static int Lag(int lastRoundTripMs, int waitingMs, bool connected)
    {
        if (!connected)
            return 2;
        int ms = waitingMs > 500 ? Math.Max(lastRoundTripMs, waitingMs) : lastRoundTripMs;
        return ms < 500 ? 0 : ms < 1500 ? 1 : 2;
    }

    /// <summary>
    /// Performance: retail's percentage of packets lost or late (inferred), under 15 good, 15–39 middling, 40 or more
    /// (or no traffic at all) bad.
    /// </summary>
    public static int Performance(int lostPercent, bool traffic) =>
        !traffic ? 2 : lostPercent < 15 ? 0 : lostPercent < 40 ? 1 : 2;
}
