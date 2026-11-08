using System.Text.Json;

namespace Mesob.Player;

/// <summary>
/// The customer's cart and the order waiting at the counter. Both are saved, so closing the app
/// (or the PC sleeping) between "Place order" and paying doesn't lose the pickup code.
/// </summary>
public static class StoreCart
{
    public sealed record Line(string Id, string TitleEn, string TitleTi, string Artist, string Kind, long Price);

    /// <summary>An order placed at a shop. <see cref="Token"/> is private; <see cref="Code"/> is said at the counter.</summary>
    public sealed record Pending(string Address, string ShopName, string Code, string Token, long Total, List<Line> Lines);

    private sealed record Saved(List<Line> Lines, Pending? Order, string LastAddress, string LastShopName, string? Replaces = null);

    public static List<Line> Lines { get; private set; } = [];
    public static Pending? Order { get; private set; }
    public static string LastAddress { get; private set; } = "";
    public static string LastShopName { get; private set; } = "";
    /// <summary>Token of an order the customer went back to change; the next order replaces it at the shop.</summary>
    public static string? Replaces { get; private set; }
    private static bool _loaded;

    private static string FilePath => Path.Combine(PlayerState.DataDir, "cart.json");

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            if (s == null) return;
            Lines = s.Lines ?? [];
            Order = s.Order;
            LastAddress = s.LastAddress ?? "";
            LastShopName = s.LastShopName ?? "";
            Replaces = s.Replaces;
        }
        catch { /* a damaged cart file just means an empty cart */ }
    }

    private static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved(Lines, Order, LastAddress, LastShopName, Replaces))); }
        catch { /* best effort */ }
    }

    public static long Total => Lines.Sum(l => l.Price);
    public static bool Contains(string id) => Lines.Any(l => l.Id == id);

    public static bool Toggle(Line line)
    {
        var had = Contains(line.Id);
        if (had) Lines.RemoveAll(l => l.Id == line.Id); else Lines.Add(line);
        Save();
        return !had;
    }

    public static void Remove(string id) { Lines.RemoveAll(l => l.Id == id); Save(); }

    /// <summary>Connected to a shop: remember it and keep only lines in <paramref name="sells"/> (sold here, not already owned). Returns how many were dropped.</summary>
    public static int AtShop(string address, string name, IReadOnlySet<string> sells)
    {
        LastAddress = address;
        LastShopName = name;
        var dropped = Lines.RemoveAll(l => !sells.Contains(l.Id));
        Save();
        return dropped;
    }

    public static void Placed(Pending p) { Order = p; Replaces = null; Save(); }

    /// <summary>Back to the cart to change it; ordering again replaces the waiting order at the shop.</summary>
    public static void ForgetOrder() { Replaces = Order?.Token; Order = null; Save(); }

    /// <summary>Paid and downloaded: empty the cart.</summary>
    public static void Finished() { Order = null; Replaces = null; Lines = []; Save(); }
}
