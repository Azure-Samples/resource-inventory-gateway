using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureAppConfiguration((hostContext, configuration) =>
    {
        configuration.SetBasePath(hostContext.HostingEnvironment.ContentRootPath)
                     .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                     .AddEnvironmentVariables();
    })
    .ConfigureServices((builder, services) =>
    {
        services.AddLogging(logBuilder =>
        {
            logBuilder.AddOpenTelemetry(otOpts =>
            {
                otOpts.SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(nameof(ResourceInventory))
                    .AddTelemetrySdk()
                    .AddEnvironmentVariableDetector());

                otOpts.IncludeFormattedMessage = true;

                otOpts.AddConsoleExporter();
            });

            logBuilder.AddConsole();
            logBuilder.SetMinimumLevel(LogLevel.Information);
        });
    })
    .Build();

host.Run();

namespace ResourceInventory
{
    /// <summary>
    /// Defines the entry point for the application.
    /// </summary>
    public partial class Program;
}
