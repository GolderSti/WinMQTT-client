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
            IsMotherboardEnabled = false // Отключаем, чтобы не засорять лог лишними данными платы
        };
        
        _computer.Open();
        _computer.Accept(_updateVisitor);
        
        Log.Information("Hardware Monitor инициализирован.");
    }

    public void Update()
    {
        _computer.Accept(_updateVisitor);
    }

    public float? GetCpuLoad() => GetSensorValue(HardwareType.Cpu, SensorType.Load, "CPU Total");
    
    public float? GetCpuTemperature() 
    {
        // Ищем именно так, как датчик называется в вашем логе
        return GetSensorValue(HardwareType.Cpu, SensorType.Temperature, "Core (Tctl/Tdie)");
    }
    
    public float? GetGpuTemperature() 
    {
        // Для встроенной AMD Radeon Graphics датчика температуры обычно не существует.
        // Оставляем поиск на всякий случай, но ожидаемо получим null.
        float? temp = GetSensorValue(HardwareType.GpuAmd, SensorType.Temperature, "GPU Core");
        if (temp == null)
        {
            temp = GetSensorValue(HardwareType.GpuNvidia, SensorType.Temperature, "GPU Core");
        }
        return temp;
    }
    
    public float? GetRamLoad() 
    {
        // В логе есть "Total Memory" и "Virtual Memory". Берем общую загрузку памяти.
        return GetSensorValue(HardwareType.Memory, SensorType.Load, "Total Memory") 
               ?? GetSensorValue(HardwareType.Memory, SensorType.Load);
    }

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