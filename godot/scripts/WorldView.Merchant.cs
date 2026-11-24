// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the merchant window (roadmap M13), as retail 1.130 draws and drives it (daoc-re, 2026-11-24). Laid out
// at the stock skin's positions
// (ui/atlantis/merchant_window.xml, 350 x 458): the title, the list, the player's money in five boxes (M P G S C), the
// quantity with its arrows and Keep Qty, BUY / SELL / APPRAISE, the page arrows and "Page x of y".
//   - Each 0x17 replaces its page's 30 rows and opens the window (and the stats panel's Inventory page). Up to 5 pages;
//     "of y" is the last 0x17's page + 1, and the page arrows wrap.
//   - A row is a small icon and "name [price]", in slot order 0–29 (empty slots are blank rows). The window type sets
//     the price text (coins, "N bp", "N x", "N gbp", "N m") and the title (the merchant's name, "Bounty Store", …).
//     An item the server marks unusable (can-use byte 0) is drawn in another colour, and BUY asks first.
//   - BUY: the selected row × the quantity (1–100, arrows ±1, back to 1 on opening and after each BUY unless Keep Qty)
//     → 0x78. SELL / APPRAISE: the item selected in the inventory (right-click) → 0x79 / 0xE0, no confirmation; nothing
//     without one. Buy and sell (and Shift+S, the Sell key, window or not) wait 250 ms after the last one.
//   - It closes on its ✕, a change of target or interacting with something else, or more than 256 units (2-D) from
//     the merchant. Closing sends nothing.
// Ours: the colour of an unusable row (retail's isn't known: grey); a row's tooltip (level, price); pages from an
// earlier merchant are dropped when another's arrive (retail keeps its table); Esc closes it; BUY with nothing selected
// does nothing (retail then sends the previous page's last slot, a bug). Not built: /mbuy (a merchant item on the
// cursor), delving a merchant item (0xD8 type 4), double click and drag (not known from retail's code).

