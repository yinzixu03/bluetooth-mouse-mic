using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Threading;
using Buffer = System.Buffer;
using Windows.Foundation;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

// WinRT callbacks run on a background thread; they must not call PowerShell.
// By default the observer records only notification time and length. Optional
// diagnostic modes expose headers, key-event metadata, or the version response.
public sealed class GattNotificationObserver : IDisposable
{
    private readonly ConcurrentQueue<string> events = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<byte[]> controlReplies = new ConcurrentQueue<byte[]>();
    private readonly ConcurrentQueue<byte[]> authenticationReplies = new ConcurrentQueue<byte[]>();
    private byte controlSequence = 40;
    private bool sessionAuthenticated;
    public bool IncludeHeaders { get; set; }
    public bool DecodeKeyEvents { get; set; }
    public bool DecodeVersionReply { get; set; }
    public bool DecodeCommandReplies { get; set; }
    public bool DecodeAudioHeaders { get; set; }
    public bool OutputSilence { get; set; }
    private readonly object codecLock = new object();
    private readonly AutoResetEvent notificationReady = new AutoResetEvent(false);
    private PcmCodec codec;
    private long decodedFrames, decodedSamples;
    private int maxAmplitude;
    private WaveSink sink;

    public void EnableAudioDecoding()
    {
        lock (codecLock) { codec = new PcmCodec(); }
    }

    public string AudioSummary()
    {
        lock (codecLock)
        {
            string output = sink == null ? "" : " " + sink.Summary();
            return String.Format("decoded_frames={0} decoded_samples={1} peak={2}{3}", decodedFrames, decodedSamples, maxAmplitude, output);
        }
    }

    public void EnableAudioOutput(string deviceName)
    {
        lock (codecLock) { sink = new WaveSink(deviceName); }
    }

    public void CloseAudioDecoding()
    {
        lock (codecLock)
        {
            if (sink != null) { sink.Dispose(); sink = null; }
            if (codec != null) { codec.Dispose(); codec = null; }
        }
    }

    public void Attach(GattCharacteristic characteristic)
    {
        characteristic.ValueChanged += OnValueChanged;
    }

