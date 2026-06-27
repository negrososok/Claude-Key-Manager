using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Diagnostics;
using AerolinkManager.Core.Managed;
using AerolinkManager.Core.Models;
using AerolinkManager.Core.Presentation;
using AerolinkManager.Core.Quota;
using AerolinkManager.Core.Security;
using AerolinkManager.Core.Selection;
using AerolinkManager.Core.Storage;
using AerolinkManager.Core.Usage;
using AerolinkManager.Core.Wrapper;
using ClaudeManager.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace AerolinkManager.App;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths = AppPaths.Default;
    private readonly JsonFileStore _store;
    private readonly WindowsDpapiSecretProtector _protector = new();
    private readonly ObservableCollection<KeyRow> _keys = [];
    private readonly ObservableCollection<ProviderRow> _providers = [];
    private readonly ObservableCollection<ProfileRow> _profiles = [];
    private readonly ObservableCollection<MonHealthRow> _monHealth = [];
    private Dictionary<string, System.Windows.Controls.Button> _navButtons = new();
    private bool _changingLanguage;
    private bool _refreshingData;

    // Provider editor overlay state
    private readonly ObservableCollection<ModelEditRow> _editorModels = [];
    private readonly ObservableCollection<HeaderEditRow> _editorHeaders = [];
    private readonly ObservableCollection<ProviderKeyRow> _providerEditorKeys = [];
    private string? _editingProviderId;       // null = adding new
    private bool _providerEditorDirty;
    private bool _providerEditorOpen;
    private bool _refreshingProviderKeyRows;

    // Key editor overlay state
    private Guid? _editingKeyId;              // null = adding new
    private bool _keyEditorDirty;
    private bool _keyEditorOpen;

    // Launch preset editor overlay state
    private string? _editingProfileId;        // null = adding new
    private bool _profileEditorDirty;
    private bool _profileEditorOpen;

    public MainWindow()
    {
        InitializeComponent();
        _store = new JsonFileStore(_paths);
        KeysGrid.ItemsSource = _keys;
        ProvidersGrid.ItemsSource = _providers;
        ProfilesGrid.ItemsSource = _profiles;
        MonProviderHealthGrid.ItemsSource = _monHealth;
        ProvEditModelsList.ItemsSource = _editorModels;
        ProvEditHeadersList.ItemsSource = _editorHeaders;
        ProvEditSavedKeysList.ItemsSource = _providerEditorKeys;

        _navButtons = new()
        {
            ["overview"] = NavOverview, ["launch"] = NavLaunch, ["keys"] = NavKeys,
            ["providers"] = NavProviders, ["profiles"] = NavProfiles,
            ["gateway"] = NavGateway, ["monitoring"] = NavMonitoring, ["settings"] = NavSettings
        };

        _changingLanguage = true;
        LanguageCombo.ItemsSource = new[] { new LanguageOption("en", "English"), new LanguageOption("uk", "Українська"), new LanguageOption("ru", "Русский") };
        LanguageCombo.SelectedValue = _store.LoadConfig().Language;
        _changingLanguage = false;
        AppDataPathText.Text = _paths.RootDirectory;
        VersionText.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "3.0.0"}";
        NavigateTo("overview");
        RefreshData();
    }

    public void RefreshDataIfVisible() { if (IsVisible) RefreshData(); }

    private ManagerConfig EnsureDefaultLaunchPreset(ManagerConfig config)
    {
        var updated = EnsureGatewayMode(new LaunchPresetProvisioner().EnsureDefaultPreset(config));
        if (!ReferenceEquals(updated, config))
        {
            _store.SaveConfig(updated);
        }
        return updated;
    }

    private static ManagerConfig EnsureGatewayMode(ManagerConfig config)
    {
        return config.Gateway.RoutingMode == RoutingMode.LocalGateway
            ? config
            : config with { Gateway = config.Gateway with { RoutingMode = RoutingMode.LocalGateway } };
    }

    private void NavigateTo(string tag)
    {
        PageOverview.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PageLaunch.Visibility = tag == "launch" ? Visibility.Visible : Visibility.Collapsed;
        PageKeys.Visibility = tag == "keys" ? Visibility.Visible : Visibility.Collapsed;
        PageProviders.Visibility = tag == "providers" ? Visibility.Visible : Visibility.Collapsed;
        PageProfiles.Visibility = tag == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        PageGateway.Visibility = tag == "gateway" ? Visibility.Visible : Visibility.Collapsed;
        PageMonitoring.Visibility = tag == "monitoring" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        TopPageTitle.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, tag switch
        {
            "overview" => "NavOverview",
            "providers" => "NavProviders",
            "launch" => "NavLaunch",
            "monitoring" => "NavMonitoring",
            "settings" => "NavSettings",
            "keys" => "NavKeys",
            "profiles" => "NavProfiles",
            "gateway" => "NavGateway",
            _ => "NavOverview"
        });

        foreach (var (key, btn) in _navButtons)
        {
            btn.Background = key == tag
                ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DCE9FF"))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);
            btn.FontWeight = key == tag ? FontWeights.SemiBold : FontWeights.Normal;
            btn.Foreground = key == tag
                ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#17212B"))
                : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#44515E"));
        }
        RefreshData();
    }

    public void RefreshData()
    {
        _refreshingData = true;
        try
        {
            var config = EnsureDefaultLaunchPreset(_store.LoadConfig());
            var state = _store.LoadState();
            var now = DateTimeOffset.Now;
            var gwRunning = IsGatewayRunning(state);

            // DataGrids
            var keyId = (KeysGrid.SelectedItem as KeyRow)?.Id;
            Replace(_keys, config.Keys.OrderBy(k => k.Priority).ThenBy(k => k.AddedOrder).Select(k => ToRow(k, config, now)));
            KeysGrid.SelectedItem = _keys.FirstOrDefault(r => r.Id == keyId);
            Replace(_providers, config.Providers.Select(p =>
            {
                var isReady = ProviderIsReady(p, config);
                return new ProviderRow(
                    p.Id, p.Name, p.Type,
                    LocalizationService.Text(ProviderCompatibility.ProtocolResourceKey(p)),
                    $"{LocalizationService.Text(ProviderCompatibility.SupportResourceKey(p))} - {LocalizationService.Text(isReady ? "ReadyStatusReady" : "ReadyStatusNeedsSetup")}",
                    isReady ? "#10B981" : "#F59E0B",
                    p.BaseUrl ?? "—", p.Enabled, p.ModelDiscoveryEnabled,
                    LocalizationService.Format("SavedKeysCount", config.Keys.Count(k => k.ProviderId == p.Id)),
                    p.DefaultModelId ?? string.Empty,
                    ProviderModelChoices(p, config));
            }));
            Replace(_profiles, config.LaunchProfiles.Select(p => new ProfileRow(p.Id, p.Name, string.Join(", ", p.ProviderIds.Select(id => ProviderName(config, id))), StrategyLabel(p.Strategy), p.ModelOverride ?? "—", p.FallbackProfileId ?? "—", p.IsDefault)));

            // Empty states
            EmptyKeysPanel.Visibility = config.Keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            KeysGrid.Visibility = config.Keys.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyProvidersPanel.Visibility = config.Providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ProvidersGrid.Visibility = Visibility.Collapsed;
            ProvidersCards.Visibility = config.Providers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyProfilesPanel.Visibility = config.LaunchProfiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ProfilesGrid.Visibility = config.LaunchProfiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            // Managed command — soft status pill (green when on, neutral grey when off; red is reserved for errors)
            var managedOn = config.ManagedCommandEnabled;
            ManagedStatusText.Text = LocalizationService.Text(managedOn ? "CommandStatusOn" : "CommandStatusOff");
            ManagedPill.Background = managedOn
                ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C6F6D5"))
                : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E2E8F0"));
            ManagedStatusText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(managedOn ? "#22543D" : "#4A5568"));
            SettingsManagedButton.SetResourceReference(ContentProperty, managedOn ? "ManageCommandBtn" : "ManageCommandBtn");

            // Claude path
            var hasPath = !string.IsNullOrWhiteSpace(config.RealClaudePath);
            ClaudePathText.Text = hasPath ? config.RealClaudePath : LocalizationService.Text("PathMissing");
            SettingsPathText.Text = ClaudePathText.Text;
            DashboardLastLaunchText.Text = state.LastRunAt is { } last
                ? $"{LocalizationService.Text("LastLaunchLabel")}: {last.LocalDateTime:g}"
                : $"{LocalizationService.Text("LastLaunchLabel")}: {LocalizationService.Text("NeverValue")}";

            // ═══ Setup control center: 5-step checklist + ONE dynamic CTA ═══
            var hasKeys = config.Keys.Count > 0;
            var availableKeys = config.Keys.Count(k => new KeySelector().Select([k], now).HasKey);
            var hasProvider = config.Providers.Any(p => p.Enabled);
            var hasApiConnection = hasProvider && hasKeys;
            var hasModel = config.Models.Count > 0 || config.Providers.Any(p => !string.IsNullOrWhiteSpace(p.DefaultModelId));

            SetStep(StepClaudeMark, StepClaudeTitle, StepClaudeSub, hasPath,
                "StepClaudeFound", "StepClaudeMissing", "StepClaudeMissingSub", hasPath ? config.RealClaudePath : null);
            SetStep(StepProviderMark, StepProviderTitle, StepProviderSub, hasApiConnection,
                "StepProviderDone",
                hasProvider ? "StepKeyMissing" : "StepProviderMissing",
                hasProvider ? "StepKeyMissingSub" : "StepProviderMissingSub");
            StepKeyRow.Visibility = Visibility.Collapsed;
            SetStep(StepModelMark, StepModelTitle, StepModelSub, hasModel,
                "StepModelDone", "StepModelMissing", "StepModelMissingSub");
            SetStep(StepCommandMark, StepCommandTitle, StepCommandSub, managedOn,
                "StepCommandDone", "StepCommandMissing", "StepCommandMissingSub");

            // ONE dynamic CTA based on the first unmet step (or "check launch" when all ready).
            string ctaKey; string ctaAction;
            if (!hasPath) { ctaKey = "CtaFindClaude"; ctaAction = "find-claude"; }
            else if (!hasProvider) { ctaKey = "CtaAddProvider"; ctaAction = "add-provider"; }
            else if (!hasKeys) { ctaKey = "CtaAddKey"; ctaAction = "add-key"; }
            else if (!hasModel) { ctaKey = "CtaSelectModel"; ctaAction = "select-model"; }
            else if (!managedOn) { ctaKey = "CtaEnableCommand"; ctaAction = "enable-command"; }
            else
            {
                ctaKey = gwRunning ? "StopGatewayBtn" : "LaunchClaudeCodeBtn";
                ctaAction = gwRunning ? "stop-gateway" : "start-gateway";
            }
            SetupPrimaryCta.SetResourceReference(ContentProperty, ctaKey);
            SetupPrimaryCta.Tag = ctaAction;

            var doneSteps = (hasPath ? 1 : 0) + (hasApiConnection ? 1 : 0) + (hasModel ? 1 : 0) + (managedOn ? 1 : 0);
            var remaining = 4 - doneSteps;
            StepsRemainingText.Text = remaining == 0
                ? LocalizationService.Text("StepAllReadyTitle")
                : LocalizationService.Format("StepsRemaining", remaining);

            // Current Status
            StatusRoutingMode.Text = "Gateway";
            var defaultProfile = config.LaunchProfiles.FirstOrDefault(p => p.IsDefault && p.Enabled);
            StatusProfile.Text = LocalizationService.Text("AutomaticKeyValue");
            StatusKeys.Text = $"{availableKeys} / {config.Keys.Count}";
            StatusGateway.Text = gwRunning ? LocalizationService.Text("GatewayRunning") : LocalizationService.Text("GatewayStopped");

            // Events (problem detection)
            var events = new ObservableCollection<string>();
            if (!hasPath) events.Add(LocalizationService.Text("EventNoClaude"));
            if (!hasKeys) events.Add(LocalizationService.Text("EventNoKeys"));
            if (hasKeys && availableKeys == 0) events.Add(LocalizationService.Text("EventNoAvailableKeys"));
            if (!managedOn) events.Add(LocalizationService.Text("EventCommandDisabled"));
            if (state.GatewayError is { } gwErr) events.Add($"{LocalizationService.Text("GatewayModeLabel")}: {gwErr}");
            if (events.Count == 0) events.Add(LocalizationService.Text("EventAllGood"));
            EventsList.ItemsSource = events;

            // Launch page
            var decision = new LaunchPlanner().Plan(config, [], now);
            LaunchProfile.Text = defaultProfile?.Name ?? "—";
            LaunchProvider.Text = decision?.Provider.Name ?? "—";
            LaunchKey.Text = decision?.Key.Name ?? "—";
            LaunchModel.Text = decision?.ResolvedModel ?? LocalizationService.Text("ClaudeDefaultModel");
            // Effective-model source: profile override > provider default > request (user-chosen at runtime).
            string modelSourceKey;
            if (!string.IsNullOrWhiteSpace(defaultProfile?.ModelOverride)) modelSourceKey = "ModelSourceProfile";
            else if (!string.IsNullOrWhiteSpace(decision?.Provider.DefaultModelId)) modelSourceKey = "ModelSourceProviderDefault";
            else if (!string.IsNullOrWhiteSpace(decision?.ResolvedModel)) modelSourceKey = "ModelSourceRequest";
            else modelSourceKey = "ModelSourceNone";
            LaunchModelSource.Text = LocalizationService.Text(modelSourceKey);
            LaunchModelMode.Text = LocalizationService.Text((defaultProfile?.ModelMode ?? ModelMode.RespectUser) switch
            {
                ModelMode.PreferProfile => "ModelModePreferProfile",
                ModelMode.ForceProfile => "ModelModeForceProfile",
                _ => "ModelModeRespectUser"
            });
            LaunchLastRun.Text = state.LastRunAt?.LocalDateTime.ToString("g") ?? LocalizationService.Text("NeverValue");
            LaunchProfile.Text = decision?.Profile.Name ?? defaultProfile?.Name ?? "—";
            LaunchProvider.Text = decision?.Provider.Name ?? "—";
            LaunchKey.Text = decision is null ? "—" : LocalizationService.Text("AutomaticKeyValue");
            LaunchMode.Text = "Gateway";
            LaunchCommand.Text = managedOn ? LocalizationService.Text("CommandStatusOn") : LocalizationService.Text("CommandStatusOff");
            LaunchFallback.Text = decision?.Profile.FallbackProfileId is { Length: > 0 } ? LocalizationService.Text("StatusEnabled") : LocalizationService.Text("StatusDisabled");
            LaunchCostGuard.Text = config.Gateway.CostTrackingEnabled ? LocalizationService.Text("StatusEnabled") : LocalizationService.Text("StatusDisabled");
            var gatewayButtonKey = gwRunning ? "StopGatewayBtn" : "LaunchClaudeCodeBtn";
            TopGatewayButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, gatewayButtonKey);
            LaunchPrimaryButton.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty, managedOn ? gatewayButtonKey : "CtaEnableCommand");
            TopGatewayButton.IsEnabled = gwRunning || managedOn || hasPath;
            LaunchPrimaryButton.IsEnabled = gwRunning || managedOn || hasPath;

            // Gateway page
            var routingMode = config.Gateway.RoutingMode;
            LauncherModeRadio.IsChecked = false;
            GatewayModeRadio.IsChecked = true;
            DefaultModeNote.Text = LocalizationService.Text("GatewayOnlyNote");
            GatewayStatusValue.Text = gwRunning ? LocalizationService.Text("GatewayRunning") : LocalizationService.Text("GatewayStopped");
            GatewayUrlValue.Text = gwRunning ? $"http://127.0.0.1:{state.GatewayPort}" : "—";
            GatewayTokenValue.Text = string.IsNullOrEmpty(config.Gateway.LocalAuthTokenEncrypted) ? LocalizationService.Text("GatewayTokenMissing") : LocalizationService.Text("GatewayTokenConfigured");
            GatewayPortBox.Text = config.Gateway.Port.ToString();

            // Sidebar status — shows WHAT is still missing + a Finish button, or "ready" when complete.
            var allGood = remaining == 0;
            SidebarDot.Fill = allGood
                ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#38A169"))
                : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D69E2E"));
            if (allGood)
            {
                SidebarText.Text = LocalizationService.Text("SidebarReady");
                SidebarSubText.Text = LocalizationService.Text("SidebarReadySub");
                SidebarFinishBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                // List the missing steps by name so the user knows exactly what is left.
                var missing = new List<string>();
                if (!hasPath) missing.Add(LocalizationService.Text("StepClaudeMissing"));
                if (!hasProvider) missing.Add(LocalizationService.Text("StepProviderMissing"));
                if (!hasKeys) missing.Add(LocalizationService.Text("StepKeyMissing"));
                if (!hasModel) missing.Add(LocalizationService.Text("StepModelMissing"));
                if (!managedOn) missing.Add(LocalizationService.Text("StepCommandMissing"));
                SidebarText.Text = LocalizationService.Format("StepsRemaining", remaining);
                SidebarSubText.Text = string.Join(" - ", missing.Where(s => !string.IsNullOrEmpty(s)));
                SidebarFinishBtn.Visibility = Visibility.Visible;
            }

            // Monitoring page
            RefreshMonitoringData(config);
        }
        catch (Exception ex) { Feedback(ex.Message); }
        finally { _refreshingData = false; }
    }

    private void RefreshMonitoringData(ManagerConfig config)
    {
        try
        {
            var dbPath = _paths.UsageDatabaseFile;
            if (!File.Exists(dbPath)) { MonRequests.Text = "0"; MonSessions.Text = "0"; MonCost.Text = "—"; EmptyMonitoringPanel.Visibility = Visibility.Visible; return; }
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM requests;";
            MonRequests.Text = (cmd.ExecuteScalar()?.ToString() ?? "0");
            cmd.CommandText = "SELECT COUNT(DISTINCT session_id) FROM requests WHERE session_id IS NOT NULL;";
            MonSessions.Text = (cmd.ExecuteScalar()?.ToString() ?? "0");
            cmd.CommandText = "SELECT COALESCE(SUM(estimated_cost_micros),0)/1000000.0 FROM requests;";
            var cost = cmd.ExecuteScalar();
            MonCost.Text = cost is double d ? $"${d:F2}" : "—";

            // Provider health
            var healthRows = new ObservableCollection<MonHealthRow>();
            cmd.CommandText = "SELECT provider_id, success_rate, p50_latency_ms, p95_latency_ms, circuit_state FROM provider_health ORDER BY provider_id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                healthRows.Add(new MonHealthRow(
                    reader.IsDBNull(0) ? "-" : reader.GetString(0),
                    reader.IsDBNull(1) ? "—" : $"{reader.GetDouble(1) * 100:F0}%",
                    reader.IsDBNull(2) ? "—" : $"{reader.GetInt64(2)}ms",
                    reader.IsDBNull(3) ? "—" : $"{reader.GetInt64(3)}ms",
                    reader.IsDBNull(4) ? "closed" : reader.GetString(4)));
            }
            Replace(_monHealth, healthRows);

            // Recent events
            var evtItems = new ObservableCollection<string>();
            using (var routeCmd = conn.CreateCommand())
            {
                routeCmd.CommandText = """
                    SELECT timestamp_utc, selected_provider_id, selected_key_id, selected_model, decision_reason
                    FROM route_decisions ORDER BY timestamp_utc DESC LIMIT 5;
                    """;
                using var routes = routeCmd.ExecuteReader();
                while (routes.Read())
                {
                    var timestamp = routes.GetString(0);
                    var provider = routes.IsDBNull(1) ? null : routes.GetString(1);
                    var key = routes.IsDBNull(2) ? null : routes.GetString(2);
                    var model = routes.IsDBNull(3) ? null : routes.GetString(3);
                    var reason = routes.GetString(4);
                    var time = timestamp[..Math.Min(19, timestamp.Length)];
                    evtItems.Add(provider is null
                        ? $"{time} — no route selected ({reason})"
                        : $"{time} — routed to {provider} / {(string.IsNullOrWhiteSpace(model) ? "default model" : model)} using {(string.IsNullOrWhiteSpace(key) ? "automatic key" : "key " + key[..Math.Min(8, key.Length)])} ({reason})");
                }
            }
            cmd.CommandText = "SELECT limit_type, timestamp_utc FROM limit_events ORDER BY timestamp_utc DESC LIMIT 5;";
            using var r2 = cmd.ExecuteReader();
            while (r2.Read()) evtItems.Add($"{r2.GetString(0)} — {r2.GetString(1)[..Math.Min(19, r2.GetString(1).Length)]}");
            if (evtItems.Count == 0) evtItems.Add("No recent limit events.");
            MonEventsList.ItemsSource = evtItems;

            // Recent sessions
            var sessItems = new ObservableCollection<string>();
            cmd.CommandText = "SELECT session_id, request_count, status FROM sessions ORDER BY last_activity_utc DESC LIMIT 8;";
            using var r3 = cmd.ExecuteReader();
            while (r3.Read()) sessItems.Add($"{r3.GetString(0)[..Math.Min(16, r3.GetString(0).Length)]} — {r3.GetInt64(1)} req — {r3.GetString(2)}");
            if (sessItems.Count == 0) sessItems.Add("No sessions recorded yet.");
            MonSessionsList.ItemsSource = sessItems;

            // Empty monitoring state
            var hasRequests = long.TryParse(MonRequests.Text, out var reqCount) && reqCount > 0;
            EmptyMonitoringPanel.Visibility = hasRequests ? Visibility.Collapsed : Visibility.Visible;
        }
        catch { /* best-effort */ }
    }

    // ═══ SIDEBAR NAVIGATION ═══
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag) NavigateTo(tag);
    }

    private void NavigateToProfiles(object sender, RoutedEventArgs e) => NavigateTo("profiles");

    // ═══ MONITORING ═══
    private void ExportUsageCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dbPath = _paths.UsageDatabaseFile;
            if (!File.Exists(dbPath)) { Feedback("No usage data to export."); return; }
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT request_id, timestamp_utc, session_id, provider_id, model, status_code, input_tokens, output_tokens, estimated_cost_micros, currency FROM requests ORDER BY timestamp_utc;";
            var rows = new List<RequestUsageRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new RequestUsageRecord
                {
                    RequestId = r.GetString(0),
                    Timestamp = DateTimeOffset.Parse(r.GetString(1)),
                    SessionId = r.IsDBNull(2) ? null : r.GetString(2),
                    ProviderId = r.IsDBNull(3) ? null : r.GetString(3),
                    Model = r.IsDBNull(4) ? null : r.GetString(4),
                    StatusCode = (int)r.GetInt64(5),
                    InputTokens = r.GetInt64(6),
                    OutputTokens = r.GetInt64(7),
                    EstimatedCostMicros = r.IsDBNull(8) ? null : r.GetInt64(8),
                    Currency = r.IsDBNull(9) ? null : r.GetString(9)
                });
            }
            var csv = UsageCsvExporter.ExportRequests(rows);
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"ClaudeManager-Usage-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");
            File.WriteAllText(path, csv);
            Feedback($"Usage exported to {path}");
        }
        catch (Exception ex) { Feedback(ex.Message); }
    }

    private void RefreshMonitoring_Click(object sender, RoutedEventArgs e) => RefreshData();

    // ═══ EXISTING HANDLERS (preserved verbatim) ═══

    private void AddKey_Click(object sender, RoutedEventArgs e)
    {
        var config = _store.LoadConfig();
        var providers = config.Providers.Where(provider => provider.Enabled).ToList();
        if (providers.Count == 0) { Feedback(LocalizationService.Text("NoEnabledProviders")); return; }
        ShowKeyEditor(null);
    }

    private void EditKey_Click(object sender, RoutedEventArgs e)
    {
        if (KeysGrid.SelectedItem is not KeyRow row) { SelectFeedback(); return; }
        var key = _store.LoadConfig().Keys.FirstOrDefault(item => item.Id == row.Id);
        if (key is null) { SelectFeedback(); return; }
        ShowKeyEditor(key);
    }

    // ═══ API KEY MODAL OVERLAY ═══

    // Usage modes map to priority + enabled. Raw priority (advanced) overrides the numeric value
    // while the mode still controls enabled/disabled state.
    private const int PriorityFirst = 10;
    private const int PriorityAuto = 100;
    private const int PriorityReserve = 1000;

    private void ShowKeyEditor(ApiKeyRecord? existing)
    {
        var config = _store.LoadConfig();
        var providers = config.Providers.Where(p => p.Enabled).ToList();
        if (providers.Count == 0) { Feedback(LocalizationService.Text("NoEnabledProviders")); return; }

        _editingKeyId = existing?.Id;

        KeyEditTitle.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty,
            existing is null ? "KeyModalTitleAdd" : "KeyModalTitleEdit");

        KeyEditName.Text = existing?.Name ?? string.Empty;

        KeyEditProvider.ItemsSource = providers;
        KeyEditProvider.SelectedValue = existing?.ProviderId ?? providers[0].Id;
        if (KeyEditProvider.SelectedItem is null && providers.Count > 0) KeyEditProvider.SelectedIndex = 0;

        KeyEditApiKey.Clear();
        // When editing, the key is optional (blank keeps existing); when adding it is required.
        KeyEditApiKeyHint.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;

        var priority = existing?.Priority ?? PriorityAuto;
        var enabled = existing?.Enabled ?? true;
        SelectComboByTag(KeyEditUsageMode, UsageModeTag(priority, enabled));
        KeyEditPriority.Text = priority.ToString(System.Globalization.CultureInfo.InvariantCulture);
        KeyEditAdvanced.IsExpanded = false;

        KeyEditError.Visibility = Visibility.Collapsed;
        _keyEditorDirty = false;
        _keyEditorOpen = true;
        KeyEditorOverlay.Visibility = Visibility.Visible;
        KeyEditName.Focus();
    }

    private static string UsageModeTag(int priority, bool enabled)
    {
        if (!enabled) return "Disabled";
        if (priority <= PriorityFirst) return "First";
        if (priority >= PriorityReserve) return "Reserve";
        return "Auto";
    }

    private void CloseKeyEditor()
    {
        KeyEditorOverlay.Visibility = Visibility.Collapsed;
        _keyEditorOpen = false;
        _keyEditorDirty = false;
        KeyEditApiKey.Clear();
    }

    private void KeyEditorBackdrop_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender)) TryCloseKeyEditor();
    }

    private void KeyEditorCancel_Click(object sender, RoutedEventArgs e) => TryCloseKeyEditor();

    private void TryCloseKeyEditor()
    {
        if (_keyEditorDirty && !Confirm("UnsavedChangesConfirm", string.Empty)) return;
        CloseKeyEditor();
    }

    private void KeyEditorField_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (_keyEditorOpen) _keyEditorDirty = true; }
    private void KeyEditorSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_keyEditorOpen) return;
        _keyEditorDirty = true;
        // Keep the raw priority box in sync when the user picks a usage mode (unless they go Disabled).
        if (ReferenceEquals(sender, KeyEditUsageMode))
        {
            var tag = (KeyEditUsageMode.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
            var mapped = tag switch { "First" => PriorityFirst, "Reserve" => PriorityReserve, _ => PriorityAuto };
            if (tag != "Disabled") KeyEditPriority.Text = mapped.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
    private void KeyEditorPassword_Changed(object sender, RoutedEventArgs e) { if (_keyEditorOpen) _keyEditorDirty = true; }
    private void KeyEditorPriority_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (_keyEditorOpen) _keyEditorDirty = true; }

    private void KeyEditorSave_Click(object sender, RoutedEventArgs e)
    {
        var name = KeyEditName.Text.Trim();
        var providerId = KeyEditProvider.SelectedValue as string;
        var apiKey = KeyEditApiKey.Password;
        var isNew = _editingKeyId is null;

        if (string.IsNullOrWhiteSpace(name)) { ShowKeyEditorError("ValidationNameRequired"); return; }
        if (string.IsNullOrWhiteSpace(providerId)) { ShowKeyEditorError("NoEnabledProviders"); return; }
        if (isNew && string.IsNullOrWhiteSpace(apiKey)) { ShowKeyEditorError("ValidationApiKeyRequired"); return; }

        var tag = (KeyEditUsageMode.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "Auto";
        var enabled = tag != "Disabled";
        // Raw priority box wins (advanced override); fall back to the mode's mapped value.
        var priority = int.TryParse(KeyEditPriority.Text.Trim(), out var p) && p > 0
            ? p
            : tag switch { "First" => PriorityFirst, "Reserve" => PriorityReserve, _ => PriorityAuto };

        if (isNew)
        {
            var encrypted = _protector.Protect(apiKey);
            _store.UpdateConfig(current => current with { Keys = [.. current.Keys, new ApiKeyRecord { Id = Guid.NewGuid(), ProviderId = providerId, Name = name, ApiKeyEncrypted = encrypted, Priority = priority, Enabled = enabled, AddedOrder = current.Keys.Count == 0 ? 1 : current.Keys.Max(key => key.AddedOrder) + 1 }] });
            Feedback(LocalizationService.Text("KeyAdded"));
        }
        else
        {
            var keyId = _editingKeyId!.Value;
            _store.UpdateConfig(current => current with { Keys = current.Keys.Select(item => item.Id == keyId ? item with { Name = name, ProviderId = providerId, Priority = priority, Enabled = enabled, ApiKeyEncrypted = string.IsNullOrWhiteSpace(apiKey) ? item.ApiKeyEncrypted : _protector.Protect(apiKey) } : item).ToList() });
            Feedback(LocalizationService.Text("KeyUpdated"));
        }

        CloseKeyEditor();
        RefreshData();
    }

    private void ShowKeyEditorError(string resourceKey)
    {
        KeyEditError.Text = LocalizationService.Text(resourceKey);
        KeyEditError.Visibility = Visibility.Visible;
    }

    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (KeysGrid.SelectedItem is not KeyRow row) { SelectFeedback(); return; }
        if (!Confirm("DeleteConfirm", row.Name)) return;
        _store.UpdateConfig(current => current with { Keys = current.Keys.Where(item => item.Id != row.Id).ToList() });
        Feedback(LocalizationService.Text("KeyDeleted")); RefreshData();
    }

    private void ToggleKey_Click(object sender, RoutedEventArgs e)
    {
        if (KeysGrid.SelectedItem is not KeyRow row) { SelectFeedback(); return; }
        var config = _store.LoadConfig();
        var key = config.Keys.FirstOrDefault(item => item.Id == row.Id);
        var wasEnabled = key?.Enabled ?? true;
        _store.UpdateConfig(current => current with { Keys = current.Keys.Select(item => item.Id == row.Id ? item with { Enabled = !item.Enabled } : item).ToList() });
        Feedback(LocalizationService.Text(wasEnabled ? "KeyDisabled" : "KeyEnabled")); RefreshData();
    }

    private void TestKey_Click(object sender, RoutedEventArgs e)
    {
        if (KeysGrid.SelectedItem is not KeyRow row) { SelectFeedback(); return; }
        var config = _store.LoadConfig();
        var key = config.Keys.FirstOrDefault(item => item.Id == row.Id);
        if (key is null) { SelectFeedback(); return; }
        var provider = config.Providers.FirstOrDefault(p => p.Id == key.ProviderId);
        Feedback(LocalizationService.Text("TestingKey"));
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var result = await new KeyTestService(client).TestAsync(provider?.BaseUrl ?? "https://api.anthropic.com/", _protector.Unprotect(key.ApiKeyEncrypted), providerName: provider?.Name ?? "Provider");
                Dispatcher.Invoke(() => Feedback(result.Message));
            }
            catch (Exception ex) { Dispatcher.Invoke(() => Feedback(ex.Message)); }
        });
    }

    private void MarkFiveHour_Click(object sender, RoutedEventArgs e) => ApplyKeyStatus(s => s with { Status = KeyStatus.FiveHourLimited, FiveHourResetAt = DateTimeOffset.Now.AddHours(5), FiveHourResetEstimated = true }, LocalizationService.Text("MarkedFive"));
    private void SetFiveHourReset_Click(object sender, RoutedEventArgs e) { var dialog = new DateTimeDialog(DateTimeOffset.Now.AddHours(5)) { Owner = this }; if (dialog.ShowDialog() != true) return; ApplyKeyStatus(s => s with { Status = KeyStatus.FiveHourLimited, FiveHourResetAt = dialog.Value, FiveHourResetEstimated = false }, LocalizationService.Text("ExactFiveSaved")); }
    private void MarkWeekly_Click(object sender, RoutedEventArgs e) => ApplyKeyStatus(s => s with { Status = KeyStatus.WeeklyLimited, WeeklyBlockedUnknown = true, WeeklyBlockedUntil = null }, LocalizationService.Text("MarkedWeekly"));
    private void SetWeeklyReset_Click(object sender, RoutedEventArgs e) { var dialog = new DateTimeDialog(DateTimeOffset.Now.AddDays(7)) { Owner = this }; if (dialog.ShowDialog() != true) return; ApplyKeyStatus(s => s with { Status = KeyStatus.WeeklyLimited, WeeklyBlockedUntil = dialog.Value, WeeklyBlockedUnknown = false }, LocalizationService.Text("ExactWeeklySaved")); }
    private void ResetStatus_Click(object sender, RoutedEventArgs e) => ApplyKeyStatus(s => s with { Status = KeyStatus.Available, FiveHourResetAt = null, FiveHourResetEstimated = false, WeeklyBlockedUntil = null, WeeklyBlockedUnknown = false, LastErrorText = null }, LocalizationService.Text("StatusResetDone"));

    private void AddProvider_Click(object sender, RoutedEventArgs e) => ShowProviderEditor(null);
    private void EditProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ProvidersGrid.SelectedItem is not ProviderRow row) { SelectFeedback(); return; }
        var provider = _store.LoadConfig().Providers.FirstOrDefault(x => x.Id == row.Id);
        if (provider is null) { SelectFeedback(); return; }
        ShowProviderEditor(provider);
    }
    // ═══ PROVIDER SETUP MODAL OVERLAY ═══

    private void ShowProviderEditor(ProviderRecord? existing)
    {
        var config = _store.LoadConfig();
        _editingProviderId = existing?.Id;
        _editorModels.Clear();
        _editorHeaders.Clear();
        _providerEditorKeys.Clear();

        ProvEditTitle.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty,
            existing is null ? "ProviderModalTitle" : "ProviderModalTitleEdit");

        ProvEditId.Text = existing?.Id ?? string.Empty;
        ProvEditId.IsEnabled = existing is null; // ID is immutable once created (keys/profiles reference it)
        ProvEditName.Text = existing?.Name ?? string.Empty;
        ProvEditBaseUrl.Text = existing?.BaseUrl ?? string.Empty;
        ProvEditAddKeys.IsEnabled = existing is not null;

        // Auth scheme combo: select by Tag (XApiKey/Bearer). Custom falls back to XApiKey display.
        var scheme = existing?.AuthScheme ?? ProviderAuthScheme.XApiKey;
        SelectComboByTag(ProvEditAuthScheme, scheme == ProviderAuthScheme.Bearer ? "Bearer" : "XApiKey");

        ProvEditApiKeys.Clear(); // never prefill secrets
        var savedKeyCount = existing is null
            ? 0
            : config.Keys.Count(key => key.ProviderId == existing.Id);
        ProvEditSavedKeysInfo.Text = LocalizationService.Format("SavedKeysCount", savedKeyCount);
        if (existing is not null)
        {
            _refreshingProviderKeyRows = true;
            try
            {
                foreach (var key in config.Keys.Where(key => key.ProviderId == existing.Id).OrderBy(key => key.Priority).ThenBy(key => key.AddedOrder))
                {
                    _providerEditorKeys.Add(ToProviderKeyRow(key, DateTimeOffset.Now));
                }
            }
            finally
            {
                _refreshingProviderKeyRows = false;
            }
        }

        // Models for this provider
        if (existing is not null)
        {
            foreach (var m in config.Models.Where(m => m.ProviderId == existing.Id))
                _editorModels.Add(new ModelEditRow { ModelId = m.ModelValue, DisplayName = m.DisplayName });
            foreach (var (k, v) in existing.CustomHeaders)
                _editorHeaders.Add(new HeaderEditRow { HeaderName = k, HeaderValue = v });
        }

        RefreshDefaultModelChoices();
        ProvEditDefaultModel.Text = existing?.DefaultModelId ?? string.Empty;

        ProvEditError.Visibility = Visibility.Collapsed;
        _providerEditorDirty = false;
        _providerEditorOpen = true;
        ProviderEditorOverlay.Visibility = Visibility.Visible;
        ProvEditId.Focus();
    }

    private void CloseProviderEditor()
    {
        ProviderEditorOverlay.Visibility = Visibility.Collapsed;
        _providerEditorOpen = false;
        _providerEditorDirty = false;
        _editorModels.Clear();
        _editorHeaders.Clear();
        _providerEditorKeys.Clear();
        ProvEditApiKeys.Clear();
    }

    private void ProviderEditorField_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_providerEditorOpen) _providerEditorDirty = true;
    }

    private void ProviderEditorSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_providerEditorOpen) _providerEditorDirty = true;
    }

    private void RefreshDefaultModelChoices()
    {
        var current = ProvEditDefaultModel.Text;
        ProvEditDefaultModel.ItemsSource = _editorModels.Select(m => m.ModelId).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        ProvEditDefaultModel.Text = current;
    }

    private IReadOnlyList<string> ParseProviderEditorApiKeys()
    {
        return ProvEditApiKeys.Text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private List<ApiKeyRecord> CreateProviderKeyRecords(ManagerConfig config, string providerId, string providerName, IReadOnlyList<string> apiKeys)
    {
        var existingProviderKeyCount = config.Keys.Count(k => k.ProviderId == providerId);
        var nextOrder = config.Keys.Count == 0 ? 1 : config.Keys.Max(k => k.AddedOrder) + 1;
        return apiKeys.Select((plain, index) => new ApiKeyRecord
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            Name = apiKeys.Count == 1 && existingProviderKeyCount == 0
                ? $"{providerName} key"
                : $"{providerName} key {existingProviderKeyCount + index + 1}",
            ApiKeyEncrypted = _protector.Protect(plain),
            Priority = 100,
            AddedOrder = nextOrder + index
        }).ToList();
    }

    private void ProviderEditorAddKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_editingProviderId is not { Length: > 0 } providerId)
        {
            ShowEditorError("ProviderAddKeysSaveFirst");
            return;
        }

        var apiKeys = ParseProviderEditorApiKeys();
        if (apiKeys.Count == 0)
        {
            ShowEditorError("ProviderAddKeysEmpty");
            return;
        }

        var config = _store.LoadConfig();
        var provider = config.Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider is null)
        {
            ShowEditorError("ProviderAddKeysSaveFirst");
            return;
        }

        var providerName = string.IsNullOrWhiteSpace(ProvEditName.Text)
            ? provider.Name
            : ProvEditName.Text.Trim();

        _store.UpdateConfig(current => current with
        {
            Keys =
            [
                .. current.Keys,
                .. CreateProviderKeyRecords(current, providerId, providerName, apiKeys)
            ]
        });

        var wasDirty = _providerEditorDirty;
        ProvEditApiKeys.Clear();
        _providerEditorDirty = wasDirty;
        ProvEditError.Visibility = Visibility.Collapsed;
        RefreshProviderEditorKeyRows(providerId);
        RefreshData();
        Feedback(LocalizationService.Format("ProviderKeysAdded", apiKeys.Count));
    }

    private void AddModelRow_Click(object sender, RoutedEventArgs e)
    {
        _editorModels.Add(new ModelEditRow());
        _providerEditorDirty = true;
    }

    private void RemoveModelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: ModelEditRow row })
        {
            _editorModels.Remove(row);
            RefreshDefaultModelChoices();
            _providerEditorDirty = true;
        }
    }

    private void AddHeaderRow_Click(object sender, RoutedEventArgs e)
    {
        _editorHeaders.Add(new HeaderEditRow());
        _providerEditorDirty = true;
    }

    private void RemoveHeaderRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: HeaderEditRow row })
        {
            _editorHeaders.Remove(row);
            _providerEditorDirty = true;
        }
    }

    private ProviderKeyRow ToProviderKeyRow(ApiKeyRecord key, DateTimeOffset now)
    {
        string mask;
        try { mask = KeyStatusFormatter.Mask(_protector.Unprotect(key.ApiKeyEncrypted)); }
        catch { mask = "protected"; }

        var status = key.Enabled ? LocalizedStatus(key.Status) : LocalizationService.Text("StatusDisabled");
        var reset = LocalizedReset(key, now);
        var runs = LocalizationService.Format("KeyRunsCount", key.Usage.Runs);
        var summary = reset == "—"
            ? $"{status} - {runs}"
            : $"{status} - {reset} - {runs}";
        return new ProviderKeyRow(
            key.Id,
            key.Name,
            mask,
            summary,
            UsageModeTag(key.Priority, key.Enabled),
            KeyUsageChoices(),
            key.Enabled,
            LocalizationService.Text(key.Enabled ? "KeyDisableShort" : "KeyEnableShort"));
    }

    private static IReadOnlyList<KeyUsageChoice> KeyUsageChoices() =>
    [
        new("First", LocalizationService.Text("KeyUsageFirst")),
        new("Auto", LocalizationService.Text("KeyUsageAuto")),
        new("Reserve", LocalizationService.Text("KeyUsageReserve")),
        new("Disabled", LocalizationService.Text("KeyUsageDisabled"))
    ];

    private static int PriorityForUsageMode(string tag) => tag switch
    {
        "First" => PriorityFirst,
        "Reserve" => PriorityReserve,
        _ => PriorityAuto
    };

    private void ProviderKeyEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not Guid id) return;
        var key = _store.LoadConfig().Keys.FirstOrDefault(item => item.Id == id);
        if (key is null) { SelectFeedback(); return; }
        if (_providerEditorDirty && !Confirm("UnsavedChangesConfirm", string.Empty)) return;
        CloseProviderEditor();
        ShowKeyEditor(key);
    }

    private void ProviderKeyToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not Guid id) return;
        var key = _store.LoadConfig().Keys.FirstOrDefault(item => item.Id == id);
        if (key is null) { SelectFeedback(); return; }
        _store.UpdateConfig(current => current with
        {
            Keys = current.Keys.Select(item => item.Id == id ? item with { Enabled = !item.Enabled } : item).ToList()
        });
        Feedback(LocalizationService.Text(key.Enabled ? "KeyDisabled" : "KeyEnabled"));
        RefreshData();
        RefreshProviderEditorKeyRows(key.ProviderId);
    }

    private void ProviderKeyUsage_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingProviderKeyRows || _refreshingData) return;
        if (sender is not System.Windows.Controls.ComboBox { DataContext: ProviderKeyRow row, SelectedValue: string tag }) return;

        var key = _store.LoadConfig().Keys.FirstOrDefault(item => item.Id == row.Id);
        if (key is null) { SelectFeedback(); return; }

        var enabled = tag != "Disabled";
        var priority = PriorityForUsageMode(tag);
        _store.UpdateConfig(current => current with
        {
            Keys = current.Keys.Select(item => item.Id == row.Id
                ? item with
                {
                    Priority = priority,
                    Enabled = enabled,
                    Status = enabled && item.Status == KeyStatus.Disabled
                        ? KeyStatus.Available
                        : (!enabled ? KeyStatus.Disabled : item.Status)
                }
                : item).ToList()
        });

        Feedback(LocalizationService.Text("KeyUpdated"));
        RefreshData();
        RefreshProviderEditorKeyRows(key.ProviderId);
    }

    private void ProviderKeyDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not Guid id) return;
        var key = _store.LoadConfig().Keys.FirstOrDefault(item => item.Id == id);
        if (key is null) { SelectFeedback(); return; }
        if (!Confirm("DeleteConfirm", key.Name)) return;
        _store.UpdateConfig(current => current with { Keys = current.Keys.Where(item => item.Id != id).ToList() });
        Feedback(LocalizationService.Text("KeyDeleted"));
        RefreshData();
        RefreshProviderEditorKeyRows(key.ProviderId);
    }

    private void RefreshProviderEditorKeyRows(string providerId)
    {
        var config = _store.LoadConfig();
        _refreshingProviderKeyRows = true;
        try
        {
            _providerEditorKeys.Clear();
            foreach (var key in config.Keys.Where(key => key.ProviderId == providerId).OrderBy(key => key.Priority).ThenBy(key => key.AddedOrder))
            {
                _providerEditorKeys.Add(ToProviderKeyRow(key, DateTimeOffset.Now));
            }
        }
        finally
        {
            _refreshingProviderKeyRows = false;
        }

        ProvEditSavedKeysInfo.Text = LocalizationService.Format("SavedKeysCount", _providerEditorKeys.Count);
    }

    private void DiscoverModels_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = ProvEditBaseUrl.Text.Trim();
        var apiKey = ParseProviderEditorApiKeys().FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            Feedback(LocalizationService.Text("DiscoverNeedsUrlKey"));
            return;
        }
        Feedback(LocalizationService.Text("DiscoverRunning"));
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var models = await new ModelDiscoveryService(client).DiscoverAsync(baseUrl, apiKey);
                Dispatcher.Invoke(() =>
                {
                    foreach (var m in models)
                    {
                        if (_editorModels.Any(r => string.Equals(r.ModelId, m.Value, StringComparison.OrdinalIgnoreCase))) continue;
                        _editorModels.Add(new ModelEditRow { ModelId = m.Value, DisplayName = m.DisplayName });
                    }
                    RefreshDefaultModelChoices();
                    _providerEditorDirty = true;
                    Feedback(LocalizationService.Format("DiscoverDone", models.Count));
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => Feedback(LocalizationService.Format("DiscoverFailed", ex.Message)));
            }
        });
    }

    private void ProviderEditorBackdrop_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only close when the click is on the backdrop itself, not bubbled from the card.
        if (ReferenceEquals(e.OriginalSource, sender)) TryCloseProviderEditor();
    }

    private void ProviderEditorCancel_Click(object sender, RoutedEventArgs e) => TryCloseProviderEditor();

    private void TryCloseProviderEditor()
    {
        if (_providerEditorDirty && !Confirm("UnsavedChangesConfirm", string.Empty)) return;
        CloseProviderEditor();
    }

    private void ProviderEditorSave_Click(object sender, RoutedEventArgs e)
    {
        var id = ProvEditId.Text.Trim();
        var name = ProvEditName.Text.Trim();
        var baseUrl = ProvEditBaseUrl.Text.Trim();
        var isNew = _editingProviderId is null;

        // Validation
        if (string.IsNullOrWhiteSpace(id)) { ShowEditorError("ValidationIdRequired"); return; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9_-]+$")) { ShowEditorError("ValidationIdFormat"); return; }
        if (string.IsNullOrWhiteSpace(name)) { ShowEditorError("ValidationNameRequired"); return; }

        var config = _store.LoadConfig();
        if (isNew && config.Providers.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            ShowEditorError("ValidationDuplicateId");
            return;
        }

        // Build custom headers (skip blanks and protected names)
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in _editorHeaders)
        {
            var hn = (h.HeaderName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(hn)) continue;
            if (ProviderHeaderRules.IsProtected(hn)) { ShowEditorError("ValidationHeaderProtected"); return; }
            headers[hn] = (h.HeaderValue ?? string.Empty).Trim();
        }

        var scheme = (ProvEditAuthScheme.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string == "Bearer"
            ? ProviderAuthScheme.Bearer
            : ProviderAuthScheme.XApiKey;
        var defaultModel = EmptyToNull(ProvEditDefaultModel.Text);
        var apiKeys = ParseProviderEditorApiKeys();

        // Snapshot models from the editor (non-empty ids)
        var modelRows = _editorModels
            .Where(m => !string.IsNullOrWhiteSpace(m.ModelId))
            .Select(m => (Id: m.ModelId!.Trim(), Display: string.IsNullOrWhiteSpace(m.DisplayName) ? m.ModelId!.Trim() : m.DisplayName!.Trim()))
            .ToList();

        _store.UpdateConfig(c =>
        {
            // Provider upsert
            List<ProviderRecord> providers;
            if (isNew)
            {
                providers =
                [
                    .. c.Providers,
                    new ProviderRecord
                    {
                        Id = id,
                        Name = name,
                        Type = ProviderType.CustomAnthropicCompatible,
                        BaseUrl = EmptyToNull(baseUrl),
                        AuthScheme = scheme,
                        DefaultModelId = defaultModel,
                        CustomHeaders = headers,
                        QuotaPolicyId = "manual",
                        ErrorPatternSetId = "generic-anthropic-compatible"
                    }
                ];
            }
            else
            {
                providers = c.Providers.Select(p => p.Id == _editingProviderId
                    ? p with
                    {
                        Name = name,
                        BaseUrl = EmptyToNull(baseUrl),
                        AuthScheme = scheme,
                        DefaultModelId = defaultModel,
                        CustomHeaders = headers
                    }
                    : p).ToList();
            }

            // Models reconcile: replace this provider's models with the editor list
            var otherModels = c.Models.Where(m => m.ProviderId != id).ToList();
            var newModels = modelRows.Select(m => new ModelRecord
            {
                Id = $"{id}:{m.Id}",
                ProviderId = id,
                DisplayName = m.Display,
                ModelValue = m.Id,
                Source = ModelSource.Manual
            });
            var models = otherModels.Concat(newModels).ToList();

            // Optional inline keys: one pasted line creates one saved key for this provider.
            var keys = c.Keys;
            if (apiKeys.Count > 0)
            {
                var keyRows = CreateProviderKeyRecords(c, id, name, apiKeys);
                keys =
                [
                    .. c.Keys,
                    .. keyRows
                ];
            }

            return c with { Providers = providers, Models = models, Keys = keys };
        });

        var hasKeyForProvider = apiKeys.Count > 0
            || _store.LoadConfig().Keys.Any(k => k.ProviderId == id);
        Feedback(LocalizationService.Text(isNew
            ? (hasKeyForProvider ? "ProviderSaved" : "ProviderSavedNoKey")
            : "ProviderUpdated"));

        CloseProviderEditor();
        RefreshData();
    }

    private void ShowEditorError(string resourceKey)
    {
        ProvEditError.Text = LocalizationService.Text(resourceKey);
        ProvEditError.Visibility = Visibility.Visible;
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is System.Windows.Controls.ComboBoxItem { Tag: string t } cbi && t == tag)
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void ToggleProvider_Click(object sender, RoutedEventArgs e) { if (ProvidersGrid.SelectedItem is not ProviderRow row) { SelectFeedback(); return; } _store.UpdateConfig(c => c with { Providers = c.Providers.Select(x => x.Id == row.Id ? x with { Enabled = !x.Enabled } : x).ToList() }); Feedback(LocalizationService.Text("Saved")); RefreshData(); }
    private void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ProvidersGrid.SelectedItem is not ProviderRow row) { SelectFeedback(); return; }
        DeleteProvider(row.Id, row.Name);
    }
    private void ToggleDiscovery_Click(object sender, RoutedEventArgs e) { if (ProvidersGrid.SelectedItem is not ProviderRow row) { SelectFeedback(); return; } _store.UpdateConfig(c => c with { Providers = c.Providers.Select(x => x.Id == row.Id ? x with { ModelDiscoveryEnabled = !x.ModelDiscoveryEnabled } : x).ToList() }); Feedback(LocalizationService.Text("Saved")); RefreshData(); }

    private void ProviderCardEdit_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as System.Windows.Controls.Button)?.Tag as string;
        if (string.IsNullOrWhiteSpace(id)) { SelectFeedback(); return; }
        var provider = _store.LoadConfig().Providers.FirstOrDefault(x => x.Id == id);
        if (provider is null) { SelectFeedback(); return; }
        ShowProviderEditor(provider);
    }

    private void ProviderCardToggle_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as System.Windows.Controls.Button)?.Tag as string;
        if (string.IsNullOrWhiteSpace(id)) { SelectFeedback(); return; }
        _store.UpdateConfig(c => c with { Providers = c.Providers.Select(x => x.Id == id ? x with { Enabled = !x.Enabled } : x).ToList() });
        Feedback(LocalizationService.Text("Saved"));
        RefreshData();
    }

    private void ProviderCardDefaultModel_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingData) return;
        if (sender is not System.Windows.Controls.ComboBox { DataContext: ProviderRow row } combo) return;
        var selected = combo.SelectedValue as string ?? string.Empty;
        if (string.Equals(selected, row.DefaultModelValue, StringComparison.Ordinal)) return;

        _store.UpdateConfig(c => c with
        {
            Providers = c.Providers.Select(provider => provider.Id == row.Id
                ? provider with { DefaultModelId = string.IsNullOrWhiteSpace(selected) ? null : selected }
                : provider).ToList()
        });
        Feedback(LocalizationService.Text("ProviderDefaultModelSaved"));
        RefreshData();
    }

    private void ProviderCardDelete_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as System.Windows.Controls.Button)?.Tag as string;
        if (string.IsNullOrWhiteSpace(id)) { SelectFeedback(); return; }
        var provider = _store.LoadConfig().Providers.FirstOrDefault(x => x.Id == id);
        if (provider is null) { SelectFeedback(); return; }
        DeleteProvider(provider.Id, provider.Name);
    }

    private void DeleteProvider(string id, string name)
    {
        if (!Confirm("DeleteConfirm", name)) return;
        _store.UpdateConfig(c =>
        {
            var deletedKeyIds = c.Keys
                .Where(key => key.ProviderId == id)
                .Select(key => key.Id)
                .ToHashSet();
            var deletedKeyScopeIds = deletedKeyIds
                .Select(keyId => keyId.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return c with
            {
                Providers = c.Providers.Where(x => x.Id != id).ToList(),
                Keys = c.Keys.Where(k => k.ProviderId != id).ToList(),
                Models = c.Models.Where(m => m.ProviderId != id).ToList(),
                ModelPricing = c.ModelPricing.Where(pricing => pricing.ProviderId != id).ToList(),
                BudgetPolicies = c.BudgetPolicies
                    .Where(policy => !(policy.Scope == BudgetScope.Provider && string.Equals(policy.ScopeId, id, StringComparison.OrdinalIgnoreCase)))
                    .Where(policy => !(policy.Scope == BudgetScope.Key && policy.ScopeId is not null && deletedKeyScopeIds.Contains(policy.ScopeId)))
                    .ToList(),
                LaunchProfiles = c.LaunchProfiles.Select(profile => profile with
                {
                    ProviderIds = profile.ProviderIds.Where(providerId => providerId != id).ToList(),
                    AllowedKeyIds = profile.AllowedKeyIds
                        .Where(keyId => !deletedKeyIds.Contains(keyId))
                        .ToList()
                }).ToList(),
                RoutingChains = c.RoutingChains.Select(chain => chain with
                {
                    Steps = chain.Steps.Select(step => step with
                    {
                        ProviderIds = step.ProviderIds.Where(providerId => providerId != id).ToList(),
                        AllowedKeyIds = step.AllowedKeyIds
                            .Where(keyId => !deletedKeyIds.Contains(keyId))
                            .ToList()
                    }).ToList()
                }).ToList()
            };
        });
        Feedback(LocalizationService.Text("ProviderDeleted"));
        RefreshData();
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e) => ShowProfileEditor(null);
    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not ProfileRow row) { SelectFeedback(); return; }
        var profile = _store.LoadConfig().LaunchProfiles.FirstOrDefault(x => x.Id == row.Id);
        if (profile is null) { SelectFeedback(); return; }
        ShowProfileEditor(profile);
    }
    private void DeleteProfile_Click(object sender, RoutedEventArgs e) { if (ProfilesGrid.SelectedItem is not ProfileRow row) { SelectFeedback(); return; } var config = _store.LoadConfig(); if (config.LaunchProfiles.Count <= 1) { Feedback(LocalizationService.Text("LastProfileRequired")); return; } if (!Confirm("DeleteConfirm", row.Name)) return; _store.UpdateConfig(c => c with { LaunchProfiles = c.LaunchProfiles.Where(x => x.Id != row.Id).ToList() }); Feedback(LocalizationService.Text("Saved")); RefreshData(); }
    private void SetDefaultProfile_Click(object sender, RoutedEventArgs e) { if (ProfilesGrid.SelectedItem is not ProfileRow row) { SelectFeedback(); return; } _store.UpdateConfig(c => c with { LaunchProfiles = c.LaunchProfiles.Select(x => x with { IsDefault = x.Id == row.Id }).ToList() }); Feedback(LocalizationService.Text("Saved")); RefreshData(); }

    private void ShowProfileEditor(LaunchProfile? existing)
    {
        var config = _store.LoadConfig();
        var providers = config.Providers.Where(provider => provider.Enabled).ToList();
        if (providers.Count == 0) { Feedback(LocalizationService.Text("NoEnabledProviders")); return; }

        _editingProfileId = existing?.Id;
        ProfileEditTitle.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty,
            existing is null ? "ProfileEditorTitleAdd" : "ProfileEditorTitleEdit");
        ProfileEditId.Text = existing?.Id ?? SuggestedProfileId(config);
        ProfileEditId.IsEnabled = existing is null;
        ProfileEditName.Text = existing?.Name ?? LocalizationService.Text("DefaultLaunchPresetName");
        ProfileEditProviders.Text = existing is null
            ? providers[0].Id
            : string.Join(", ", existing.ProviderIds.Count == 0 ? providers.Select(provider => provider.Id) : existing.ProviderIds);
        ProfileEditModel.Text = existing?.ModelOverride ?? string.Empty;
        ProfileEditAllowedKeys.Text = existing is null ? string.Empty : string.Join(", ", existing.AllowedKeyIds);
        ProfileEditFallback.Text = existing?.FallbackProfileId ?? string.Empty;
        ProfileEditDefault.IsChecked = existing?.IsDefault ?? config.LaunchProfiles.Count == 0;
        SelectComboByTag(ProfileEditStrategy, StrategyTag(existing?.Strategy ?? SelectionStrategy.PriorityThenLru));
        SelectComboByTag(ProfileEditModelMode, ModelModeTag(existing?.ModelMode ?? ModelMode.RespectUser));
        ProfileEditError.Visibility = Visibility.Collapsed;
        _profileEditorDirty = false;
        _profileEditorOpen = true;
        ProfileEditorOverlay.Visibility = Visibility.Visible;
        ProfileEditName.Focus();
    }

    private static string SuggestedProfileId(ManagerConfig config)
    {
        if (config.LaunchProfiles.All(profile => !profile.Id.Equals("default", StringComparison.OrdinalIgnoreCase)))
        {
            return "default";
        }

        var index = config.LaunchProfiles.Count + 1;
        while (config.LaunchProfiles.Any(profile => profile.Id.Equals($"preset-{index}", StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }
        return $"preset-{index}";
    }

    private void CloseProfileEditor()
    {
        ProfileEditorOverlay.Visibility = Visibility.Collapsed;
        _profileEditorOpen = false;
        _profileEditorDirty = false;
    }

    private void ProfileEditorBackdrop_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender)) TryCloseProfileEditor();
    }

    private void ProfileEditorCancel_Click(object sender, RoutedEventArgs e) => TryCloseProfileEditor();
    private void ProfileEditorField_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (_profileEditorOpen) _profileEditorDirty = true; }
    private void ProfileEditorSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (_profileEditorOpen) _profileEditorDirty = true; }
    private void ProfileEditorCheck_Changed(object sender, RoutedEventArgs e) { if (_profileEditorOpen) _profileEditorDirty = true; }

    private void TryCloseProfileEditor()
    {
        if (_profileEditorDirty && !Confirm("UnsavedChangesConfirm", string.Empty)) return;
        CloseProfileEditor();
    }

    private void ProfileEditorSave_Click(object sender, RoutedEventArgs e)
    {
        var config = _store.LoadConfig();
        var isNew = _editingProfileId is null;
        var id = ProfileEditId.Text.Trim();
        var name = ProfileEditName.Text.Trim();
        var model = EmptyToNull(ProfileEditModel.Text);
        var fallback = EmptyToNull(ProfileEditFallback.Text);
        var strategy = ParseStrategyTag(ProfileEditStrategy);
        var modelMode = ParseModelModeTag(ProfileEditModelMode);

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) { ShowProfileEditorError("NameIdRequired"); return; }
        if (isNew && config.LaunchProfiles.Any(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) { ShowProfileEditorError("DuplicateId"); return; }
        if (!isNew && !id.Equals(_editingProfileId, StringComparison.OrdinalIgnoreCase)) { ShowProfileEditorError("DuplicateId"); return; }
        if (!ParseProfileReferences(config, ProfileEditProviders.Text, ProfileEditAllowedKeys.Text, strategy, out var providers, out var keys)) return;
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            if (fallback.Equals(id, StringComparison.OrdinalIgnoreCase)) { ShowProfileEditorError("FallbackCycle"); return; }
            if (config.LaunchProfiles.All(profile => !profile.Id.Equals(fallback, StringComparison.OrdinalIgnoreCase))) { ShowProfileEditorError("InvalidFallbackProfile"); return; }
        }

        var makeDefault = ProfileEditDefault.IsChecked == true || config.LaunchProfiles.Count == 0;
        var profile = new LaunchProfile
        {
            Id = id,
            Name = name,
            ProviderIds = providers,
            AllowedKeyIds = keys,
            ModelOverride = model,
            Strategy = strategy,
            FallbackProfileId = fallback,
            ModelMode = modelMode,
            IsDefault = makeDefault,
            RoutingChainId = config.RoutingChains.FirstOrDefault()?.Id
        };

        _store.UpdateConfig(current =>
        {
            var profiles = isNew
                ? [.. current.LaunchProfiles, profile]
                : current.LaunchProfiles.Select(existing => existing.Id == _editingProfileId ? profile : existing).ToList();
            if (makeDefault)
            {
                profiles = profiles.Select(existing => existing with { IsDefault = existing.Id == id }).ToList();
            }
            return current with { LaunchProfiles = profiles };
        });

        Feedback(LocalizationService.Text("Saved"));
        CloseProfileEditor();
        RefreshData();
    }

    private static string StrategyTag(SelectionStrategy strategy) => strategy switch
    {
        SelectionStrategy.LeastRecentlyUsed => "least_used_recently",
        SelectionStrategy.PriorityOrder => "priority_order",
        SelectionStrategy.Random => "random",
        SelectionStrategy.ManualKey => "manual_key",
        SelectionStrategy.ProviderFallback => "provider_fallback",
        _ => "automatic"
    };

    private static SelectionStrategy ParseStrategyTag(System.Windows.Controls.ComboBox combo) =>
        ((combo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string) switch
        {
            "least_used_recently" => SelectionStrategy.LeastRecentlyUsed,
            "priority_order" => SelectionStrategy.PriorityOrder,
            "random" => SelectionStrategy.Random,
            "manual_key" => SelectionStrategy.ManualKey,
            "provider_fallback" => SelectionStrategy.ProviderFallback,
            _ => SelectionStrategy.PriorityThenLru
        };

    private static string ModelModeTag(ModelMode mode) => mode switch
    {
        ModelMode.PreferProfile => "prefer_preset",
        ModelMode.ForceProfile => "force_preset",
        _ => "request_model"
    };

    private static ModelMode ParseModelModeTag(System.Windows.Controls.ComboBox combo) =>
        ((combo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string) switch
        {
            "prefer_preset" => ModelMode.PreferProfile,
            "force_preset" => ModelMode.ForceProfile,
            _ => ModelMode.RespectUser
        };

    private void ShowProfileEditorError(string resourceKey)
    {
        ProfileEditError.Text = LocalizationService.Text(resourceKey);
        ProfileEditError.Visibility = Visibility.Visible;
    }

    private void RetryDetection_Click(object sender, RoutedEventArgs e) { var detector = new ClaudeDetector(); var candidates = detector.FindCandidates(_paths); var claude = candidates.FirstOrDefault(c => !ClaudeDetector.IsManaged(c, _paths)); if (claude is null) { Feedback(LocalizationService.Text("ClaudeNotFound")); return; } _store.UpdateConfig(c => c with { RealClaudePath = claude }); Feedback(LocalizationService.Format("ClaudeDetected", claude)); RefreshData(); }
    private void ManagedCommand_Click(object sender, RoutedEventArgs e) { var service = new ManagedCommandService(_paths); var config = _store.LoadConfig(); if (config.ManagedCommandEnabled) { service.Disable(); _store.UpdateConfig(c => c with { ManagedCommandEnabled = false }); Feedback(LocalizationService.Text("ManagedDisabledFeedback")); } else { if (string.IsNullOrWhiteSpace(config.RealClaudePath)) { Feedback(LocalizationService.Text("ClaudeNotFound")); return; } var wrapper = ManagedCommandService.FindSiblingWrapper(Environment.ProcessPath ?? string.Empty); if (wrapper is null) { Feedback(LocalizationService.Text("WrapperMissing")); return; } service.Enable(wrapper); _store.UpdateConfig(c => c with { ManagedCommandEnabled = true }); Feedback(LocalizationService.Text("ManagedEnabledFeedback")); } RefreshData(); }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) { var dir = _paths.LogsDirectory; Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); }

    private void ExportConfig_Click(object sender, RoutedEventArgs e) { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "ClaudeManager-config.json" }; if (dialog.ShowDialog(this) != true) return; new ConfigurationTransferService().Export(_store.LoadConfig(), dialog.FileName); Feedback(LocalizationService.Text("ExportDone")); }
    private void ImportConfig_Click(object sender, RoutedEventArgs e) { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json" }; if (dialog.ShowDialog(this) != true) return; if (!Confirm("ImportConfirm", "")) return; var imported = new ConfigurationTransferService().Import(dialog.FileName); _store.SaveConfig(imported); Feedback(LocalizationService.Text("ImportDone")); RefreshData(); }

    private void RoutingMode_Changed(object sender, RoutedEventArgs e) { if (LauncherModeRadio == null || GatewayModeRadio == null) return; _store.UpdateConfig(c => c with { Gateway = c.Gateway with { RoutingMode = RoutingMode.LocalGateway } }); DefaultModeNote.Text = LocalizationService.Text("GatewayOnlyNote"); RefreshData(); }
    private void SaveGatewaySettings_Click(object sender, RoutedEventArgs e) { var portStr = GatewayPortBox?.Text ?? "17844"; if (!int.TryParse(portStr, out var port) || port <= 0 || port > 65535) { Feedback(LocalizationService.Text("InvalidPort")); return; } _store.UpdateConfig(c => c with { Gateway = c.Gateway with { Port = port } }); Feedback(LocalizationService.Text("Saved")); RefreshData(); }
    private async void TestGateway_Click(object sender, RoutedEventArgs e) { var config = _store.LoadConfig(); var port = config.Gateway.Port; using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; try { var token = _protector.Unprotect(config.Gateway.LocalAuthTokenEncrypted ?? string.Empty); var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/health"); req.Headers.Add("x-api-key", token); var resp = await client.SendAsync(req); var body = await resp.Content.ReadAsStringAsync(); Feedback(resp.IsSuccessStatusCode ? LocalizationService.Text("GatewayHealthy") + " " + body : LocalizationService.Format("GatewayUnhealthy", (int)resp.StatusCode)); } catch (Exception ex) { Feedback(LocalizationService.Format("GatewayUnhealthy", ex.Message)); } }
    private void RegenerateToken_Click(object sender, RoutedEventArgs e) { if (!Confirm("RegenerateTokenConfirm", "")) return; var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); var encrypted = _protector.Protect(token); _store.UpdateConfig(c => c with { Gateway = c.Gateway with { LocalAuthTokenEncrypted = encrypted } }); Feedback(LocalizationService.Text("TokenRegenerated")); RefreshData(); }
    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e) { try { var config = _store.LoadConfig(); var state = _store.LoadState(); var logs = new List<string>(); var logDir = _paths.LogsDirectory; if (Directory.Exists(logDir)) { foreach (var file in Directory.GetFiles(logDir, "*.log").Take(3)) { try { logs.AddRange(File.ReadLines(file).TakeLast(50)); } catch { } } } var report = DiagnosticsExporter.Export(config, state, _protector, logs); var reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"ClaudeManager-Diagnostics-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.txt"); File.WriteAllText(reportPath, report); Feedback(LocalizationService.Format("DiagnosticsExported", reportPath)); } catch (Exception ex) { Feedback(ex.Message); } }

    // ═══ HELPERS ═══

    private static readonly System.Windows.Media.Brush StepDoneBrush =
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#38A169"));
    private static readonly System.Windows.Media.Brush StepPendingBrush =
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A0AEC0"));

    private static void SetStep(System.Windows.Controls.TextBlock mark, System.Windows.Controls.TextBlock title, System.Windows.Controls.TextBlock sub,
        bool done, string doneKey, string missingKey, string missingSubKey, string? doneDetail = null)
    {
        mark.Text = done ? "✓" : "○";
        mark.Foreground = done ? StepDoneBrush : StepPendingBrush;
        title.Text = LocalizationService.Text(done ? doneKey : missingKey);
        sub.Text = done ? (doneDetail ?? string.Empty) : LocalizationService.Text(missingSubKey);
        sub.Visibility = string.IsNullOrEmpty(sub.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SidebarFinish_Click(object sender, RoutedEventArgs e) => NavigateTo("overview");

    private void SetupPrimaryCta_Click(object sender, RoutedEventArgs e)
    {
        var action = (sender as System.Windows.Controls.Button)?.Tag as string;
        switch (action)
        {
            case "find-claude": RetryDetection_Click(sender, e); break;
            case "add-provider": NavigateTo("providers"); ShowProviderEditor(null); break;
            case "add-key": NavigateTo("providers"); ShowKeyEditor(null); break;
            case "select-model": NavigateTo("providers"); break;
            case "enable-command": ManagedCommand_Click(sender, e); break;
            case "check-launch":
            case "start-gateway": LaunchPrimary_Click(sender, e); break;
            case "stop-gateway": StopGateway(); break;
        }
    }

    private async void LaunchPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (IsGatewayRunning(_store.LoadState()))
        {
            StopGateway();
            return;
        }

        var config = EnsureDefaultLaunchPreset(_store.LoadConfig());
        var now = DateTimeOffset.Now;
        try
        {
            LaunchDecision? decision = new LaunchPlanner().Plan(config, [], now);
            var gateway = new GatewayProcessManager(_paths, _store, _protector);
            var (port, _) = await gateway.EnsureReadyAsync(TimeSpan.FromSeconds(15));
            var gatewayState = _store.LoadState();
            _store.SaveState(gatewayState with
            {
                CurrentProfileId = decision?.Profile.Id,
                CurrentProviderId = decision?.Provider.Id,
                CurrentModel = decision?.ResolvedModel,
                LastRunAt = now,
                GatewayPort = port
            });
            Feedback(LocalizationService.Format("GatewayReadyFeedback", port));
            RefreshData();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Feedback(LocalizationService.Format("LaunchFailed", ex.Message));
        }
    }

    private void StopGateway()
    {
        try
        {
            var gateway = new GatewayProcessManager(_paths, _store, _protector);
            gateway.Stop();
            Feedback(LocalizationService.Text("GatewayStoppedFeedback"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Feedback(LocalizationService.Format("GatewayUnavailable", ex.Message));
        }

        RefreshData();
    }

    private static void StartClaudeCode(string realClaudePath, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo
        {
            FileName = realClaudePath,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = false
        };
        foreach (var pair in environment)
        {
            start.Environment[pair.Key] = pair.Value;
        }
        Process.Start(start);
    }

    private void ApplyKeyStatus(Func<ApiKeyRecord, ApiKeyRecord> update, string feedback) { if (KeysGrid.SelectedItem is not KeyRow row) { SelectFeedback(); return; } _store.UpdateConfig(c => c with { Keys = c.Keys.Select(k => k.Id == row.Id ? update(k) : k).ToList() }); Feedback(feedback); RefreshData(); }
    private void SelectFeedback() => Feedback(LocalizationService.Text("SelectRecord"));
    private bool Confirm(string resource, string value) => System.Windows.MessageBox.Show(this, LocalizationService.Format(resource, value), LocalizationService.Text("AppTitle"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
    private void Feedback(string message) => FeedbackText.Text = message;
    private void LanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (_changingLanguage || LanguageCombo.SelectedValue is not string language) return; _store.UpdateConfig(c => c with { Language = language }); LocalizationService.Apply(language); (System.Windows.Application.Current as App)?.RefreshLocalization(); RefreshData(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App { IsExiting: false })
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _providerEditorOpen)
        {
            TryCloseProviderEditor();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && _keyEditorOpen)
        {
            TryCloseKeyEditor();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && _profileEditorOpen)
        {
            TryCloseProfileEditor();
            e.Handled = true;
        }
    }

    private KeyRow ToRow(ApiKeyRecord key, ManagerConfig config, DateTimeOffset now) { string mask; try { mask = KeyStatusFormatter.Mask(_protector.Unprotect(key.ApiKeyEncrypted)); } catch { mask = "protected"; } return new KeyRow(key.Id, key.Name, ProviderName(config, key.ProviderId), mask, key.Priority, LocalizedStatus(key.Status), LocalizedReset(key, now), key.Usage.Runs); }
    private static string ProviderName(ManagerConfig config, string id) => config.Providers.FirstOrDefault(p => p.Id == id)?.Name ?? id;
    private static IReadOnlyList<ModelChoice> ProviderModelChoices(ProviderRecord provider, ManagerConfig config)
    {
        var choices = new List<ModelChoice> { new(string.Empty, LocalizationService.Text("AutoRequestModel")) };
        choices.AddRange(config.Models
            .Where(model => model.Enabled && model.ProviderId == provider.Id)
            .OrderBy(model => model.DisplayName)
            .Select(model => new ModelChoice(model.ModelValue, string.IsNullOrWhiteSpace(model.DisplayName) ? model.ModelValue : model.DisplayName))
            .DistinctBy(choice => choice.Value, StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(provider.DefaultModelId)
            && choices.All(choice => !string.Equals(choice.Value, provider.DefaultModelId, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new ModelChoice(provider.DefaultModelId, provider.DefaultModelId));
        }

        return choices;
    }
    private static bool ProviderIsReady(ProviderRecord p, ManagerConfig config)
    {
        if (!p.Enabled) return false;
        if (!ProviderCompatibility.IsAnthropicCompatible(p)) return false;
        var hasUrl = p.Type == ProviderType.AnthropicOfficial || !string.IsNullOrWhiteSpace(p.BaseUrl);
        var hasKey = config.Keys.Any(k => k.ProviderId == p.Id);
        return hasUrl && hasKey;
    }
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private bool ParseProfileReferences(ManagerConfig config, string providerText, string allowedKeyText, SelectionStrategy strategy, out List<string> providers, out List<Guid> keys)
    {
        providers = providerText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        keys = [];

        if (providers.Count == 0 || providers.Any(id => config.Providers.All(p => !p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
        {
            ShowProfileEditorError("InvalidProviderList");
            return false;
        }

        foreach (var value in allowedKeyText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(value, out var id) || config.Keys.All(k => k.Id != id))
            {
                ShowProfileEditorError("InvalidKeyList");
                return false;
            }
            keys.Add(id);
        }

        if (strategy is SelectionStrategy.ManualKey && keys.Count != 1)
        {
            ShowProfileEditorError("ManualKeyRequired");
            return false;
        }

        return true;
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var v in values) target.Add(v); }
    private static bool IsGatewayRunning(ManagerState state)
    {
        if (state.GatewayPort is null || state.GatewayProcessId is not { } pid)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string LocalizedStatus(KeyStatus status) => LocalizationService.Text(status switch { KeyStatus.Available => "StatusAvailable", KeyStatus.Active => "StatusActive", KeyStatus.FiveHourLimited => "StatusFive", KeyStatus.WeeklyLimited => "StatusWeekly", KeyStatus.Disabled => "StatusDisabled", _ => "StatusUnknown" });
    private static string LocalizedReset(ApiKeyRecord key, DateTimeOffset now) { if (key.Status == KeyStatus.FiveHourLimited) return key.FiveHourResetAt is null ? LocalizationService.Text("ResetUnknown") : LocalizationService.Format(key.FiveHourResetEstimated ? "ResetEstimated" : "ResetExact", FormatDuration(key.FiveHourResetAt.Value - now)); if (key.Status == KeyStatus.WeeklyLimited) return key.WeeklyBlockedUnknown || key.WeeklyBlockedUntil is null ? LocalizationService.Text("WeeklyUnknown") : LocalizationService.Format("WeeklyExact", key.WeeklyBlockedUntil.Value.LocalDateTime.ToString("g")); return "—"; }
    private static string FormatDuration(TimeSpan value) { if (value < TimeSpan.Zero) value = TimeSpan.Zero; return value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{Math.Max(1, value.Minutes)}m"; }
    // Rows
    private sealed record KeyRow(Guid Id, string Name, string Provider, string Mask, int Priority, string Status, string Reset, long Runs);
    private sealed record ModelChoice(string Value, string Display);
    private sealed record ProviderKeyRow(Guid Id, string Name, string Mask, string Summary, string UsageModeValue, IReadOnlyList<KeyUsageChoice> UsageModes, bool Enabled, string ToggleText);
    private sealed record KeyUsageChoice(string Value, string Display);
    private sealed record ProviderRow(string Id, string Name, ProviderType Type, string TypeLabel, string Readiness, string ReadinessBrush, string BaseUrl, bool Enabled, bool Discovery, string KeyCountText, string DefaultModelValue, IReadOnlyList<ModelChoice> ModelChoices);
    private sealed record ProfileRow(string Id, string Name, string Providers, string Strategy, string Model, string Fallback, bool IsDefault);

    private static string StrategyLabel(SelectionStrategy s) => LocalizationService.Text($"Strategy{s}");
    private sealed record MonHealthRow(string Provider, string SuccessRate, string P50, string P95, string State);
    private sealed record LanguageOption(string Code, string Display);

    // Editable rows for the provider setup overlay (two-way bound TextBoxes).
    private sealed class ModelEditRow : INotifyPropertyChanged
    {
        private string? _modelId;
        private string? _displayName;
        public string? ModelId { get => _modelId; set { _modelId = value; OnChanged(nameof(ModelId)); } }
        public string? DisplayName { get => _displayName; set { _displayName = value; OnChanged(nameof(DisplayName)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private sealed class HeaderEditRow : INotifyPropertyChanged
    {
        private string? _headerName;
        private string? _headerValue;
        public string? HeaderName { get => _headerName; set { _headerName = value; OnChanged(nameof(HeaderName)); } }
        public string? HeaderValue { get => _headerValue; set { _headerValue = value; OnChanged(nameof(HeaderValue)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
