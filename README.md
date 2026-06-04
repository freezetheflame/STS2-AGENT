# STS2 AI Agent

杀戮尖塔2 的 AI 策略助手 — 通过 mod 导出完整游戏状态，交给 LLM 做决策分析。

## 项目结构

```
STS2-AGENT/
├── mod/                       # C# Mod (Godot .NET)
│   ├── STS2Agent.json         # Mod 元数据
│   ├── STS2Agent.csproj       # .NET 项目文件
│   └── Scripts/
│       ├── Entry.cs           # Mod 入口
│       ├── AgentTelemetry.cs  # 事件监听 + HTTP 发送
│       └── GameStateExtractor.cs  # 游戏状态提取（反射）
├── server/                    # Python 桥接服务器
│   ├── main.py                # FastAPI 服务器
│   └── requirements.txt
├── docs/
│   └── DEBUG.md               # 调试日志 & 踩坑记录
└── logs/                      # 运行时日志（不提交）
```

## 架构

```
STS2 游戏 + Mod  →  POST /event →  Python 服务器  →  终端实时输出
                  (游戏状态JSON)   (localhost:8765)    (后续接入 LLM)
```

## 当前状态

| 阶段 | 状态 |
|------|------|
| Phase 1: 数据导出 | 🚧 调试中 — 事件流已通，部分字段待修复 |
| Phase 2: LLM 集成 | ⏳ 未开始 |
| Phase 3: 游戏内反馈 | ⏳ 未开始 |

详见 [`docs/DEBUG.md`](docs/DEBUG.md)

## Windows 端环境搭建

### 1. 安装工具

- **[Godot 4.x .NET 版](https://godotengine.org/download/windows/)** — 点第二个按钮 "Godot Engine – .NET"（标注 C# support）。**不要从 Steam/Epic 下载**（无 C#）。
- **[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)**
- Rider（推荐）或 VS Code + C# Dev Kit

### 2. 安装 RitsuLib（⚠️ 重要）

RitsuLib 发布包采用 Loader + 变体架构。**不要只下载单个 DLL！**

去 https://github.com/BAKAOLC/STS2-RitsuLib/releases 下载 **`STS2-RitsuLib.<version>.variant-pack.zip`**，解压到 `Slay the Spire 2/mods/STS2-RitsuLib/`，得到：

```
mods/STS2-RitsuLib/
├── STS2-RitsuLib.dll          ← Loader（~24KB，不含API）
├── ritsulib-variants.json
└── lib/
    └── 0.106.1/
        └── STS2-RitsuLib.dll  ← 真正的 API DLL（~2MB）
```

### 3. 克隆项目 & 修改路径

```bash
git clone https://github.com/freezetheflame/STS2-AGENT.git
```

打开 `mod/STS2Agent.csproj`，检查：
```xml
<Sts2Dir>D:\SteamLibrary\steamapps\common\Slay the Spire 2</Sts2Dir>
```
（如果你的 Steam 库在其他盘，先查 `C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf`）

### 4. 构建 & 运行

```powershell
cd mod
dotnet build
```

启动桥接服务器（WSL 或 Windows 均可）：
```bash
cd server
pip install -r requirements.txt
python main.py
```

启动 STS2 → 启用模组 → 重启游戏 → 进战斗 → 观察服务器终端。

## 调试

游戏日志位置：
```
C:\Users\<user>\AppData\Roaming\SlayTheSpire2\logs\godot.log
```

搜索 `[DUMP]` 查看运行时类型诊断输出。

## 许可

MIT