    public bool WaitForEvent(int timeoutMilliseconds) { return notificationReady.WaitOne(timeoutMilliseconds); }
    public void AttachDevice(BluetoothLEDevice device) { device.ConnectionStatusChanged += ConnectionChanged; }
    public void DetachDevice(BluetoothLEDevice device) { device.ConnectionStatusChanged -= ConnectionChanged; }
    private void ConnectionChanged(BluetoothLEDevice device, object args)
    {
        if (device.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        { events.Enqueue("connection=disconnected"); SignalNotification(); }
    }
    private void SignalNotification() { try { notificationReady.Set(); } catch (ObjectDisposedException) { } }
    public void Dispose() { CloseAudioDecoding(); notificationReady.Dispose(); }

    public void Detach(GattCharacteristic characteristic)
    {
        characteristic.ValueChanged -= OnValueChanged;
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (args.CharacteristicValue.Length >= 8 && args.CharacteristicValue.Length <= 128)
        {
            using (var controlReader = DataReader.FromBuffer(args.CharacteristicValue))
            {
                var reply = new byte[(int)args.CharacteristicValue.Length];
                controlReader.ReadBytes(reply);
                if (reply[0] == 0xFE && (reply[2] == 0x81 || reply[2] == 1)) controlReplies.Enqueue(reply);
            }
        }
        if (codec != null && args.CharacteristicValue.Length == 53)
        {
            using (var audioReader = DataReader.FromBuffer(args.CharacteristicValue))
            {
                byte[] frame = new byte[53];
                audioReader.ReadBytes(frame);
                if (frame[0] == 0xFE && frame[2] == 0xA0 && frame[3] == 42 && frame[4] == 0 && frame[5] == 0 && frame[6] == 0 && frame[7] == 2 && frame[8] == 0)
                {
                    byte[] encoded = new byte[40];
                    System.Buffer.BlockCopy(frame, 9, encoded, 0, encoded.Length);
                    lock (codecLock)
                    {
                        if (codec != null)
                        {
                            short[] samples;
                            if (codec.Decode(encoded, out samples))
                            {
                                decodedFrames++;
                                decodedSamples += samples.Length;
                                foreach (short sample in samples) maxAmplitude = Math.Max(maxAmplitude, Math.Abs((int)sample));
                                if (sink != null)
                                {
                                    try { sink.Write(OutputSilence ? new short[samples.Length] : samples); }
                                    catch (Exception error) { events.Enqueue("audio_output_error=" + error.Message); }
                                }
                            }
                        }
                    }
                }
            }
        }
        var value = DateTime.Now.ToString("HH:mm:ss.fff") + " length=" + args.CharacteristicValue.Length;
        if ((IncludeHeaders || DecodeKeyEvents || DecodeVersionReply || DecodeCommandReplies || DecodeAudioHeaders) && args.CharacteristicValue.Length >= 3)
        {
            using (var reader = DataReader.FromBuffer(args.CharacteristicValue))
            {
                int length = (int)Math.Min(args.CharacteristicValue.Length, 15u);
                byte[] prefix = new byte[length];
                reader.ReadBytes(prefix);
                if (IncludeHeaders)
                    value += String.Format(" header={0:X2}{1:X2}{2:X2}", prefix[0], prefix[1], prefix[2]);
                if (DecodeKeyEvents && length >= 12 && prefix[0] == 0xFE && prefix[2] == 0x09 && prefix[7] == 1)
                {
                    value += " " + DescribeKeyEvent(prefix);
                }
                if (DecodeVersionReply && length >= 15 && prefix[0] == 0xFE && prefix[1] == 1 && prefix[2] == 0x81)
                {
                    value += " version_reply=" + BitConverter.ToString(prefix, 7, 8);
                }
                if (DecodeCommandReplies && length >= 11 && prefix[0] == 0xFE && prefix[2] == 0x81)
                {
                    value += String.Format(" reply_sequence={0} result={1}", prefix[1], BitConverter.ToUInt32(prefix, 7));
                }
                if (DecodeAudioHeaders && length >= 9 && prefix[0] == 0xFE && prefix[2] == 0xA0)
                {
                    value += String.Format(" audio_type={0} channel={1} payload_length={2}", prefix[7], prefix[8], BitConverter.ToUInt32(prefix, 3));
                }
            }
        }
        events.Enqueue(value);
        SignalNotification();
    }

    public string[] Drain()
    {
        var result = new List<string>();
        string value;
        while (events.TryDequeue(out value)) result.Add(value);
        return result.ToArray();
    }

    public static string DescribeKeyEvent(byte[] frame)
    {
        if (frame == null || frame.Length < 12 || frame[0] != 0xFE || frame[2] != 0x09 || frame[7] != 1) return null;
        uint code = BitConverter.ToUInt32(frame, 8);
        return String.Format("key={0} state={1}", code >> 16, code & 0xFFFF);
    }

    public IAsyncOperation<GattCommunicationStatus> QueryProtocolVersion(GattCharacteristic characteristic)
    {
        byte[] request = { 0xFE, 0x01, 0x06, 0x01, 0x00, 0x00, 0x00, 0x08, 0x9E, 0x0B, 0xA4, 0xD8 };
        return SendRequest(characteristic, request);
    }

    public IAsyncOperation<GattCommunicationStatus> StartAudio(GattCharacteristic characteristic)
    {
        // Protocol version 1: start recording in mode 0; CRC32C of 0x00.
        byte[] request = { 0xFE, 0x02, 0x02, 0x01, 0x00, 0x00, 0x00, 0x00, 0x51, 0x53, 0x7D, 0x52 };
        return SendRequest(characteristic, request);
    }

    public IAsyncOperation<GattCommunicationStatus> StopAudio(GattCharacteristic characteristic)
    {
        byte[] request = { 0xFE, 0x03, 0x03, 0x00, 0x00, 0x00, 0x00 };
        return SendRequest(characteristic, request);
    }

    private static IAsyncOperation<GattCommunicationStatus> SendRequest(GattCharacteristic characteristic, byte[] request)
    {
        using (var writer = new DataWriter())
        {
            writer.WriteBytes(request);
            return characteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
        }
    }

    private static byte[] Slice(byte[] bytes, int offset, int length)
    {
        var result = new byte[length]; Buffer.BlockCopy(bytes, offset, result, 0, length); return result;
    }

    private static uint Crc32C(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82F63B78u : 0u);
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static uint Sdbm(byte[] bytes)
    {
        uint hash = 0;
        unchecked { foreach (byte value in bytes) hash = value + (hash << 6) + (hash << 16) - hash; }
        return hash & 0x7FFFFFFFu;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int size = 0; foreach (var part in parts) size += part.Length;
        var result = new byte[size]; int offset = 0;
        foreach (var part in parts) { Buffer.BlockCopy(part, 0, result, offset, part.Length); offset += part.Length; }
        return result;
    }

    private async Task<byte> SendControlAsync(GattCharacteristic tx, byte command, byte[] payload)
    {
        byte sequence = unchecked(++controlSequence);
        byte[] request = Concat(new byte[] {0xFE, sequence, command}, BitConverter.GetBytes(payload.Length), payload,
            payload.Length == 0 ? new byte[0] : BitConverter.GetBytes(Crc32C(payload)));
        var status = await OperationTask(SendRequest(tx, request)).ConfigureAwait(false);
        if (status != GattCommunicationStatus.Success) throw new InvalidOperationException("Bluetooth command could not be delivered.");
        return sequence;
    }

    private static Task<GattCommunicationStatus> OperationTask(IAsyncOperation<GattCommunicationStatus> operation)
    {
        Type extensions = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            extensions = assembly.GetType("System.WindowsRuntimeSystemExtensions", false);
            if (extensions != null) break;
        }
        if (extensions == null) throw new InvalidOperationException("Windows Runtime task support has not been loaded.");
        foreach (var method in extensions.GetMethods())
        {
            if (method.Name != "AsTask" || !method.IsGenericMethodDefinition || method.GetParameters().Length != 1) continue;
            if (method.GetParameters()[0].ParameterType.Name != "IAsyncOperation`1") continue;
            return (Task<GattCommunicationStatus>)method.MakeGenericMethod(typeof(GattCommunicationStatus)).Invoke(null, new object[] {operation});
        }
        throw new InvalidOperationException("Windows Runtime task adapter unavailable.");
    }

