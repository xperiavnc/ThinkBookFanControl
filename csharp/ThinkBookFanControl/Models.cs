using System;
using System.Collections.Generic;

namespace ThinkBookFanControl;

public sealed record FanLimit(string Fan, uint Id, int MinRpm, int MaxRpm);

public sealed record FanSnapshot(
    DateTimeOffset Timestamp,
    int Fan1Rpm,
    int Fan2Rpm,
    IReadOnlyDictionary<string, FanLimit> Limits);

public sealed record TemperatureSnapshot(
    double? CpuTempC,
    double? GpuTempC,
    double? VramTempC,
    string CpuSensor,
    string GpuSensor,
    string VramSensor);

public sealed record FanTargets(int Fan1Rpm, int Fan2Rpm);

public sealed class FanProfile
{
    public string Name { get; set; } = "";
    public double TemperatureSmoothing { get; set; } = 3;
    public double RampDownRpmPerSecond { get; set; } = 20;
    public List<int> CpuFan1Curve { get; set; } = [];
    public List<int> CpuFan2Curve { get; set; } = [];
    public List<int> GpuFan1Curve { get; set; } = [];
    public List<int> GpuFan2Curve { get; set; } = [];
    public List<int> CpuCurve { get; set; } = [];
    public List<int> GpuCurve { get; set; } = [];
}

public sealed class AppSettings
{
    public string Language { get; set; } = "zh-CN";
    public string Theme { get; set; } = "light";
    public double IntervalSeconds { get; set; } = 2.0;
    public int LastProfileIndex { get; set; }
    public int EditFan { get; set; } = 1;
    public bool SyncFanSpeeds { get; set; }
    public bool ResumeFanControlOnNextStart { get; set; }
    public bool FanControlWasRunning { get; set; }
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool CloseToTray { get; set; }
}
