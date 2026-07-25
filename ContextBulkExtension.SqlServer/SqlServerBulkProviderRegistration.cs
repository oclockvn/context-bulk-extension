using ContextBulkExtension.Core.Abstractions;

namespace ContextBulkExtension.SqlServer;

internal static class SqlServerBulkProviderRegistration
{
    internal static void Initialize()
    {
        BulkProviderRegistry.Register(new SqlServerBulkProvider());
    }
}
