using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using PcMqttAgent.Models;

namespace PcMqttAgent;

public partial class SettingsWindow : Window
{
    // Локальная копия настроек для редактирования (чтобы не портить App.Settings при отмене)
    public AppSettings Settings { get; set; }
    
    // Список уровней логирования для ComboBox
    public string[] LogLevels { get; } = { "Verbose", "Debug", "Information", "Warning", "Error" };

    public SettingsWindow()
    {
        InitializeComponent();
        
        // Глубокое копирование настроек через JSON-сериализацию
        var json = JsonSerializer.Serialize(App.Settings);
        Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        
        // Устанавливаем контекст данных на само окно (чтобы работали биндинги Settings.X и LogLevels)
        DataContext = this;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Простая валидация
            if (Settings.Publisher.IntervalSeconds <= 0)
            {
                MessageBox.Show("Интервал отправки должен быть больше 0.", "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(Settings.Mqtt.Server))
            {
                MessageBox.Show("Имя MQTT-сервера не может быть пустым.", "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 1. Сохраняем в appsettings.json
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(Settings, options);
            
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            File.WriteAllText(configPath, json);

            // 2. Обновляем глобальные настройки в памяти
            // Это позволит некоторым параметрам (например, интервалу) примениться без перезапуска, 
            // если вы добавите соответствующую логику в MqttService.
            // App.Settings = Settings;

            MessageBox.Show(
                "Настройки успешно сохранены!\n\nВнимание: Для применения настроек MQTT (сервер, логин, пароль, Client ID) требуется перезапуск приложения.", 
                "Успех", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
            
            this.Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка сохранения файла настроек:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}