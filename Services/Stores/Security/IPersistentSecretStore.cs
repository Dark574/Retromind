namespace Retromind.Services.Stores.Security;

/// <summary>
/// A secret store whose successful writes survive application restarts.
/// Use this boundary for migrations that may remove the only persisted copy.
/// </summary>
public interface IPersistentSecretStore : ISecretStore
{
}
