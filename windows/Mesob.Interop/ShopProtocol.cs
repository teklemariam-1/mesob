namespace Mesob.Interop;

/// <summary>
/// The shop's local-network API (served by the Shop app on its own Wi-Fi). Plain HTTP is safe
/// here: title files are encrypted and signed, and licenses are signed and sealed to one device,
/// so a fake or tampered shop can't produce anything the Player will accept.
///
/// GET  /api/catalog              → { shop_id, shop_name, titles:[{title_id,title_en,title_ti,artist,kind,size,price}] }
/// GET  /api/poster/{title_id}    → image bytes
/// POST /api/orders               { device_public, title_ids:[...], replaces?: token } → { code, token, total, items:[{title_id,title_en,price}] }
/// GET  /api/orders/{token}       → { code, status: pending|paid|cancelled, total, licenses:[{title_id,bundle}] }
///
/// The code ("K472") is shown or said at the counter; the cashier types it to find the order.
/// The token is the buyer's private handle for polling. Codes are guessable, so the API never takes one.
/// GET  /api/titles/{title_id}    → the .msb file (supports Range for resuming)
/// </summary>
public static class ShopProtocol
{
    public const int DefaultPort = 8765;

    public static Uri BaseUri(string address)
    {
        var a = address.Trim();
        if (a.StartsWith("mesob-shop://", StringComparison.OrdinalIgnoreCase)) a = a["mesob-shop://".Length..];
        if (!a.Contains(':')) a += ":" + DefaultPort;
        return new Uri("http://" + a.TrimEnd('/') + "/");
    }
}
