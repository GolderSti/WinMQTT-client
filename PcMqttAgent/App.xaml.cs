using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Configuration;
using PcMqttAgent.Models;
using PcMqttAgent.Services;
using Serilog;

namespace PcMqttAgent;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static MqttService? MqttService { get; private set; }
    public static HardwareMonitorService? HardwareMonitor { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // 1. Загрузка настроек
            var config = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var loadedSettings = config.Get<AppSettings>();
            if (loadedSettings != null) Settings = loadedSettings;

            // Создаем папку для логов
            var logDir = Path.GetDirectoryName(Settings.Logging.FilePath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            // 2. Инициализация логгера
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(Enum.Parse<Serilog.Events.LogEventLevel>(Settings.Logging.LogLevel))
                .WriteTo.File(
                    path: Settings.Logging.FilePath,
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();

            Log.Information("=== Приложение PcMqttAgent запущено ===");

            // 3. Инициализация монитора железа
            HardwareMonitor = new HardwareMonitorService();

            // 4. АВТООБНОВЛЕНИЕ КОНФИГУРАЦИИ ДАТЧИКОВ
            SyncSensorConfig();

            // 5. Запуск MQTT
            MqttService = new MqttService(Settings);
            await MqttService.StartAsync();

            // 6. Показываем главное окно (скрытое)
            var mainWindow = new MainWindow();
            mainWindow.Show(); 
        }
        catch (Exception ex)
        {
            Console.WriteLine($"КРИТИЧЕСКАЯ ОШИБКА ЗАПУСКА: {ex}");
            try { File.WriteAllText("crash.log", ex.ToString()); } catch { }
            MessageBox.Show($"Ошибка запуска:\n{ex.Message}\n\nСм. crash.log", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    private void SyncSensorConfig()
    {
        Log.Information("Синхронизация конфигурации датчиков...");
        bool configChanged = false;
        var availableSensors = HardwareMonitor!.GetAllAvailableSensors();

        foreach (var avail in availableSensors)
        {
            // Проверяем, есть ли уже такой датчик в конфиге
            bool exists = Settings.Sensors.Any(s => 
                s.HardwareType.Equals(avail.HardwareType, StringComparison.OrdinalIgnoreCase) &&
                s.SensorType.Equals(avail.SensorType, StringComparison.OrdinalIgnoreCase) &&
                s.SensorName.Equals(avail.SensorName, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                // Добавляем новый датчик с Enabled = false
                Settings.Sensors.Add(new SensorConfig
                {
                    HardwareType = avail.HardwareType,
                    SensorType = avail.SensorType,
                    SensorName = avail.SensorName,
                    TopicSuffix = $"auto/{avail.HardwareType.ToLower()}_{avail.SensorType.ToLower()}",
                    Enabled = false // По умолчанию выключен, как вы и просили
                });
                configChanged = true;
                Log.Information($"Обнаружен новый датчик: [{avail.HardwareType}] {avail.SensorName} (добавлен в конфиг как Disabled)");
            }
        }

        if (configChanged)
        {
            SaveConfig();
            Log.Information("Файл appsettings.json обновлен новыми датчиками.");
        }
        else
        {
            Log.Information("Конфигурация датчиков актуальна.");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(Settings, options);
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Не удалось сохранить appsettings.json");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Завершение работы...");
        if (MqttService != null) await MqttService.StopAsync();
        HardwareMonitor?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
        Environment.Exit(0);
    }
}