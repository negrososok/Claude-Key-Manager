namespace AerolinkManager.Tests;

[TestClass]
public sealed class LaunchUxStaticTests
{
    private static readonly string AppRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.App"));

    [TestMethod]
    public void ProfileAddEdit_UsesInAppOverlay_NotOldOsDialog()
    {
        var code = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml"));

        Assert.IsFalse(code.Contains("RecordEditorDialog(RecordEditorKind.Profile", StringComparison.Ordinal),
            "Launch preset add/edit must not open the old OS dialog.");
        StringAssert.Contains(xaml, "x:Name=\"ProfileEditorOverlay\"");
        StringAssert.Contains(code, "ProfileEditorOverlay.Visibility = Visibility.Visible");
        StringAssert.Contains(code, "ProfileEditorOverlay.Visibility = Visibility.Collapsed");
    }

    [TestMethod]
    public void GatewayFirstShell_HidesLauncherComplexity()
    {
        var xaml = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml.cs"));
        var resources = File.ReadAllText(Path.Combine(AppRoot, "Resources", "Strings.en.xaml"));

        StringAssert.Contains(resources, "<sys:String x:Key=\"LaunchClaudeCodeBtn\">Start Gateway</sys:String>");
        StringAssert.Contains(resources, "<sys:String x:Key=\"StopGatewayBtn\">Stop Gateway</sys:String>");
        StringAssert.Contains(resources, "<sys:String x:Key=\"GatewayStoppedFeedback\">Gateway stopped.</sys:String>");
        StringAssert.Contains(resources, "<sys:String x:Key=\"GatewayOnlyNote\">");
        Assert.IsFalse(resources.Contains("Gateway mode is optional", StringComparison.Ordinal),
            "Visible Gateway copy must not describe the old launcher path as the normal product.");
        Assert.IsFalse(resources.Contains("Use Launcher mode", StringComparison.Ordinal),
            "Visible Gateway copy should keep users on the single Gateway-first path.");
        StringAssert.Contains(xaml, "x:Name=\"TopGatewayButton\"");
        StringAssert.Contains(code, "StatusRoutingMode.Text = \"Gateway\";");
        StringAssert.Contains(code, "StopGateway()");
        StringAssert.Contains(code, "IsGatewayRunning");
        StringAssert.Contains(code, "GatewayReadyFeedback");
        Assert.IsFalse(code.Contains("StartClaudeCode(config.RealClaudePath", StringComparison.Ordinal),
            "The primary action should start/reuse Gateway, not launch another Claude Code process.");
    }

    [TestMethod]
    public void Sidebar_PrimaryPathUsesGatewayAndHidesTechnicalPages()
    {
        var xaml = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml"));
        var resources = File.ReadAllText(Path.Combine(AppRoot, "Resources", "Strings.en.xaml"));

        StringAssert.Contains(resources, "<sys:String x:Key=\"NavProviders\">API connections</sys:String>");
        StringAssert.Contains(resources, "<sys:String x:Key=\"NavGateway\">Gateway</sys:String>");
        StringAssert.Contains(xaml, "x:Name=\"NavGateway\"");
        StringAssert.Contains(xaml, "x:Name=\"NavLaunch\" Visibility=\"Collapsed\"");
        StringAssert.Contains(xaml, "x:Name=\"NavKeys\" Visibility=\"Collapsed\"");
        StringAssert.Contains(xaml, "x:Name=\"NavProfiles\" Visibility=\"Collapsed\"");

        Assert.IsTrue(IndexOf(xaml, "x:Name=\"NavProviders\"") < IndexOf(xaml, "x:Name=\"NavGateway\""),
            "API connections should lead directly into Gateway.");
        Assert.IsFalse(xaml.Contains("Header=\"{DynamicResource AdvancedNavHeader}\"", StringComparison.Ordinal),
            "The primary UI should not expose an Advanced drawer for normal use.");
    }

