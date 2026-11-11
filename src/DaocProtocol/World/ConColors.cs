// SPDX-License-Identifier: GPL-3.0-or-later
//
// How a creature "cons" to a player: the colour of its level against yours. The table is CapnBry's
// (http://capnbry.net/daoc/concolors.html) as OpenDAoC-Core carries it (GameServer/gameutils/ConLevelGrid.cs,
// CharacterConLevelGrid, GPL-3.0): for each player level 0-50, how many creature levels from 0 up con grey,
// green, blue, yellow, orange and red; everything above is purple. Above 50 the server shifts level 50's row
// by one per level, as here. Items use the same rule with the wider bands of OpenDAoC's ItemConLevelGrid (same file);
// the client colours an item's name by it (the mini_info title).

namespace DaocProtocol.World;

public enum ConColor { Grey = -3, Green = -2, Blue = -1, Yellow = 0, Orange = 1, Red = 2, Purple = 3 }

public static class ConColors
{
    // Per player level: spans of (grey, green, blue, yellow, orange, red), counted from creature level 0.
    private static readonly int[][] Spans =
    [
        /* 0*/ [0, 0, 0, 1, 1, 1], /* 1*/ [0, 0, 1, 1, 1, 1], /* 2*/ [0, 1, 1, 1, 1, 1], /* 3*/ [1, 1, 1, 1, 1, 1],
        /* 4*/ [2, 1, 1, 1, 1, 1], /* 5*/ [3, 1, 1, 1, 1, 1], /* 6*/ [4, 1, 1, 1, 1, 1], /* 7*/ [5, 1, 1, 1, 1, 1],
        /* 8*/ [6, 1, 1, 1, 1, 1], /* 9*/ [7, 1, 1, 1, 1, 1], /*10*/ [7, 1, 2, 1, 1, 2], /*11*/ [7, 1, 2, 2, 2, 2],
        /*12*/ [7, 2, 2, 2, 2, 2], /*13*/ [8, 2, 2, 2, 2, 2], /*14*/ [9, 2, 2, 2, 2, 2], /*15*/ [10, 2, 2, 2, 2, 2],
        /*16*/ [11, 2, 2, 2, 2, 2], /*17*/ [12, 2, 2, 2, 2, 2], /*18*/ [13, 2, 2, 2, 2, 2], /*19*/ [14, 2, 2, 2, 2, 2],
        /*20*/ [14, 2, 3, 2, 2, 3], /*21*/ [14, 2, 3, 3, 3, 3], /*22*/ [14, 3, 3, 3, 3, 3], /*23*/ [15, 3, 3, 3, 3, 3],
        /*24*/ [16, 3, 3, 3, 3, 3], /*25*/ [17, 3, 3, 3, 3, 3], /*26*/ [18, 3, 3, 3, 3, 3], /*27*/ [19, 3, 3, 3, 3, 3],
        /*28*/ [20, 3, 3, 3, 3, 3], /*29*/ [21, 3, 3, 3, 3, 3], /*30*/ [22, 3, 3, 3, 3, 3], /*31*/ [23, 3, 3, 3, 3, 3],
        /*32*/ [24, 3, 3, 3, 3, 3], /*33*/ [25, 3, 3, 3, 3, 3], /*34*/ [26, 3, 3, 3, 3, 3], /*35*/ [26, 3, 3, 4, 4, 3],
        /*36*/ [26, 3, 3, 5, 5, 4], /*37*/ [26, 4, 3, 5, 5, 5], /*38*/ [26, 4, 4, 5, 5, 5], /*39*/ [26, 4, 5, 5, 5, 5],
        /*40*/ [26, 5, 5, 5, 5, 5], /*41*/ [27, 5, 5, 5, 5, 5], /*42*/ [28, 5, 5, 5, 5, 5], /*43*/ [29, 5, 5, 5, 5, 5],
        /*44*/ [30, 5, 5, 5, 5, 5], /*45*/ [31, 5, 5, 5, 5, 5], /*46*/ [32, 5, 5, 5, 5, 5], /*47*/ [33, 5, 5, 5, 5, 5],
        /*48*/ [34, 5, 5, 5, 5, 5], /*49*/ [35, 5, 5, 5, 5, 5], /*50*/ [36, 5, 5, 5, 5, 5],
    ];

