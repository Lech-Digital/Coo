using Coo.Domain.Households;

namespace Coo.Mobile.Infrastructure.Persistence;

/// <summary>The baby's sex as stored and sent: "girl" or "boy". Anything else (a later build's value) reads as not set.</summary>
internal static class BabySexText
{
    public static BabySex? Parse(string? value) => value switch
    {
        "girl" => BabySex.Girl,
        "boy" => BabySex.Boy,
        _ => null,
    };

    public static string? Text(BabySex? sex) => sex switch
    {
        BabySex.Girl => "girl",
        BabySex.Boy => "boy",
        _ => null,
    };
}
