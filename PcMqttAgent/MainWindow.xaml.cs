using System;
using System.IO;
using System.Windows;
using Serilog;

namespace PcMqttAgent;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        if (App.MqttService != null)
        {
            App.MqttService.ConnectionStateChanged += OnConnectionStateChanged;
        }
    }

    private void OnConnectionStateChanged(bool isConnected)
    {
        // Обновляем UI строго в главном потоке
        Log.Information("Обновляем иконки в трее.");
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                string iconName = isConnected ? "icon_connected.ico" : "icon_disconnected.ico";
                Log.Information($"Загружаем иконку {iconName}");
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, iconName);
                
                if (File.Exists(iconPath))
                {
                    // Используем System.Drawing.Icon для надежной загрузки .ico файла
                    Log.Information($"Загружаем иконку {iconPath}");
                    var icon = new System.Drawing.Icon(iconPath);
                    TrayIcon.Icon = icon;
                }
                
                TrayIcon.ToolTipText = isConnected 
                    ? "PcMqtt Agent (Подключено к брокеру)" 
                    : "PcMqtt Agent (Отключено, идет переподключение...)";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка смены иконки в трее.");
            }
        });
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow();
        settingsWindow.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}