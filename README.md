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

## 原理与源码来源

工具通过蓝牙完成设备握手，接收语音键通知和 ICOV1 音频帧，将音频转换为标准 G.722.1 帧并解码，再通过 VB-CABLE 送入语音软件。音频格式为 16 kHz、16 位、单声道，每帧 20 ms。

网易联动在鼠标按下和松开时各发送一次完整的右 Alt 点按，分别控制录音开始和结束。

- 界面、蓝牙协议适配、音频输出和快捷键控制：`src/`。
- 解码库：[libsiren](https://github.com/kakaroto/libsiren)，源码位于 `src/codec/`；版本与修改记录见该目录的 `UPSTREAM.md`。
- 发布包的对应源码：`support/source.zip`。

移植其他型号时，调整设备查找条件、蓝牙 UUID、握手、按键编号和音频帧格式。

## 构建

构建环境：64 位 Windows、Windows PowerShell 5.1、系统 .NET Framework 和 [Zig 0.16.0](https://ziglang.org/download/)。

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
