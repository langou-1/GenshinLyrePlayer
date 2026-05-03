# GenshinLyrePlayer

基于 **C# + Avalonia 11 + .NET 8** 的原神琴（风物之诗琴）自动演奏工具。

- 🎹 21 键（3 个八度，C 大调自然音阶）键位映射
- 📂 导入 `.mid / .midi` 文件
- 👀 钢琴卷帘预览（可演奏音 / 不支持音视觉区分）
- ⏯ 从任意时刻开始演奏（点击卷帘或拖动时间条跳转）
- ⏱ 可配置倒计时、播放倍速
- 🎚 手动 / 自动移调（在 ±36 半音内寻找最多可演奏音的最佳偏移）
- 🚫 自动跳过琴上不存在的音（钢琴卷帘中标红）

## 键位映射

| 八度 | C | D | E | F | G | A | B |
|-----|---|---|---|---|---|---|---|
| 高 (C5–B5) | Q | W | E | R | T | Y | U |
| 中 (C4–B4) | A | S | D | F | G | H | J |
| 低 (C3–B3) | Z | X | C | V | B | N | M |

> 仅支持自然大调（白键）；`#/b` 黑键与超出 C3–B5 范围的音会被识别为"不支持"，在界面中显示为红色，演奏时跳过。

## 构建 / 运行

需要 **.NET 8 SDK**（Windows 10/11）。

```bash
cd GenshinLyrePlayer
dotnet restore
dotnet run -c Release
```

或打包为单文件发布：

```bash
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 使用流程

1. 启动原神并进入弹琴界面。
2. 打开本程序，点击 **📂 导入 MIDI**。
3. 查看钢琴卷帘：
   - 🟢 绿色矩形 = 琴上可演奏的音
   - 🔴 红色矩形 = 不支持的音（黑键 / 越界），演奏时会跳过
4. 如果红色音太多，点击 **自动** 让程序自动挑选最佳移调；也可用 **+ / −** 手动微调。
5. 点击卷帘任意位置或拖动底部时间条，把播放头移动到期望开始的时刻。
6. **切换到原神窗口** 并打开琴界面，按 **`F8`** 即可启动演奏（全局热键，无需焦点在本程序）。
   - 也可在本程序里点击 **▶ 播放**，再在倒计时内切到游戏（默认 3 秒）。
7. 演奏中再按 **`F8`** 可 **暂停**（保留当前播放位置），按 **`F9`** 可完全 **停止并回到开头**。

## 快捷键

| 按键 | 作用范围 | 功能 |
|------|---------|------|
| `F8` | **全局**（任意窗口焦点均可） | 播放 / 暂停 切换 |
| `F9` | **全局**（任意窗口焦点均可） | 停止并回到开头 |
| `Space` | 仅本程序窗口 | 播放 / 暂停 切换 |
| `Home` | 仅本程序窗口 | 回到开头 |

> 💡 推荐工作流：加载好 MIDI 后，把鼠标切到原神 → 打开琴 → 按 **F8**。倒计时结束后开始演奏，中途需要停下就再按一次 F8，继续演奏也只需按 F8。

## 技术要点

- **按键注入**：使用 Win32 `SendInput` 并设置 `KEYEVENTF_SCANCODE`，通过 `MapVirtualKey` 将虚拟键码转换为扫描码。扫描码注入兼容绝大多数通过 DirectInput / Raw Input 读键的游戏（含原神）。
- **时间精度**：播放线程使用 `Stopwatch` 驱动，动态根据下一事件距离调用 `Thread.Sleep` 与 `Thread.SpinWait`，在 CPU 占用和时序精度间取得平衡。
- **MIDI 解析**：使用 [DryWetMidi](https://github.com/melanchall/drywetmidi) 将 MIDI 事件按 tempo map 转换为秒级绝对时间。
- **MVVM**：ViewModel 使用 [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) 的源生成器（`ObservableProperty` / `RelayCommand`）。
- **自绘卷帘**：`PianoRoll` 继承 `Control`，重写 `Render`，内部以 `StyledProperty` + `AffectsRender` 驱动失效。

## 注意事项

- 若游戏以管理员身份运行，则本程序也需以管理员身份启动（修改 [`app.manifest`](app.manifest) 里的 `requestedExecutionLevel` 或右键"以管理员身份运行"）。
- 键盘事件会直接发送到当前拥有焦点的窗口——开始演奏前请确保已切回游戏。
- 若按键失败，关闭可能拦截全局键盘的第三方输入法或反作弊工具。
