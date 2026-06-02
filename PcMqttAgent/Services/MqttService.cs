using System;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using PcMqttAgent.Models;
using Serilog;

namespace PcMqttAgent.Services;

public class MqttService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly IMqttClient _mqttClient;
    private Timer? _publishTimer;
    private bool _isConnected;

    public MqttService(AppSettings settings)
    {
        _settings = settings;
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        _mqttClient.ConnectedAsync += OnConnectedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
    }

    public async Task StartAsync()
    {
        Log.Information("Запуск MQTT сервиса...");
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.Mqtt.Server, _settings.Mqtt.Port)
            .WithClientId(_settings.Mqtt.ClientId)
            .WithCredentials(_settings.Mqtt.User, _settings.Mqtt.Password)
            .WithCleanSession()
            // Настройка Last Will and Testament (LWT)
            .WithWillTopic($"{_settings.Mqtt.BaseTopic}/status")
            .WithWillPayload("OFFLINE")
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain(true)
            .Build();

        await _mqttClient.ConnectAsync(options, CancellationToken.None);
    }

    private async Task OnConnectedAsync(MqttClientConnectedEventArgs e)
    {
        _isConnected = true;
        Log.Information("Успешно подключено к MQTT брокеру.");
        
        // Публикуем ONLINE при подключении
        await PublishStatusAsync("ONLINE");

        // Запускаем таймер периодической отправки данных
        _publishTimer = new Timer(
            async _ => await PublishPeriodicDataAsync(), 
            null, 
            TimeSpan.Zero, 
            TimeSpan.FromSeconds(_settings.Publisher.IntervalSeconds)
        );
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        _isConnected = false;
        Log.Warning("Соединение с MQTT брокером разорвано. Попытка переподключения через 5 сек...");
        
        _publishTimer?.Dispose();

        // Автоматическое переподключение
        await Task.Delay(5000);
        try
        {
            await _mqttClient.ConnectAsync(_mqttClient.Options, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка переподключения к MQTT.");
        }
    }

    private async Task PublishStatusAsync(string status)
    {
        if (!_isConnected) return;
        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"{_settings.Mqtt.BaseTopic}/status")
            .WithPayload(status)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build();

        await _mqttClient.PublishAsync(message);
        Log.Information($"Опубликован статус: {status}");
    }

    private async Task PublishPeriodicDataAsync()
    {
        if (!_isConnected) return;

        try
        {
            // ЭТАП 1: Только версия и Uptime
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"dd\.hh\:mm\:ss");

            // Публикация версии
            await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/version", version);
            // Публикация Uptime
            await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/uptime", uptime);

            Log.Debug($"Опубликованы данные: Version={version}, Uptime={uptime}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка при публикации периодических данных.");

        }
    }

    private async Task PublishSingleAsync(string topic, string payload)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build();

        await _mqttClient.PublishAsync(message);
    }

    public async Task StopAsync()
    {
        Log.Information("Корректное завершение работы MQTT сервиса...");
        _publishTimer?.Dispose();
        
        if (_isConnected)
        {
            await PublishStatusAsync("OFFLINE");
            await _mqttClient.DisconnectAsync();
        }
    }

    public void Dispose()
    {
        _mqttClient?.Dispose();
    }
}