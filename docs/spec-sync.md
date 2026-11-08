# Sync sticks and the shop network

## Stick layout

Everything sits under a `MESOB` folder at the root of a USB stick or drive.

```
MESOB/
  setup-request.json          shop tablet → studio: {sign_public, kex_public, model, android, kex_hardware, attestation[]}
  setup-shop-N.json           studio → tablet: {shop_id, shop_name, studio_cert, shop_cert, sealed_secret}
  to-shop-N/                  studio → shop N
    studio.cert, shop.cert
    capacity/<block_id>.bin   vendor capacity blocks used by this shop's packs
    packs/<pack_id>.bin       credit packs (arrive locked; unlocked by code)
    titles/<title_id>.msb     encrypted titles
    keys/<title_id>.sealed    each title key sealed to this shop's key-exchange key
    catalog.json, catalog.sig studio-signed catalog with prices
  from-shop-N/
    log.json                  {shop_id, entries:[base64 signed log entries]}, the shop's whole log
```

Every item is signed, sealed or both, so the stick can travel with anyone. The shop always sends
its whole log; Studio Control checks the full chain each time, imports only new entries, and
rejects a log that was edited, reordered or got shorter.

## Customer USB exchange (Windows Player)

* The Player writes `mesob-request.msr` (device request) to the stick.
* The Shop app takes cash, then writes `mesob-licenses.msls` and any missing `<title_id>.msb` files.
* The Player imports both.

## Shop network API

The Shop app serves plain HTTP on port 8765 on its own Wi-Fi. The shop's QR code is
`mesob-shop://<ip>:8765`. Players only connect to local addresses and bind to the Wi-Fi network.

| Method | Path | Body / answer |
|--------|------|---------------|
| GET | `/api/catalog` | `{shop_id, shop_name, titles:[{title_id, title_en, title_ti, artist, kind, size, price}]}` |
| GET | `/api/poster/{title_id}` | image bytes |
| POST | `/api/orders` | `{device_public, title_ids:[…], replaces?: token}` → `{code, token, total, items:[{title_id, title_en, price}]}` |
| GET | `/api/orders/{token}` | `{code, status: pending\|paid\|cancelled, total, licenses:[{title_id, bundle}]}` |
| GET | `/api/titles/{title_id}` | the `.msb` file; `Range: bytes=N-` supported for resuming |

Plain HTTP is deliberate: titles are encrypted and signed and licenses are sealed to one device,
so nothing on the network is worth stealing or forging. At most 30 orders may be pending at once.

**Checkout (cart → code → cashier).** The customer builds a cart in the Player (search, filter,
add, remove; the cart survives closing the app) and taps *Place order*. The shop answers with a
pickup **code** such as `K472` (a letter without I/O lookalikes plus three digits, unique among
the orders the tablet holds) and a private 48-hex **token**. The Player shows the code full screen;
the customer pays cash and the cashier types the code on the tablet, checks the items and total,
and taps *Paid*. Only then does the shop sign licenses, sealed to the phone that placed the order,
and the Player (polling with its token) downloads the titles into the library. When the customer
goes back to change the cart, the next order carries the old order's token in `replaces` and the
shop drops the old one, so editing never leaves stale orders in the queue. Only the token can do
this: device public keys travel in the clear on the Wi-Fi, so they must not be enough to cancel
someone's order. The design follows MediaVault's in-shop
checkout, with the difference that a Mesob title is useless to anyone but the buyer's device.
