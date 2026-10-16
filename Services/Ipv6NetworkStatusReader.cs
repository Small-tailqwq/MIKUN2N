using System.Text.Json;
using MikuN2N.Models;

namespace MikuN2N.Services;

public static class Ipv6NetworkStatusReader
{
    public static Ipv6NetworkStatus Read(JsonElement row, bool requested, DateTimeOffset now)
    {
        if (!requested) return new(false, Ipv6Availability.Disabled, ReportedAt: now);
        if (!row.TryGetProperty("ipv6_enabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new(true, Ipv6Availability.Unsupported, ReportedAt: now);
        if (!enabled.GetBoolean()) return new(true, Ipv6Availability.Disabled, ReportedAt: now);
        var state = Text(row, "ipv6_status") switch
        {
            "checking" => Ipv6Availability.Checking,
            "available" => Ipv6Availability.Available,
            "unavailable" => Ipv6Availability.Unavailable,
            "disabled" => Ipv6Availability.Disabled,
            "unverified" or null => Ipv6Availability.Unverified,
            _ => Ipv6Availability.Unknown
        };
        var kind = Text(row, "ipv6_address_kind") switch
        {
            "global" => Ipv6AddressKind.Global,
            "ula" => Ipv6AddressKind.UniqueLocal,
            _ => Ipv6AddressKind.Unknown
        };
        var mapping = state == Ipv6Availability.Available ? Text(row, "ipv6_mapping") switch
        {
            "untranslated" => Ipv6MappingKind.Untranslated,
            "address-translated" => Ipv6MappingKind.AddressTranslated,
            "port-translated" => Ipv6MappingKind.PortTranslated,
            _ => Ipv6MappingKind.Unknown
        } : Ipv6MappingKind.Unknown;
        return new(true, state, kind, mapping, now);
    }

    public static Ipv6NetworkStatus Current(bool requested, bool isN3n, Ipv6NetworkStatus? status, DateTimeOffset now)
    {
        if (!requested) return new(false, Ipv6Availability.Disabled);
        if (!isN3n) return new(true, Ipv6Availability.Unsupported);
        if (status is null) return new(true, Ipv6Availability.Checking);
        // Existing NAT polling runs every four seconds; do not keep a stale green result.
        return status.ReportedAt is { } at && now - at <= TimeSpan.FromSeconds(12)
            ? status : new(true, Ipv6Availability.Unknown);
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
