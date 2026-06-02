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
    public static AppSettings Settings { get; private set; } = new();
    public static MqttService? MqttService { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Загрузка настроек
        var config = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        config.Bind(Settings);

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

        // 3. Запуск MQTT сервиса
        MqttService = new MqttService(Settings);
        await MqttService.StartAsync();

        // 4. Показываем главное окно (оно скрыто, но нужно для жизни приложения и трея)
        var mainWindow = new MainWindow();
        mainWindow.Show(); 
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (MqttService != null)
        {
            await MqttService.StopAsync();
        }
        Log.Information("Приложение завершено.");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}