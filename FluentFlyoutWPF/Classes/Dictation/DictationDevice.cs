// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;

namespace FluentFlyoutWPF.Classes.Dictation;

public enum DictationDevice
{
    Cpu,
    IntegratedGpu,
    DedicatedGpu,
}

public enum DictationCudaVersion
{
    Cuda12,
    Cuda13,
}

/// <summary>XML-serializable preference keyed by the model's stable file name.</summary>
public sealed class DictationModelDevicePreference
{
    public string Model { get; set; } = "";
    public DictationDevice Device { get; set; }
    public DictationCudaVersion CudaVersion { get; set; } = DictationCudaVersion.Cuda12;
}

public static class DictationDevices
{
    public static string ModelKey(string model) => Path.GetFileName(model);

    public static DictationDevice GpuForModel(string model) =>
        DictationModelStore.Find(ModelKey(model))?.Backend == DictationModelBackend.CrispAsr
            ? DictationDevice.IntegratedGpu
            : DictationDevice.DedicatedGpu;

    public static bool Supports(string model, DictationDevice device) =>
        device == DictationDevice.Cpu || device == DictationDevice.DedicatedGpu
        || (device == DictationDevice.IntegratedGpu
            && DictationModelStore.Find(ModelKey(model))?.Backend == DictationModelBackend.CrispAsr);
}
