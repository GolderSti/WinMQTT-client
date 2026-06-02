namespace PcMqttAgent.Models;

public class AppSettings
{
    public MqttSettings Mqtt { get; set; } = new();
    public PublisherSettings Publisher { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
}

public class MqttSettings
{
    public string Server { get; set; } = "mqtt-server.l";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "MAINPC";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string BaseTopic { get; set; } = "MAINPC";
}

public class PublisherSettings
{
    public int IntervalSeconds { get; set; } = 60;
}

public class LoggingSettings
{
    public string FilePath { get; set; } = "logs/agent-.log";
    public string LogLevel { get; set; } = "Information";
}