using ContextBulkExtension.Core.Abstractions;

namespace ContextBulkExtension.PostgreSql;

internal static class PostgreSqlBulkProviderRegistration
{
    internal static void Initialize()
    {
        BulkProviderRegistry.Register(new PostgreSqlBulkProvider());
    }
}
