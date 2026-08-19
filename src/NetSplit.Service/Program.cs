using NetSplit.Core;
using NetSplit.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "NetSplit Service");
builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<INetworkAdapterProvider, WindowsNetworkAdapterProvider>();
builder.Services.AddSingleton<IConfigurationValidatorFacade, ConfigurationValidatorFacade>();
builder.Services.AddSingleton<FileLogBuffer>();
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<ISubscriptionLoader, SubscriptionLoader>();
builder.Services.AddSingleton<IMihomoControllerClient, MihomoControllerClient>();
builder.Services.AddSingleton<IMihomoProcessManager, MihomoProcessManager>();
builder.Services.AddSingleton<NetSplitCoordinator>();
builder.Services.AddHostedService<CoordinatorHostedService>();
builder.Services.AddHostedService<PipeServerHostedService>();

await builder.Build().RunAsync().ConfigureAwait(false);
