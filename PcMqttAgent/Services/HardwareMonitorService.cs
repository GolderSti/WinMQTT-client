using System;
using LibreHardwareMonitor.Hardware;
using Serilog;

namespace PcMqttAgent.Services;

public class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor;

    public HardwareMonitorService()
    {
        _updateVisitor = new UpdateVisitor();
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = false 
        };
        
        _computer.Open();
        Log.Information("Hardware Monitor инициализирован.");
    }

    public void Update()
    {
        _computer.Accept(_updateVisitor);
    }

    public float? GetCpuLoad()
    {
        return GetSensorValue(HardwareType.Cpu, SensorType.Load);
    }

    public float? GetCpuTemperature()
    {
        return GetSensorValue(HardwareType.Cpu, SensorType.Temperature, "CPU Package");
    }

    public float? GetGpuTemperature()
    {
        float? temp = GetSensorValue(HardwareType.GpuNvidia, SensorType.Temperature, "GPU Core");
        if (temp == null)
        {
            temp = GetSensorValue(HardwareType.GpuAmd, SensorType.Temperature, "GPU Core");
        }
        return temp;
    }

    public float? GetRamLoad()
    {
        return GetSensorValue(HardwareType.Memory, SensorType.Load);
    }

    // ИСПРАВЛЕНИЕ 1: Добавлен '?' к string, чтобы разрешить null
    private float? GetSensorValue(HardwareType hardwareType, SensorType sensorType, string? nameContains = null)
    {
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType == hardwareType)
            {
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == sensorType)
                    {
                        if (string.IsNullOrEmpty(nameContains) || sensor.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                        {
                            if (sensor.Value.HasValue)
                            {
                                // ИСПРАВЛЕНИЕ 2: Явное приведение double к float
                                return (float)Math.Round(sensor.Value.Value, 1);
                            }
                        }
                    }
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        _computer.Close();
    }
}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) { computer.Traverse(this); }
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}