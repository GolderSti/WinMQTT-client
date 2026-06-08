using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    private readonly CancellationTokenSource _cts = new();
    
    // Настройки экспоненциальной задержки (в миллисекундах)
    private const int BaseDelayMs = 2000;      // Начальная задержка 2 сек
    private const int MaxDelayMs = 60000;      // Максимальная задержка 60 сек
    private const int JitterMs = 2000;         // Случайная добавка от 0 до 2 сек
    private int _reconnectAttempts = 0;
    private readonly Random _random = new();

    private Timer? _publishTimer;
    private bool _isConnected;
    private bool _isStopping;

    // Событие для уведомления UI об изменении статуса подключения
    public event Action<bool>? ConnectionStateChanged;

    private string PowerTopic => $"/{_settings.Mqtt.BaseTopic.Trim('/')}/POWER";
    private string PowerSetTopic => $"/{_settings.Mqtt.BaseTopic.Trim('/')}/POWER/SET";
    private string CommandTopic => $"{_settings.Mqtt.BaseTopic}/command";
    private string AckTopic => $"{_settings.Mqtt.BaseTopic}/command/ack";

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

        await _mqttClient.ConnectAsync(options, _cts.Token);
    }

    private async Task OnConnectedAsync(MqttClientConnectedEventArgs e)
    {
        _isConnected = true;
        _reconnectAttempts = 0; // Сбрасываем счетчик при успешном подключении
        Log.Information("Успешно подключено к MQTT брокеру.");
        
        NotifyConnectionStateChanged(true);
        await PublishPowerStateAsync("ON").ConfigureAwait(false);
        await ClearRetainedCommandsAsync().ConfigureAwait(false);

        await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(CommandTopic).Build()).ConfigureAwait(false);
        await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(PowerSetTopic).Build()).ConfigureAwait(false);

        if (_publishTimer == null)
        {
            _publishTimer = new Timer(
                async _ => await PublishPeriodicDataAsync().ConfigureAwait(false), 
                null, 
                TimeSpan.Zero, 
                TimeSpan.FromSeconds(_settings.Publisher.IntervalSeconds)
            );
        }
        else
        {
            _publishTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(_settings.Publisher.IntervalSeconds));
        }
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        _isConnected = false;
        NotifyConnectionStateChanged(false);
        _publishTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (_isStopping || _cts.IsCancellationRequested)
        {
            Log.Information("MQTT сервис отключен штатно.");
            return;
        }

        // Экспоненциальная задержка со случайной компонентой (Jitter)
        _reconnectAttempts++;
        int delayMs = (int)Math.Min(MaxDelayMs, BaseDelayMs * Math.Pow(2, _reconnectAttempts - 1));
        int jitter = _random.Next(0, JitterMs);
        int totalDelayMs = delayMs + jitter;

        Log.Warning($"Соединение разорвано. Переподключение через {totalDelayMs / 1000.0:F1} сек. (Попытка #{_reconnectAttempts})");

        try
        {
            await Task.Delay(totalDelayMs, _cts.Token);
            await _mqttClient.ConnectAsync(_mqttClient.Options, _cts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            Log.Information("Переподключение отменено (закрытие приложения).");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка переподключения к MQTT.");
        }
    }

    private void NotifyConnectionStateChanged(bool isConnected)
    {
        // Безопасный вызов события из любого потока
        ConnectionStateChanged?.Invoke(isConnected);
    }

    private async Task ClearRetainedCommandsAsync()
    {
        await PublishSingleAsync(CommandTopic, "", true).ConfigureAwait(false);
        await PublishSingleAsync(PowerSetTopic, "", true).ConfigureAwait(false);
        Log.Information("Retain-команды на брокере очищены.");
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment)
                                    .Replace("\n", "").Replace("\r", "").Trim();
        var topic = e.ApplicationMessage.Topic;

        if (string.IsNullOrWhiteSpace(payload)) return;

        Log.Information($"Получена команда по топику {topic}: '{payload}'");
        await PublishAckAsync(payload).ConfigureAwait(false);

        if (topic.Equals(PowerSetTopic, StringComparison.OrdinalIgnoreCase))
        {
            if (payload.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            {
                await ProcessPowerCommandAsync("shutdown", "/s /f /t 0").ConfigureAwait(false);
            }
            return;
        }

        if (topic.Equals(CommandTopic, StringComparison.OrdinalIgnoreCase))
        {
            switch (payload.ToLowerInvariant())
            {
                case "shutdown":
                    await ProcessPowerCommandAsync("shutdown", "/s /f /t 0").ConfigureAwait(false);
                    break;
                case "reboot":
                case "restart":
                    await ProcessPowerCommandAsync("reboot", "/r /f /t 0").ConfigureAwait(false);
                    break;
                default:
                    Log.Warning($"Неизвестная команда: '{payload}'");
                    break;
            }
        }
    }

    private async Task ProcessPowerCommandAsync(string actionName, string shutdownArgs)
    {
        Log.Warning($"Инициализация процесса {actionName.ToUpper()} с предупреждением пользователя.");

        bool? userConfirmed = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new ShutdownWarningWindow(actionName);
            return dialog.ShowDialog();
        });

        if (userConfirmed == true)
        {
            Log.Information("Пользователь подтвердил действие. Выполнение...");
            //await PrepareForPowerStateChangeAsync().ConfigureAwait(false);
            ExecuteSystemCommand(shutdownArgs);
        }
        else
        {
            Log.Information("Пользователь отменил действие.");
            await PublishCanceledAsync(actionName).ConfigureAwait(false);
        }
    }

    private async Task PrepareForPowerStateChangeAsync()
    {
        await PublishOffAndDisconnectAsync().ConfigureAwait(false);
    }

    public async Task HandleSessionEndingAsync()
    {
        Log.Warning("Получено уведомление SessionEnding. Публикуем OFF и отключаемся.");
        await PublishOffAndDisconnectAsync().ConfigureAwait(false);
    }

    private async Task PublishOffAndDisconnectAsync()
    {
        _isStopping = true;
        _publishTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (!_isConnected) return;

        var publishSucceeded = false;
        try
        {
            await PublishPowerStateAsync("OFF").WaitAsync(MqttPowerChangeTimeout).ConfigureAwait(false);
            publishSucceeded = true;
        }
        catch (TimeoutException)
        {
            Log.Warning($"Публикация OFF не завершилась за {MqttPowerChangeTimeout.TotalSeconds} сек. Рассчитываем на LWT.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Не удалось опубликовать OFF в {PowerTopic}.");
        }

        if (!publishSucceeded) return;

        try
        {
            await _mqttClient.DisconnectAsync().WaitAsync(MqttPowerChangeTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось штатно отключиться от MQTT.");
        }
    }

    private void ExecuteSystemCommand(string arguments)
    {
        try
        {
            Log.Information($"Выполнение: C:\\Windows\\System32\\shutdown.exe {arguments}");
            var startInfo = new ProcessStartInfo
            {
                FileName = "C:\\Windows\\System32\\shutdown.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                Log.Error("Не удалось запустить shutdown.exe: Process.Start вернул null.");
            }
            else
            {
                Log.Information("Команда shutdown.exe успешно передана ОС.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "КРИТИЧЕСКАЯ ОШИБКА: Не удалось запустить shutdown.exe.");
        }
    }

    private async Task PublishPowerStateAsync(string state)
    {
        if (!_isConnected) return;
        await PublishSingleAsync(PowerTopic, state, true).ConfigureAwait(false);
    }

    private async Task PublishAckAsync(string command)
    {
        if (!_isConnected) return;
        await PublishSingleAsync(AckTopic, $"received: {command}", false).ConfigureAwait(false);
    }

    private async Task PublishCanceledAsync(string command)
    {
        if (!_isConnected) return;
        await PublishSingleAsync(AckTopic, $"canceled: {command}", false).ConfigureAwait(false);
    }

    private async Task PublishPeriodicDataAsync()
    {
        if (!_isConnected || App.HardwareMonitor == null) return;

        try
        {
            App.HardwareMonitor.Update();
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"dd\.hh\:mm\:ss");
            
            await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/version", version, true).ConfigureAwait(false);
            await PublishSingleAsync($"{_settings.Mqtt.BaseTopic}/info/uptime", uptime, true).ConfigureAwait(false);

            foreach (var sensorConfig in App.Settings.Sensors.Where(s => s.Enabled))
            {
                var value = App.HardwareMonitor.GetValue(sensorConfig);
                if (value.HasValue)
                {
                    string formattedValue = FormatValue(value.Value, sensorConfig.SensorType);
                    string topic = $"{_settings.Mqtt.BaseTopic}/info/{sensorConfig.TopicSuffix}";
                    await PublishSingleAsync(topic, formattedValue, true).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка при публикации периодических данных.");
        }
    }

    private string FormatValue(float value, string sensorType)
    {
        if (sensorType.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) return $"{value}";
        if (sensorType.Equals("Load", StringComparison.OrdinalIgnoreCase)) return $"{value}";
        if (sensorType.Equals("Data", StringComparison.OrdinalIgnoreCase)) return $"{value}";
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

        await _mqttClient.PublishAsync(message).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        Log.Information("Завершение работы MQTT сервиса (штатный выход)...");
        await PrepareForPowerStateChangeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel(); // Прерываем любые ожидающие Task.Delay
        _cts.Dispose();
        _mqttClient?.Dispose();
        _publishTimer?.Dispose();
    }
}