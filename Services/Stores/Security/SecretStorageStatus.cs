namespace Retromind.Services.Stores.Security;

/// <summary>Durability of the last observed value; Unknown also covers an absent value.</summary>
public enum SecretStorageStatus
{
    Unknown,
    Persistent,
    SessionOnly,
    Failed
}