using System.Linq;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int MerchantMaxQuantity = 100;
    private const double MerchantLockout = 0.25;      // buy and sell: 250 ms between them
    private const float MerchantRange = 256;          // units, 2-D, from the merchant
    private HudWindow? _merchantWindow;
    private ItemList? _merchantList;
    private Label? _merchantTitle, _merchantQuantity, _merchantPageLabel;
    private readonly Label[] _merchantCoins = new Label[5];
    private CheckBox? _merchantKeepQty;
    private readonly MerchantItem?[,] _merchantSlots = new MerchantItem?[MerchantPage.Pages, MerchantPage.ItemsPerPage];
    private byte _merchantType;      // the last 0x17's window type, echoed in 0x78
    private int _merchantPageCount;  // the last 0x17's page + 1
    private int _merchantShown;      // the page shown
    private ushort _merchantId;      // the merchant the pages came from
    private int _merchantQty = 1;
    private double _merchantLockAt = -10;
    private bool _devBuySell;        // --buy-sell: buy, appraise and sell back once, headless (WorldView.Shots)

    private void BuildMerchantWindow(CanvasLayer overlay)
    {
        float s = UiScale;
        var w = new HudWindow("merchant", new Vector2(350, 458), s, screen => new Vector2(40, 120)) { Visible = false };
        _merchantWindow = w;
        _merchantTitle = w.Label(new Vector2(0, 2), new Vector2(350, 16), 11, centred: true);
        w.Arrow("✕", new Vector2(333, 2), new Vector2(15, 14), CloseMerchant);
        _merchantList = Styled(new ItemList(), w, s, new Vector2(10, 21), new Vector2(330, 334));
        _merchantList.FixedIconSize = new Vector2I(1, 1) * Mathf.RoundToInt(16 * s);
        string[] coins = ["Mithril", "Platinum", "Gold", "Silver", "Copper"];
        float[] coinX = [75, 118, 160, 208, 253];
        for (int i = 0; i < 5; i++)
        {
            _merchantCoins[i] = w.Label(new Vector2(coinX[i], 360), new Vector2(35, 16), 9);
            _merchantCoins[i].TooltipText = coins[i];
        }
        w.Label(new Vector2(81, 397), new Vector2(64, 16), 10).Text = "Quantity";
        _merchantQuantity = w.Label(new Vector2(150, 397), new Vector2(60, 16), 10, centred: true);
        w.Arrow("◀", new Vector2(220, 396), new Vector2(18, 17), () => StepQuantity(-1));
        w.Arrow("▶", new Vector2(240, 396), new Vector2(18, 17), () => StepQuantity(1));
        _merchantKeepQty = new CheckBox { Text = "Keep Qty", Position = new Vector2(265, 396) * s, FocusMode = Control.FocusModeEnum.None };
        _merchantKeepQty.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(8 * s));
        w.AddChild(_merchantKeepQty);
        w.Button("BUY", new Vector2(10, 425), 64, Buy);
        w.Button("SELL", new Vector2(80, 425), 64, Sell);
        w.Button("APPRAISE", new Vector2(150, 425), 64, Appraise);
        w.Arrow("◀", new Vector2(220, 425), new Vector2(18, 17), () => TurnMerchantPage(-1));
        w.Arrow("▶", new Vector2(240, 425), new Vector2(18, 17), () => TurnMerchantPage(1));
        _merchantPageLabel = w.Label(new Vector2(265, 426), new Vector2(84, 16), 10);
        overlay.AddChild(w);
        if (_merchantPageCount > 0 && _merchantId == _target && _target != 0)
        {
            w.Visible = true;
            ShowMerchantPage();
        }
    }

    // 0x17: one page of the targeted merchant's items.
    private void OnMerchantPage(MerchantPage page)
    {
        if (_merchantWindow is null)
            return;
        if (page.Page >= MerchantPage.Pages)
        {
            GD.PrintErr($"[world] merchant page {page.Page} ignored (retail keeps pages 0–4)");
            return;
        }
        if (!_merchantWindow.Visible || _merchantId != _target)
        {
            System.Array.Clear(_merchantSlots);
            (_merchantShown, _merchantId) = (0, _target);
            if (_merchantKeepQty is not { ButtonPressed: true })
                _merchantQty = 1;
        }
        for (int i = 0; i < MerchantPage.ItemsPerPage; i++)
            _merchantSlots[page.Page, i] = null;
        foreach (MerchantItem item in page.Items.Take(MerchantPage.ItemsPerPage).Where(i => i.Index < MerchantPage.ItemsPerPage))
            _merchantSlots[page.Page, item.Index] = item;
        (_merchantType, _merchantPageCount) = (page.WindowType, page.Page + 1);
        GD.Print($"[world] merchant {_merchantId}: page {page.Page}, {page.Items.Count} item(s), type {page.WindowType}");
        _merchantWindow.Visible = true;
        if (_statsPanel is not null && !(_statsPanel.Visible && _statsPage == StatsInventory))
            ToggleStatsPage(StatsInventory);
        ShowMerchantPage();
        ShowMerchantMoney(_money);
        if (_devBuySell)
        {
            _devBuySell = false;
            GetTree().CreateTimer(1.0).Timeout += DevBuySell;
        }
    }

    private void ShowMerchantPage()
    {
        if (_merchantList is null || _merchantTitle is null || _merchantPageLabel is null)
            return;
        _merchantTitle.Text = MerchantTitle();
        _merchantShown = Mathf.Clamp(_merchantShown, 0, System.Math.Max(0, _merchantPageCount - 1));
        _merchantPageLabel.Text = $"Page {_merchantShown + 1} of {_merchantPageCount}";
        _merchantList.Clear();
        for (int i = 0; i < MerchantPage.ItemsPerPage; i++)
        {
            if (_merchantSlots[_merchantShown, i] is not MerchantItem item)
            {
                int blank = _merchantList.AddItem("");
                _merchantList.SetItemSelectable(blank, false);
                continue;
            }
            int row = _merchantList.AddItem($"{item.Name} [{PriceText(item.Price, _merchantType)}]", MerchantIcon(item));
            _merchantList.SetItemMetadata(row, item.Index);
            _merchantList.SetItemTooltip(row, $"{item.Name}\nLevel {item.Level}" + (item.PackSize > 1 ? $"\nA pack of {item.PackSize}" : "")
                + (item.CanUse ? "" : "\nYou can't use this."));
            if (!item.CanUse)
                _merchantList.SetItemCustomFgColor(row, new Color(0.55f, 0.55f, 0.55f));
        }
        ShowMerchantQuantity();
    }

    // The title by window type; with no interact target, "Items To Buy".
    private string MerchantTitle()
    {
        string name = _entities.TryGetValue(_merchantId, out Entity? e) && e.Name.Length > 0 ? e.Name : "Items To Buy";
        return _merchantType switch
        {
            1 => "Bounty Store", 3 => "House Store", 5 => "House Merchant Store", 6 => "House Decoration Store",
            7 => "House Garden Store", 8 => "House Vault Store", 9 => "House Tools Store", 10 => "House Bindstone Store",
            12 => "House Bounty Store", 13 => "House Guild Bounty Store", _ => name,
        };
    }

    /// <summary>A row's price as retail writes it for the window type: coins ("1p 2g 5c", zero parts left out, no
    /// mithril; "FREE" at 0), "N bp", "N x", "N gbp" or "N m" (retail's quirk at 0 for bounty points: "0 bpFREE").</summary>
    internal static string PriceText(uint price, byte windowType)
    {
        string unit = windowType switch { 1 or 12 => "bp", 2 => "x", 13 => "gbp", 14 => "m", _ => "" };
        if (unit.Length > 0)
            return $"{price} {unit}" + (price == 0 && unit.EndsWith("bp", System.StringComparison.Ordinal) ? "FREE" : "");
        if (price == 0)
            return "FREE";
        (long p, long g, long sv, long c) = (price / 10_000_000L, price / 10_000 % 1000, price / 100 % 100, price % 100);
        return string.Join(" ", new[] { (p, "p"), (g, "g"), (sv, "s"), (c, "c") }.Where(x => x.Item1 > 0).Select(x => $"{x.Item1}{x.Item2}"));
    }

    private Texture2D? MerchantIcon(MerchantItem item)
    {
        _iconTable ??= LoadIconTable();
        return _iconTable.Resolve(DaocAssets.Ui.IconKind.Item, item.Model) is { } p ? IconTexture(p.Base) : null;
    }

    // The five boxes: the player's own money (as the character sheet's).
    private void ShowMerchantMoney(MoneyUpdate? m)
    {
        long[] parts = m is null ? [0, 0, 0, 0, 0] : [m.Mithril, m.Platinum, m.Gold, m.Silver, m.Copper];
        for (int i = 0; i < 5; i++)
            if (_merchantCoins[i] is { } l)
                l.Text = parts[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void ShowMerchantQuantity()
    {
        if (_merchantQuantity is not null)
            _merchantQuantity.Text = _merchantQty.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private MerchantItem? SelectedMerchantItem() =>
        _merchantList is not null && _merchantPageCount > 0 && _merchantList.GetSelectedItems() is { Length: > 0 } sel
            && _merchantList.GetItemMetadata(sel[0]).VariantType == Variant.Type.Int
            ? _merchantSlots[_merchantShown, _merchantList.GetItemMetadata(sel[0]).AsInt32()]
            : null;

    private void StepQuantity(int step)
    {
        _merchantQty = Mathf.Clamp(_merchantQty + step, 1, MerchantMaxQuantity);
        ShowMerchantQuantity();
    }

    private void TurnMerchantPage(int step)
    {
        if (_merchantPageCount == 0)
            return;
        _merchantShown = (_merchantShown + step + _merchantPageCount) % _merchantPageCount;
        ShowMerchantPage();
    }

    private bool MerchantLocked()
    {
        if (_seconds - _merchantLockAt < MerchantLockout)
        {
            AppendChat("You must wait one more second to interact again!", new Color(1f, 1f, 1f));
            return true;
        }
        _merchantLockAt = _seconds;
        return false;
    }

    private void Buy()
    {
        if (SelectedMerchantItem() is not MerchantItem item)
            return;
        if (item.CanUse)
            BuyNow(item);
        else
            AskYesNo("You cannot use this item.\nDo you really wish to buy it?", () => BuyNow(item));
    }

    private void BuyNow(MerchantItem item)
    {
        if (_session is not { } session || MerchantLocked())
            return;
        var slot = (ushort)(_merchantShown * MerchantPage.ItemsPerPage + item.Index);
        (byte count, byte type) = ((byte)_merchantQty, _merchantType);
        _ = Send(() => session.BuyAsync(slot, count, type), "buy");
        if (_merchantKeepQty is not { ButtonPressed: true })
        {
            _merchantQty = 1;
            ShowMerchantQuantity();
        }
    }

    // SELL, and the Sell key (Shift+S) with or without the window: the item selected in the inventory.
    private void Sell()
    {
        if (_session is not { } session || _miniSlot is not byte slot || MerchantLocked())
            return;
        _ = Send(() => session.SellAsync(slot), "sell");
    }

    private void Appraise()
    {
        if (_session is not { } session || _miniSlot is not byte slot)
            return;
        _ = Send(() => session.AppraiseAsync(slot), "appraise");
    }

    // Each frame while open: more than 256 units (2-D) from the merchant closes it.
    private void UpdateMerchantRange()
    {
        if (_merchantWindow is not { Visible: true } || !_entities.TryGetValue(_merchantId, out Entity? e))
            return;
        Vector3 d = e.Node.Position - _body.Position;
        d.Y = 0;
        if (d.Length() > MerchantRange * RegionMetresPerUnit)
            CloseMerchant();
    }

    private void CloseMerchant()
    {
        if (_merchantWindow is not null)
            _merchantWindow.Visible = false;
    }

    // --buy-sell: buy the cheapest item the character can use, then appraise it and sell it back from the backpack,
    // through the window's own buttons; the server's answers show in the log (chat, money, inventory).
    private void DevBuySell()
    {
        if (_merchantList is null)
            return;
        MerchantItem? pick = Enumerable.Range(0, MerchantPage.ItemsPerPage).Select(i => _merchantSlots[_merchantShown, i])
            .OfType<MerchantItem>().OrderBy(i => !i.CanUse).ThenBy(i => i.Price).FirstOrDefault();
        if (pick is null)
        {
            GD.Print("[merchant] --buy-sell: nothing for sale");
            return;
        }
        for (int row = 0; row < _merchantList.ItemCount; row++)
            if (_merchantList.GetItemMetadata(row).VariantType == Variant.Type.Int && _merchantList.GetItemMetadata(row).AsInt32() == pick.Index)
                _merchantList.Select(row);
        var before = _bridge.Inventory.Items.Keys.Where(k => k is >= InventorySlot.FirstBackpack and <= InventorySlot.LastBackpack).ToHashSet();
        GD.Print($"[merchant] --buy-sell: buying {pick.Name} [{PriceText(pick.Price, _merchantType)}], money {(_money is { } m ? Money(m) : "?")}");
        Buy();
        GetTree().CreateTimer(2.0).Timeout += () =>
        {
            byte? bought = _bridge.Inventory.Items.Keys
                .Where(k => k is >= InventorySlot.FirstBackpack and <= InventorySlot.LastBackpack && !before.Contains(k))
                .Select(k => (byte?)k).FirstOrDefault();
            GD.Print($"[merchant] --buy-sell: {(bought is byte b ? $"in backpack slot {b}" : "nothing new in the backpack")}, money {(_money is { } m2 ? Money(m2) : "?")}");
            if (bought is not byte slot)
                return;
            _miniSlot = slot;
            Appraise();
            GetTree().CreateTimer(1.5).Timeout += () =>
            {
                _miniSlot = slot;
                Sell();
                GetTree().CreateTimer(2.0).Timeout += () => GD.Print(
                    $"[merchant] --buy-sell: slot {slot} {(_bridge.Inventory.Items.ContainsKey(slot) ? "still full" : "empty")}, money {(_money is { } m3 ? Money(m3) : "?")}, window {(_merchantWindow is { Visible: true } ? "open" : "closed")}");
            };
        };
    }
}
