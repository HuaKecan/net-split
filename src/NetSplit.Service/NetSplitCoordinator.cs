using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetSplit.Core;

namespace NetSplit.Service;

public sealed class NetSplitCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan TrafficHistorySampleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProxyDelayCacheDuration = TimeSpan.FromMinutes(5);
    private const int ProxyDelayProbeConcurrency = 8;

    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly ISecretProtector _secretProtector;
    private readonly INetworkAdapterProvider _adapterProvider;
    private readonly IConfigurationValidatorFacade _validator;
    private readonly ISubscriptionLoader _subscriptionLoader;
    private readonly IMihomoProcessManager _processManager;
    private readonly IMihomoControllerClient _controller;
    private readonly FileLogBuffer _logs;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _proxyDelayGate = new(1, 1);
    private readonly Dictionary<string, TrafficSample> _trafficSamples =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TrafficHistoryBuffer _trafficHistory = new();

    private SplitRouteSettings _settings = new();
    private SplitRouteSettings? _activeSettings;
    private RuntimeStatus _status = new()
    {
        Mode = RuntimeMode.Starting
    };
    private CoordinatorReadiness _readiness = CoordinatorReadiness.Starting;
    private bool _initialized;
    private DateTimeOffset _nextRestartAttempt = DateTimeOffset.MinValue;
    private int _restartFailures;
    private DateTimeOffset _lastHealthCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSubscriptionCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTrafficHistorySampleAt = DateTimeOffset.MinValue;
    private bool _lastDirectAvailable;
    private bool _lastProxyAvailable;
    private string _appliedDirectAdapterName = string.Empty;
    private string _appliedProxyAdapterName = string.Empty;
    private DateTimeOffset? _adapterReapplyDue;
    private ProxyDelayCacheEntry? _proxyDelayCache;
    private int _proxyDelayCacheGeneration;

    public NetSplitCoordinator(
        AppPaths paths,
        SettingsStore settingsStore,
        ISecretProtector secretProtector,
        INetworkAdapterProvider adapterProvider,
        IConfigurationValidatorFacade validator,
        ISubscriptionLoader subscriptionLoader,
        IMihomoProcessManager processManager,
        IMihomoControllerClient controller,
        FileLogBuffer logs)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _secretProtector = secretProtector;
        _adapterProvider = adapterProvider;
        _validator = validator;
        _subscriptionLoader = subscriptionLoader;
        _processManager = processManager;
        _controller = controller;
        _logs = logs;
        _processManager.Exited += OnMihomoExited;
    }

    public RuntimeStatus Status => _status;
    public IReadOnlyList<TrafficPoint> TrafficHistorySnapshot => _trafficHistory.Snapshot();
    public bool IsReady => _initialized && _readiness == CoordinatorReadiness.Ready;
    public ClientSettingsSnapshot ClientSettings => new()
    {
        Enabled = _settings.Enabled,
        DirectAdapter = _settings.DirectAdapter,
        ProxyAdapter = _settings.ProxyAdapter,
        MihomoPath = _settings.MihomoPath,
        GeoDataDirectory = _settings.GeoDataDirectory,
        MihomoAvailable = File.Exists(_settings.MihomoPath),
        GeoDataAvailable = Directory.Exists(_settings.GeoDataDirectory),
        ResidentialProxy = new ResidentialProxySummary
        {
            Enabled = _settings.ResidentialProxy.Enabled,
            Host = _settings.ResidentialProxy.Host,
            Port = _settings.ResidentialProxy.Port,
            AuthenticationEnabled = _settings.ResidentialProxy.AuthenticationEnabled,
            HasCredentials = !string.IsNullOrWhiteSpace(
                                 _settings.ResidentialProxy.ProtectedUsername)
                             && !string.IsNullOrWhiteSpace(
                                 _settings.ResidentialProxy.ProtectedPassword),
            RouteMode = _settings.ResidentialProxy.RouteMode
        },
        Subscriptions = _settings.Subscriptions.Select(item => new SubscriptionSummary
        {
            Id = item.Id,
            Name = item.Name,
            SourceKind = item.SourceKind,
            DisplaySource = item.DisplaySource,
            Enabled = item.Enabled,
            UpdateIntervalMinutes = item.UpdateIntervalMinutes,
            LastUpdated = item.LastUpdated
        }).ToArray(),
        Rules = _settings.Rules
    };

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            _readiness = CoordinatorReadiness.Starting;
            _status = _status with
            {
                Mode = RuntimeMode.Starting,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _paths.EnsureDirectories();
            _settings = DiscoverRuntimeDefaults(await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false));
            var startupDisableActive = File.Exists(_paths.StartupDisableMarkerFile);
            if (startupDisableActive)
            {
                _settings = _settings with { Enabled = false };
                await _logs.WriteAsync(
                    "WARN",
                    "安装或恢复保护正在生效，本次服务启动将保持 Mihomo、TUN 和 DNS 接管关闭。",
                    cancellationToken).ConfigureAwait(false);
            }

            if (await RecoverInterruptedTransactionAsync(
                    startupDisableActive,
                    cancellationToken).ConfigureAwait(false))
            {
                await _logs.WriteAsync(
                    "WARN",
                    "检测到未完成事务，已恢复上一代运行状态。",
                    cancellationToken).ConfigureAwait(false);
                _initialized = true;
                _readiness = CoordinatorReadiness.Ready;
                return;
            }

            await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            await _logs.WriteAsync("INFO", "net-split 服务已初始化。", cancellationToken).ConfigureAwait(false);

            if (_settings.Enabled)
            {
                await ApplyCoreAsync(false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _processManager.StopAsync(
                    _settings,
                    CancellationToken.None).ConfigureAwait(false);

                _activeSettings = null;
                UpdateStatus(_adapterProvider.GetAdapters(), null, null);
            }

            _initialized = true;
            _readiness = CoordinatorReadiness.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _initialized = false;
            _readiness = CoordinatorReadiness.Starting;
            throw;
        }
        catch (Exception exception)
        {
            _initialized = false;
            _readiness = CoordinatorReadiness.RecoveryRequired;
            var cleanupError = await StopAfterInitializationFailureAsync()
                .ConfigureAwait(false);
            var error = cleanupError is null
                ? exception.Message
                : $"{exception.Message}; failed to stop the managed core: {cleanupError}";
            SetError(RuntimeMode.Misconfigured, error);
            _status = _status with
            {
                MihomoRunning = _processManager.IsRunning,
                TunEnabled = false,
                DnsEnabled = false,
                DnsStatusKnown = false,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            try
            {
                await _logs.WriteAsync(
                    "ERROR",
                    $"初始化失败：{error}",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Initialization diagnostics must remain available if file logging fails.
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public IReadOnlyList<NetworkAdapterSnapshot> DiscoverAdapters()
    {
        return _adapterProvider.GetAdapters();
    }

    public async Task<DiagnosticsSnapshot> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        var settings = _settings;
        var geoDataDirectory = settings.GeoDataDirectory;
        var fileTasks = new[]
        {
            DescribeFileAsync("mihomo.exe", settings.MihomoPath, cancellationToken),
            DescribeFileAsync(
                "geoip.dat",
                string.IsNullOrWhiteSpace(geoDataDirectory)
                    ? null
                    : Path.Combine(geoDataDirectory, "geoip.dat"),
                cancellationToken),
            DescribeFileAsync(
                "geosite.dat",
                string.IsNullOrWhiteSpace(geoDataDirectory)
                    ? null
                    : Path.Combine(geoDataDirectory, "geosite.dat"),
                cancellationToken),
            DescribeFileAsync(
                "runtime-config.yaml",
                _paths.RuntimeConfigFile,
                cancellationToken),
            DescribeFileAsync(
                "candidate-config.yaml",
                _paths.CandidateConfigFile,
                cancellationToken),
            DescribeFileAsync(
                "transaction.pending.json",
                _paths.TransactionJournalFile,
                cancellationToken),
            DescribeFileAsync(
                "startup.force-disabled",
                _paths.StartupDisableMarkerFile,
                cancellationToken),
            DescribeFileAsync(
                "mihomo.pid",
                _paths.MihomoPidFile,
                cancellationToken)
        };

        var files = await Task.WhenAll(fileTasks).ConfigureAwait(false);
        var residential = settings.ResidentialProxy ?? new ResidentialProxySettings();
        var settingsSummary = new DiagnosticsSettingsSummary
        {
            Enabled = settings.Enabled,
            DirectAdapterName = _status.DirectAdapterName,
            ProxyAdapterName = _status.ProxyAdapterName,
            MihomoAvailable = !string.IsNullOrWhiteSpace(settings.MihomoPath)
                && File.Exists(settings.MihomoPath),
            GeoDataAvailable = !string.IsNullOrWhiteSpace(settings.GeoDataDirectory)
                && Directory.Exists(settings.GeoDataDirectory),
            HealthCheckSeconds = settings.HealthCheckSeconds,
            SubscriptionCount = settings.Subscriptions.Count,
            EnabledSubscriptionCount = settings.Subscriptions.Count(item => item.Enabled),
            RuleCount = settings.Rules.Count,
            ResidentialProxyEnabled = residential.Enabled,
            ResidentialProxyRouteMode = residential.RouteMode,
            ResidentialProxyHasCredentials =
                !string.IsNullOrWhiteSpace(residential.ProtectedUsername)
                && !string.IsNullOrWhiteSpace(residential.ProtectedPassword)
        };

        return new DiagnosticsSnapshot
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            ApplicationVersion =
                typeof(NetSplitCoordinator).Assembly.GetName().Version?.ToString()
                ?? "unknown",
            ServiceReady = IsReady,
            Readiness = _readiness,
            StartupDisableActive = File.Exists(_paths.StartupDisableMarkerFile),
            Runtime = _status,
            Settings = settingsSummary,
            Adapters = _adapterProvider.GetAdapters(),
            Files = files,
            RecentLogs = _logs.Snapshot(100)
        };
    }

    public ConfigurationValidationResult ValidateCurrentSettings()
    {
        return _validator.Validate(_settings, _adapterProvider.GetAdapters());
    }

    public async Task<ConfigurationValidationResult> ValidateRuntimeAsync(
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousSettings = _settings;
            var adapters = _adapterProvider.GetAdapters();
            var validation = _validator.Validate(_settings, adapters);
            if (!validation.IsValid)
            {
                return validation;
            }

            var direct = ResolveForRuntime(_settings.DirectAdapter, adapters, "网卡1");
            var proxy = ResolveForRuntime(_settings.ProxyAdapter, adapters, "网卡2");
            var subscriptions = await _subscriptionLoader.LoadAllAsync(
                _settings.Subscriptions,
                false,
                cancellationToken).ConfigureAwait(false);
            var config = MihomoConfigGenerator.Generate(
                subscriptions,
                _settings,
                direct,
                proxy,
                adapters,
                ResolveResidentialProxyCredentials(_settings));
            await WriteConfigFileAsync(
                _paths.CandidateConfigFile,
                config.Yaml,
                cancellationToken).ConfigureAwait(false);
            ProcessValidationResult processValidation;
            try
            {
                processValidation = await _processManager.ValidateAsync(
                    _settings,
                    _paths.CandidateConfigFile,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DeleteIfExists(_paths.CandidateConfigFile);
            }

            var errors = validation.Errors.ToList();
            var warnings = validation.Warnings.Concat(config.Warnings).ToList();
            if (!processValidation.IsValid)
            {
                errors.Add(string.IsNullOrWhiteSpace(processValidation.Output)
                    ? "Mihomo 配置验证失败。"
                    : processValidation.Output);
            }

            return new ConfigurationValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                Warnings = warnings
            };
        }
        catch (Exception exception)
        {
            return new ConfigurationValidationResult
            {
                IsValid = false,
                Errors = [exception.Message]
            };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task UpdateBindingsAsync(
        UpdateBindingsRequest request,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousSettings = _settings;
            var adapters = _adapterProvider.GetAdapters();
            var direct = adapters.FirstOrDefault(item =>
                item.Id.Equals(request.DirectAdapterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("找不到所选网卡1。");
            var proxy = adapters.FirstOrDefault(item =>
                item.Id.Equals(request.ProxyAdapterId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("找不到所选网卡2。");
            if (direct.Id.Equals(proxy.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("网卡1和网卡2必须不同。");
            }

            _settings = _settings with
            {
                DirectAdapter = ToBinding(direct),
                ProxyAdapter = ToBinding(proxy)
            };
            await PersistSettingsChangeAsync(
                previousSettings,
                forceRefresh: false,
                cancellationToken).ConfigureAwait(false);
            await _logs.WriteAsync("INFO", "网卡映射已更新。", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task UpdateResidentialProxyAsync(
        UpdateResidentialProxyRequest request,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Enum.IsDefined(request.RouteMode))
            {
                throw new InvalidOperationException("住宅代理连接方式无效。");
            }

            var previousSettings = _settings;
            var previousProxy = _settings.ResidentialProxy;
            var hasHost = !string.IsNullOrWhiteSpace(request.Host);
            var host = hasHost
                ? ResidentialProxyValidator.NormalizeHost(request.Host)
                : string.Empty;
            if (request.Enabled && !hasHost)
            {
                throw new InvalidOperationException("启用住宅代理前请填写服务器地址。");
            }

            if (hasHost)
            {
                ResidentialProxyValidator.ValidatePort(request.Port);
            }

            var protectedUsername = previousProxy.ProtectedUsername;
            var protectedPassword = previousProxy.ProtectedPassword;
            if (!request.AuthenticationEnabled)
            {
                protectedUsername = string.Empty;
                protectedPassword = string.Empty;
            }
            else if (request.ReplaceCredentials)
            {
                var username = request.Username.Trim();
                if (string.IsNullOrWhiteSpace(username)
                    || string.IsNullOrEmpty(request.Password))
                {
                    throw new InvalidOperationException("住宅代理用户名和密码必须同时填写。");
                }

                protectedUsername = _secretProtector.Protect(username);
                protectedPassword = _secretProtector.Protect(request.Password);
            }

            if (request.Enabled
                && request.AuthenticationEnabled
                && (string.IsNullOrWhiteSpace(protectedUsername)
                    || string.IsNullOrWhiteSpace(protectedPassword)))
            {
                throw new InvalidOperationException("启用住宅代理前请填写用户名和密码。");
            }

            _settings = _settings with
            {
                ResidentialProxy = new ResidentialProxySettings
                {
                    Enabled = request.Enabled,
                    Host = host,
                    Port = hasHost ? request.Port : 1080,
                    AuthenticationEnabled = request.AuthenticationEnabled,
                    ProtectedUsername = protectedUsername,
                    ProtectedPassword = protectedPassword,
                    RouteMode = request.RouteMode
                }
            };
            await PersistSettingsChangeAsync(
                previousSettings,
                forceRefresh: false,
                cancellationToken).ConfigureAwait(false);
            await _logs.WriteAsync(
                "INFO",
                "住宅 SOCKS5 代理配置已更新。",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SetProxyExitModeAsync(
        SetProxyExitModeRequest request,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        if (!Enum.IsDefined(request.Mode))
        {
            throw new InvalidOperationException("代理最终出口模式无效。");
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var residentialEnabled = request.Mode == ProxyExitMode.Residential;
            if (_settings.ResidentialProxy.Enabled == residentialEnabled)
            {
                return;
            }

            if (residentialEnabled)
            {
                EnsureResidentialProxyCanBeEnabled(_settings.ResidentialProxy);
            }

            var previousSettings = _settings;
            _settings = _settings with
            {
                ResidentialProxy = _settings.ResidentialProxy with
                {
                    Enabled = residentialEnabled
                }
            };
            await PersistSettingsChangeAsync(
                previousSettings,
                forceRefresh: false,
                cancellationToken).ConfigureAwait(false);
            _lastHealthCheck = DateTimeOffset.MinValue;
            await WritePostCommitLogAsync(
                "INFO",
                residentialEnabled
                    ? "境外流量最终出口已切换为住宅 SOCKS5。"
                    : "境外流量最终出口已切换为机场节点。").ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task AddSubscriptionAsync(
        SubscriptionInput input,
        CancellationToken cancellationToken)
    {
        var name = input.Name.Trim();
        var source = input.Source.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException("订阅名称和来源不能为空。");
        }

        EnsureReady();
        var item = new SubscriptionSettings
        {
            Name = name,
            SourceKind = input.SourceKind,
            ProtectedSource = _secretProtector.Protect(source),
            DisplaySource = CreateDisplaySource(input.SourceKind, source),
            UpdateIntervalMinutes = Math.Clamp(input.UpdateIntervalMinutes, 5, 10080)
        };

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousSettings = _settings;
            _settings = _settings with
            {
                Subscriptions = _settings.Subscriptions.Concat([item]).ToArray()
            };
            await PersistSettingsChangeAsync(
                previousSettings,
                forceRefresh: true,
                cancellationToken).ConfigureAwait(false);
            await _logs.WriteAsync("INFO", $"已添加订阅“{name}”。", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RemoveSubscriptionAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousSettings = _settings;
            _settings = _settings with
            {
                Subscriptions = _settings.Subscriptions.Where(item => item.Id != id).ToArray()
            };
            await PersistSettingsChangeAsync(
                previousSettings,
                forceRefresh: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task AddRuleAsync(CustomRule rule, CancellationToken cancellationToken)
    {
        var normalized = rule with { Value = rule.Value.Trim() };
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousSettings = _settings;
            var validationSettings = _settings with
            {
                Rules = _settings.Rules.Concat([normalized]).ToArray()
            };
            var validation = _validator.Validate(validationSettings, _adapterProvider.GetAdapters());
            var ruleErrors = validation.Errors
                .Where(error => error.Contains("规则", StringComparison.Ordinal)
                    || error.Contains(normalized.Value, StringComparison.Ordinal))
                .ToArray();
            if (ruleErrors.Length > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, ruleErrors));
            }

            _settings = validationSettings;
            await PersistSettingsChangeAsync(
                previousSettings,
                forceRefresh: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RemoveRuleAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousSettings = _settings;
            _settings = _settings with
            {
                Rules = _settings.Rules.Where(item => item.Id != id).ToArray()
            };
            await PersistSettingsChangeAsync(
                previousSettings,
                forceRefresh: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task EnableAsync(CancellationToken cancellationToken)
    {
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var wasEnabled = _settings.Enabled;
        try
        {
            EnsureCoreStartAllowed();
            _settings = _settings with { Enabled = true };
            await ApplyCoreAsync(
                true,
                cancellationToken,
                restartLastKnownGoodOnFailure: wasEnabled).ConfigureAwait(false);
        }
        catch
        {
            _settings = _settings with { Enabled = wasEnabled };
            await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previousSettings = _settings;
        var disabledSettings = _settings with { Enabled = false };
        try
        {
            _status = _status with
            {
                Mode = RuntimeMode.Stopping,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _processManager.StopAsync(disabledSettings, cancellationToken).ConfigureAwait(false);
            _settings = disabledSettings;
            await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            _activeSettings = null;
            UpdateStatus(_adapterProvider.GetAdapters(), null, null);
            await _logs.WriteAsync("INFO", "透明分流已关闭。", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _settings = previousSettings;
            Exception? recoveryException = null;
            try
            {
                if (previousSettings.Enabled && !_processManager.IsRunning)
                {
                    await _processManager.StartAsync(
                        previousSettings,
                        CancellationToken.None).ConfigureAwait(false);
                    _activeSettings = previousSettings;
                }

                await _settingsStore.SaveAsync(
                    previousSettings,
                    CancellationToken.None).ConfigureAwait(false);
                UpdateStatus(_adapterProvider.GetAdapters(), null, null);
            }
            catch (Exception restoreException)
            {
                recoveryException = restoreException;
            }

            if (recoveryException is not null)
            {
                throw new InvalidOperationException(
                    "Disabling net-split failed and restoring the previous state also failed.",
                    new AggregateException(exception, recoveryException));
            }

            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RepairAsync(CancellationToken cancellationToken)
    {
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _processManager.StopAsync(_settings, cancellationToken).ConfigureAwait(false);
            if (_settings.Enabled)
            {
                await ApplyCoreAsync(false, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lastKnownGood = await LoadLastKnownGoodAsync(cancellationToken).ConfigureAwait(false);
            var validation = await _processManager.ValidateAsync(
                lastKnownGood.Settings,
                lastKnownGood.ConfigPath,
                cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException("上次可用配置未通过 Mihomo 校验。");
            }

            var shouldRestart = _settings.Enabled;
            await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            MihomoApiSnapshot? snapshot = null;
            string? snapshotError = null;
            try
            {
                await _processManager.StopAsync(_settings, cancellationToken).ConfigureAwait(false);
                await CopyFileAtomicallyAsync(
                    lastKnownGood.ConfigPath,
                    _paths.RuntimeConfigFile,
                    cancellationToken).ConfigureAwait(false);
                await UpdateTransactionPhaseAsync(
                    TransactionPhase.RuntimeSwapped,
                    cancellationToken).ConfigureAwait(false);
                await RestoreCacheManifestAsync(
                    lastKnownGood.CacheManifestJson,
                    cancellationToken).ConfigureAwait(false);
                await UpdateTransactionPhaseAsync(
                    TransactionPhase.CacheCommitted,
                    cancellationToken).ConfigureAwait(false);
                _settings = lastKnownGood.Settings with { Enabled = shouldRestart };
                await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
                await UpdateTransactionPhaseAsync(
                    TransactionPhase.SettingsCommitted,
                    cancellationToken).ConfigureAwait(false);
                if (shouldRestart)
                {
                    await _processManager.StartAsync(_settings, cancellationToken).ConfigureAwait(false);
                    _activeSettings = _settings;
                    await UpdateTransactionPhaseAsync(
                        TransactionPhase.CoreStarted,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _activeSettings = null;
                }

                await UpdateTransactionPhaseAsync(
                    TransactionPhase.Committed,
                    cancellationToken).ConfigureAwait(false);
                ClearTransactionFiles();
                if (shouldRestart)
                {
                    try
                    {
                        snapshot = await _controller.GetSnapshotAsync(
                            _settings,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (HttpRequestException exception)
                    {
                        snapshotError = exception.Message;
                    }
                    catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        snapshotError = exception.Message;
                    }
                }
            }
            catch (Exception rollbackException)
            {
                var restoreError = await RestoreLastKnownGoodAsync(shouldRestart).ConfigureAwait(false);
                throw new InvalidOperationException(
                    restoreError is null
                        ? "回退配置失败，已恢复回退前的运行状态。"
                        : $"回退配置失败，且恢复回退前状态失败：{restoreError}",
                    rollbackException);
            }

            UpdateStatus(_adapterProvider.GetAdapters(), snapshot, null);
            if (snapshotError is not null)
            {
                SetError(RuntimeMode.CoreUnavailable, snapshotError);
                await _logs.WriteAsync(
                    "WARN",
                    $"回退配置已应用，但首次状态读取失败：{snapshotError}",
                    cancellationToken).ConfigureAwait(false);
            }
            await _logs.WriteAsync(
                "INFO",
                "已恢复上次验证成功的 Mihomo 配置。",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RefreshSubscriptionsAsync(CancellationToken cancellationToken)
    {
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settings.Enabled)
            {
                await ApplyCoreAsync(true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var subscriptions = await _subscriptionLoader.LoadAllAsync(
                    _settings.Subscriptions,
                    true,
                    cancellationToken).ConfigureAwait(false);
                var previousSettings = _settings;
                await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _subscriptionLoader.CommitGenerationAsync(
                        subscriptions,
                        cancellationToken).ConfigureAwait(false);
                    await UpdateTransactionPhaseAsync(
                        TransactionPhase.CacheCommitted,
                        cancellationToken).ConfigureAwait(false);
                    var refreshedIds = subscriptions
                        .Where(item => !item.FromCache)
                        .Select(item => item.Id)
                        .ToHashSet();
                    _settings = _settings with
                    {
                        Subscriptions = _settings.Subscriptions.Select(item =>
                            refreshedIds.Contains(item.Id)
                                ? item with { LastUpdated = DateTimeOffset.UtcNow }
                                : item).ToArray()
                    };
                    await _settingsStore.SaveAsync(_settings, cancellationToken)
                        .ConfigureAwait(false);
                    await UpdateTransactionPhaseAsync(
                        TransactionPhase.SettingsCommitted,
                        cancellationToken).ConfigureAwait(false);
                    await UpdateTransactionPhaseAsync(
                        TransactionPhase.Committed,
                        cancellationToken).ConfigureAwait(false);
                    ClearTransactionFiles();
                }
                catch (Exception exception)
                {
                    try
                    {
                        var journal = await ReadJsonAsync<TransactionJournal>(
                            _paths.TransactionJournalFile,
                            CancellationToken.None).ConfigureAwait(false);
                        await RestoreCacheManifestAsync(
                            journal,
                            CancellationToken.None).ConfigureAwait(false);
                        _settings = previousSettings;
                        await _settingsStore.SaveAsync(
                            previousSettings,
                            CancellationToken.None).ConfigureAwait(false);
                        ClearTransactionFiles();
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            "Subscription refresh failed and its rollback also failed.",
                            new AggregateException(exception, rollbackException));
                    }

                    throw;
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SelectProxyAsync(string name, CancellationToken cancellationToken)
    {
        EnsureReady();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_processManager.IsRunning)
            {
                throw new InvalidOperationException("Mihomo 当前未运行。");
            }

            if (!_status.AvailableProxies.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("所选代理不在当前代理组中。");
            }

            await _controller.SelectProxyAsync(_settings, name, cancellationToken)
                .ConfigureAwait(false);
            _status = _status with
            {
                CurrentProxy = name,
                EffectiveProxy = _settings.ResidentialProxy.Enabled
                    ? MihomoConfigGenerator.ResidentialProxyName
                    : name.Equals(
                        MihomoConfigGenerator.AutoProxyGroupName,
                        StringComparison.Ordinal)
                        ? string.Empty
                        : name,
                ProxyRouteAvailable = _status.ProxyAdapterAvailable,
                ProxyRouteHealthKnown = false,
                ProxyRouteFailure = _status.ProxyAdapterAvailable
                    ? ProxyRouteFailureReason.HealthCheckPending
                    : ProxyRouteFailureReason.ProxyAdapterUnavailable,
                HealthyProxyCount = 0,
                HealthyProxies = [],
                ProxyDelayMilliseconds = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _lastHealthCheck = DateTimeOffset.MinValue;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ProxyDelayBatchResult> MeasureProxyDelaysAsync(
        CancellationToken cancellationToken)
    {
        EnsureReady();
        await _proxyDelayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = _status;
            if (!status.Enabled
                || !_processManager.IsRunning
                || !status.MihomoRunning
                || !status.TunEnabled
                || !status.DnsStatusKnown
                || !status.DnsEnabled)
            {
                throw new InvalidOperationException("Mihomo TUN 与 DNS 就绪后才能测速。");
            }

            if (!status.ProxyAdapterAvailable)
            {
                throw new InvalidOperationException("网卡2当前不可用，无法测试代理节点。");
            }

            var nodeNames = status.AvailableProxies
                .Where(IsMeasurableProxy)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var measuredAt = DateTimeOffset.UtcNow;
            if (nodeNames.Length == 0)
            {
                return new ProxyDelayBatchResult
                {
                    MeasuredAt = measuredAt
                };
            }

            var signature = string.Join(
                "\u001F",
                nodeNames.OrderBy(name => name, StringComparer.Ordinal));
            var generation = Volatile.Read(ref _proxyDelayCacheGeneration);
            var cached = _proxyDelayCache;
            if (cached is not null
                && cached.Generation == generation
                && cached.Signature.Equals(signature, StringComparison.Ordinal)
                && IsProxyDelayCacheFresh(cached.MeasuredAt, measuredAt)
                && generation == Volatile.Read(ref _proxyDelayCacheGeneration))
            {
                return new ProxyDelayBatchResult
                {
                    Results = cached.Results,
                    MeasuredAt = cached.MeasuredAt,
                    FromCache = true
                };
            }

            var settings = _activeSettings ?? _settings;
            using var slots = new SemaphoreSlim(
                ProxyDelayProbeConcurrency,
                ProxyDelayProbeConcurrency);
            var probes = await Task.WhenAll(
                nodeNames.Select(name => MeasureProxyNodeAsync(
                    settings,
                    name,
                    slots,
                    cancellationToken))).ConfigureAwait(false);
            measuredAt = DateTimeOffset.UtcNow;
            var results = probes.Select(probe => new ProxyDelayResult
            {
                Name = probe.Name,
                DelayMilliseconds = probe.Delay,
                MeasuredAt = measuredAt,
                Error = probe.Error
            }).ToArray();

            if (generation == Volatile.Read(ref _proxyDelayCacheGeneration))
            {
                _proxyDelayCache = new ProxyDelayCacheEntry(
                    generation,
                    signature,
                    measuredAt,
                    results);
            }

            var availableCount = results.Count(result => result.DelayMilliseconds.HasValue);
            await _logs.WriteAsync(
                "INFO",
                $"节点批量测速完成：{availableCount}/{results.Length} 个节点可用。",
                cancellationToken).ConfigureAwait(false);
            return new ProxyDelayBatchResult
            {
                Results = results,
                MeasuredAt = measuredAt
            };
        }
        finally
        {
            _proxyDelayGate.Release();
        }
    }

    public async Task MaintainAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var adapters = _adapterProvider.GetAdapters();
            if (!_settings.Enabled)
            {
                UpdateStatus(adapters, null, null);
                return;
            }

            if (!_processManager.IsRunning)
            {
                if (DateTimeOffset.UtcNow >= _nextRestartAttempt)
                {
                    try
                    {
                        await ApplyCoreAsync(false, cancellationToken).ConfigureAwait(false);
                        _restartFailures = 0;
                    }
                    catch (Exception exception)
                    {
                        _restartFailures++;
                        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, _restartFailures)));
                        _nextRestartAttempt = DateTimeOffset.UtcNow + delay;
                        SetError(RuntimeMode.CoreUnavailable, exception.Message);
                        await _logs.WriteAsync(
                            "ERROR",
                            $"Mihomo 自动恢复失败，将在 {delay.TotalSeconds:0} 秒后重试：{exception.Message}",
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                return;
            }

            MihomoApiSnapshot? apiSnapshot = null;
            int? directDelay = null;
            int? proxyDelay = null;
            string? healthError = null;
            var delayProbeErrors = new List<string>();
            var previousHealthError = _status.Mode == RuntimeMode.CoreUnavailable
                ? _status.LastError
                : string.Empty;
            if (DateTimeOffset.UtcNow - _lastHealthCheck
                >= TimeSpan.FromSeconds(Math.Max(10, _settings.HealthCheckSeconds)))
            {
                try
                {
                    apiSnapshot = await _controller.GetSnapshotAsync(_settings, cancellationToken)
                        .ConfigureAwait(false);
                    var proxyProbeName = _settings.ResidentialProxy.Enabled
                        ? MihomoConfigGenerator.ResidentialProxyName
                        : MihomoConfigGenerator.ProxyGroupName;
                    var delayProbes = await Task.WhenAll(
                        MeasureDelayProbeAsync(
                            MihomoConfigGenerator.DirectProxyName,
                            "https://www.baidu.com",
                            cancellationToken),
                        MeasureDelayProbeAsync(
                            proxyProbeName,
                            "https://www.gstatic.com/generate_204",
                            cancellationToken)).ConfigureAwait(false);
                    directDelay = delayProbes[0].Delay;
                    proxyDelay = delayProbes[1].Delay;
                    delayProbeErrors.AddRange(
                        delayProbes
                            .Where(result => !string.IsNullOrWhiteSpace(result.Error))
                            .Select(result => result.Error!));
                    _lastHealthCheck = DateTimeOffset.UtcNow;
                }
                catch (HttpRequestException exception)
                {
                    healthError = exception.Message;
                    _lastHealthCheck = DateTimeOffset.UtcNow;
                }
                catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    healthError = exception.Message;
                    _lastHealthCheck = DateTimeOffset.UtcNow;
                }
            }

            UpdateStatus(adapters, apiSnapshot, (directDelay, proxyDelay));
            if (healthError is not null)
            {
                SetError(RuntimeMode.CoreUnavailable, healthError);
                await _logs.WriteAsync(
                    "WARN",
                    $"Mihomo health check failed: {healthError}",
                    cancellationToken).ConfigureAwait(false);
            }
            else if (apiSnapshot is null && !string.IsNullOrWhiteSpace(previousHealthError))
            {
                SetError(RuntimeMode.CoreUnavailable, previousHealthError);
            }
            foreach (var delayProbeError in delayProbeErrors)
            {
                await _logs.WriteAsync(
                    "WARN",
                    $"Mihomo delay probe failed: {delayProbeError}",
                    cancellationToken).ConfigureAwait(false);
            }
            var directAdapter = Resolve(_settings.DirectAdapter, adapters);
            var proxyAdapter = Resolve(_settings.ProxyAdapter, adapters);
            var directAvailable = directAdapter is { IsSelectable: true };
            var proxyAvailable = proxyAdapter is { IsSelectable: true };
            var adapterRecovered = directAvailable && !_lastDirectAvailable
                || proxyAvailable && !_lastProxyAvailable;
            var interfaceNameChanged =
                directAvailable
                && !directAdapter!.Name.Equals(
                    _appliedDirectAdapterName,
                    StringComparison.OrdinalIgnoreCase)
                || proxyAvailable
                && !proxyAdapter!.Name.Equals(
                    _appliedProxyAdapterName,
                    StringComparison.OrdinalIgnoreCase);
            _lastDirectAvailable = directAvailable;
            _lastProxyAvailable = proxyAvailable;

            if (adapterRecovered || interfaceNameChanged)
            {
                _adapterReapplyDue ??= DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            }

            if (_adapterReapplyDue is not null
                && DateTimeOffset.UtcNow >= _adapterReapplyDue.Value)
            {
                _adapterReapplyDue = null;
                await ApplyCoreAsync(false, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (DateTimeOffset.UtcNow - _lastSubscriptionCheck >= TimeSpan.FromMinutes(1)
                && _settings.Subscriptions.Any(IsSubscriptionDue))
            {
                _lastSubscriptionCheck = DateTimeOffset.UtcNow;
                await ApplyCoreAsync(false, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<DelayProbeResult> MeasureDelayProbeAsync(
        string proxyName,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = await _controller.MeasureDelayAsync(
                _settings,
                proxyName,
                url,
                cancellationToken).ConfigureAwait(false);
            return new DelayProbeResult(delay, null);
        }
        catch (HttpRequestException exception)
        {
            return new DelayProbeResult(null, exception.Message);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new DelayProbeResult(null, exception.Message);
        }
    }

    private async Task<ProxyDelayProbeResult> MeasureProxyNodeAsync(
        SplitRouteSettings settings,
        string proxyName,
        SemaphoreSlim slots,
        CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var delay = await _controller.MeasureDelayAsync(
                    settings,
                    proxyName,
                    "https://www.gstatic.com/generate_204",
                    cancellationToken).ConfigureAwait(false);
                return delay.HasValue
                    ? new ProxyDelayProbeResult(proxyName, delay, string.Empty)
                    : new ProxyDelayProbeResult(proxyName, null, "节点未返回可用延迟。");
            }
            catch (HttpRequestException)
            {
                return new ProxyDelayProbeResult(proxyName, null, "连接失败。");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ProxyDelayProbeResult(proxyName, null, "测速超时。");
            }
        }
        finally
        {
            slots.Release();
        }
    }

    private static bool IsMeasurableProxy(string name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && !name.Equals(
                MihomoConfigGenerator.AutoProxyGroupName,
                StringComparison.Ordinal)
            && !name.Equals(
                MihomoConfigGenerator.ProxyGroupName,
                StringComparison.Ordinal)
            && !name.Equals(
                MihomoConfigGenerator.DirectProxyName,
                StringComparison.Ordinal)
            && !name.Equals(
                MihomoConfigGenerator.ResidentialProxyName,
                StringComparison.Ordinal);
    }

    internal static bool IsProxyDelayCacheFresh(
        DateTimeOffset measuredAt,
        DateTimeOffset currentTime)
    {
        var age = currentTime - measuredAt;
        return age >= TimeSpan.Zero && age < ProxyDelayCacheDuration;
    }

    private async Task ApplyCoreAsync(
        bool forceRefresh,
        CancellationToken cancellationToken,
        bool restartLastKnownGoodOnFailure = true)
    {
        Interlocked.Increment(ref _proxyDelayCacheGeneration);
        EnsureCoreStartAllowed();
        _status = _status with
        {
            Mode = RuntimeMode.Starting,
            Enabled = true,
            MihomoRunning = false,
            TunEnabled = false,
            DnsEnabled = false,
            DnsStatusKnown = false,
            LastError = string.Empty,
            ProxyRouteAvailable = false,
            ProxyRouteHealthKnown = false,
            ProxyRouteFailure = ProxyRouteFailureReason.Starting,
            HealthyProxyCount = 0,
            CurrentProxy = string.Empty,
            EffectiveProxy = string.Empty,
            AvailableProxies = [],
            HealthyProxies = [],
            DirectDelayMilliseconds = null,
            ProxyDelayMilliseconds = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var adapters = _adapterProvider.GetAdapters();
        var validation = _validator.Validate(_settings, adapters);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
        }

        var direct = ResolveForRuntime(_settings.DirectAdapter, adapters, "网卡1");
        var proxy = ResolveForRuntime(_settings.ProxyAdapter, adapters, "网卡2");
        var subscriptions = await _subscriptionLoader.LoadAllAsync(
            _settings.Subscriptions,
            forceRefresh,
            cancellationToken).ConfigureAwait(false);
        var config = MihomoConfigGenerator.Generate(
            subscriptions,
            _settings,
            direct,
            proxy,
            adapters,
            ResolveResidentialProxyCredentials(_settings));
        await WriteConfigFileAsync(
            _paths.CandidateConfigFile,
            config.Yaml,
            cancellationToken).ConfigureAwait(false);

        ProcessValidationResult processValidation;
        try
        {
            processValidation = await _processManager.ValidateAsync(
                _settings,
                _paths.CandidateConfigFile,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteIfExists(_paths.CandidateConfigFile);
            throw;
        }

        if (!processValidation.IsValid)
        {
            DeleteIfExists(_paths.CandidateConfigFile);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(processValidation.Output)
                    ? "Mihomo 配置验证失败。"
                    : processValidation.Output);
        }

        if (_processManager.IsRunning && File.Exists(_paths.RuntimeConfigFile))
        {
            await SaveLastKnownGoodAsync(
                _paths.RuntimeConfigFile,
                _activeSettings ?? _settings,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!File.Exists(_paths.LastKnownGoodManifestFile)
                 && File.Exists(_paths.RuntimeConfigFile))
        {
            var previousValidation = await _processManager.ValidateAsync(
                _settings,
                _paths.RuntimeConfigFile,
                cancellationToken).ConfigureAwait(false);
            if (previousValidation.IsValid)
            {
                await SaveLastKnownGoodAsync(
                    _paths.RuntimeConfigFile,
                    _activeSettings ?? _settings,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var refreshedSubscriptionIds = subscriptions
            .Where(item => !item.FromCache)
            .Select(item => item.Id)
            .ToHashSet();
        try
        {
            File.Move(_paths.CandidateConfigFile, _paths.RuntimeConfigFile, true);
            await UpdateTransactionPhaseAsync(
                TransactionPhase.RuntimeSwapped,
                cancellationToken).ConfigureAwait(false);
            await _processManager.StartAsync(_settings, cancellationToken).ConfigureAwait(false);
            await UpdateTransactionPhaseAsync(
                TransactionPhase.CoreStarted,
                cancellationToken).ConfigureAwait(false);
            await _subscriptionLoader.CommitGenerationAsync(
                subscriptions,
                cancellationToken).ConfigureAwait(false);
            await UpdateTransactionPhaseAsync(
                TransactionPhase.CacheCommitted,
                cancellationToken).ConfigureAwait(false);
            _settings = _settings with
            {
                Enabled = true,
                DirectAdapter = ToBinding(direct),
                ProxyAdapter = ToBinding(proxy),
                Subscriptions = _settings.Subscriptions.Select(item =>
                    item.Enabled && refreshedSubscriptionIds.Contains(item.Id)
                        ? item with { LastUpdated = DateTimeOffset.UtcNow }
                        : item).ToArray()
            };
            await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            await UpdateTransactionPhaseAsync(
                TransactionPhase.SettingsCommitted,
                cancellationToken).ConfigureAwait(false);
            await SaveLastKnownGoodAsync(
                _paths.RuntimeConfigFile,
                _settings,
                cancellationToken).ConfigureAwait(false);
            await UpdateTransactionPhaseAsync(
                TransactionPhase.LkgCommitted,
                cancellationToken).ConfigureAwait(false);
            _activeSettings = _settings;
            await UpdateTransactionPhaseAsync(
                TransactionPhase.Committed,
                cancellationToken).ConfigureAwait(false);
            ClearTransactionFiles();
        }
        catch (Exception startException)
        {
            var rollbackError = await RestoreLastKnownGoodAsync(
                restartLastKnownGoodOnFailure).ConfigureAwait(false);
            var startError = startException.Message.Trim();
            throw new InvalidOperationException(
                rollbackError is null
                    ? $"新配置启动失败：{startError}；已恢复上次可用配置。"
                    : $"新配置启动失败：{startError}；且恢复上次配置失败：{rollbackError}",
                startException);
        }

        await CompleteSuccessfulApplyAsync(
            adapters,
            direct,
            proxy,
            config.Warnings).ConfigureAwait(false);
    }

    private async Task CompleteSuccessfulApplyAsync(
        IReadOnlyList<NetworkAdapterSnapshot> adapters,
        NetworkAdapterSnapshot direct,
        NetworkAdapterSnapshot proxy,
        IReadOnlyList<string> warnings)
    {
        _appliedDirectAdapterName = direct.Name;
        _appliedProxyAdapterName = proxy.Name;
        _lastDirectAvailable = direct.IsSelectable;
        _lastProxyAvailable = proxy.IsSelectable;
        _adapterReapplyDue = null;

        if (warnings.Count > 0)
        {
            await WritePostCommitLogAsync(
                "WARN",
                SummarizeWarnings(warnings)).ConfigureAwait(false);
        }

        MihomoApiSnapshot? snapshot = null;
        string? snapshotError = null;
        try
        {
            using var snapshotTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            snapshot = await _controller.GetSnapshotAsync(
                _settings,
                snapshotTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            snapshotError = exception.Message;
        }

        try
        {
            UpdateStatus(adapters, snapshot, null);
            if (snapshotError is not null)
            {
                SetError(RuntimeMode.CoreUnavailable, snapshotError);
            }
        }
        catch (Exception exception)
        {
            snapshotError = string.IsNullOrWhiteSpace(snapshotError)
                ? exception.Message
                : $"{snapshotError}；状态更新失败：{exception.Message}";
            _status = _status with
            {
                Mode = RuntimeMode.Starting,
                Enabled = true,
                MihomoRunning = _processManager.IsRunning,
                TunEnabled = false,
                DnsEnabled = false,
                DnsStatusKnown = false,
                ProxyRouteAvailable = false,
                ProxyRouteHealthKnown = false,
                ProxyRouteFailure = ProxyRouteFailureReason.Starting,
                LastError = string.Empty,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        if (snapshotError is not null)
        {
            await WritePostCommitLogAsync(
                "WARN",
                $"Mihomo 已启动，但首次状态读取失败：{snapshotError}").ConfigureAwait(false);
        }

        await WritePostCommitLogAsync(
            "INFO",
            "Mihomo 已通过验证并启动透明分流。").ConfigureAwait(false);
    }

    private async Task WritePostCommitLogAsync(string level, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writeTask = _logs.WriteAsync(level, message, timeout.Token);
        try
        {
            await writeTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            _ = writeTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    private static string SummarizeWarnings(IReadOnlyList<string> warnings)
    {
        const int maximumWarnings = 20;
        const int maximumCharacters = 4000;
        var summary = string.Join("；", warnings.Take(maximumWarnings));
        if (warnings.Count > maximumWarnings)
        {
            summary += $"；另有 {warnings.Count - maximumWarnings} 条警告已省略";
        }

        return summary.Length <= maximumCharacters
            ? summary
            : summary[..(maximumCharacters - 1)] + "…";
    }

    private void UpdateStatus(
        IReadOnlyList<NetworkAdapterSnapshot> adapters,
        MihomoApiSnapshot? apiSnapshot,
        (int? Direct, int? Proxy)? delays)
    {
        var direct = Resolve(_settings.DirectAdapter, adapters);
        var proxy = Resolve(_settings.ProxyAdapter, adapters);
        var directAvailable = direct is { IsSelectable: true };
        var proxyAvailable = proxy is { IsSelectable: true };
        var mihomoRunning = _processManager.IsRunning;
        var coreExpected = _settings.Enabled && mihomoRunning;
        var canReuseCoreStatus = coreExpected && _status.MihomoRunning;
        var tunEnabled = coreExpected
            && (apiSnapshot?.TunEnabled
                ?? (canReuseCoreStatus && _status.TunEnabled));
        var dnsStatusKnown = !_settings.Enabled
            || (mihomoRunning
                && (apiSnapshot is not null
                    || (canReuseCoreStatus && _status.DnsStatusKnown)));
        var dnsEnabled = coreExpected
            && (apiSnapshot?.DnsEnabled
                ?? (canReuseCoreStatus && _status.DnsEnabled));
        var coreTrafficReady = coreExpected
            && tunEnabled
            && dnsStatusKnown
            && dnsEnabled;
        var proxyCoreAvailable = coreTrafficReady && proxyAvailable;
        var selectedProxyHealthy = proxyCoreAvailable
            ? apiSnapshot?.SelectedProxyHealthy
            : null;
        var proxyRouteHealthKnown = proxyCoreAvailable
            && (selectedProxyHealthy.HasValue || _status.ProxyRouteHealthKnown);
        var proxyRouteAvailable = proxyCoreAvailable
            && (selectedProxyHealthy
                ?? (_status.ProxyRouteHealthKnown
                    ? _status.ProxyRouteAvailable
                    : proxyAvailable));
        proxyRouteAvailable &= proxyAvailable;
        var mode = !_settings.Enabled
            ? RuntimeMode.Disabled
            : !mihomoRunning
                || !coreTrafficReady
                ? RuntimeMode.CoreUnavailable
                : !directAvailable
                    ? RuntimeMode.DirectUnavailable
                    : !proxyRouteAvailable
                        ? RuntimeMode.ProxyUnavailable
                        : RuntimeMode.Healthy;
        var proxyRouteFailure = ResolveProxyRouteFailure(
            coreTrafficReady,
            proxyAvailable,
            proxyRouteAvailable,
            proxyRouteHealthKnown);
        var coreReadinessError = apiSnapshot is null
            ? string.Empty
            : !tunEnabled
                ? "Mihomo TUN 未就绪。"
                : !dnsEnabled
                    ? "Mihomo DNS 未就绪。"
                    : string.Empty;

        _status = new RuntimeStatus
        {
            Mode = mode,
            Enabled = _settings.Enabled,
            MihomoRunning = mihomoRunning,
            TunEnabled = tunEnabled,
            DnsEnabled = dnsEnabled,
            DnsStatusKnown = dnsStatusKnown,
            DirectAdapterAvailable = directAvailable,
            ProxyAdapterAvailable = proxyAvailable,
            ProxyRouteAvailable = _settings.Enabled
                && mihomoRunning
                && proxyRouteAvailable,
            ProxyRouteHealthKnown = proxyRouteHealthKnown,
            ProxyRouteFailure = proxyRouteFailure,
            HealthyProxyCount = proxyCoreAvailable
                ? apiSnapshot is not null
                    ? apiSnapshot.HealthyProxies.Count
                    : _status.HealthyProxyCount
                : 0,
            DirectAdapterName = direct?.Name ?? _settings.DirectAdapter?.LastKnownName ?? string.Empty,
            ProxyAdapterName = proxy?.Name ?? _settings.ProxyAdapter?.LastKnownName ?? string.Empty,
            DirectTraffic = CalculateTraffic(direct),
            ProxyTraffic = CalculateTraffic(proxy),
            CurrentProxy = mihomoRunning
                ? apiSnapshot?.CurrentProxy ?? _status.CurrentProxy
                : string.Empty,
            EffectiveProxy = mihomoRunning
                ? apiSnapshot?.EffectiveProxy ?? _status.EffectiveProxy
                : string.Empty,
            AvailableProxies = mihomoRunning
                ? apiSnapshot?.AvailableProxies ?? _status.AvailableProxies
                : [],
            HealthyProxies = proxyCoreAvailable
                ? apiSnapshot is not null
                    ? apiSnapshot.HealthyProxies
                    : _status.HealthyProxies
                : [],
            DirectDelayMilliseconds = delays?.Direct ?? _status.DirectDelayMilliseconds,
            ProxyDelayMilliseconds = delays?.Proxy ?? _status.ProxyDelayMilliseconds,
            LastError = mode is RuntimeMode.Healthy or RuntimeMode.Disabled
                ? string.Empty
                : !string.IsNullOrWhiteSpace(coreReadinessError)
                    ? coreReadinessError
                    : _status.LastError,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (!IsTrafficHistorySampleDue(
                _lastTrafficHistorySampleAt,
                _status.UpdatedAt))
        {
            return;
        }

        _trafficHistory.Add(new TrafficPoint
        {
            Timestamp = _status.UpdatedAt,
            DirectReceiveBps = _status.DirectTraffic.ReceiveBytesPerSecond,
            DirectSendBps = _status.DirectTraffic.SendBytesPerSecond,
            ProxyReceiveBps = _status.ProxyTraffic.ReceiveBytesPerSecond,
            ProxySendBps = _status.ProxyTraffic.SendBytesPerSecond,
        });
        _lastTrafficHistorySampleAt = _status.UpdatedAt;
    }

    internal static bool IsTrafficHistorySampleDue(
        DateTimeOffset lastSampleAt,
        DateTimeOffset currentSampleAt)
    {
        return lastSampleAt == DateTimeOffset.MinValue
            || currentSampleAt - lastSampleAt >= TrafficHistorySampleInterval;
    }

    private ProxyRouteFailureReason ResolveProxyRouteFailure(
        bool coreAvailable,
        bool proxyAdapterAvailable,
        bool proxyRouteAvailable,
        bool proxyRouteHealthKnown)
    {
        if (!_settings.Enabled)
        {
            return ProxyRouteFailureReason.None;
        }

        if (!coreAvailable)
        {
            return ProxyRouteFailureReason.CoreUnavailable;
        }

        if (!proxyAdapterAvailable)
        {
            return ProxyRouteFailureReason.ProxyAdapterUnavailable;
        }

        if (!proxyRouteHealthKnown)
        {
            return ProxyRouteFailureReason.HealthCheckPending;
        }

        if (proxyRouteAvailable)
        {
            return ProxyRouteFailureReason.None;
        }

        return _settings.ResidentialProxy.Enabled
            ? ProxyRouteFailureReason.ResidentialProxyUnavailable
            : ProxyRouteFailureReason.NoHealthyProxy;
    }

    private AdapterTraffic CalculateTraffic(NetworkAdapterSnapshot? adapter)
    {
        if (adapter is null)
        {
            return new AdapterTraffic();
        }

        var now = DateTimeOffset.UtcNow;
        if (!_trafficSamples.TryGetValue(adapter.Id, out var previous))
        {
            _trafficSamples[adapter.Id] = new TrafficSample(
                adapter.BytesReceived,
                adapter.BytesSent,
                now);
            return new AdapterTraffic();
        }

        var seconds = Math.Max(0.1, (now - previous.Timestamp).TotalSeconds);
        var received = Math.Max(0, adapter.BytesReceived - previous.BytesReceived);
        var sent = Math.Max(0, adapter.BytesSent - previous.BytesSent);
        _trafficSamples[adapter.Id] = new TrafficSample(adapter.BytesReceived, adapter.BytesSent, now);
        return new AdapterTraffic
        {
            ReceiveBytesPerSecond = (long)(received / seconds),
            SendBytesPerSecond = (long)(sent / seconds)
        };
    }

    private void SetError(RuntimeMode mode, string error)
    {
        _status = _status with
        {
            Mode = mode,
            LastError = error,
            ProxyRouteFailure = mode switch
            {
                RuntimeMode.CoreUnavailable when _status.MihomoRunning =>
                    ProxyRouteFailureReason.ControllerUnavailable,
                RuntimeMode.CoreUnavailable => ProxyRouteFailureReason.CoreUnavailable,
                RuntimeMode.Misconfigured => ProxyRouteFailureReason.ConfigurationInvalid,
                _ => _status.ProxyRouteFailure
            },
            ProxyRouteAvailable = mode is not RuntimeMode.CoreUnavailable
                and not RuntimeMode.Misconfigured
                && _status.ProxyRouteAvailable,
            ProxyRouteHealthKnown = mode is not RuntimeMode.CoreUnavailable
                and not RuntimeMode.Misconfigured
                && _status.ProxyRouteHealthKnown,
            DnsEnabled = false,
            DnsStatusKnown = false,
            HealthyProxyCount = mode is RuntimeMode.CoreUnavailable
                or RuntimeMode.Misconfigured
                ? 0
                : _status.HealthyProxyCount,
            CurrentProxy = mode is RuntimeMode.CoreUnavailable
                or RuntimeMode.Misconfigured
                ? string.Empty
                : _status.CurrentProxy,
            EffectiveProxy = mode is RuntimeMode.CoreUnavailable
                or RuntimeMode.Misconfigured
                ? string.Empty
                : _status.EffectiveProxy,
            AvailableProxies = mode is RuntimeMode.CoreUnavailable
                or RuntimeMode.Misconfigured
                ? Array.Empty<string>()
                : _status.AvailableProxies,
            HealthyProxies = mode is RuntimeMode.CoreUnavailable
                or RuntimeMode.Misconfigured
                ? Array.Empty<string>()
                : _status.HealthyProxies,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var runtimeConfigExists = File.Exists(_paths.RuntimeConfigFile);
        try
        {
            if (runtimeConfigExists)
            {
                await CopyFileAtomicallyAsync(
                    _paths.RuntimeConfigFile,
                    _paths.TransactionRuntimeBackupFile,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                DeleteIfExists(_paths.TransactionRuntimeBackupFile);
            }

            var journal = new TransactionJournal
            {
                PreviousSettingsJson = await ReadTextIfExistsAsync(
                    _paths.SettingsFile,
                    cancellationToken).ConfigureAwait(false),
                PreviousLkgManifestJson = await ReadTextIfExistsAsync(
                    _paths.LastKnownGoodManifestFile,
                    cancellationToken).ConfigureAwait(false),
                PreviousCacheManifestJson = await ReadTextIfExistsAsync(
                    _paths.CacheManifestFile,
                    cancellationToken).ConfigureAwait(false),
                PreviousRuntimeConfigExisted = runtimeConfigExists,
                PreviousRuntimeConfigSha256 = runtimeConfigExists
                    ? ComputeSha256(_paths.TransactionRuntimeBackupFile)
                    : null
            };
            _ = await ValidatePreviousTransactionStateAsync(
                journal,
                cancellationToken).ConfigureAwait(false);
            await WriteJsonAtomicallyAsync(
                _paths.TransactionJournalFile,
                journal,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteIfExists(_paths.TransactionRuntimeBackupFile);
            throw;
        }
    }

    private async Task UpdateTransactionPhaseAsync(
        TransactionPhase phase,
        CancellationToken cancellationToken)
    {
        var journal = await ReadJsonAsync<TransactionJournal>(
            _paths.TransactionJournalFile,
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAtomicallyAsync(
            _paths.TransactionJournalFile,
            journal with { Phase = phase },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RecoverInterruptedTransactionAsync(
        bool startupDisableActive,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.TransactionJournalFile))
        {
            return false;
        }

        var journal = await ReadJsonAsync<TransactionJournal>(
            _paths.TransactionJournalFile,
            cancellationToken).ConfigureAwait(false);
        var recovery = await ValidatePreviousTransactionStateAsync(
            journal,
            cancellationToken).ConfigureAwait(false);
        if (!startupDisableActive
            && journal.Phase == TransactionPhase.Committed
            && await TryResumeCommittedTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        await RestoreLkgManifestAsync(journal, cancellationToken).ConfigureAwait(false);
        await RestoreCacheManifestAsync(journal, cancellationToken).ConfigureAwait(false);
        var shouldRestart = !startupDisableActive
            && (recovery.PreviousSettings?.Enabled ?? _settings.Enabled);
        await _processManager.StopAsync(_settings, CancellationToken.None).ConfigureAwait(false);
        var runtimeConfigRestored = await RestorePreviousRuntimeConfigAsync(
            recovery,
            cancellationToken).ConfigureAwait(false);
        shouldRestart &= runtimeConfigRestored;
        _settings = DiscoverRuntimeDefaults(
            (recovery.PreviousSettings ?? recovery.LastKnownGood?.Settings ?? _settings)
                with
            { Enabled = shouldRestart });
        await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        MihomoApiSnapshot? snapshot = null;
        if (shouldRestart)
        {
            var validation = await _processManager.ValidateAsync(
                _settings,
                _paths.RuntimeConfigFile,
                cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException("事务恢复配置未通过 Mihomo 校验。");
            }

            await _processManager.StartAsync(_settings, cancellationToken).ConfigureAwait(false);
            _activeSettings = _settings;
            try
            {
                snapshot = await _controller.GetSnapshotAsync(_settings, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }
        }
        else
        {
            _activeSettings = null;
        }

        ClearTransactionFiles();
        UpdateStatus(_adapterProvider.GetAdapters(), snapshot, null);
        return true;
    }

    private async Task<bool> TryResumeCommittedTransactionAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_paths.CacheManifestFile))
            {
                ValidateCacheManifest(
                    await File.ReadAllTextAsync(
                        _paths.CacheManifestFile,
                        cancellationToken).ConfigureAwait(false));
            }

            MihomoApiSnapshot? snapshot = null;
            if (!_settings.Enabled)
            {
                await _processManager.StopAsync(_settings, CancellationToken.None)
                    .ConfigureAwait(false);
                _activeSettings = null;
            }
            else
            {
                if (!File.Exists(_paths.RuntimeConfigFile)
                    || !File.Exists(_paths.LastKnownGoodManifestFile))
                {
                    return false;
                }

                var currentLastKnownGood = await LoadLastKnownGoodAsync(
                    cancellationToken).ConfigureAwait(false);
                if (!ComputeSha256(_paths.RuntimeConfigFile).Equals(
                        ComputeSha256(currentLastKnownGood.ConfigPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var currentCacheManifestJson = await ReadTextIfExistsAsync(
                    _paths.CacheManifestFile,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                        currentCacheManifestJson,
                        currentLastKnownGood.CacheManifestJson,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var validation = await _processManager.ValidateAsync(
                    _settings,
                    _paths.RuntimeConfigFile,
                    cancellationToken).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    return false;
                }

                await _processManager.StopAsync(_settings, CancellationToken.None)
                    .ConfigureAwait(false);
                await _processManager.StartAsync(_settings, cancellationToken)
                    .ConfigureAwait(false);
                _activeSettings = _settings;
                try
                {
                    snapshot = await _controller.GetSnapshotAsync(
                        _settings,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                }
            }

            ClearTransactionFiles();
            UpdateStatus(_adapterProvider.GetAdapters(), snapshot, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> RestoreLastKnownGoodAsync(bool restartCore)
    {
        try
        {
            var journal = await ReadJsonAsync<TransactionJournal>(
                _paths.TransactionJournalFile,
                CancellationToken.None).ConfigureAwait(false);
            var recovery = await ValidatePreviousTransactionStateAsync(
                journal,
                CancellationToken.None).ConfigureAwait(false);
            await _processManager.StopAsync(_settings, CancellationToken.None).ConfigureAwait(false);
            await RestoreLkgManifestAsync(journal, CancellationToken.None).ConfigureAwait(false);
            await RestoreCacheManifestAsync(journal, CancellationToken.None).ConfigureAwait(false);
            var runtimeConfigRestored = await RestorePreviousRuntimeConfigAsync(
                recovery,
                CancellationToken.None).ConfigureAwait(false);
            var enabled = recovery.PreviousSettings?.Enabled ?? _settings.Enabled;
            _settings = (recovery.PreviousSettings ?? recovery.LastKnownGood?.Settings ?? _settings)
                with
            { Enabled = enabled && runtimeConfigRestored };
            await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(false);
            if (restartCore && _settings.Enabled)
            {
                await _processManager.StartAsync(_settings, CancellationToken.None).ConfigureAwait(false);
                _activeSettings = _settings;
            }
            else
            {
                _activeSettings = null;
            }

            ClearTransactionFiles();
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private async Task<bool> RestorePreviousRuntimeConfigAsync(
        TransactionRecoveryState recovery,
        CancellationToken cancellationToken)
    {
        var sourcePath = recovery.PreviousRuntimeConfigExisted switch
        {
            true => recovery.PreviousRuntimeConfigPath,
            false => null,
            null => recovery.LastKnownGood?.ConfigPath
        };
        if (sourcePath is null)
        {
            DeleteIfExists(_paths.RuntimeConfigFile);
            return false;
        }

        await CopyFileAtomicallyAsync(
            sourcePath,
            _paths.RuntimeConfigFile,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RestoreCacheManifestAsync(
        TransactionJournal journal,
        CancellationToken cancellationToken)
    {
        await RestoreCacheManifestAsync(
            journal.PreviousCacheManifestJson,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreCacheManifestAsync(
        string? manifestJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            DeleteIfExists(_paths.CacheManifestFile);
            return;
        }

        ValidateCacheManifest(manifestJson);
        await WriteTextAtomicallyAsync(
            _paths.CacheManifestFile,
            manifestJson,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransactionRecoveryState> ValidatePreviousTransactionStateAsync(
        TransactionJournal journal,
        CancellationToken cancellationToken)
    {
        ValidateCacheManifest(journal.PreviousCacheManifestJson);
        SplitRouteSettings? previousSettings = null;
        if (!string.IsNullOrWhiteSpace(journal.PreviousSettingsJson))
        {
            previousSettings = DeserializeJson<SplitRouteSettings>(
                journal.PreviousSettingsJson);
        }

        string? previousRuntimeConfigPath = null;
        if (journal.PreviousRuntimeConfigExisted is true)
        {
            if (string.IsNullOrWhiteSpace(journal.PreviousRuntimeConfigSha256)
                || !File.Exists(_paths.TransactionRuntimeBackupFile)
                || !ComputeSha256(_paths.TransactionRuntimeBackupFile).Equals(
                    journal.PreviousRuntimeConfigSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("事务中的上一份运行配置完整性校验失败。");
            }

            previousRuntimeConfigPath = _paths.TransactionRuntimeBackupFile;
        }
        else if (journal.PreviousRuntimeConfigExisted is false
                 && !string.IsNullOrWhiteSpace(journal.PreviousRuntimeConfigSha256))
        {
            throw new InvalidDataException("事务中的运行配置元数据不一致。");
        }

        if (string.IsNullOrWhiteSpace(journal.PreviousLkgManifestJson))
        {
            return new TransactionRecoveryState(
                null,
                previousSettings,
                previousRuntimeConfigPath,
                journal.PreviousRuntimeConfigExisted);
        }

        var manifest = DeserializeJson<LastKnownGoodManifest>(
            journal.PreviousLkgManifestJson);
        var lastKnownGood = await LoadLastKnownGoodAsync(
            manifest,
            journal.PreviousCacheManifestJson,
            cancellationToken).ConfigureAwait(false);
        return new TransactionRecoveryState(
            lastKnownGood,
            previousSettings,
            previousRuntimeConfigPath,
            journal.PreviousRuntimeConfigExisted);
    }

    private void ValidateCacheManifest(string? manifestJson)
    {
        if (!string.IsNullOrWhiteSpace(manifestJson))
        {
            SubscriptionLoader.ValidateManifest(
                manifestJson,
                _paths.CacheDirectory);
        }
    }

    private async Task RestoreLkgManifestAsync(
        TransactionJournal journal,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(journal.PreviousLkgManifestJson))
        {
            DeleteIfExists(_paths.LastKnownGoodManifestFile);
            return;
        }

        await WriteTextAtomicallyAsync(
            _paths.LastKnownGoodManifestFile,
            journal.PreviousLkgManifestJson,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveLastKnownGoodAsync(
        string configPath,
        SplitRouteSettings settings,
        CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var cacheManifestJson = await ReadTextIfExistsAsync(
            _paths.CacheManifestFile,
            cancellationToken).ConfigureAwait(false);
        var cacheGeneration = SubscriptionLoader.ReadGeneration(
            cacheManifestJson,
            _paths.CacheDirectory);
        var generation = Guid.NewGuid().ToString("N");
        var configFileName = $"config-{generation}.yaml";
        var settingsFileName = $"settings-{generation}.json";
        var cacheManifestFileName = string.IsNullOrWhiteSpace(cacheManifestJson)
            ? null
            : $"cache-{generation}.json";
        var generationConfigPath = Path.Combine(
            _paths.LastKnownGoodDirectory,
            configFileName);
        var generationSettingsPath = Path.Combine(
            _paths.LastKnownGoodDirectory,
            settingsFileName);
        var generationCacheManifestPath = cacheManifestFileName is null
            ? null
            : Path.Combine(_paths.LastKnownGoodDirectory, cacheManifestFileName);
        await CopyFileAtomicallyAsync(
            configPath,
            generationConfigPath,
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAtomicallyAsync(
            generationSettingsPath,
            settings,
            cancellationToken).ConfigureAwait(false);
        if (generationCacheManifestPath is not null)
        {
            await WriteTextAtomicallyAsync(
                generationCacheManifestPath,
                cacheManifestJson!,
                cancellationToken).ConfigureAwait(false);
        }

        var manifest = new LastKnownGoodManifest
        {
            Generation = generation,
            ConfigFileName = configFileName,
            SettingsFileName = settingsFileName,
            ConfigSha256 = ComputeSha256(generationConfigPath),
            SettingsSha256 = ComputeSha256(generationSettingsPath),
            CacheGeneration = cacheGeneration,
            CacheManifestFileName = cacheManifestFileName,
            CacheManifestSha256 = generationCacheManifestPath is null
                ? null
                : ComputeSha256(generationCacheManifestPath)
        };
        await WriteJsonAtomicallyAsync(
            _paths.LastKnownGoodManifestFile,
            manifest,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LastKnownGoodState> LoadLastKnownGoodAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.LastKnownGoodManifestFile))
        {
            throw new InvalidOperationException("没有完整的上次可用配置与设置快照。");
        }

        var manifest = await ReadJsonAsync<LastKnownGoodManifest>(
            _paths.LastKnownGoodManifestFile,
            cancellationToken).ConfigureAwait(false);
        var currentCacheManifestJson = await ReadTextIfExistsAsync(
            _paths.CacheManifestFile,
            cancellationToken).ConfigureAwait(false);
        return await LoadLastKnownGoodAsync(
            manifest,
            currentCacheManifestJson,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LastKnownGoodState> LoadLastKnownGoodAsync(
        LastKnownGoodManifest manifest,
        string? legacyCacheManifestJson,
        CancellationToken cancellationToken)
    {
        var configPath = ResolveLastKnownGoodFile(manifest.ConfigFileName);
        var settingsPath = ResolveLastKnownGoodFile(manifest.SettingsFileName);
        if (!ComputeSha256(configPath).Equals(
                manifest.ConfigSha256,
                StringComparison.OrdinalIgnoreCase)
            || !ComputeSha256(settingsPath).Equals(
                manifest.SettingsSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("上次可用配置快照的完整性校验失败。");
        }

        var settings = await ReadJsonAsync<SplitRouteSettings>(
            settingsPath,
            cancellationToken).ConfigureAwait(false);
        var cacheManifestJson = await LoadLastKnownGoodCacheManifestAsync(
            manifest,
            legacyCacheManifestJson,
            cancellationToken).ConfigureAwait(false);
        return new LastKnownGoodState(configPath, settings, cacheManifestJson);
    }

    private async Task<string?> LoadLastKnownGoodCacheManifestAsync(
        LastKnownGoodManifest manifest,
        string? legacyCacheManifestJson,
        CancellationToken cancellationToken)
    {
        var hasCacheManifestFile = !string.IsNullOrWhiteSpace(manifest.CacheManifestFileName);
        var hasCacheManifestHash = !string.IsNullOrWhiteSpace(manifest.CacheManifestSha256);
        if (hasCacheManifestFile != hasCacheManifestHash)
        {
            throw new InvalidDataException("上次可用快照的订阅缓存清单元数据不完整。");
        }

        if (hasCacheManifestFile)
        {
            string cacheManifestPath;
            try
            {
                cacheManifestPath = ResolveLastKnownGoodFile(manifest.CacheManifestFileName!);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    "上次可用快照引用的订阅缓存清单不存在或无效。",
                    exception);
            }

            if (!ComputeSha256(cacheManifestPath).Equals(
                    manifest.CacheManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("上次可用快照的订阅缓存清单完整性校验失败。");
            }

            var cacheManifestJson = await File.ReadAllTextAsync(
                cacheManifestPath,
                cancellationToken).ConfigureAwait(false);
            var cacheGeneration = SubscriptionLoader.ReadGeneration(
                cacheManifestJson,
                _paths.CacheDirectory);
            if (string.IsNullOrWhiteSpace(manifest.CacheGeneration)
                || !string.Equals(
                    manifest.CacheGeneration,
                    cacheGeneration,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("上次可用快照与订阅缓存代际不一致。");
            }

            return cacheManifestJson;
        }

        if (string.IsNullOrWhiteSpace(manifest.CacheGeneration))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(legacyCacheManifestJson))
        {
            try
            {
                var activeGeneration = SubscriptionLoader.ReadGeneration(
                    legacyCacheManifestJson,
                    _paths.CacheDirectory);
                if (string.Equals(
                        manifest.CacheGeneration,
                        activeGeneration,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return legacyCacheManifestJson;
                }
            }
            catch (InvalidDataException)
            {
            }
        }

        return ReconstructLegacyCacheManifest(manifest.CacheGeneration);
    }

    private string ReconstructLegacyCacheManifest(string generation)
    {
        if (generation.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !Path.GetFileName(generation).Equals(generation, StringComparison.Ordinal))
        {
            throw new InvalidDataException("旧版上次可用快照包含无效的订阅缓存代际。");
        }

        var generationDirectory = Path.GetFullPath(
            Path.Combine(_paths.CacheGenerationsDirectory, generation));
        var generationsRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_paths.CacheGenerationsDirectory)) + Path.DirectorySeparatorChar;
        if (!generationDirectory.StartsWith(generationsRoot, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(generationDirectory))
        {
            throw new InvalidDataException(
                "旧版上次可用快照缺少对应的订阅缓存清单。请先成功启用一次以生成新版快照。");
        }

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(
                     generationDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            var subscriptionId = Path.GetFileNameWithoutExtension(fileName);
            if (!Path.GetExtension(fileName).Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(subscriptionId, "N", out _))
            {
                throw new InvalidDataException(
                    "旧版订阅缓存代际包含无法安全重建的文件。请先成功启用一次以生成新版快照。");
            }

            entries[subscriptionId] = Path.GetRelativePath(_paths.CacheDirectory, path);
        }

        var cacheManifestJson = JsonSerializer.Serialize(
            new SubscriptionCacheManifest
            {
                Generation = generation,
                Entries = entries
            },
            JsonDefaults.Create());
        ValidateCacheManifest(cacheManifestJson);
        return cacheManifestJson;
    }

    private string ResolveLastKnownGoodFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("上次可用配置清单包含无效文件名。");
        }

        var path = Path.GetFullPath(Path.Combine(_paths.LastKnownGoodDirectory, fileName));
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_paths.LastKnownGoodDirectory)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new InvalidDataException("上次可用配置清单引用的文件不存在。");
        }

        return path;
    }

    private async Task PersistSettingsChangeAsync(
        SplitRouteSettings previousSettings,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_settings.Enabled)
            {
                await ApplyCoreAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _settings = previousSettings;
            await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(false);
            UpdateStatus(_adapterProvider.GetAdapters(), null, null);
            throw;
        }
    }

    private static async Task<DiagnosticsFileSnapshot> DescribeFileAsync(
        string name,
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new DiagnosticsFileSnapshot
            {
                Name = name,
                Exists = false
            };
        }

        try
        {
            var info = new FileInfo(path);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            var hash = await SHA256.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false);
            return new DiagnosticsFileSnapshot
            {
                Name = name,
                Exists = true,
                Length = info.Length,
                LastWriteTimeUtc = new DateTimeOffset(
                    info.LastWriteTimeUtc,
                    TimeSpan.Zero),
                Sha256 = Convert.ToHexString(hash).ToLowerInvariant()
            };
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return new DiagnosticsFileSnapshot
            {
                Name = name,
                Exists = true,
                Error = exception.GetType().Name
            };
        }
    }

    private async Task WriteConfigFileAsync(
        string path,
        string yaml,
        CancellationToken cancellationToken)
    {
        await WriteTextAtomicallyAsync(path, yaml, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteTextAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        await AtomicFile.WriteAsync(
            path,
            new UTF8Encoding(false).GetBytes(content),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadTextIfExistsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static T DeserializeJson<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonDefaults.Create())
            ?? throw new InvalidDataException($"{typeof(T).Name} 内容无效。");
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await AtomicFile.WriteAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Create()),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        return await JsonSerializer.DeserializeAsync<T>(
            stream,
            JsonDefaults.Create(),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} 内容无效。");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task CopyFileAtomicallyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await AtomicFile.CopyAsync(
            source,
            destination,
            cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void ClearTransactionFiles()
    {
        DeleteIfExists(_paths.TransactionJournalFile);
        DeleteIfExists(_paths.TransactionRuntimeBackupFile);
    }

    private void EnsureReady()
    {
        if (!IsReady)
        {
            throw new InvalidOperationException(
                "net-split service initialization has not completed.");
        }
    }

    private void EnsureCoreStartAllowed()
    {
        if (File.Exists(_paths.StartupDisableMarkerFile))
        {
            throw new InvalidOperationException(
                "安装或恢复保护正在生效，分流保持关闭；请等待安装或恢复完成后重试。");
        }
    }

    private static AdapterBinding ToBinding(NetworkAdapterSnapshot adapter)
    {
        return new AdapterBinding
        {
            Id = adapter.Id,
            MacAddress = adapter.MacAddress,
            LastKnownName = adapter.Name
        };
    }

    private static NetworkAdapterSnapshot ResolveForRuntime(
        AdapterBinding? binding,
        IReadOnlyList<NetworkAdapterSnapshot> adapters,
        string displayName)
    {
        var resolved = Resolve(binding, adapters);
        if (resolved is not null)
        {
            return resolved;
        }

        if (binding is null || string.IsNullOrWhiteSpace(binding.LastKnownName))
        {
            throw new InvalidOperationException($"找不到{displayName}。");
        }

        return new NetworkAdapterSnapshot
        {
            Id = binding.Id,
            Name = binding.LastKnownName,
            Description = $"{displayName}（当前离线）",
            MacAddress = binding.MacAddress
        };
    }

    private static NetworkAdapterSnapshot? Resolve(
        AdapterBinding? binding,
        IReadOnlyList<NetworkAdapterSnapshot> adapters)
    {
        if (binding is null)
        {
            return null;
        }

        return adapters.FirstOrDefault(item => item.Id.Equals(binding.Id, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(binding.MacAddress)
                && item.MacAddress.Equals(binding.MacAddress, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateDisplaySource(SubscriptionSourceKind sourceKind, string source)
    {
        if (sourceKind == SubscriptionSourceKind.File)
        {
            return Path.GetFileName(source);
        }

        return Uri.TryCreate(source, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}/..."
            : "HTTPS 订阅";
    }

    private ResidentialProxyCredentials? ResolveResidentialProxyCredentials(
        SplitRouteSettings settings)
    {
        var residentialProxy = settings.ResidentialProxy;
        if (!residentialProxy.Enabled || !residentialProxy.AuthenticationEnabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(residentialProxy.ProtectedUsername)
            || string.IsNullOrWhiteSpace(residentialProxy.ProtectedPassword))
        {
            throw new InvalidOperationException("住宅代理凭据不完整，请重新保存。");
        }

        try
        {
            return new ResidentialProxyCredentials(
                _secretProtector.Unprotect(residentialProxy.ProtectedUsername),
                _secretProtector.Unprotect(residentialProxy.ProtectedPassword));
        }
        catch (Exception exception) when (
            exception is FormatException
                or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException("住宅代理凭据无法解密，请重新输入。", exception);
        }
    }

    private static void EnsureResidentialProxyCanBeEnabled(
        ResidentialProxySettings residentialProxy)
    {
        if (string.IsNullOrWhiteSpace(residentialProxy.Host))
        {
            throw new InvalidOperationException("启用住宅代理前请先完成住宅 SOCKS5 配置。");
        }

        _ = ResidentialProxyValidator.NormalizeHost(residentialProxy.Host);
        ResidentialProxyValidator.ValidatePort(residentialProxy.Port);
        if (residentialProxy.AuthenticationEnabled
            && (string.IsNullOrWhiteSpace(residentialProxy.ProtectedUsername)
                || string.IsNullOrWhiteSpace(residentialProxy.ProtectedPassword)))
        {
            throw new InvalidOperationException("启用住宅代理前请先保存用户名和密码。");
        }
    }

    private static SplitRouteSettings DiscoverRuntimeDefaults(SplitRouteSettings settings)
    {
        var bundledMihomo = Path.Combine(AppContext.BaseDirectory, "mihomo.exe");
        var mihomoPath = File.Exists(bundledMihomo)
            ? bundledMihomo
            : settings.MihomoPath;
        if (string.IsNullOrWhiteSpace(mihomoPath) || !File.Exists(mihomoPath))
        {
            var installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Clash Verge",
                "verge-mihomo.exe");
            if (File.Exists(installed))
            {
                mihomoPath = installed;
            }
        }

        var bundledGeoData = Path.Combine(AppContext.BaseDirectory, "geodata");
        var geoDataDirectory = Directory.Exists(bundledGeoData)
            ? bundledGeoData
            : settings.GeoDataDirectory;
        if (string.IsNullOrWhiteSpace(geoDataDirectory) || !Directory.Exists(geoDataDirectory))
        {
            geoDataDirectory = DiscoverClashGeoDataDirectory() ?? string.Empty;
        }

        return settings with
        {
            MihomoPath = mihomoPath,
            GeoDataDirectory = geoDataDirectory
        };
    }

    private static string? DiscoverClashGeoDataDirectory()
    {
        var installedDirectory = ClashVergeDiscovery.FindGeoDataDirectory();
        if (installedDirectory is not null)
        {
            return installedDirectory;
        }

        var usersRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (string.IsNullOrWhiteSpace(usersRoot) || !Directory.Exists(usersRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(usersRoot)
                .Select(user => Path.Combine(
                    user,
                    "AppData",
                    "Roaming",
                    "io.github.clash-verge-rev.clash-verge-rev"))
                .FirstOrDefault(directory =>
                    File.Exists(Path.Combine(directory, "geoip.dat"))
                    && File.Exists(Path.Combine(directory, "geosite.dat")));
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSubscriptionDue(SubscriptionSettings subscription)
    {
        return subscription.Enabled
            && (subscription.LastUpdated is null
                || DateTimeOffset.UtcNow - subscription.LastUpdated.Value
                >= TimeSpan.FromMinutes(Math.Max(5, subscription.UpdateIntervalMinutes)));
    }

    private void OnMihomoExited(object? sender, EventArgs eventArgs)
    {
        if (_settings.Enabled)
        {
            _status = _status with
            {
                Mode = RuntimeMode.CoreUnavailable,
                MihomoRunning = false,
                TunEnabled = false,
                DnsEnabled = false,
                DnsStatusKnown = false,
                ProxyRouteAvailable = false,
                ProxyRouteHealthKnown = false,
                ProxyRouteFailure = ProxyRouteFailureReason.CoreUnavailable,
                HealthyProxyCount = 0,
                CurrentProxy = string.Empty,
                EffectiveProxy = string.Empty,
                AvailableProxies = [],
                HealthyProxies = [],
                DirectDelayMilliseconds = null,
                ProxyDelayMilliseconds = null,
                LastError = "Mihomo 意外退出，服务将自动重启。",
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        _processManager.Exited -= OnMihomoExited;
        if (_processManager.IsRunning)
        {
            await _processManager.StopAsync(_settings, CancellationToken.None).ConfigureAwait(false);
        }

        _operationGate.Dispose();
        _proxyDelayGate.Dispose();
    }

    private sealed record TrafficSample(
        long BytesReceived,
        long BytesSent,
        DateTimeOffset Timestamp);

    private sealed record DelayProbeResult(int? Delay, string? Error);

    private sealed record ProxyDelayProbeResult(
        string Name,
        int? Delay,
        string Error);

    private sealed record ProxyDelayCacheEntry(
        int Generation,
        string Signature,
        DateTimeOffset MeasuredAt,
        IReadOnlyList<ProxyDelayResult> Results);

    private sealed record LastKnownGoodManifest
    {
        public string Generation { get; init; } = string.Empty;
        public string ConfigFileName { get; init; } = string.Empty;
        public string SettingsFileName { get; init; } = string.Empty;
        public string ConfigSha256 { get; init; } = string.Empty;
        public string SettingsSha256 { get; init; } = string.Empty;
        public string? CacheGeneration { get; init; }
        public string? CacheManifestFileName { get; init; }
        public string? CacheManifestSha256 { get; init; }
    }

    private sealed record LastKnownGoodState(
        string ConfigPath,
        SplitRouteSettings Settings,
        string? CacheManifestJson);

    private sealed record TransactionRecoveryState(
        LastKnownGoodState? LastKnownGood,
        SplitRouteSettings? PreviousSettings,
        string? PreviousRuntimeConfigPath,
        bool? PreviousRuntimeConfigExisted);

    private sealed record TransactionJournal
    {
        public string TransactionId { get; init; } = Guid.NewGuid().ToString("N");
        public TransactionPhase Phase { get; init; } = TransactionPhase.Prepared;
        public string? PreviousSettingsJson { get; init; }
        public string? PreviousLkgManifestJson { get; init; }
        public string? PreviousCacheManifestJson { get; init; }
        public bool? PreviousRuntimeConfigExisted { get; init; }
        public string? PreviousRuntimeConfigSha256 { get; init; }
    }

    private enum TransactionPhase
    {
        Prepared,
        RuntimeSwapped,
        CoreStarted,
        CacheCommitted,
        SettingsCommitted,
        LkgCommitted,
        Committed
    }

    private async Task<string?> StopAfterInitializationFailureAsync()
    {
        _activeSettings = null;
        try
        {
            await _processManager.StopAsync(
                _settings,
                CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }
}
