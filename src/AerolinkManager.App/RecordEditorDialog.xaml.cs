using System.Windows;
using AerolinkManager.Core.Models;

namespace AerolinkManager.App;

public enum RecordEditorKind { Provider, Model, Profile, QuotaPolicy }

public partial class RecordEditorDialog : Window
{
    public string RecordName => NameBox.Text.Trim();
    public string RecordId => IdBox.Text.Trim();
    public string? ProviderId => ProviderBox.SelectedValue as string;
    public IReadOnlyList<string> ProviderIds => ProviderBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public string Value => ValueBox.Text.Trim();
    public object? Option => OptionBox.SelectedItem;
    public string ExtraValue => ExtraBox.Text.Trim();
    public string MoreValue => MoreBox.Text.Trim();
    public ModelMode ModelMode => ModelModeBox.SelectedValue is ModelMode mode ? mode : ModelMode.RespectUser;

    public RecordEditorDialog(
        RecordEditorKind kind,
        IReadOnlyList<ProviderRecord> providers,
        string? name = null,
        string? id = null,
        string? providerId = null,
        string? value = null,
        object? option = null,
        string? extraValue = null,
        string? moreValue = null,
        ModelMode? modelMode = null)
    {
        InitializeComponent();
        NameBox.Text = name ?? string.Empty;
        IdBox.Text = id ?? string.Empty;
        IdBox.IsReadOnly = id is not null;
        ValueBox.Text = value ?? string.Empty;
        ExtraBox.Text = extraValue ?? string.Empty;
        MoreBox.Text = moreValue ?? string.Empty;
        ProviderBox.ItemsSource = providers;
        ProviderBox.SelectedValue = providerId ?? providers.FirstOrDefault()?.Id;
        NameLabel.Text = LocalizationService.Text("NameLabel");
        IdLabel.Text = "ID";
        ProviderEditorLabel.Text = LocalizationService.Text("ProviderEditorLabel");

        switch (kind)
        {
            case RecordEditorKind.Provider:
                Title = LocalizationService.Text("ProviderEditorTitle");
                DescriptionText.Text = LocalizationService.Text("ProviderEditorHelp");
                ProviderRow.Visibility = Visibility.Collapsed;
                ValueLabel.Text = LocalizationService.Text("BaseUrlLabel");
                OptionLabel.Text = LocalizationService.Text("ProviderTypeLabel");
                OptionBox.ItemsSource = Enum.GetValues<ProviderType>();
                break;
            case RecordEditorKind.Model:
                Title = LocalizationService.Text("ModelEditorTitle");
                DescriptionText.Text = LocalizationService.Text("ModelEditorHelp");
                ValueLabel.Text = LocalizationService.Text("ModelValueLabel");
                OptionLabel.Text = LocalizationService.Text("SourceLabel");
                OptionBox.ItemsSource = Enum.GetValues<ModelSource>();
                break;
            case RecordEditorKind.Profile:
                Title = LocalizationService.Text("ProfileEditorTitle");
                DescriptionText.Text = LocalizationService.Text("ProfileEditorHelp");
                ProviderEditorLabel.Text = LocalizationService.Text("PrimaryProviderLabel");
                ProviderBox.IsEditable = true;
                ProviderBox.Text = providerId ?? providers.FirstOrDefault()?.Id ?? string.Empty;
                ValueLabel.Text = LocalizationService.Text("ModelOverrideLabel");
                OptionLabel.Text = LocalizationService.Text("StrategyLabel");
                OptionBox.ItemsSource = Enum.GetValues<SelectionStrategy>();
                ExtraRow.Visibility = Visibility.Visible;
                ExtraLabel.Text = LocalizationService.Text("FallbackLabel");
                MoreRow.Visibility = Visibility.Visible;
                MoreLabel.Text = LocalizationService.Text("AllowedKeysLabel");
                ModelModeRow.Visibility = Visibility.Visible;
                ModelModeLabel.Text = LocalizationService.Text("ModelModeLabel");
                ModelModeBox.ItemsSource = new[]
                {
                    new ModelModeOption(ModelMode.RespectUser, LocalizationService.Text("ModelModeRespectUser")),
                    new ModelModeOption(ModelMode.PreferProfile, LocalizationService.Text("ModelModePreferProfile")),
                    new ModelModeOption(ModelMode.ForceProfile, LocalizationService.Text("ModelModeForceProfile")),
                };
                ModelModeBox.SelectedValue = modelMode ?? ModelMode.RespectUser;
                break;
            default:
                Title = LocalizationService.Text("QuotaEditorTitle");
                DescriptionText.Text = LocalizationService.Text("QuotaEditorHelp");
                ProviderRow.Visibility = Visibility.Collapsed;
                ValueLabel.Text = LocalizationService.Text("FallbackHoursLabel");
                OptionLabel.Text = LocalizationService.Text("QuotaTypeLabel");
                OptionBox.ItemsSource = Enum.GetValues<QuotaPolicyType>();
                break;
        }
        OptionBox.SelectedItem = option ?? OptionBox.Items.Cast<object>().FirstOrDefault();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RecordName) || string.IsNullOrWhiteSpace(RecordId))
        {
            ValidationText.Text = LocalizationService.Text("NameIdRequired");
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record ModelModeOption(ModelMode Mode, string Display);
}
