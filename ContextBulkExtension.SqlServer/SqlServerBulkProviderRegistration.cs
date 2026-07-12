using System.Runtime.CompilerServices;
using ContextBulkExtension.Abstractions;

namespace ContextBulkExtension.SqlServer;

internal static class SqlServerBulkProviderRegistration
{
    internal static void Initialize()
    {
        BulkProviderRegistry.Register(new SqlServerBulkProvider());
    }
}
