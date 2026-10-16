namespace MikuN2N.Models;

public enum Ipv6Availability { Disabled, Checking, Unverified, Available, Unavailable, Unsupported, Unknown }
public enum Ipv6AddressKind { Unknown, Global, UniqueLocal }
public enum Ipv6MappingKind { Unknown, Untranslated, AddressTranslated, PortTranslated }

public sealed record Ipv6NetworkStatus(bool Requested, Ipv6Availability Availability,
    Ipv6AddressKind AddressKind = Ipv6AddressKind.Unknown,
    Ipv6MappingKind Mapping = Ipv6MappingKind.Unknown, DateTimeOffset? ReportedAt = null);
