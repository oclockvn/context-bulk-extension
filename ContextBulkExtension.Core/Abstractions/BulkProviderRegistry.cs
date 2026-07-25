using System.Data.Common;
using System.Reflection;

namespace ContextBulkExtension.Core.Abstractions;

internal static class BulkProviderRegistry
{
    private static readonly List<IBulkProvider> Providers = new();
    private static readonly object Gate = new();
    private static int _providersLoaded;

    public static void Register(IBulkProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            // Avoid duplicate instances of same provider type
            if (Providers.Any(p => p.GetType() == provider.GetType()))
                return;
            Providers.Add(provider);
        }
    }

    public static IBulkProvider Resolve(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        EnsureProviderAssembliesLoaded();

        lock (Gate)
        {
            foreach (var provider in Providers)
            {
                if (provider.Supports(connection))
                    return provider;
            }
        }

        throw new InvalidOperationException(
            $"No bulk provider registered for connection type '{connection.GetType().FullName}'. " +
            "Reference ContextBulkExtension.SqlServer or ContextBulkExtension.Postgres. " +
            "Auto-discovery loads provider DLLs from AppContext.BaseDirectory; " +
            "trimmed/AOT/single-file apps must keep the provider assembly deployed beside the app.");
    }

    // ponytail: library ModuleInitializer unreliable; load provider dll + invoke Register entrypoint
    private static void EnsureProviderAssembliesLoaded()
    {
        if (Volatile.Read(ref _providersLoaded) == 1)
            return;

        lock (Gate)
        {
            if (_providersLoaded == 1)
                return;

            TryLoadAndRegister(
                "ContextBulkExtension.SqlServer",
                "ContextBulkExtension.SqlServer.SqlServerBulkProviderRegistration",
                "Initialize");

            TryLoadAndRegister(
                "ContextBulkExtension.Postgres",
                "ContextBulkExtension.Postgres.PostgresBulkProviderRegistration",
                "Initialize");

            Volatile.Write(ref _providersLoaded, 1);
        }
    }

    private static void TryLoadAndRegister(string assemblyName, string typeName, string methodName)
    {
        try
        {
            Assembly? asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName);

            if (asm == null)
            {
                var path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
                if (!File.Exists(path))
                    return;
                asm = Assembly.LoadFrom(path);
            }

            var type = asm.GetType(typeName, throwOnError: false);
            var method = type?.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            method?.Invoke(null, null);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException
            or FileLoadException
            or BadImageFormatException
            or DirectoryNotFoundException
            or IOException)
        {
            // Provider package not present beside app — ignore
        }
    }
}
