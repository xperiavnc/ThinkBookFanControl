using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace ThinkBookFanControl;

public sealed class TemperatureReader : IDisposable
{
    private static readonly TimeSpan SensorCacheRefreshInterval = TimeSpan.FromMinutes(1);
    private readonly Computer _computer;
    private readonly List<TemperatureSensor> _sensors = [];
    private DateTimeOffset _lastSensorCacheRefresh;

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
        RefreshSensorCacheIfNeeded();
        UpdateCachedHardware();

        var cpu = PickCpuSensor(_sensors);
        var gpu = PickSensor(_sensors, ["gpu core"]);
        var vram = PickSensor(_sensors, ["gpu memory junction", "memory junction", "gpu memory", "vram"]);
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

    private void RefreshSensorCacheIfNeeded()
    {
        var now = DateTimeOffset.Now;
        if (_sensors.Count > 0 && now - _lastSensorCacheRefresh < SensorCacheRefreshInterval)
            return;

        _sensors.Clear();
        foreach (var hardware in _computer.Hardware)
            CollectHardware(hardware, "", _sensors);
        _lastSensorCacheRefresh = now;
    }

    private void UpdateCachedHardware()
    {
        var updated = new HashSet<IHardware>();
        foreach (var sensor in _sensors)
        {
            if (updated.Add(sensor.Hardware))
                sensor.Hardware.Update();
        }
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
                    sensor,
                    hardware,
                    sensor.Name,
                    hardware.Name,
                    hardware.HardwareType,
                    hardwarePath + "/" + sensor.Name));
            }
        }

        foreach (var subHardware in hardware.SubHardware)
            CollectHardware(subHardware, hardwarePath, sensors);
    }

    private static (TemperatureSensor? Sensor, string Name) PickSensor(IEnumerable<TemperatureSensor> sensors, string[] patterns)
    {
        var selected = sensors
            .Where(sensor => sensor.ValueC is not null)
            .Where(sensor => patterns.Any(pattern =>
                (sensor.Name + " " + sensor.Identifier).Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(sensor => sensor.ValueC!.Value)
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
            .Where(sensor => sensor.ValueC is not null)
            .OrderByDescending(sensor => sensor.ValueC)
            .Select(sensor => ((TemperatureSensor?)sensor, sensor.Identifier))
            .FirstOrDefault();

        if (selected.Item1 is not null)
            return selected;

        return PickSensor(sensors, ["cpu package", "core max", "cpu core", "tctl", "tdie"]);
    }

    private sealed record TemperatureSensor(ISensor Sensor, IHardware Hardware, string Name, string HardwareName, HardwareType HardwareType, string Identifier)
    {
        public double? ValueC => Sensor.Value;
    }
}