    private async Task<byte[]> RequestControlAsync(GattCharacteristic tx, byte command, byte[] payload)
    {
        byte sequence = await SendControlAsync(tx, command, payload).ConfigureAwait(false);
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            byte[] reply;
            while (controlReplies.TryDequeue(out reply))
            {
                if (reply.Length >= 12 && reply[2] == 1) { authenticationReplies.Enqueue(Slice(reply, 7, reply.Length - 11)); continue; }
                if (reply.Length < 15 || reply[2] != 0x81 || reply[1] != sequence) continue;
                uint result = BitConverter.ToUInt32(reply, 7);
                if (command == 0 && (result & 0xFFFFu) == 1)
                {
                    sessionAuthenticated = (result >> 16) == 1;
                    return Slice(reply, 11, reply.Length - 15);
                }
                if (result != 1) throw new InvalidOperationException("Mouse rejected command " + command + " (device result " + result + ").");
                return Slice(reply, 11, reply.Length - 15);
            }
            await Task.Delay(30).ConfigureAwait(false);
        }
        throw new TimeoutException("Mouse did not acknowledge command " + command + " (sequence " + sequence + ").");
    }

    private async Task<byte[]> WaitAuthenticationPhaseAsync(byte phase)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            byte[] data;
            while (authenticationReplies.TryDequeue(out data)) if (data.Length > 0 && data[0] == phase) return Slice(data, 1, data.Length - 1);
            while (controlReplies.TryDequeue(out data))
            {
                if (data.Length < 12 || data[2] != 1) continue;
                byte[] body = Slice(data, 7, data.Length - 11);
                if (body.Length > 0 && body[0] == phase) return Slice(body, 1, body.Length - 1);
                authenticationReplies.Enqueue(body);
            }
            await Task.Delay(30).ConfigureAwait(false);
        }
        throw new TimeoutException("Mouse connection handshake timed out.");
    }

    private static byte[] AesBlock(byte[] input, byte[] key, bool encrypt)
    {
        using (Aes aes = Aes.Create())
        {
            aes.Key = key; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            using (ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor())
                return transform.TransformFinalBlock(input, 0, input.Length);
        }
    }

    public async Task InitializeSessionAsync(GattCharacteristic tx)
    {
        byte[] clientFinger = new byte[16]; for (int i = 0; i < 16; i++) clientFinger[i] = (byte)i;
        await SendControlAsync(tx, 5, Concat(new byte[] {15}, BitConverter.GetBytes(1))).ConfigureAwait(false);
        await SendControlAsync(tx, 5, Concat(new byte[] {7}, clientFinger)).ConfigureAwait(false);
        byte[] finger = await RequestControlAsync(tx, 6, new byte[] {12}).ConfigureAwait(false);
        byte[] mac = await RequestControlAsync(tx, 6, new byte[] {10}).ConfigureAwait(false);
        byte[] version = await RequestControlAsync(tx, 6, new byte[] {9}).ConfigureAwait(false);
        if (finger.Length != 16 || mac.Length != 6 || version.Length != 4) throw new InvalidOperationException("Unexpected mouse handshake information.");
        byte[] seed = Concat(mac, new byte[] {123}, version, new byte[] {125});
        byte[] key = Concat(seed, BitConverter.GetBytes(Sdbm(seed)));
        await RequestControlAsync(tx, 0, BitConverter.GetBytes(1)).ConfigureAwait(false);
        if (sessionAuthenticated) return;
        byte[] challenge = await WaitAuthenticationPhaseAsync(1).ConfigureAwait(false);
        if (challenge.Length != 16) throw new InvalidOperationException("Unexpected connection challenge.");
        byte[] random = AesBlock(challenge, key, false);
        for (int i = 0; i < 16; i++) random[i] ^= finger[i];
        byte[] response;
        using (MD5 md5 = MD5.Create()) response = md5.ComputeHash(Concat(BitConverter.GetBytes(Sdbm(finger)), random));
        byte[] clientRandom = new byte[16]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(clientRandom);
        byte[] encrypted = new byte[16];
        for (int i = 0; i < 16; i++) { clientRandom[i] %= 61; encrypted[i] = (byte)(clientFinger[i] ^ clientRandom[i]); }
        encrypted = AesBlock(encrypted, key, true);
        await SendControlAsync(tx, 1, Concat(new byte[] {2}, response, encrypted)).ConfigureAwait(false);
        byte[] confirmation = await WaitAuthenticationPhaseAsync(3).ConfigureAwait(false);
        byte[] expected;
        using (MD5 md5 = MD5.Create()) expected = md5.ComputeHash(Concat(BitConverter.GetBytes(Sdbm(clientFinger)), clientRandom));
        int difference = confirmation.Length ^ expected.Length;
        for (int i = 0; i < Math.Min(confirmation.Length, expected.Length); i++) difference |= confirmation[i] ^ expected[i];
        Array.Clear(key, 0, key.Length); Array.Clear(random, 0, random.Length); Array.Clear(clientRandom, 0, clientRandom.Length);
        if (difference != 0) throw new InvalidOperationException("Mouse connection confirmation did not match.");
    }

    public async Task StartAudioCheckedAsync(GattCharacteristic tx)
    {
        await RequestControlAsync(tx, 2, new byte[] {0}).ConfigureAwait(false);
    }

    public async Task StopAudioCheckedAsync(GattCharacteristic tx)
    {
        await RequestControlAsync(tx, 3, new byte[0]).ConfigureAwait(false);
    }

    private sealed class PcmCodec : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr library, string name);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr library);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr Init(int sampleRate);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Convert(IntPtr state, IntPtr input, IntPtr output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Uninit(IntPtr state);

        // libsiren has a process-wide bit reader; serialize all decoder instances.
        private static readonly object NativeGate = new object();
        private IntPtr library, state;
        private readonly Convert convert;
        private readonly Uninit uninit;

        private T Function<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(library, name);
            if (address == IntPtr.Zero) throw new InvalidOperationException("Audio library export missing: " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        public PcmCodec()
        {
            if (IntPtr.Size != 8) throw new InvalidOperationException("This build requires 64-bit Windows PowerShell.");
            string path = Path.Combine(Path.GetDirectoryName(typeof(GattNotificationObserver).Assembly.Location), "m110_g7221.dll");
            lock (NativeGate)
            {
                library = LoadLibraryExW(path, IntPtr.Zero, 0x900);
                if (library == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the local audio decoder.");
                try
                {
                    var init = Function<Init>("Siren7_NewDecoder");
                    convert = Function<Convert>("Siren7_DecodeFrame");
                    uninit = Function<Uninit>("Siren7_CloseDecoder");
                    state = init(16000);
                    if (state == IntPtr.Zero) throw new InvalidOperationException("Audio decoder initialization failed.");
                }
                catch { FreeLibrary(library); library = IntPtr.Zero; throw; }
            }
        }

        public bool Decode(byte[] encoded, out short[] samples)
        {
            if (encoded == null || encoded.Length != 40) { samples = new short[0]; return false; }
            ushort[] words = new ushort[20];
            for (int i = 0; i < 20; i++) words[i] = (ushort)(encoded[2 * i] | (encoded[2 * i + 1] << 8));
            int[,] rotations = {{4,16,9},{8,3,11},{2,6,18},{7,13,17}};
            for (int i = 0; i < 4; i++)
            {
                int a = rotations[i,0], b = rotations[i,1], c = rotations[i,2];
                ushort old = words[a]; words[a] = words[c]; words[c] = words[b]; words[b] = old;
            }
            byte[] packet = new byte[40];
            for (int i = 0; i < 20; i++)
            {
                ushort value = (ushort)(words[i] ^ 0x0416);
                packet[2 * i] = (byte)(value >> 8); packet[2 * i + 1] = (byte)value;
            }
            short[] buffer = new short[320];
            var input = GCHandle.Alloc(packet, GCHandleType.Pinned);
            var output = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                lock (NativeGate)
                {
                    if (state == IntPtr.Zero || convert(state, input.AddrOfPinnedObject(), output.AddrOfPinnedObject()) != 0)
                    { samples = new short[0]; return false; }
                }
                samples = buffer;
                return true;
            }
            finally { input.Free(); output.Free(); }
        }

        public void Dispose()
        {
            lock (NativeGate)
            {
                if (state != IntPtr.Zero) { uninit(state); state = IntPtr.Zero; }
                if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; }
            }
        }
    }

    private sealed class WaveSink : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormat
        {
            public ushort format, channels;
            public uint sampleRate, bytesPerSecond;
            public ushort blockAlign, bitsPerSample, extraSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr data;
            public uint bufferLength, bytesRecorded;
            public UIntPtr user;
            public uint flags, loops;
            public IntPtr next;
            public UIntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WaveCaps
        {
            public ushort manufacturer, product;
            public uint version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
            public uint formats;
            public ushort channels, reserved;
            public uint support;
        }

        [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveOutGetDevCapsW(UIntPtr id, out WaveCaps caps, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, UIntPtr callback, UIntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);

        private sealed class BufferPair { public IntPtr header, data; }
        private readonly List<BufferPair> buffers = new List<BufferPair>();
        private readonly uint headerSize = (uint)Marshal.SizeOf(typeof(WaveHeader));
        private IntPtr handle;
        private long submitted, dropped;

        private static void Check(uint status, string action)
        {
            if (status != 0) throw new InvalidOperationException(action + " failed with Windows audio code " + status);
        }

        public WaveSink(string deviceName)
        {
            if (String.IsNullOrWhiteSpace(deviceName)) throw new ArgumentException("A specific virtual output name is required.");
            var matches = new List<uint>();
            for (uint i = 0; i < waveOutGetNumDevs(); i++)
            {
                WaveCaps caps;
                Check(waveOutGetDevCapsW((UIntPtr)i, out caps, (uint)Marshal.SizeOf(typeof(WaveCaps))), "Enumerating output");
                if (caps.name.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(i);
            }
            if (matches.Count != 1) throw new InvalidOperationException("Expected one matching virtual audio output; found " + matches.Count);
            var fmt = new WaveFormat { format = 1, channels = 1, sampleRate = 16000, bytesPerSecond = 32000, blockAlign = 2, bitsPerSample = 16, extraSize = 0 };
            Check(waveOutOpen(out handle, matches[0], ref fmt, UIntPtr.Zero, UIntPtr.Zero, 0), "Opening output");
        }

        private void Reap(bool all)
        {
            for (int i = buffers.Count - 1; i >= 0; i--)
            {
                var item = buffers[i];
                var header = (WaveHeader)Marshal.PtrToStructure(item.header, typeof(WaveHeader));
                if (!all && (header.flags & 1) == 0) continue;
                uint status = waveOutUnprepareHeader(handle, item.header, headerSize);
                if (status != 0) continue;
                Marshal.FreeHGlobal(item.data);
                Marshal.FreeHGlobal(item.header);
                buffers.RemoveAt(i);
            }
        }

        public void Write(short[] samples)
        {
            Reap(false);
            if (buffers.Count >= 40) { dropped++; return; }
            var item = new BufferPair();
            item.data = Marshal.AllocHGlobal(samples.Length * 2);
            item.header = Marshal.AllocHGlobal((int)headerSize);
            Marshal.Copy(samples, 0, item.data, samples.Length);
            var header = new WaveHeader { data = item.data, bufferLength = (uint)(samples.Length * 2) };
            Marshal.StructureToPtr(header, item.header, false);
            uint prepare = waveOutPrepareHeader(handle, item.header, headerSize);
            if (prepare != 0) { Marshal.FreeHGlobal(item.data); Marshal.FreeHGlobal(item.header); Check(prepare, "Preparing audio"); }
            uint write = waveOutWrite(handle, item.header, headerSize);
            if (write != 0)
            {
                waveOutUnprepareHeader(handle, item.header, headerSize);
                Marshal.FreeHGlobal(item.data); Marshal.FreeHGlobal(item.header);
                Check(write, "Writing audio");
            }
            buffers.Add(item);
            submitted++;
        }

        public string Summary() { return String.Format("output_frames={0} dropped_frames={1}", submitted, dropped); }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            waveOutReset(handle);
            Reap(true);
            waveOutClose(handle);
            handle = IntPtr.Zero;
        }
    }
}
