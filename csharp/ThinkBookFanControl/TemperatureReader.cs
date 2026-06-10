using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace ThinkBookFanControl;

public sealed class TemperatureReader : IDisposable
{
    private readonly Computer _computer;

    public TemperatureReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true
        };
        _computer.Open();
    }

    public TemperatureSnapshot Read()
    {
        var sensors = new List<TemperatureSensor>();
        foreach (var hardware in _computer.Hardware)
            CollectHardware(hardware, "", sensors);

        var cpu = PickCpuSensor(sensors);
        var gpu = PickSensor(sensors, ["gpu core"]);
        var vram = PickSensor(sensors, ["gpu memory junction", "memory junction", "gpu memory", "vram"]);
        return new TemperatureSnapshot(
            cpu.Sensor?.ValueC,
            gpu.Sensor?.ValueC,
            vram.Sensor?.ValueC,
            cpu.Name,
            gpu.Name,
            vram.Name);
    }

    public void Dispose()
    {
        _computer.Close();
    }

    private static void CollectHardware(IHardware hardware, string path, List<TemperatureSensor> sensors)
    {
        hardware.Update();
        var hardwarePath = string.IsNullOrWhiteSpace(path) ? hardware.Name : path + "/" + hardware.Name;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature && sensor.Value is not null)
            {
                sensors.Add(new TemperatureSensor(
                    sensor.Name,
                    hardware.Name,
                    hardware.HardwareType,
                    hardwarePath + "/" + sensor.Name,
                    sensor.Value.Value));
            }
        }

        foreach (var subHardware in hardware.SubHardware)
            CollectHardware(subHardware, hardwarePath, sensors);
    }

    private static (TemperatureSensor? Sensor, string Name) PickSensor(IEnumerable<TemperatureSensor> sensors, string[] patterns)
    {
        var selected = sensors
            .Where(sensor => patterns.Any(pattern =>
                (sensor.Name + " " + sensor.Identifier).Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(sensor => sensor.ValueC)
            .FirstOrDefault();

        return selected is null ? (null, "not found") : (selected, selected.Identifier);
    }

    private static (TemperatureSensor? Sensor, string Name) PickCpuSensor(IEnumerable<TemperatureSensor> sensors)
    {
        var cpuSensors = sensors
            .Where(sensor => sensor.HardwareType == HardwareType.Cpu)
            .ToList();

        var selected = PickSensor(cpuSensors, ["cpu package", "package", "core max", "cpu core", "tctl", "tdie"]);
        if (selected.Sensor is not null)
            return selected;

        selected = cpuSensors
            .OrderByDescending(sensor => sensor.ValueC)
            .Select(sensor => ((TemperatureSensor?)sensor, sensor.Identifier))
            .FirstOrDefault();

        if (selected.Item1 is not null)
            return selected;

        return PickSensor(sensors, ["cpu package", "core max", "cpu core", "tctl", "tdie"]);
    }

    private sealed record TemperatureSensor(string Name, string HardwareName, HardwareType HardwareType, string Identifier, double ValueC);
}
