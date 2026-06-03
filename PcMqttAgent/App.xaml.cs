using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using PcMqttAgent.Models;
using PcMqttAgent.Services;
using Serilog;

namespace PcMqttAgent;

public partial class App : Application
{
    // Статические свойства для глобального доступа
    public static AppSettings Settings { get; private set; } = new();
    public static MqttService? MqttService { get; private set; }

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

            // Современный способ привязки в .NET 8/9
            var loadedSettings = config.Get<AppSettings>();
            if (loadedSettings != null)
            {
                Settings = loadedSettings;
            }

            // Создаем папку для логов, если её нет
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
            Log.Information($"Загружены настройки: Server={Settings.Mqtt.Server}, ClientId={Settings.Mqtt.ClientId}");

            // 3. Запуск MQTT сервиса
            MqttService = new MqttService(Settings);
            await MqttService.StartAsync();

            // 4. Показываем главное окно (оно скрыто, но нужно для жизни приложения и трея)
            var mainWindow = new MainWindow();
            mainWindow.Show(); 
        }
        catch (Exception ex)
        {
            // 1. Пишем в консоль (видно в терминале VS Code)
            Console.WriteLine($"КРИТИЧЕСКАЯ ОШИБКА ЗАПУСКА: {ex}");
            
            // 2. Пишем в файл crash.log рядом с exe, чтобы точно не потерять
            try 
            {
                File.WriteAllText("crash.log", ex.ToString());
            } 
            catch { /* Игнорируем ошибки записи лога */ }

            // 3. Пытаемся показать окно
            try 
            {
                MessageBox.Show($"Ошибка запуска приложения:\n{ex.Message}\n\nПодробности в файле crash.log", 
                                "Критическая ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            } 
            catch { /* Игнорируем */ }
            
            Current.Shutdown();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Начало корректного завершения работы...");
        
        if (MqttService != null)
        {
            await MqttService.StopAsync();
        }
        
        Log.Information("Приложение завершено.");
        Log.CloseAndFlush();
        
        base.OnExit(e);
        Environment.Exit(0); // Гарантируем полный выход из процесса
    }
}