    [TestMethod]
    public void SetupChecklist_FoldsProviderAndKeyIntoOneApiConnectionStep()
    {
        var xaml = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml.cs"));
        var resources = File.ReadAllText(Path.Combine(AppRoot, "Resources", "Strings.en.xaml"));

        StringAssert.Contains(xaml, "x:Name=\"StepKeyRow\"");
        StringAssert.Contains(xaml, "x:Name=\"StepKeyRow\" Margin=\"0,0,0,10\" Visibility=\"Collapsed\"");
        StringAssert.Contains(code, "var hasApiConnection = hasProvider && hasKeys;");
        StringAssert.Contains(code, "SetStep(StepProviderMark, StepProviderTitle, StepProviderSub, hasApiConnection");
        StringAssert.Contains(code, "var remaining = 4 - doneSteps;");
        StringAssert.Contains(resources, "<sys:String x:Key=\"StepProviderDone\">API connection ready</sys:String>");
        StringAssert.Contains(resources, "<sys:String x:Key=\"StepProviderMissing\">No API connection</sys:String>");
        Assert.IsFalse(resources.Contains("Provider configured", StringComparison.Ordinal));
        Assert.IsFalse(resources.Contains("No provider configured", StringComparison.Ordinal));
        Assert.IsFalse(resources.Contains("provider/key", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(resources.Contains("My AI provider", StringComparison.Ordinal));
        Assert.IsFalse(resources.Contains("Providers</sys:String>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Shell_UsesCleanGatewayFirstLayoutWithoutFakeControls()
    {
        var xaml = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml"));
        var app = File.ReadAllText(Path.Combine(AppRoot, "App.xaml"));

        StringAssert.Contains(xaml, "x:Name=\"TopPageTitle\"");
        StringAssert.Contains(xaml, "Grid Margin=\"280,0,28,0\"");
        StringAssert.Contains(xaml, "ColumnDefinition Width=\"260\"");
        StringAssert.Contains(xaml, "x:Name=\"ProvidersCards\"");
        StringAssert.Contains(xaml, "<StackPanel/>");
        StringAssert.Contains(xaml, "Click=\"ProviderCardEdit_Click\"");
        StringAssert.Contains(xaml, "Click=\"ProviderCardToggle_Click\"");
        StringAssert.Contains(xaml, "Text=\"{DynamicResource RecentEventsTitle}\"");
        StringAssert.Contains(xaml, "x:Name=\"EventsList\"");
        StringAssert.Contains(app, "Color=\"#F4F7F9\"");
        StringAssert.Contains(app, "Color=\"#041627\"");

        Assert.IsFalse(xaml.Contains("Search...", StringComparison.Ordinal),
            "Do not show a top-bar search box unless it actually searches.");
        Assert.IsFalse(xaml.Contains("Search connections...", StringComparison.Ordinal),
            "Do not show provider search unless it is functional.");
        Assert.IsFalse(xaml.Contains("Background=\"{StaticResource TerminalBrush}\"", StringComparison.Ordinal),
            "Recent events should read as an app activity list, not a fake terminal.");
        Assert.IsFalse(app.Contains("x:Key=\"TerminalBrush\"", StringComparison.Ordinal),
            "Remove unused terminal-only styling from the calm desktop shell.");
    }

    [TestMethod]
    public void WindowClose_HidesToTrayAndTrayExitTerminates()
    {
        var code = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml.cs"));
        var appXaml = File.ReadAllText(Path.Combine(AppRoot, "App.xaml"));
        var appCode = File.ReadAllText(Path.Combine(AppRoot, "App.xaml.cs"));

        StringAssert.Contains(code, "e.Cancel = true;");
        StringAssert.Contains(code, "Hide();");
        Assert.IsFalse(code.Contains("TrayStillRunningTitle", StringComparison.Ordinal),
            "Closing to tray should be silent; no balloon notification on every close.");
        StringAssert.Contains(appXaml, "ShutdownMode=\"OnMainWindowClose\"");
        StringAssert.Contains(appCode, "MainWindow = _window;");
        StringAssert.Contains(appCode, "Shutdown(0);");
        StringAssert.Contains(appCode, "base.OnSessionEnding(e);");
        Assert.IsFalse(appCode.Contains("OnSessionEnding(SessionEndingCancelEventArgs e)\r\n    {\r\n        IsExiting = true;\r\n        CleanupShell();\r\n        Environment.Exit(0);", StringComparison.Ordinal),
            "Windows shutdown/logoff must not force Environment.Exit from the WPF session-ending hook.");
        StringAssert.Contains(appCode, "Keep background mode quiet.");
        Assert.IsFalse(appCode.Contains("Gateway ready on port", StringComparison.Ordinal),
            "Starting Gateway should update in-app status, not show a passive tray balloon.");
        Assert.IsFalse(appCode.Contains("Claude Manager key selected", StringComparison.Ordinal),
            "Automatic key switches should be visible in monitoring, not pushed as tray balloons.");
        Assert.IsFalse(appCode.Contains("Provider quota detected", StringComparison.Ordinal),
            "Quota polling should stay quiet unless the user explicitly asks for tray status.");
        Assert.IsFalse(appCode.Contains("_window?.Close();", StringComparison.Ordinal),
            "ExitApplication must not re-enter the same closing handler before terminating.");
    }

    [TestMethod]
    public void ProviderEditor_SupportsBulkKeysAndKeepsAuthAdvanced()
    {
        var xaml = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml.cs"));
        var resources = File.ReadAllText(Path.Combine(AppRoot, "Resources", "Strings.en.xaml"));

        StringAssert.Contains(xaml, "x:Name=\"ProvEditApiKeys\"");
        StringAssert.Contains(xaml, "<Border Width=\"760\"");
        StringAssert.Contains(xaml, "x:Name=\"ProvEditAddKeys\"");
        StringAssert.Contains(xaml, "Click=\"ProviderEditorAddKeys_Click\"");
        StringAssert.Contains(xaml, "x:Name=\"ProvEditSavedKeysInfo\"");
        StringAssert.Contains(xaml, "x:Name=\"ProvEditSavedKeysList\"");
        StringAssert.Contains(xaml, "AcceptsReturn=\"True\"");
        StringAssert.Contains(xaml, "Text=\"{Binding KeyCountText}\"");
        StringAssert.Contains(xaml, "Header=\"{DynamicResource AdvancedAuthHeader}\"");
        StringAssert.Contains(code, "ParseProviderEditorApiKeys()");
        StringAssert.Contains(code, "SavedKeysCount");
        StringAssert.Contains(code, "ProviderKeyEdit_Click");
        StringAssert.Contains(code, "ProviderKeyToggle_Click");
        StringAssert.Contains(code, "ProviderKeyDelete_Click");
        StringAssert.Contains(code, "ProviderKeyUsage_Changed");
        StringAssert.Contains(code, "PriorityForUsageMode");
        StringAssert.Contains(code, "ProviderEditorAddKeys_Click");
        StringAssert.Contains(code, "CreateProviderKeyRecords");
        StringAssert.Contains(code, "RefreshProviderEditorKeyRows");
        StringAssert.Contains(code, "apiKeys.Select((plain, index)");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding UsageModes}\"");
        StringAssert.Contains(xaml, "SelectedValue=\"{Binding UsageModeValue, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "SelectionChanged=\"ProviderKeyUsage_Changed\"");
        StringAssert.Contains(xaml, "<ColumnDefinition Width=\"138\"/>");
        StringAssert.Contains(xaml, "Grid.Row=\"1\" Margin=\"0,8,0,0\"");
        StringAssert.Contains(xaml, "<WrapPanel Grid.Column=\"1\" HorizontalAlignment=\"Right\" Margin=\"10,0,0,0\">");
        StringAssert.Contains(resources, "<sys:String x:Key=\"ProviderAddKeysBtn\">Add keys</sys:String>");
        StringAssert.Contains(resources, "<sys:String x:Key=\"ProviderKeysAdded\">Added keys: {0}.</sys:String>");
        StringAssert.Contains(code, "ProviderCardDelete_Click");
        StringAssert.Contains(code, "ProviderDeleted");
        Assert.IsFalse(xaml.Contains("<PasswordBox x:Name=\"ProvEditApiKey\"", StringComparison.Ordinal),
            "Provider setup should accept one-or-many keys in a multiline field, not a single PasswordBox.");
    }

    [TestMethod]
    public void ProviderCards_ExposeModelSwitchAndClearDeleteIcon()
    {
        var xaml = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml.cs"));
        var resources = File.ReadAllText(Path.Combine(AppRoot, "Resources", "Strings.en.xaml"));

        StringAssert.Contains(xaml, "ItemsSource=\"{Binding ModelChoices}\"");
        StringAssert.Contains(xaml, "SelectedValue=\"{Binding DefaultModelValue, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "SelectionChanged=\"ProviderCardDefaultModel_Changed\"");
        StringAssert.Contains(xaml, "Fill=\"{Binding ReadinessBrush}\"");
        StringAssert.Contains(xaml, "VerticalAlignment=\"Center\" Margin=\"22,0,0,0\" Width=\"176\"");
        StringAssert.Contains(xaml, "HorizontalAlignment=\"Stretch\"");
        StringAssert.Contains(code, "isReady ? \"#10B981\" : \"#F59E0B\"");
        StringAssert.Contains(xaml, "Content=\"&#x00D7;\"");
        StringAssert.Contains(code, "ProviderCardDefaultModel_Changed");
        StringAssert.Contains(code, "if (_refreshingData) return;");
        StringAssert.Contains(code, "ProviderDefaultModelSaved");
        StringAssert.Contains(resources, "<sys:String x:Key=\"AutoRequestModel\">Auto / request model</sys:String>");
    }

    [TestMethod]
    public void Shell_RemovesDecorativeGlyphsThatRenderUnreliably()
    {
        var xaml = File.ReadAllText(Path.Combine(AppRoot, "MainWindow.xaml"));
        var resources = File.ReadAllText(Path.Combine(AppRoot, "Resources", "Strings.en.xaml"));

        Assert.IsFalse(xaml.Contains("⌕", StringComparison.Ordinal), "Search icon glyph rendered inconsistently; keep search placeholders plain.");
        Assert.IsFalse(xaml.Contains("🌐", StringComparison.Ordinal), "Empty states should avoid decorative emoji.");
        Assert.IsFalse(xaml.Contains("📋", StringComparison.Ordinal), "Empty states should avoid decorative emoji.");
        Assert.IsFalse(xaml.Contains("📈", StringComparison.Ordinal), "Empty states should avoid decorative emoji.");
        Assert.IsFalse(xaml.Contains("Content=\"✕\"", StringComparison.Ordinal), "Use XML entity for close/delete glyphs.");
        StringAssert.Contains(resources, "<sys:String x:Key=\"AppSubtitle\">Local API gateway</sys:String>");
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static int IndexOf(string text, string value)
    {
        var index = text.IndexOf(value, StringComparison.Ordinal);
        Assert.IsTrue(index >= 0, $"Expected to find '{value}'.");
        return index;
    }
}
