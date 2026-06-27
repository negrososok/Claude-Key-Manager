using System.Globalization;
using System.Windows;

namespace AerolinkManager.App;

public partial class DateTimeDialog : Window
{
    public DateTimeOffset Value { get; private set; }

    public DateTimeDialog(DateTimeOffset suggested)
    {
        InitializeComponent();
        ValueBox.Text = suggested.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private void Set_Click(object sender, RoutedEventArgs e)
    {
        if (!DateTime.TryParseExact(ValueBox.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            System.Windows.MessageBox.Show(this, LocalizationService.Text("InvalidDate"), LocalizationService.Text("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Value = new DateTimeOffset(parsed);
        DialogResult = true;
    }
}
