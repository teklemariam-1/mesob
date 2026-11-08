using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mesob.Studio;

/// <summary>A generic two-line row: what something is, its details, and an amount on the right.</summary>
public sealed class Row(string text, string detail, string right = "", object? tag = null)
{
    public string Text { get; } = text;
    public string Detail { get; } = detail;
    public string Right { get; } = right;
    public object? Tag { get; } = tag;
}

static class Ui
{
    public static Visibility Show(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
    public static ImageSource? Poster(string titleId)
    {
        var p = Studio.PosterPath(titleId);
        return File.Exists(p) ? new BitmapImage(new Uri(p)) : null;
    }
    public static string KindLabel(string kind) => kind switch { "audio" => "Music", "pdf" => "Book", _ => "Video" };
    public static string KindGlyph(string kind) => kind switch { "audio" => "", "pdf" => "", _ => "" };
    public static string Mb(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576.0:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    public static string Nakfa(long v) => $"{v:N0} Nfa";
}

public sealed class Tile(string label, string value, string caption, string glyph)
{
    public string Label { get; } = label;
    public string Value { get; } = value;
    public string Caption { get; } = caption;
    public string Glyph { get; } = glyph;
}

public sealed class TitleRow
{
    public required string Id { get; init; }
    public required string TitleEn { get; init; }
    public string TitleTi { get; init; } = "";
    public string Artist { get; init; } = "";
    public required string Kind { get; init; }
    public long Size { get; init; }
    public long Sold { get; init; }
    public long Price { get; init; }
    public long Revenue { get; init; }
    public double Share { get; init; }
    public int Rank { get; init; }
    public string KindLabel => Ui.KindLabel(Kind);
    public string KindGlyph => Ui.KindGlyph(Kind);
    public string SizeLabel => Ui.Mb(Size);
    public string SoldLabel => $"{Sold:N0}";
    public string PriceLabel => Ui.Nakfa(Price);
    public string RevenueLabel => Ui.Nakfa(Revenue);
    public string RankLabel => Rank.ToString();
    public string Subtitle => string.Join(" · ", new[] { TitleTi, Artist }.Where(s => !string.IsNullOrEmpty(s)));
    public ImageSource? Poster => Ui.Poster(Id);
    public Visibility NoPoster => Ui.Show(!File.Exists(Studio.PosterPath(Id)));
    public string PlayTip => Kind == "pdf" ? "Open the book" : "Play";
    public string PlayGlyph => Kind == "pdf" ? "" : "";
    public void Play(object sender, RoutedEventArgs e) => PreviewWindow.Open(this);
}

public sealed class ShopRow
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Town { get; init; } = "";
    public string Phone { get; init; } = "";
    public long Credits { get; init; }
    public long Sold { get; init; }
    public long Revenue { get; init; }
    public long Paid { get; init; }
    public bool Blocked { get; init; }
    public string IdLabel => Id.ToString();
    public string Place => string.IsNullOrEmpty(Phone) ? Town : $"{Town} · {Phone}";
    public string CreditsLabel => $"{Credits:N0}";
    public string SoldLabel => $"{Sold:N0}";
    public string RevenueLabel => Ui.Nakfa(Revenue);
    public string PaidLabel => Ui.Nakfa(Paid);
    public Action<ShopRow, FrameworkElement>? OnMore { get; set; }
    public void More(object sender, RoutedEventArgs e) => OnMore?.Invoke(this, (FrameworkElement)sender);
    public Visibility ActiveVisibility => Ui.Show(!Blocked);
    public Visibility BlockedVisibility => Ui.Show(Blocked);
}

public sealed class PackRow
{
    public long Id { get; init; }
    public long ShopId { get; init; }
    public string ShopName { get; init; } = "";
    public long Start { get; init; }
    public long Count { get; init; }
    public long Used { get; init; }
    public long Value { get; init; }
    public bool Unlocked { get; init; }
    public string IdLabel => $"Pack {Id}";
    public string ShopLabel => $"Shop {ShopId} · {ShopName}";
    public string Serials => $"{Start:N0} – {Start + Count - 1:N0}";
    public string SerialsTip => $"License serials {Serials}";
    public string UsageLabel => $"{Used:N0} of {Count:N0} used";
    public double UsagePercent => Count == 0 ? 0 : Used * 100.0 / Count;
    public string ValueLabel => Ui.Nakfa(Value);
    public Action<PackRow, bool>? OnAction { get; set; }
    public void Unlock() => OnAction?.Invoke(this, false);
    public void ShowCode() => OnAction?.Invoke(this, true);
    public Visibility UnlockedVisibility => Ui.Show(Unlocked);
    public Visibility LockedVisibility => Ui.Show(!Unlocked);
}

public sealed class StepResult(bool ok, string text)
{
    public string Text { get; } = text;
    public string Glyph { get; } = ok ? "" : "";
    public Visibility OkVisibility { get; } = Ui.Show(ok);
    public Visibility BadVisibility { get; } = Ui.Show(!ok);
}
