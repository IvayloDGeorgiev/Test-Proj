namespace Test_Proj.Export;

public static class ExportRegistration
{
    public static IServiceCollection AddCsvExport(this IServiceCollection services)
    {
        services.AddSingleton<OperationLease>();
        services.AddSingleton<IAtomicFileOperations, AtomicFileOperations>();
        services.AddSingleton<ICsvExporter, CsvExporter>();
        return services;
    }
}
