namespace MusicOrganiser.Core.Parsers;

/// <summary>
/// Audio Unit manufacturer codes. Projects saved on a Mac with AU plugins often store
/// only this four-character code, not the vendor's name.
/// </summary>
public static class AuVendors
{
    static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        // Seen in real FL Studio projects saved on a Mac.
        ["UHfX"] = "u-he",
        ["xlnA"] = "XLN Audio",
        ["Outp"] = "Output",
        ["RoCl"] = "Roland",
        ["DvMc"] = "Devious Machines",
        ["RPCX"] = "Rob Papen",
        ["-NI-"] = "Native Instruments",
        // Apple's own Audio Units.
        ["appl"] = "Apple",
    };

    /// <summary>The vendor's name, or "" when the code isn't known.</summary>
    public static string Name(string code) => Known.GetValueOrDefault(code, "");
}
