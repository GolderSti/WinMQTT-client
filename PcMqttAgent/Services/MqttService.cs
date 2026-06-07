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
    private static readonly TimeSpan MqttPowerChangeTimeout = TimeSpan.FromSeconds(2);

    private readonly HardwareMonitorService _hardwareMonitor;
    private Timer? _publishTimer;
    private bool _isConnected;
    private bool _isStopping;

    private string PowerTopic => $"/{_settings.Mqtt.BaseTopic.Trim('/')}/POWER";
    private string PowerSetTopic => $"/{_settings.Mqtt.BaseTopic.Trim('/')}/POWER/SET";

    public MqttService(AppSettings settings)
    {
        _settings = settings;
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();
        _hardwareMonitor = new HardwareMonitorService();

        _mqttClient.ConnectedAsync += OnConnectedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public async Task StartAsync()
    {
        Log.Information("Запуск MQTT сервиса...");
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.Mqtt.Server, _settings.Mqtt.Port)
            .WithClientId(_settings.Mqtt.ClientId)
            .WithCredentials(_settings.Mqtt.User, _settings.Mqtt.Password)
            .WithCleanSession()
            .WithWillTopic(PowerTopic)
            .WithWillPayload("OFF")
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain(true)
            .Build();

        await _mqttClient.ConnectAsync(options, CancellationToken.None);
    }

    private async Task OnConnectedAsync(MqttClientConnectedEventArgs e)
    {
        _isConnected = true;
        Log.Information("Успешно подключено к MQTT брокеру.");
        
        await PublishPowerStateAsync("ON");

        var commandTopic = $"{_settings.Mqtt.BaseTopic}/command";
        await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(commandTopic).Build());
        Log.Information($"Подписка на топик команд: {commandTopic}");

        await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(PowerSetTopic).Build());
        Log.Information($"Подписка на топик управления питанием: {PowerSetTopic}");

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
        _publishTimer?.Dispose();

        if (_isStopping)
        {
            Log.Information("MQTT сервис отключен штатно.");
            return;
        }

        Log.Warning("Соединение с MQTT брокером разорвано. Переподключение через 5 сек...");
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

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment).Trim();
        Log.Information($"Получена команда по топику {topic}: {payload}");

        if (topic.Equals(PowerSetTopic, StringComparison.OrdinalIgnoreCase))
        {
            if (payload.Equals("OFF", StringComparison.OrdinalIgnoreCase))
//If a retained OFF message ever exists on /POWER/SET (for example from mosquitto_pub -r or a retained automation command), the broker will deliver it immediately after every subscribe/reconnect and this handler will shut the machine down without a fresh command. Command topics should reject retained deliveries or clear the retained command after processing so a stale control message cannot repeat on every agent start.
            {
                Log.Warning("Получена команда ВЫКЛЮЧЕНИЯ ПК через топик управления питанием.");
                await PrepareForPowerStateChangeAsync();
                ExecuteSystemCommand("/s /t 0");
            }
            else
            {
                Log.Warning($"Неизвестная команда управления питанием: {payload}");
            }

            return;
        }

        if (topic.EndsWith("/command", StringComparison.OrdinalIgnoreCase))
        {
            switch (payload.ToLowerInvariant())
            {
                case "shutdown":
                    Log.Warning("Получена команда ВЫКЛЮЧЕНИЯ ПК.");
                    await PrepareForPowerStateChangeAsync();
                    ExecuteSystemCommand("/s /t 0");
                    break;
                case "reboot":
                case "restart":
                    Log.Warning("Получена команда ПЕРЕЗАГРУЗКИ ПК.");
                    await PrepareForPowerStateChangeAsync();
                    ExecuteSystemCommand("/r /t 0");
                    break;
                default:
                    Log.Warning($"Неизвестная команда: {payload}");
                    break;
            }
        }
        
        return;
    }
    

    private async Task PrepareForPowerStateChangeAsync()
    {
        _isStopping = true;
        _publishTimer?.Dispose();

        if (!_isConnected)
        {
            return;
        }

        try
        {
            await PublishPowerStateAsync("OFF").WaitAsync(MqttPowerChangeTimeout);
        }
        catch (TimeoutException)
        {
            Log.Warning($"Публикация OFF в топик {PowerTopic} не завершилась за {MqttPowerChangeTimeout.TotalSeconds} сек. Продолжаем выполнение команды питания.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Не удалось опубликовать OFF в топик {PowerTopic} перед изменением состояния питания.");
        }

        try
        {
            await _mqttClient.DisconnectAsync().WaitAsync(MqttPowerChangeTimeout);
        }
        catch (TimeoutException)
        {
            Log.Warning($"Отключение от MQTT брокера не завершилось за {MqttPowerChangeTimeout.TotalSeconds} сек. Продолжаем выполнение команды питания.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Не удалось штатно отключиться от MQTT брокера перед изменением состояния питания.");
        }
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

    private async Task PublishPowerStateAsync(string state)
    {
        if (!_isConnected) return;
        await PublishSingleAsync(PowerTopic, state, true);
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

            Log.Information($"Статус: CPU={cpuLoad?.ToString() ?? "N/A"}%, RAM={ramLoad?.ToString() ?? "N/A"}%, CPU_T={cpuTemp?.ToString() ?? "N/A"}°C, GPU_T={gpuTemp?.ToString() ?? "N/A"}°C");
        }
        catch (Exception ex)
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
        await PrepareForPowerStateChangeAsync();
        _hardwareMonitor.Dispose();
    }

    public void Dispose()
    {
        _mqttClient?.Dispose();
    }
}