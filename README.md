# FloatWord 悬浮背单词

一个常驻桌面的**半透明悬浮背单词工具**：像歌词一样浮在所有窗口之上，直接在单词上逐字输入，边打边背。

参考 [qwerty.kaiyi.cool](https://qwerty.kaiyi.cool/) 的设计思路，用 **WPF + 离线 Piper 语音** 重写为原生 Windows 桌面应用：单机运行、**完全离线**、无边框、可自由摆放。

---

## 目录

- [功能特性](#功能特性)
- [截图](#截图)
- [环境要求](#环境要求)
- [快速开始](#快速开始)
- [使用说明](#使用说明)
- [间隔复习（SRS）规则](#间隔复习srs规则)
- [内置词典](#内置词典)
- [词典格式与导入](#词典格式与导入)
- [配置文件](#配置文件)
- [技术栈](#技术栈)
- [项目结构](#项目结构)
- [发布到 GitHub 前的准备](#发布到-github-前的准备)
- [已知问题](#已知问题)
- [致谢](#致谢)
- [许可证](#许可证)

---

## 功能特性

### 悬浮窗口

- **真 per-pixel alpha** 分层窗口：背景可设为全透明，文字边缘**没有色键方案那种毛边**
- 无边框卡片，**多层圆角阴影**（不用 `DropShadowEffect`，避免半透明卡片被"洗白"）
- 可拖动、置顶、不占任务栏；**窗口尺寸变化时保持中心不动**
- 背景透明度与文字透明度**相互独立**：调背景不会把文字一起变淡
- 文字可加几何描边，缓解小字号锯齿；字号放大不会被裁切

### 学习流程

| 模式 | 说明 |
| --- | --- |
| **学习** | 显示单词 / 音标 / 释义，同时可以逐字输入，拼对自动进入下一个 |
| **本组默写** | 每学满 **3 个**单词，自动进入一次这 3 个词的默写（隐藏单词，只留释义） |
| **复习** | 依据 SRS 状态机生成的复习队列，打乱顺序默写，复习完自动回到学习 |

- **逐字输入**：点一下窗口获得焦点，直接在单词上敲键盘，不需要输入框
- 输错 → 单词变红并清空，同时可播报一次
- 拼对 → 可播报一次，短暂显示答案后自动进入下一个

### 提示功能（复习模式）

- 顶部状态栏的「提示」按钮：**按住显示答案，松开隐藏**
- 状态判定**以是否用过提示为准**：
  - **使用提示** → 连续天数**清零**
  - 单纯拼错 → 连续天数**不清零**

### 间隔复习（SRS）

- 完整的 4 阶段状态机（详见下文），单词按阶段自动排队、按天复习
- 复习模式只显示释义，单词以**等宽横线**占位，间距一致
- 状态栏实时显示该词的学习情况：`未学 / 学习中 / 连续 N/3 天 / 首次成功 / 二次成功 / 已完成`

### 语音播报（离线）

- 使用 **Piper** 离线神经语音，常驻进程（约 **74 ms/词**，端到端约 213 ms）
- 可调**音量 / 增益 / 语速**，可设置新词自动播报、答对播报、答错播报
- 不需要联网，不需要 API Key

### 词典

- 内置 **8 本**考试词典（中考 / 高考 / CET4 / CET6 / 考研 / 雅思 / 托福 / GRE）
- 释义**按词性分行完整显示**，不截断
- 每次启动**确定性打乱**单词顺序（以词典名为随机种子，顺序稳定但非字母序）
- 支持**导入自定义词典**（两种 JSON 格式，见下文）

### 个性化设置

- 背景透明度、文字透明度、单词 / 音标 / 释义字号、文字颜色、描边宽度与颜色
- 字体族（17 种可选）、是否加粗
- 提示框颜色与透明度
- 工具栏**默认隐藏**，鼠标移入才显示；可设为常显
- 设置自动持久化到 `floatword_config.json`

### 学习进度

设置页顶部提供进度条（分母为**当前词典的总词汇数**）：

```
复习（阶段 0）   [          ]   N / 总词数
阶段 1           [          ]   N / 总词数
阶段 2           [          ]   N / 总词数
已完成           [          ]   N / 总词数
```

---

## 截图

> 截图待补充。建议放到 `docs/` 目录后在此引用：
>
> ```markdown
> ![学习模式](docs/study.png)
> ![复习模式](docs/review.png)
> ![设置界面](docs/settings.png)
> ```

---

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809+ / Windows 11（Mica 云母效果需要 Windows 11） |
| 运行时 | .NET 8 Desktop Runtime（从源码构建需 .NET 8 SDK） |
| 网络 | 不需要（语音与词典全部离线） |

> 界面基于 **Fluent 2 / Windows 11 深色令牌** 设计，在 Windows 11 上观感最佳。

---

## 快速开始

### 方式一：直接运行

```
FloatWord.exe
```

程序目录下需要有 `dicts\`（词典）和 `piper\`（语音引擎与模型），发布包/构建产物里已包含。

### 方式二：从源码构建

```powershell
cd FloatWordWpf

# 编译（Debug）
dotnet run

# 编译（Release，产物在 bin\Release\net8.0-windows\）
dotnet build -c Release
```

生成的 `bin\Release\net8.0-windows\FloatWord.exe` 即为可运行程序，`dicts\` 与 `piper\` 会被自动复制到输出目录。

### 方式三：自包含发布（无需用户装 .NET）

```powershell
cd FloatWordWpf
dotnet publish -c Release -r win-x64 --self-contained true
```

> 注意：词典与 Piper 语音是**内容文件**，分发时需要连同 `dicts\`、`piper\` 目录一起打包。

---

## 使用说明

### 鼠标

- **鼠标移入**悬浮窗 → 显示顶部工具栏（可在设置中改为常显）
- **按住卡片空白处拖动** → 移动窗口
- 工具栏按钮：`模式（学习 ⇄ 复习）`、`朗读`、`提示`、`上一个`、`下一个`、`设置`、`退出`

### 键盘

| 按键 | 作用 |
| --- | --- |
| 字母 / 数字 | 逐字输入当前单词 |
| `←` / `→` | 上一个 / 下一个单词 |
| `Enter` | 当前词已拼完时，跳到下一个 |
| `Esc` | 退出程序 |

### 典型流程

1. 启动 → 默认**学习模式**，直接开始逐字输入
2. 学满 3 个 → 自动进入这 3 个词的**本组默写**
3. 默写完成 → 回到学习，继续下一组
4. 点工具栏「模式」→ 进入**复习**，按 SRS 队列默写今天的词
5. 复习中卡住 → **按住「提示」**看答案（注意：会清零连续天数）

---

## 间隔复习（SRS）规则

每个单词有 4 个阶段：

| 阶段 | 名称 | 含义 | 下次复习 |
| --- | --- | --- | --- |
| 0 | 学习中 | 正在学习，**每天**都要默写一遍 | 每天 |
| 1 | 首次成功 | 连续 3 天无提示默写通过 | 10 天后 |
| 2 | 二次成功 | 阶段 1 的复习无提示通过 | 30 天后 |
| 3 | 已完成 | 阶段 2 的复习无提示通过 | — |

### 流转规则

```
阶段 0（学习中）
  │  连续 3 天「无提示」默写通过
  ▼
阶段 1（首次成功）  ── 10 天后随机取 10 个重新加入复习
  │  无提示默写通过
  ▼
阶段 2（二次成功）  ── 30 天后再次加入复习
  │  无提示默写通过
  ▼
阶段 3（已完成）    ── 移出复习队列
```

- **「连续」按日期严格判定**：中间断档一天，连续天数从当天重新计算。
- **只有「用提示」会清零连续天数**；在阶段 0 单纯拼错**不影响**连续天数。
- **阶段 1 / 2 默写错误** → 打回**阶段 0**，需要重新学习。
- 每日复习队列由三部分组成：
  1. 阶段 0 中**今天还没判定过**的词（每日必默写）
  2. 阶段 1 中**已到期**的词，每次**随机取 10 个**
  3. 阶段 2 中**已到期**的词

> 升级说明：旧版本把学习记录存在配置的 `history` 字段里；首次启动新版时会自动迁移为阶段 0，保证已学过的词继续参与复习。

---

## 内置词典

| 词典 | 词数 |
| --- | ---: |
| 中考 | 1,595 |
| 高考 | 3,671 |
| CET4 | 3,837 |
| CET6 | 5,396 |
| 考研 | 4,801 |
| 雅思 | 5,013 |
| 托福 | 6,951 |
| GRE | 7,479 |

数据整理自 **ECDICT**，按考试大纲标签（`zk` / `gk` / `cet4` / `cet6` / `ky` / `ielts` / `toefl` / `gre`）拆分，每条包含单词、音标与按词性分行的中文释义。

---

## 词典格式与导入

### 格式一：本项目格式（推荐）

```json
[
  {
    "word": "abandon",
    "phonetic": "/əˈbændən/",
    "meaning": "v. 放弃，抛弃，遗弃\nn. 放任，纵情"
  }
]
```

### 格式二：Qwerty Learner 格式

```json
[
  {
    "name": "abandon",
    "trans": ["v. 放弃，抛弃", "n. 放任，纵情"],
    "usphone": "ə'bændən"
  }
]
```

解析细节：

- `trans` 数组会**按元素分行**显示
- `usphone` 会自动补上 `/ /`，并把 `'` 转成重音符号 `ˈ`
- 两种格式可以混在同一个文件里

### 导入方法

1. 打开**设置** → 词典 → **导入词典…**
2. 选择 JSON 文件 → 自动校验并复制到程序目录的 `dicts\`
3. 下拉列表会立即刷新并切换到新词典

> 也可以直接把 JSON 文件放进 `dicts\` 目录，重启后出现在词典列表中。文件名即词典名。同名会提示是否覆盖。

---

## 配置文件

保存位置：**与 `FloatWord.exe` 同目录**的 `floatword_config.json`（自动保存，无需手动操作）。

主要字段：

| 字段 | 说明 |
| --- | --- |
| `dict` | 当前词典名 |
| `bg_alpha` / `text_alpha` | 背景 / 文字透明度（0–100） |
| `font_size` / `phon_size` / `mean_size` | 单词 / 音标 / 释义字号 |
| `text_color` / `outline_color` / `outline_w` | 文字颜色、描边颜色与宽度 |
| `hint_color` / `hint_alpha` | 提示框颜色与透明度 |
| `font_family` / `font_bold` | 字体族与是否加粗 |
| `toolbar_pinned` / `show_dict_name` | 工具栏常显 / 显示词典名 |
| `volume` / `gain` / `rate` / `voice` | 音量、增益、语速、语音模型 |
| `autoplay` / `speak_correct` / `speak_wrong` | 自动播报、答对播报、答错播报 |
| `progress` | 每个词典的学习位置（下标） |
| `learn` | **SRS 进度**：词典 → 单词 → `{stage, streak, learned, last_ok, due}` |

> 想重置学习进度，删除 `learn`（单词级 SRS）与 `progress`（当前学到哪）两个字段即可；程序与 Python 旧版的配置键名兼容，未知字段会原样保留。

---

## 技术栈

- **.NET 8 / WPF**（`net8.0-windows`）
- **[WPF-UI](https://github.com/lepoco/wpfui) 4.3.0**：Fluent 2 设计令牌与全量控件样式
- **Piper**（`onnxruntime`）离线神经语音合成
- 分层窗口 + 真 per-pixel alpha；`FormattedText.BuildGeometry` 绘制带描边的逐字文本
- 无 WebView、无 Electron、无 Python 运行时

---

## 项目结构

```
float-word/
├─ FloatWordWpf/
│  ├─ App.xaml / App.xaml.cs      应用入口、Fluent 资源合并、路径
│  ├─ Theme.cs                    Fluent 调色板与颜色工具
│  ├─ app.manifest                清单：Per-Monitor V2 DPI 感知、Windows 10/11 兼容声明
│  ├─ FloatWordWpf.csproj         项目文件（词典与 piper 作为内容输出）
│  ├─ Controls/
│  │  └─ WordCanvas.cs            单词绘制：逐字、描边、等宽槽位排版
│  ├─ Domain/
│  │  ├─ AppSettings.cs           配置持久化 + SRS 状态机
│  │  ├─ Dictionary.cs            词库加载 / 双格式解析 / 确定性打乱
│  │  └─ PiperService.cs          离线 TTS（常驻进程 + 增益）
│  ├─ Views/
│  │  ├─ MainWindow.xaml(.cs)     悬浮窗：学习 / 默写 / 复习 与输入
│  │  ├─ SettingsWindow.cs        设置窗口（FluentWindow + Mica）
│  │  └─ FluentTokens.xaml        少量 Fluent 补充样式
│  ├─ dicts/                      内置词典（8 本 JSON）
│  └─ piper/                      离线语音引擎（bin）与模型（voices）
└─ README.md
```

---

## 发布到 GitHub 前的准备

仓库里已经放好了 [`.gitignore`](.gitignore) 和 [`LICENSE`](LICENSE)，克隆下来直接提交即可。

### 关于 `piper/`：可以让程序直接跑，所以**已随仓库一起上传**

`piper/` 是**绿色免安装**的 Windows 二进制包，不需要安装任何东西，拷到程序目录就能用：

```
piper/
├─ bin/
│  ├─ piper.exe                     引擎本体
│  ├─ espeak-ng.dll / espeak-ng-data  音素化
│  ├─ onnxruntime.dll               推理运行时
│  ├─ piper_phonemize.dll
│  └─ libtashkeel_model.ort
└─ voices/
   └─ en_US-lessac-medium.onnx      语音模型（约 60 MB）
```

因此它**直接跟随仓库提交**，用户下载源码或 Release 后立刻就能发声，无需额外步骤。

需要注意的两点：

- 整个 `piper/` 约 **97 MB**，其中模型 `en_US-lessac-medium.onnx` 约 **60 MB**。GitHub 的单文件提醒阈值是 50 MB（硬限制 100 MB），推送时会有提示但**可以正常上传**。
- 如果以后想给仓库瘦身，可以换成 **Git LFS** 跟踪 `FloatWordWpf/piper/voices/*.onnx`，或把 `piper/` 挪到 Release 附件里。

### `.gitignore` 的一个坑

`.gitignore` 里只写了锚定路径 `/FloatWordWpf/bin/` 与 `/FloatWordWpf/obj/`，**刻意没有**用通配的 `bin/`：

> 如果写成 `bin/`，Git 会把 `FloatWordWpf/piper/bin/` 也忽略掉，Piper 引擎就传不上去了。

---

## 已知问题

- **设置窗口用 Mica + 硬件加速渲染**，部分外部截图/录屏工具抓不到画面（不影响正常使用和显示）。
- 背景透明度设为 0 时窗口仍可拖动（内部把 alpha 字节下限锁在 1/255，肉眼与全透明无异）。
- 复习模式下提示按钮只在有复习队列时才有意义；提示会清零连续天数，请谨慎使用。

---

## 致谢

- [WPF-UI](https://github.com/lepoco/wpfui) —— Fluent 2 控件库
- [Piper](https://github.com/rhasspy/piper) —— 离线神经语音合成
- [ECDICT](https://github.com/skywind3000/ECDICT) —— 词典数据来源
- [qwerty.kaiyi.cool](https://qwerty.kaiyi.cool/) —— 交互形式的最初灵感

---

## 许可证

本项目采用 **MIT License**，详见 [LICENSE](LICENSE)。

随仓库一起分发的第三方组件各自遵循其原始许可证：

| 组件 | 位置 | 许可证 |
| --- | --- | --- |
| [Piper](https://github.com/rhasspy/piper) | `FloatWordWpf/piper/bin`、`piper/voices` | MIT |
| [espeak-ng](https://github.com/espeak-ng/espeak-ng) | `piper/bin/espeak-ng.dll`、`piper/bin/espeak-ng-data` | GPL-3.0 |
| [onnxruntime](https://github.com/microsoft/onnxruntime) | `piper/bin/onnxruntime*.dll` | MIT |
| [ECDICT](https://github.com/skywind3000/ECDICT) | `FloatWordWpf/dicts/*.json` | 以上游仓库声明为准 |

> 二次分发（尤其是商业用途）时请自行核对各上游仓库的许可证，特别是 **GPL-3.0** 的 espeak-ng。
