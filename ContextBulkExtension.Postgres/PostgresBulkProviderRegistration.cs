using ContextBulkExtension.Core.Abstractions;

namespace ContextBulkExtension.Postgres;

internal static class PostgresBulkProviderRegistration
{
    internal static void Initialize()
    {
        BulkProviderRegistry.Register(new PostgresBulkProvider());
    }
}
