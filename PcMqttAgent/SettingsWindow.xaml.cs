using System.IO;
using System.Text.Json;
using System.Windows;

namespace PcMqttAgent;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // Привязываем текущие настройки к окну
        DataContext = App.Settings;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Сохраняем обратно в appsettings.json
            var json = JsonSerializer.Serialize(App.Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText("appsettings.json", json);
            
            MessageBox.Show("Настройки сохранены. Перезапустите приложение для применения изменений MQTT.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            this.Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка сохранения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}