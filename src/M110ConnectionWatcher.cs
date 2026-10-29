using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

// Watches Windows' paired-device connection updates; does not scan on a timer.
public sealed class M110ConnectionWatcher : IDisposable
{
    private readonly DeviceWatcher watcher;
    private readonly Dictionary<string, DeviceInformation> devices = new Dictionary<string, DeviceInformation>();
    private readonly object gate = new object();
    private readonly AutoResetEvent changed = new AutoResetEvent(false);
    private bool disposed, lastConnected, reported;
    private long connectionEpoch;

    public M110ConnectionWatcher()
    {
        watcher = DeviceInformation.CreateWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            new string[] {"System.Devices.Aep.IsConnected"}, DeviceInformationKind.AssociationEndpoint);
        watcher.Added += Added; watcher.Updated += Updated; watcher.Removed += Removed;
        watcher.EnumerationCompleted += Enumerated;
    }
    public void Start() { watcher.Start(); }
    public bool IsConnected { get { lock (gate) { return lastConnected && !disposed; } } }
    public long ConnectionEpoch { get { lock (gate) { return connectionEpoch; } } }
    public bool WaitForChange(int milliseconds) { return changed.WaitOne(milliseconds); }
    private void Added(DeviceWatcher sender, DeviceInformation device)
    { lock (gate) { if (disposed) return; devices[device.Id] = device; Publish(); } }
    private void Updated(DeviceWatcher sender, DeviceInformationUpdate update)
    { lock (gate) { DeviceInformation device; if (disposed || !devices.TryGetValue(update.Id, out device)) return; device.Update(update); Publish(); } }
    private void Removed(DeviceWatcher sender, DeviceInformationUpdate update)
    { lock (gate) { if (disposed) return; devices.Remove(update.Id); Publish(); } }
    private void Enumerated(DeviceWatcher sender, object args)
    { lock (gate) { if (!disposed) Publish(); } }
    private void Publish()
    {
        bool connected = false;
        foreach (var device in devices.Values)
        {
            object value;
            if (device.Name.StartsWith("M110", StringComparison.OrdinalIgnoreCase) &&
                device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out value) && value is bool && (bool)value)
            { connected = true; break; }
        }
        if (!reported || connected != lastConnected)
        { if (connected) connectionEpoch++; reported = true; lastConnected = connected; changed.Set(); }
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; devices.Clear(); }
        watcher.Added -= Added; watcher.Updated -= Updated; watcher.Removed -= Removed;
        watcher.EnumerationCompleted -= Enumerated;
        if (watcher.Status == DeviceWatcherStatus.Started || watcher.Status == DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        changed.Dispose();
    }
}
