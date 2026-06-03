using System;
using System.Diagnostics;
using System.Text;
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
    private readonly HardwareMonitorService _hardwareMonitor;
    private Timer? _publishTimer;
    private bool _isConnected;

    public MqttService(AppSettings settings)
    {
        _settings = settings;
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();
        _hardwareMonitor = new HardwareMonitorService();

        _mqttClient.ConnectedAsync += OnConnectedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync; // <-- Новая строка
    }

    public async Task StartAsync()
    {
        Log.Information("Запуск MQTT сервиса...");
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.Mqtt.Server, _settings.Mqtt.Port)
            .WithClientId(_settings.Mqtt.ClientId)
            .WithCredentials(_settings.Mqtt.User, _settings.Mqtt.Password)
            .WithCleanSession()
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
        
        await PublishStatusAsync("ONLINE");

        // <-- ПОДПИСКА НА КОМАНДЫ
        var topic = $"{_settings.Mqtt.BaseTopic}/command";
        await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(topic).Build());
        Log.Information($"Подписка на топик команд: {topic}");

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
        Log.Warning("Соединение с MQTT брокером разорвано. Переподключение через 5 сек...");
        _publishTimer?.Dispose();

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

    // <-- ОБРАБОТКА ВХОДЯЩИХ КОМАНД
private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
        Log.Information($"Получена команда по топику {topic}: {payload}");

        if (topic.EndsWith("/command", StringComparison.OrdinalIgnoreCase))
        {
            switch (payload.ToLower().Trim())
            {
                case "shutdown":
                    Log.Warning("Получена команда ВЫКЛЮЧЕНИЯ ПК.");
                    ExecuteSystemCommand("/s /t 0");
                    break;
                case "reboot":
                case "restart":
                    Log.Warning("Получена команда ПЕРЕЗАГРУЗКИ ПК.");
                    ExecuteSystemCommand("/r /t 0");
                    break;
                default:
                    Log.Warning($"Неизвестная команда: {payload}");
                    break;
            }
        }
        
        return Task.CompletedTask;
    }
    
    private void ExecuteSystemCommand(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Не удалось выполнить системную команду. Возможно, недостаточно прав.");
        }
    }

    private async Task PublishStatusAsync(string status)
    {
        if (!_isConnected) return;
        await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/status", status, true);
    }

    private async Task PublishPeriodicDataAsync()
    {
        if (!_isConnected) return;

try
        {
            _hardwareMonitor.Update();

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"dd\.hh\:mm\:ss");
            
            var cpuLoad = _hardwareMonitor.GetCpuLoad();
            var ramLoad = _hardwareMonitor.GetRamLoad();
            var cpuTemp = _hardwareMonitor.GetCpuTemperature();
            var gpuTemp = _hardwareMonitor.GetGpuTemperature();

            await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/version", version, true);
            await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/uptime", uptime, true);
            
            if (cpuLoad.HasValue) await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/cpu", $"{cpuLoad}%", true);
            if (ramLoad.HasValue) await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/ram", $"{ramLoad}%", true);
            if (cpuTemp.HasValue) await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/temp/cpu", $"{cpuTemp}°C", true);
            if (gpuTemp.HasValue) await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/temp/gpu", $"{gpuTemp}°C", true);

            // ИЗМЕНЕНО: Log.Information вместо Log.Debug
            Log.Information($"Статус: CPU={cpuLoad?.ToString() ?? "N/A"}%, RAM={ramLoad?.ToString() ?? "N/A"}%, CPU_T={cpuTemp?.ToString() ?? "N/A"}°C, GPU_T={gpuTemp?.ToString() ?? "N/A"}°C");
        }        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка при публикации периодических данных.");
        }
    }

    private async Task PublishSingleAsync(string topic, string payload, bool retain = true)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        await _mqttClient.PublishAsync(message);
    }

    public async Task StopAsync()
    {
        Log.Information("Завершение работы MQTT сервиса...");
        _publishTimer?.Dispose();
        _hardwareMonitor.Dispose();
        
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