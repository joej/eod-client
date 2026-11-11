// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Ui;

namespace DaocAssets.Tests;

/// <summary>Icon numbers to the install's 10 x 10 sheets of 32-pixel icons, named by their first number.</summary>
public sealed class IconSheetsTests
{
    // wgsm_0 listed first: a strip, not a sheet, must not take number 0.
    private static readonly Dictionary<int, string> Sheets = IconSheets.Index(
        ["icons/wgsm_0.bmp", "icons/spl_0.bmp", "icons/spl_100.bmp", "icons/cbt_500.bmp", "icons/ITM_1500.bmp", "icons/4way.cur", "icons/spl_1800.bmp", "icons/WPN_900.bmp", "icons/WPN_1000.BMP"]);

    [Fact]
    public void ANumberFindsItsSheetAndCell()
    {
        Assert.Equal(("spl_0.bmp", 0, 0), IconSheets.Locate(Sheets, 0));
        // 113: sheet 100, cell 13: second row, fourth column.
        Assert.Equal(("spl_100.bmp", 96, 32), IconSheets.Locate(Sheets, 113));
        Assert.Equal(("cbt_500.bmp", 288, 288), IconSheets.Locate(Sheets, 599));
        Assert.Equal(("ITM_1500.bmp", 32, 0), IconSheets.Locate(Sheets, 1501));
        // The weapon sheets, whatever the case of their names (daoc-re RE/75: the dagger's base 901).
        Assert.Equal(("WPN_900.bmp", 32, 0), IconSheets.Locate(Sheets, 901));
        Assert.Equal(("WPN_1000.BMP", 0, 0), IconSheets.Locate(Sheets, 1000));
    }

    [Fact]
    public void ANumberWithoutASheetHasNoIcon()
    {
        Assert.Null(IconSheets.Locate(Sheets, 1150));
        Assert.Null(IconSheets.Locate(Sheets, -1));
    }

    [Fact]
    public void OnlyNumberedBitmapsAreSheets()
    {
        Assert.Equal("spl_0.bmp", Sheets[0]);
        Assert.DoesNotContain(Sheets.Values, v => v.EndsWith(".cur"));
    }
}
