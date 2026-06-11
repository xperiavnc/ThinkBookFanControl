using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ThinkBookFanControl;

public sealed class FanController
{
    private const string NamespacePath = @"root\wmi";
    private const uint Fan1Id = 0x04030001;
    private const uint Fan2Id = 0x04030002;
    private IReadOnlyDictionary<string, FanLimit>? _cachedLimits;
    private ReadFeatureMode? _readFeatureMode;
    private SetFeatureMode? _setFeatureMode;

    public FanSnapshot ReadSnapshot()
    {
        var limits = ReadLimits();
        using var other = FindActiveOtherMethod();
        var fan1 = ReadFeatureValue(other, Fan1Id);
        var fan2 = ReadFeatureValue(other, Fan2Id);
        return new FanSnapshot(DateTimeOffset.Now, fan1, fan2, limits);
    }

    public IReadOnlyDictionary<string, FanLimit> ReadLimits()
    {
        return _cachedLimits ??= ReadFanLimits();
    }

    public void ApplyBoth(int rpm)
    {
        Apply(rpm, rpm);
    }

    public void Apply(int fan1Rpm, int fan2Rpm)
    {
        using var other = FindActiveOtherMethod();
        SetFeatureValue(other, Fan1Id, fan1Rpm);
        SetFeatureValue(other, Fan2Id, fan2Rpm);
    }

    public void RestoreAuto()
    {
        ApplyBoth(0);
    }

    public static int ClampForBoth(int rpm, IReadOnlyDictionary<string, FanLimit> limits)
    {
        var minimum = Math.Max(limits["fan1"].MinRpm, limits["fan2"].MinRpm);
        var maximum = Math.Min(limits["fan1"].MaxRpm, limits["fan2"].MaxRpm);
        return Math.Max(minimum, Math.Min(maximum, rpm));
    }

    public static (int MinRpm, int MaxRpm) SharedRange(IReadOnlyDictionary<string, FanLimit> limits)
    {
        return (
            Math.Max(limits["fan1"].MinRpm, limits["fan2"].MinRpm),
            Math.Min(limits["fan1"].MaxRpm, limits["fan2"].MaxRpm));
    }

    private static ManagementObject FindActiveOtherMethod()
    {
        using var searcher = new ManagementObjectSearcher(NamespacePath, "SELECT * FROM LENOVO_OTHER_METHOD");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            if (IsActive(item))
            {
                var activeItem = (ManagementObject)item.Clone();
                item.Dispose();
                return activeItem;
            }
            item.Dispose();
        }

