using System;
using System.Diagnostics;
using System.Linq;
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

        Log.Warning("Соединение разорвано. Переподключение через 5 сек...");
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
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment)
                                   .Replace("\n", "").Replace("\r", "").Trim();
        var topic = e.ApplicationMessage.Topic;
        Log.Information($"Получена команда по топику {topic}: '{payload}'");

        if (topic.Equals(PowerSetTopic, StringComparison.OrdinalIgnoreCase))
        {
            if (payload.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Получена команда ВЫКЛЮЧЕНИЯ ПК через топик управления питанием.");
                await PrepareForPowerStateChangeAsync();
                ExecuteSystemCommand("/s /f /t 0"); // /f принудительно закрывает приложения
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
                    ExecuteSystemCommand("/s /f /t 0");
                    break;
                case "reboot":
                case "restart":
                    Log.Warning("Получена команда ПЕРЕЗАГРУЗКИ ПК.");
                    await PrepareForPowerStateChangeAsync();
                    ExecuteSystemCommand("/r /f /t 0");
                    break;
                default:
                    Log.Warning($"Неизвестная команда: '{payload}'");
                    break;
            }
        }
    }

    private async Task PrepareForPowerStateChangeAsync()
    {
        _isStopping = true;
        _publishTimer?.Dispose();

        if (!_isConnected) return;

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
            Log.Error(ex, $"Не удалось опубликовать OFF в {PowerTopic} перед изменением состояния питания.");
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
            Log.Warning(ex, "Не удалось штатно отключиться от MQTT, продолжаем выключение.");
        }
    }

    private void ExecuteSystemCommand(string arguments)
    {
        try
        {
            Log.Information($"Выполнение: shutdown.exe {arguments}");
            var startInfo = new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(startInfo);
            Log.Information("Команда shutdown.exe успешно передана ОС.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "КРИТИЧЕСКАЯ ОШИБКА: Не удалось запустить shutdown.exe.");
        }
    }

    private async Task PublishPowerStateAsync(string state)
    {
        if (!_isConnected) return;
        await PublishSingleAsync(PowerTopic, state, true);
    }

    private async Task PublishPeriodicDataAsync()
    {
        if (!_isConnected || App.HardwareMonitor == null) return;

        try
        {
            App.HardwareMonitor.Update();

            // Публикация версии и uptime (всегда)
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"dd\.hh\:mm\:ss");
            await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/version", version, true);
            await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/uptime", uptime, true);

            // Динамическая публикация только ВКЛЮЧЕННЫХ датчиков из конфига
            foreach (var sensorConfig in App.Settings.Sensors.Where(s => s.Enabled))
            {
                var value = App.HardwareMonitor.GetValue(sensorConfig);
                if (value.HasValue)
                {
                    string formattedValue = FormatValue(value.Value, sensorConfig.SensorType);
                    string topic = $"{_settings.Mqtt.BaseTopic}/info/{sensorConfig.TopicSuffix}";
                    await PublishSingleAsync(topic, formattedValue, true);
                }
            }
            
            Log.Information("Периодические данные успешно опубликованы.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка при публикации периодических данных.");
        }
    }

    private string FormatValue(float value, string sensorType)
    {
        // Автоматическое добавление единиц измерения для удобства
        if (sensorType.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) return $"{value}°C";
        if (sensorType.Equals("Load", StringComparison.OrdinalIgnoreCase)) return $"{value}%";
        if (sensorType.Equals("Data", StringComparison.OrdinalIgnoreCase)) return $"{value} MB"; // Или GB, зависит от датчика
        return value.ToString();
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
    }

    public void Dispose()
    {
        _mqttClient?.Dispose();
    }
}