    // Per player level: the item grid's spans of (grey, green, blue, yellow, orange, red), counted from item level 0.
    private static readonly int[][] ItemSpans =
    [
        /* 0*/ [0, 0, 0, 2, 1, 1], /* 1*/ [0, 0, 0, 3, 2, 2], /* 2*/ [0, 0, 1, 3, 2, 2], /* 3*/ [0, 0, 2, 3, 2, 2],
        /* 4*/ [0, 1, 2, 3, 2, 2], /* 5*/ [0, 2, 2, 3, 2, 2], /* 6*/ [1, 2, 2, 3, 2, 2], /* 7*/ [2, 2, 2, 3, 2, 2],
        /* 8*/ [3, 2, 2, 3, 2, 2], /* 9*/ [4, 2, 2, 3, 2, 2], /*10*/ [4, 2, 3, 3, 2, 3], /*11*/ [4, 2, 3, 4, 3, 3],
        /*12*/ [4, 3, 3, 4, 3, 3], /*13*/ [5, 3, 3, 4, 3, 3], /*14*/ [6, 3, 3, 4, 3, 3], /*15*/ [7, 3, 3, 4, 3, 3],
        /*16*/ [8, 3, 3, 4, 3, 3], /*17*/ [9, 3, 3, 4, 3, 3], /*18*/ [10, 3, 3, 4, 3, 3], /*19*/ [11, 3, 3, 4, 3, 3],
        /*20*/ [11, 3, 4, 4, 3, 4], /*21*/ [11, 3, 4, 5, 4, 4], /*22*/ [11, 4, 4, 5, 4, 4], /*23*/ [12, 4, 4, 5, 4, 4],
        /*24*/ [13, 4, 4, 5, 4, 4], /*25*/ [14, 4, 4, 5, 4, 4], /*26*/ [15, 4, 4, 5, 4, 4], /*27*/ [16, 4, 4, 5, 4, 4],
        /*28*/ [17, 4, 4, 5, 4, 4], /*29*/ [18, 4, 4, 5, 4, 4], /*30*/ [19, 4, 4, 5, 4, 4], /*31*/ [20, 4, 4, 5, 4, 4],
        /*32*/ [21, 4, 4, 5, 4, 4], /*33*/ [22, 4, 4, 5, 4, 4], /*34*/ [23, 4, 4, 5, 4, 4], /*35*/ [23, 4, 4, 6, 5, 4],
        /*36*/ [23, 4, 4, 7, 6, 5], /*37*/ [23, 5, 4, 7, 6, 6], /*38*/ [23, 5, 5, 7, 6, 6], /*39*/ [23, 5, 6, 7, 6, 6],
        /*40*/ [23, 6, 6, 7, 6, 6], /*41*/ [24, 6, 6, 7, 6, 6], /*42*/ [25, 6, 6, 7, 6, 6], /*43*/ [26, 6, 6, 7, 6, 6],
        /*44*/ [27, 6, 6, 7, 6, 6], /*45*/ [28, 6, 6, 7, 6, 6], /*46*/ [29, 6, 6, 7, 6, 6], /*47*/ [30, 6, 6, 7, 6, 6],
        /*48*/ [31, 6, 6, 7, 6, 6], /*49*/ [32, 6, 6, 7, 6, 6], /*50*/ [33, 6, 6, 7, 6, 6],
    ];

    /// <summary>How a creature of <paramref name="targetLevel"/> cons to a player of <paramref name="playerLevel"/>.</summary>
    public static ConColor Of(int playerLevel, int targetLevel) => Lookup(Spans, playerLevel, targetLevel);

    /// <summary>How an item of <paramref name="itemLevel"/> cons to a player of <paramref name="playerLevel"/>.</summary>
    public static ConColor OfItem(int playerLevel, int itemLevel) => Lookup(ItemSpans, playerLevel, itemLevel);

    private static ConColor Lookup(int[][] grid, int playerLevel, int targetLevel)
    {
        if (playerLevel < 0 || targetLevel < 0)
            return ConColor.Yellow;
        int[] spans = grid[Math.Min(playerLevel, 50)];
        int greyExtra = Math.Max(0, playerLevel - 50);   // above 50: the grey span grows by one per level
        int bound = 0;
        for (int i = 0; i < spans.Length; i++)
        {
            bound += spans[i] + (i == 0 ? greyExtra : 0);
            if (targetLevel < bound)
                return (ConColor)(i - 3);
        }
        return ConColor.Purple;
    }
}
