# M110 Mic · 鼠标麦克风

将你闲置的老旧蓝牙语音鼠标变成麦克风！

本项目以讯飞 M110 蓝牙语音鼠标为例，通过蓝牙接收鼠标音频，解码后送入虚拟麦克风，接入网易叭哥说等语音软件。按住语音键说话，松开结束录音。实测环境为 M110、Windows 11 x64、蓝牙连接，其他型号可参考源码移植。

## 使用

[下载 Windows 版](https://github.com/yinzixu03/bluetooth-mouse-mic/releases/latest)

1. 通过 Windows 蓝牙配对 M110，关闭其他占用鼠标语音通道的程序。
2. 安装 [VB-CABLE](https://vb-audio.com/Cable/)。
3. 解压发布包，打开 `M110Mic.exe`，保持程序与 `support` 文件夹位于同一目录。
4. 在语音软件中选择 `CABLE Output` 作为麦克风。
5. 使用网易联动时，将网易快捷键设为 `Right Alt`，开启工具中的联动开关，并让网易处于待录音状态。
6. 按住鼠标语音键说话，松开结束。

系统默认播放设备保持为扬声器或耳机。工具将鼠标声音送入 `CABLE Input`，语音软件从 `CABLE Output` 接收声音。

点击“暂停连接”停止桥接；最小化或收起到托盘后继续运行；关闭窗口退出。单次录音上限为 60 秒。手动切换网易录音后，可先结束录音，再重新连接工具以同步状态。

## 原理解析

鼠标通过蓝牙发送压缩音频，工具把它还原成 PCM 声音，再送进虚拟音频设备，供语音软件读取。M110 的处理过程是：

```text
蓝牙设备握手 → 语音键通知 → 开启采音 → 接收 ICOV1 音频
→ 转换为 G.722.1 帧 → 解码为 PCM → VB-CABLE → 语音软件
```

M110 每帧音频长 20 ms，解码后得到 320 个采样，格式为 16 kHz、16 位、单声道。网易联动在鼠标按下和松开时各发送一次完整的右 Alt 点按，分别控制录音开始和结束。

### 源码来源

- 界面、蓝牙协议适配、音频输出和快捷键控制代码位于 [`src/`](src/)。
- 解码库来自 [libsiren](https://github.com/kakaroto/libsiren)，版本为 `c7724c03d4a6daf4d6dff9e917c14ce5071dce66`。适配后的源码位于 [`src/codec/`](src/codec/)，作者、许可和修改记录见 [`UPSTREAM.md`](src/codec/UPSTREAM.md) 与 [`第三方说明`](licenses/THIRD_PARTY_NOTICES.md)。
- 可运行包的 `support/source.zip` 提供该版本的对应源码。

### 移植到其他鼠标

按下面的顺序逐段接通。先按下方“从源码生成程序”的步骤编译项目；修改 `src/` 后重新编译，运行生成的 `support/` 中的后台脚本。

#### 1. 找到蓝牙数据通道

配对目标鼠标，用 BLE 调试工具枚举它的服务和特征，记录服务 UUID、可写特征 UUID，以及支持 Notify 的特征 UUID。可从微软的 [Bluetooth LE Explorer](https://github.com/microsoft/BluetoothLEExplorer) 入手，订阅通知后观察按键和采音时的数据变化。

在 `src/monitor_key_events.ps1` 中调整设备名称筛选条件 `^M110`，并替换三处 UUID。当前 M110 使用以下通道：

| 用途 | UUID |
| --- | --- |
| 服务 | `0000eae0-0000-1000-8000-00805f9b34fb` |
| 写入控制指令 | `0000eae1-0000-1000-8000-00805f9b34fb` |
| 接收通知 | `0000eae2-0000-1000-8000-00805f9b34fb` |

重新构建后，在源码目录运行以下命令，期间按几次语音键：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\support\monitor_key_events.ps1 -DurationSeconds 30 -IncludeHeaders -DecodeKeyEvents
```

这一步观察通知时间、长度、前三个字节，以及符合现有格式的按键事件。需要比较完整报文时，可在 BLE 调试工具中查看通知原始字节。

#### 2. 识别按下与松开

分别记录短按、长按和松开时的通知，多次比较，找出按键编号与状态字段。M110 的事件以 `FE` 开头，命令字为 `09`，事件类型为 `01`；从字节偏移 `8`（从 `0` 计数）读取一个小端 32 位值，高 16 位是按键编号，低 16 位是状态。语音键编号为 `0`，按下为 `1`，松开为 `0`。

在 `src/GattNotificationObserver.cs` 中修改 `OnValueChanged()` 的按键事件筛选条件和 `DescribeKeyEvent()`，让后台统一输出 `key=编号 state=状态`。随后调整启动参数 `-KeyId`，用 `-KeyMode hold` 将按下映射为开始、松开映射为结束。界面的默认启动参数在 `src/M110MicApp.cs` 的 `StartBridge()` 中。

验收点：每次按下和松开各产生一条正确事件，按住时重复通知也能保持同一次录音。

#### 3. 完成握手和开关采音

查看设备的公开协议或现有实现；协议需要自行分析时，对照原软件连接、按下语音键、松开语音键三个阶段的蓝牙通信，整理指令顺序、序号、长度、校验和应答。

对应代码集中在 `GattNotificationObserver.cs`：

- `InitializeSessionAsync()`：设备初始化与连接验证。
- `SendControlAsync()`、`RequestControlAsync()`：发送指令并等待匹配的应答。
- `StartAudioCheckedAsync()`、`StopAudioCheckedAsync()`：开启与停止采音。

按目标协议调整这些方法。M110 的握手还涉及 SDBM、AES 和 MD5，设备信息在连接时读取；其他型号需要按各自的握手流程处理。

验收时退出其他占用程序，将鼠标关机再开机，单独运行工具。确认设备回应开关采音指令，指示灯随之变化，采音期间持续收到音频通知。

#### 4. 拆出音频帧并接入解码器

比较连续采音时的通知，确定音频类型、报头、有效载荷、校验字段和分片方式。M110 当前每条音频通知为 53 字节：命令字为 `A0`，音频类型为 `2`，从偏移 `9` 取出 40 字节 ICOV1 数据。

在 `OnValueChanged()` 中调整音频报文筛选和载荷截取；有分片的协议先拼成完整编码帧。随后修改 `PcmCodec.Decode()` 中的转换：本项目先对 ICOV1 数据做字排列、异或和字节序转换，再送入 libsiren 解码。

复用解码器的前提是编码、码率、采样率和帧格式一致。目标设备使用其他编码时，替换 `PcmCodec` 中的解码调用；输出采样率、通道数或位深改变时，同步调整 `WaveSink` 的声音格式。

握手和开关采音已接通后，可用以下命令短暂开麦约 3 秒，查看解码帧数、采样数和峰值：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\support\monitor_key_events.ps1 -TestAudioStream -DecodeAudio -DurationSeconds 5
```

验收点：解码采样数符合帧时长，说话时音量随声音变化，静音时电平降低。接上 VB-CABLE 后，再从 `CABLE Output` 试听，检查语速、音调和连续性。

#### 5. 接入语音软件并整理适配结果

先关闭界面中的网易联动，验证鼠标声音能送到语音软件；再开启联动，检查按下开始、松开结束。快捷键逻辑位于 `src/M110HotkeyLink.cs`，界面启动参数位于 `M110MicApp.cs`。

完成后记录鼠标型号、固件、连接方式、UUID、按键格式、编码参数，以及冷启动和连续录音的测试结果。提交适配代码时附上这些信息，后续使用同款鼠标的人就能照着复现。

### 从源码生成程序

这一步将界面、蓝牙桥接和解码器源码编译成可运行的 `M110Mic.exe`、`GattNotificationObserver.dll` 和 `m110_g7221.dll`。修改源码后，重新编译即可生成更新后的程序。

编译环境：64 位 Windows、Windows PowerShell 5.1、系统 .NET Framework 和 [Zig 0.16.0](https://ziglang.org/download/)。

在源码目录运行，将 Zig 路径替换为本机位置：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build.ps1 -ZigPath 'C:\tools\zig\zig.exe'
```

生成 `M110Mic.exe` 和 `support/`。运行以下命令打包：

```powershell
powershell -NoProfile -File .\tools\package.ps1
```

`dist/` 中生成源码包和 Windows 可运行包。

## 许可

项目自有代码采用 [MIT](LICENSE) 许可。libsiren 采用 LGPL 2.1 或更高版本，许可文本位于 `src/codec/COPYING`，第三方说明见 `licenses/THIRD_PARTY_NOTICES.md`。发布包附带对应源码，支持修改、重新构建和替换解码 DLL。
