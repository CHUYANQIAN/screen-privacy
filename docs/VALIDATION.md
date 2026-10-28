# v0.3.0 验证与文件身份

日期：2026-10-28（Asia/Shanghai）。作者已确认完成本机使用检查。此次重建只更新发行版本信息，功能逻辑保持一致；没有将作者的确认补写为不存在的逐用例录像或测量数据。

## 文件

| 项目 | 值 |
| --- | --- |
| 标签与产品版本 | v0.3.0 / 0.3.0 |
| 数字文件版本 | 0.3.0.0 |
| 分发包 | `ScreenPrivacy-v0.3.0-windows-anycpu.zip` |
| 分发包 SHA-256 | `8C5A92A40447056E0673EAB3F2BEF6614F07AEEA9950832AF172B3C51C9CC380` |
| 主程序 SHA-256 | `9D5FA29E4472489A1C98D8B36FC8A1C5197FD0031B0CCB336ABA617EB54AB989` |
| 签名 | NotSigned |
| 许可 | MIT，CHUYANQIAN |

下载包包含 `app/ScreenPrivacy.exe`、兼容恢复入口、两个 `.config`、使用说明和 LICENSE。未包含用户配置、密码派生记录或运行日志。

## 检查记录

| 项目 | 本轮结果 | 范围 |
| --- | --- | --- |
| 作者本机使用检查 | 作者确认完成 | 高层使用确认；未提供公开摄像原片或逐项计数 |
| [逻辑检查](validation/self-test.json) | 97/97 | 实际生产模型；零可见遮罩 |
| [原生热键检查](validation/native-hotkey-checks.json) | 5/5 | 35 次登记、39 次独立探针；没有发送按键 |
| [默认键位隐藏窗口](validation/hidden-capabilities.json) | 通过 | 两屏隐藏窗口属性准备与读回 |
| [自定义键位隐藏窗口](validation/custom-hidden-capabilities.json) | 通过 | 恢复键改为 Ctrl+Alt+Shift+F6，F10 可分配主屏 |
| [便携包首次启动](validation/portable-startup.json) | 通过 | 本机新解压目录，配置可写、默认零遮蔽、默认密码关闭、三键就绪；未代表另一台设备 |
| [源码身份](validation/source-identity.json) | 已记录 | 实际构建输入逐文件 SHA-256 |

构建命令：`./tools/build.ps1 -OutputDirectory <独立输出目录>`。构建使用 Windows .NET Framework 编译器；再次构建可能有不同二进制哈希，需重新记录。

环境：Windows build 26300.9711（DisplayVersion 26H2），x64；.NET 注册表版本 4.8.10008、Release 534296；系统 AppliedDPI=144（150%）。主屏 2560×1600 位于 (0,0)，一块副屏 2560×734 位于 (0,1600)。已查询的 Codex 桌面包版本为 26.1023.6123.0；界面查看使用 computer-use / @oai/sky。没有据此宣称其他捕获工具或每屏缩放组合均兼容。

仓库五张界面 PNG 来自该 EXE 的隐藏 WinForms 控件合成渲染。它们是静态布局图，不是双视角实拍。[演示说明](DEMO.md)记录现有素材及未来拍摄步骤。[兼容性说明](COMPATIBILITY.md)列出其他未覆盖环境。
