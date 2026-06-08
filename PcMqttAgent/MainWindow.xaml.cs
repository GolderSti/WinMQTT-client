using System;
using System.Windows;

namespace PcMqttAgent;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        // Подписываемся на изменение статуса подключения
        if (App.MqttService != null)
        {
            App.MqttService.ConnectionStateChanged += OnConnectionStateChanged;
            // Инициализируем начальное состояние (если уже подключено к моменту создания окна)
            // Но так как подключение асинхронное, лучше положиться на событие
        }
    }

    private void OnConnectionStateChanged(bool isConnected)
    {
        // ВАЖНО: Событие приходит из фонового потока MQTTnet. 
        // Обновление UI должно происходить в главном потоке через Dispatcher.
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (isConnected)
            {
                TrayIcon.IconSource = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/icon_connected.ico"));
                TrayIcon.ToolTipText = "PcMqtt Agent (Подключено к брокеру)";
            }
            else
            {
                TrayIcon.IconSource = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/icon_disconnected.ico"));
                TrayIcon.ToolTipText = "PcMqtt Agent (Отключено, идет переподключение...)";
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