        throw new InvalidOperationException("No active LENOVO_OTHER_METHOD instance found.");
    }

    private static IReadOnlyDictionary<string, FanLimit> ReadFanLimits()
    {
        using var searcher = new ManagementObjectSearcher(NamespacePath, "SELECT * FROM LENOVO_FAN_TEST_DATA");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                if (!IsActive(item))
                    continue;

                var ids = ToIntArray(item["FanId"]);
                var mins = ToIntArray(item["FanMinSpeed"]);
                var maxes = ToIntArray(item["FanMaxSpeed"]);
                if (ids.Length < 2 || mins.Length < 2 || maxes.Length < 2)
                    break;

                return new Dictionary<string, FanLimit>
                {
                    ["fan1"] = new("fan1", Fan1Id, mins[0], maxes[0]),
                    ["fan2"] = new("fan2", Fan2Id, mins[1], maxes[1]),
                };
            }
        }

        throw new InvalidOperationException("No active LENOVO_FAN_TEST_DATA instance found.");
    }

    private int ReadFeatureValue(ManagementObject other, uint id)
    {
        var errors = new List<string>();

        if (_readFeatureMode is ReadFeatureMode cachedMode)
        {
            if (TryReadFeatureValue(other, id, cachedMode, out var cachedValue, out var cachedError))
                return cachedValue;

            errors.Add($"{DescribeReadMode(cachedMode)} cached: {cachedError}");
            _readFeatureMode = null;
        }

        foreach (var mode in Enum.GetValues<ReadFeatureMode>())
        {
            if (TryReadFeatureValue(other, id, mode, out var value, out var error))
            {
                _readFeatureMode = mode;
                return Convert.ToInt32(value);
            }

            errors.Add($"{DescribeReadMode(mode)}: {error}");
        }

        throw new InvalidOperationException($"GetFeatureValue(0x{id:X8}) failed. " + string.Join(" | ", errors));
    }

    private void SetFeatureValue(ManagementObject other, uint id, int value)
    {
        var errors = new List<string>();

        if (_setFeatureMode is SetFeatureMode cachedMode)
        {
            if (TrySetFeatureValue(other, id, value, cachedMode, out var cachedError))
                return;

            errors.Add($"{DescribeSetMode(cachedMode)} cached: {cachedError}");
            _setFeatureMode = null;
        }

        foreach (var mode in Enum.GetValues<SetFeatureMode>())
        {
            if (TrySetFeatureValue(other, id, value, mode, out var error))
            {
                _setFeatureMode = mode;
                return;
            }

            errors.Add($"{DescribeSetMode(mode)}: {error}");
        }

        throw new InvalidOperationException($"SetFeatureValue(0x{id:X8}, {value}) failed. " + string.Join(" | ", errors));
    }

    private static bool TryReadFeatureValue(ManagementObject other, uint id, ReadFeatureMode mode, out int value, out string error)
    {
        value = 0;
        try
        {
            switch (mode)
            {
                case ReadFeatureMode.PositionalOut:
                {
                    var args = new object?[] { id, null };
                    other.InvokeMethod("GetFeatureValue", args);
                    if (args.Length > 1 && args[1] is not null)
                    {
                        value = Convert.ToInt32(args[1]);
                        error = "";
                        return true;
                    }

                    error = "returned no out value";
                    return false;
                }
                case ReadFeatureMode.PositionalReturn:
                {
                    var result = other.InvokeMethod("GetFeatureValue", new object?[] { id });
                    if (result is not null)
                    {
                        value = Convert.ToInt32(result);
                        error = "";
                        return true;
                    }

                    error = "returned null";
                    return false;
                }
                case ReadFeatureMode.Named:
                {
                    using var inParams = other.GetMethodParameters("GetFeatureValue");
                    SetParameter(inParams, ["Data", "Id", "ID", "FeatureId", "AttributeId"], id);
                    using var outParams = other.InvokeMethod("GetFeatureValue", inParams, null);
                    if (TryGetParameter(outParams, ["value", "Value", "Data"], out var parameterValue))
                    {
                        value = Convert.ToInt32(parameterValue);
                        error = "";
                        return true;
                    }

                    error = "returned no value";
                    return false;
                }
                default:
                    error = "unknown mode";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    private static bool TrySetFeatureValue(ManagementObject other, uint id, int value, SetFeatureMode mode, out string error)
    {
        try
        {
            switch (mode)
            {
                case SetFeatureMode.Positional:
                    other.InvokeMethod("SetFeatureValue", new object[] { id, unchecked((uint)value) });
                    error = "";
                    return true;
                case SetFeatureMode.Named:
                    using (var inParams = other.GetMethodParameters("SetFeatureValue"))
                    {
                        SetParameter(inParams, ["Data", "Id", "ID", "FeatureId", "AttributeId"], id);
                        SetParameter(inParams, ["Value", "value", "Data2"], unchecked((uint)value));
                        other.InvokeMethod("SetFeatureValue", inParams, null);
                    }
                    error = "";
                    return true;
                default:
                    error = "unknown mode";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    private static void SetParameter(ManagementBaseObject parameters, string[] names, object value)
    {
        foreach (var name in names)
        {
            if (parameters.Properties[name] is not null)
            {
                parameters[name] = value;
                return;
            }
        }

        var available = string.Join(", ", parameters.Properties.Cast<PropertyData>().Select(property => property.Name));
        throw new InvalidOperationException($"None of the WMI parameters [{string.Join(", ", names)}] exist. Available: {available}");
    }

    private static bool TryGetParameter(ManagementBaseObject parameters, string[] names, out object? value)
    {
        foreach (var name in names)
        {
            if (parameters.Properties[name] is not null)
            {
                value = parameters[name];
                return value is not null;
            }
        }

        value = null;
        return false;
    }

    private static string DescribeException(Exception exception)
    {
        return exception is ManagementException managementException
            ? $"{exception.GetType().Name}({managementException.ErrorCode}): {exception.Message}"
            : $"{exception.GetType().Name}: {exception.Message}";
    }

    private static bool IsActive(ManagementBaseObject item)
    {
        return item.Properties["Active"] is null || Convert.ToBoolean(item["Active"]);
    }

    private static int[] ToIntArray(object? value)
    {
        if (value is Array array)
            return array.Cast<object>().Select(Convert.ToInt32).ToArray();
        return [];
    }

    private static string DescribeReadMode(ReadFeatureMode mode)
    {
        return mode switch
        {
            ReadFeatureMode.PositionalOut => "positional [id, out value]",
            ReadFeatureMode.PositionalReturn => "positional [id]",
            ReadFeatureMode.Named => "named parameters",
            _ => mode.ToString()
        };
    }

    private static string DescribeSetMode(SetFeatureMode mode)
    {
        return mode switch
        {
            SetFeatureMode.Positional => "positional [id, value]",
            SetFeatureMode.Named => "named parameters",
            _ => mode.ToString()
        };
    }

    private enum ReadFeatureMode
    {
        PositionalOut,
        PositionalReturn,
        Named
    }

    private enum SetFeatureMode
    {
        Positional,
        Named
    }
}
