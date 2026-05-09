namespace Retromind.Models.Stores;

public sealed record StoreGameRecord(
    string ProviderId,
    string StoreGameId,
    string Title,
    string? Platform,
    string? Version);
