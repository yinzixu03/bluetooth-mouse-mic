# 第三方许可

项目界面、桥接程序、脚本和文档采用根目录 `LICENSE` 中的 MIT 许可。

## libsiren / m110_g7221.dll

- 来源：[kakaroto/libsiren](https://github.com/kakaroto/libsiren)
- 版本：`c7724c03d4a6daf4d6dff9e917c14ce5071dce66`
- 作者：Youness Alaoui
- 许可：上游源码允许使用 GNU Library GPL 第 2 版或后续版本，本项目选择 LGPL 2.1 或更高版本。
- 许可文本：`src/codec/COPYING`；可运行包中为 `support/LICENSE.LGPL`。
- 源码与修改记录：`src/codec/` 和其中的 `UPSTREAM.md`。
- 构建方法：`README.md` 和 `tools/build.ps1`。

可运行包的 `support/source.zip` 提供对应源码。解码库通过动态链接加载，用户可以修改、重新构建和替换兼容 DLL，也可为调试这些修改而逆向分析程序。再分发时保留源码、作者信息、修改记录和许可文本。

## 外部依赖

Windows 提供 .NET Framework、WinRT 和音频接口。Zig 用于构建解码库。VB-CABLE 需单独安装，遵循其[发布条款](https://vb-audio.com/Cable/)。